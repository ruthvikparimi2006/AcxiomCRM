using System.Net;
using System.Text.RegularExpressions;
using AcxiomCRM.Models;
using Microsoft.EntityFrameworkCore;
using static AcxiomCRM.Tests.TestHttp;

namespace AcxiomCRM.Tests;

// Step 7: FUP-01, FUP-03..09 for customer/lead follow-ups, reminders (D7), the date rule (D10), scope and audit.
[Collection("db")]
public class FollowUpTests(DbFixture fx)
{
    const string RelatedMessage = "Choose one related record: a customer, a lead or an opportunity.";
    static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);
    static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd");

    async Task<(ApplicationUser User, HttpClient Client)> UserAsync(string role = Roles.SalesExecutive, string? managerId = null)
    {
        var user = await fx.CreateIdentityUserAsync(role, managerId: managerId);
        return (user, await SignedInAsync(fx, user.UserName!, DbFixture.Password));
    }

    async Task<int> CustomerOfAsync(string ownerId)
    {
        using var db = fx.NewContext();
        var c = DbFixture.NewCustomer(ownerId);
        db.Customers.Add(c);
        await db.SaveChangesAsync();
        return c.CustomerId;
    }

    async Task<int> LeadOfAsync(string userId)
    {
        using var db = fx.NewContext();
        var l = new Lead { LeadName = $"Lead {DbFixture.Next()}", AssignedTo = userId };
        db.Leads.Add(l);
        await db.SaveChangesAsync();
        return l.LeadId;
    }

    // Inserted directly so tests can create overdue rows, which the app itself refuses to schedule.
    async Task<int> InsertAsync(string userId, int customerId, DateOnly date, string subject,
        FollowUpStatus status = FollowUpStatus.Planned, int? leadId = null)
    {
        using var db = fx.NewContext();
        var f = new FollowUp
        {
            CustomerId = leadId is null ? customerId : null, LeadId = leadId, FollowUpDate = date, Subject = subject,
            FollowUpType = ActivityType.Call, Status = status, AssignedTo = userId,
        };
        db.FollowUps.Add(f);
        await db.SaveChangesAsync();
        return f.FollowUpId;
    }

    static Dictionary<string, string> Form(int? customerId = null, int? leadId = null, DateOnly? date = null,
        string? subject = null, string? assignedTo = null) => new()
    {
        ["CustomerId"] = customerId?.ToString() ?? "",
        ["LeadId"] = leadId?.ToString() ?? "",
        ["FollowUpDate"] = Iso(date ?? Today.AddDays(2)),
        ["FollowUpType"] = "Call",
        ["Subject"] = subject ?? $"Call back {DbFixture.Next()}",
        ["Remarks"] = "Discuss pricing",
        ["AssignedTo"] = assignedTo ?? "",
    };

    static async Task<int> CreateAsync(HttpClient client, Dictionary<string, string> form)
    {
        var response = await PostFormAsync(client, "/FollowUps/Create", form);
        Assert.True(response.StatusCode == HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync());
        return int.Parse(response.Headers.Location!.OriginalString.Split('/').Last());
    }

    static Task<HttpResponseMessage> ActAsync(HttpClient client, int id, string action, Dictionary<string, string>? fields = null) =>
        PostFormAsync(client, $"/FollowUps/{action}/{id}", fields ?? new(), $"/FollowUps/Details/{id}");

    async Task<FollowUp> LoadAsync(int id)
    {
        using var db = fx.NewContext();
        return await db.FollowUps.IgnoreQueryFilters().SingleAsync(f => f.FollowUpId == id);
    }

    async Task<List<AuditLog>> AuditAsync(int id)
    {
        using var db = fx.NewContext();
        return await db.AuditLogs.Where(a => a.EntityName == "FollowUp" && a.RecordId == id.ToString()).ToListAsync();
    }

    [Fact]
    public async Task Scheduling_a_follow_up_for_a_customer_or_lead_creates_a_planned_follow_up()
    {
        var (me, client) = await UserAsync();
        var customer = await CustomerOfAsync(me.Id);
        var lead = await LeadOfAsync(me.Id);

        var forCustomer = await LoadAsync(await CreateAsync(client, Form(customerId: customer)));
        Assert.Equal(FollowUpStatus.Planned, forCustomer.Status);
        Assert.Equal(me.Id, forCustomer.AssignedTo);
        Assert.Equal(customer, forCustomer.CustomerId);
        Assert.Contains(await AuditAsync(forCustomer.FollowUpId), a => a.Action == "Create");

        var forLead = await LoadAsync(await CreateAsync(client, Form(leadId: lead, date: Today)));
        Assert.Equal(lead, forLead.LeadId);
        Assert.Equal(Today, forLead.FollowUpDate); // today is allowed
    }

    [Theory] // §17.19 #8, FUP-06, VAL-16, D10 (no role is exempt)
    [InlineData(Roles.SalesExecutive)]
    [InlineData(Roles.Admin)]
    public async Task A_follow_up_dated_before_today_is_rejected_for_every_role(string role)
    {
        var (me, client) = role == Roles.Admin
            ? ((await LoadUserAsync(DbFixture.AdminEmail)), await SignedInAsync(fx, DbFixture.AdminEmail, DbFixture.AdminPassword))
            : await UserAsync(role);
        var form = Form(customerId: await CustomerOfAsync(me.Id), date: Today.AddDays(-1));

        var response = await PostFormAsync(client, "/FollowUps/Create", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Follow-up date cannot be earlier than today.", await response.Content.ReadAsStringAsync());
        using var db = fx.NewContext();
        Assert.False(await db.FollowUps.AnyAsync(f => f.Subject == form["Subject"]));
    }

    async Task<ApplicationUser> LoadUserAsync(string email)
    {
        using var db = fx.NewContext();
        return await db.Users.SingleAsync(u => u.Email == email);
    }

    [Fact]
    public async Task Create_form_carries_the_client_side_date_rule() // VAL-05
    {
        var (_, client) = await UserAsync();
        var html = await client.GetStringAsync("/FollowUps/Create");
        Assert.Contains("data-val-notbeforetoday=\"Follow-up date cannot be earlier than today.\"", html);
        Assert.Contains($"min=\"{Iso(Today)}\"", html);
        Assert.Contains("/js/validation-rules.js", html);
        Assert.Contains("data-val-required=\"Subject is required.\"", html);
    }

    [Fact]
    public async Task Exactly_one_visible_related_record_is_required() // VAL-09
    {
        var (me, client) = await UserAsync();
        var (other, _) = await UserAsync();
        var mine = await CustomerOfAsync(me.Id);
        var myLead = await LeadOfAsync(me.Id);

        async Task Rejected(Dictionary<string, string> form, string message)
        {
            var response = await PostFormAsync(client, "/FollowUps/Create", form);
            Assert.Contains(message, await response.Content.ReadAsStringAsync());
        }

        await Rejected(Form(), RelatedMessage);
        await Rejected(Form(customerId: mine, leadId: myLead), RelatedMessage);
        await Rejected(Form(customerId: await CustomerOfAsync(other.Id)), "Select a valid customer.");
        await Rejected(Form(leadId: await LeadOfAsync(other.Id)), "Select a valid lead.");
        await Rejected(Form(customerId: 999999), "Select a valid customer.");
    }

    [Theory] // VAL-07, VAL-12
    [InlineData("Subject", "", "Subject is required.")]
    [InlineData("Subject", "LONG", "Subject cannot exceed 200 characters.")]
    [InlineData("FollowUpType", "Fax", "Enter a valid value for Type.")]
    [InlineData("FollowUpDate", "", "Follow-up date is required.")]
    [InlineData("FollowUpDate", "31/31/2030", "Enter a valid value for Follow-up date.")]
    public async Task Invalid_input_is_rejected_by_the_server(string field, string value, string message)
    {
        var (me, client) = await UserAsync();
        var form = Form(customerId: await CustomerOfAsync(me.Id));
        form[field] = value == "LONG" ? new string('x', 201) : value;

        var response = await PostFormAsync(client, "/FollowUps/Create", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(message, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
    }

    async Task<int> OpportunityOfAsync(string userId)
    {
        using var db = fx.NewContext();
        var o = new Opportunity
        {
            OpportunityName = $"Deal {DbFixture.Next()}", CustomerId = await CustomerOfAsync(userId), AssignedTo = userId,
            Amount = 1000, Probability = 50, ExpectedCloseDate = Today.AddDays(30),
        };
        db.Opportunities.Add(o);
        await db.SaveChangesAsync();
        return o.OpportunityId;
    }

    [Fact]
    public async Task Follow_ups_can_be_scheduled_against_opportunities() // FUP-02 (Step 8)
    {
        var (me, client) = await UserAsync();
        var opportunity = await OpportunityOfAsync(me.Id);
        var form = Form(subject: $"Proposal call {DbFixture.Next()}");
        form["OpportunityId"] = opportunity.ToString();

        var id = await CreateAsync(client, form);
        Assert.Equal(opportunity, (await LoadAsync(id)).OpportunityId);
        Assert.Contains("(opportunity)", await client.GetStringAsync($"/FollowUps/Details/{id}"));
        Assert.Contains(form["Subject"], await client.GetStringAsync("/FollowUps/Pending"));

        // Completing it updates the opportunity (FUP-04).
        await ActAsync(client, id, "Complete");
        using var db = fx.NewContext();
        Assert.NotNull((await db.Opportunities.SingleAsync(o => o.OpportunityId == opportunity)).ModifiedDate);
    }

    [Fact]
    public async Task Opportunity_follow_ups_need_a_visible_opportunity_and_only_one_link()
    {
        var (me, client) = await UserAsync();
        var (other, _) = await UserAsync();

        var foreign = Form();
        foreign["OpportunityId"] = (await OpportunityOfAsync(other.Id)).ToString();
        Assert.Contains("Select a valid opportunity.", await (await PostFormAsync(client, "/FollowUps/Create", foreign)).Content.ReadAsStringAsync());

        var two = Form(customerId: await CustomerOfAsync(me.Id));
        two["OpportunityId"] = (await OpportunityOfAsync(me.Id)).ToString();
        Assert.Contains(RelatedMessage, await (await PostFormAsync(client, "/FollowUps/Create", two)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Deleting_an_opportunity_removes_its_follow_ups_from_reminders()
    {
        var (me, client) = await UserAsync();
        var opportunity = await OpportunityOfAsync(me.Id);
        var subject = $"Opportunity reminder {DbFixture.Next()}";
        using (var db = fx.NewContext())
        {
            db.FollowUps.Add(new FollowUp
            {
                OpportunityId = opportunity, FollowUpDate = Today, Subject = subject, FollowUpType = ActivityType.Call,
                AssignedTo = me.Id,
            });
            await db.SaveChangesAsync();
        }
        Assert.Contains(subject, await client.GetStringAsync("/FollowUps/Pending"));

        await PostFormAsync(client, $"/Opportunities/Delete/{opportunity}", new(), $"/Opportunities/Details/{opportunity}");
        Assert.DoesNotContain(subject, await client.GetStringAsync("/FollowUps/Pending"));
    }

    [Theory] // FUP-04, FUP-07
    [InlineData("Complete", FollowUpStatus.Completed, "Completed")]
    [InlineData("Missed", FollowUpStatus.Missed, "Missed")]
    [InlineData("Cancel", FollowUpStatus.Cancelled, "Cancelled")]
    public async Task Planned_follow_ups_can_be_closed_once_and_the_related_record_is_updated(
        string action, FollowUpStatus expected, string auditAction)
    {
        var (me, client) = await UserAsync();
        var customer = await CustomerOfAsync(me.Id);
        var id = await CreateAsync(client, Form(customerId: customer));

        Assert.Equal(HttpStatusCode.Redirect, (await ActAsync(client, id, action)).StatusCode);

        Assert.Equal(expected, (await LoadAsync(id)).Status);
        using (var db = fx.NewContext())
            Assert.NotNull((await db.Customers.SingleAsync(c => c.CustomerId == customer)).ModifiedDate);
        Assert.Single(await AuditAsync(id), a => a.Action == auditAction);

        // A closed follow-up cannot change again.
        var again = await ActAsync(client, id, "Complete");
        Assert.Contains($"This follow-up is already {expected}.", await client.GetStringAsync(again.Headers.Location!));
        Assert.Equal(expected, (await LoadAsync(id)).Status);
    }

    [Fact]
    public async Task Rescheduling_changes_the_date_and_is_audited_but_never_into_the_past() // FUP-04, FUP-07
    {
        var (me, client) = await UserAsync();
        var lead = await LeadOfAsync(me.Id);
        var id = await CreateAsync(client, Form(leadId: lead, date: Today.AddDays(1)));

        await ActAsync(client, id, "Reschedule", new() { ["newDate"] = Iso(Today.AddDays(5)) });
        Assert.Equal(Today.AddDays(5), (await LoadAsync(id)).FollowUpDate);
        var rescheduled = Assert.Single(await AuditAsync(id), a => a.Action == "Rescheduled");
        Assert.Contains(Iso(Today.AddDays(1)), rescheduled.OldValue);
        Assert.Contains(Iso(Today.AddDays(5)), rescheduled.NewValue);
        using (var db = fx.NewContext())
            Assert.NotNull((await db.Leads.SingleAsync(l => l.LeadId == lead)).ModifiedDate);

        var past = await ActAsync(client, id, "Reschedule", new() { ["newDate"] = Iso(Today.AddDays(-1)) });
        Assert.Contains("Follow-up date cannot be earlier than today.", await client.GetStringAsync(past.Headers.Location!));
        Assert.Equal(Today.AddDays(5), (await LoadAsync(id)).FollowUpDate);

        await ActAsync(client, id, "Complete");
        var closed = await ActAsync(client, id, "Reschedule", new() { ["newDate"] = Iso(Today.AddDays(9)) });
        Assert.Contains("cannot be rescheduled", await client.GetStringAsync(closed.Headers.Location!));
    }

    [Fact]
    public async Task Editing_the_date_counts_as_a_reschedule_and_closed_follow_ups_are_read_only()
    {
        var (me, client) = await UserAsync();
        var form = Form(customerId: await CustomerOfAsync(me.Id));
        var id = await CreateAsync(client, form);

        form["FollowUpDate"] = Iso(Today.AddDays(10));
        Assert.Equal(HttpStatusCode.Redirect, (await PostFormAsync(client, $"/FollowUps/Edit/{id}", form, $"/FollowUps/Edit/{id}")).StatusCode);
        Assert.Contains(await AuditAsync(id), a => a.Action == "Rescheduled");

        await ActAsync(client, id, "Complete");
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync($"/FollowUps/Edit/{id}")).StatusCode);
        form["Subject"] = "Changed after completion";
        var response = await PostFormAsync(client, $"/FollowUps/Edit/{id}", form, $"/FollowUps/Details/{id}");
        Assert.Contains("can no longer be edited", await response.Content.ReadAsStringAsync());
        Assert.NotEqual("Changed after completion", (await LoadAsync(id)).Subject);
    }

    [Fact]
    public async Task Reminders_show_overdue_and_next_7_days_in_the_bell_and_pending_page() // FUP-05, D7
    {
        var (me, client) = await UserAsync();
        var (other, _) = await UserAsync();
        var customer = await CustomerOfAsync(me.Id);
        var tag = $"R{DbFixture.Next()}";
        await InsertAsync(me.Id, customer, Today.AddDays(-3), $"{tag} overdue");
        await InsertAsync(me.Id, customer, Today, $"{tag} today");
        await InsertAsync(me.Id, customer, Today.AddDays(7), $"{tag} day7");
        await InsertAsync(me.Id, customer, Today.AddDays(8), $"{tag} day8");
        await InsertAsync(me.Id, customer, Today.AddDays(-1), $"{tag} done", FollowUpStatus.Completed);
        await InsertAsync(other.Id, await CustomerOfAsync(other.Id), Today.AddDays(-1), $"{tag} someone else");

        var pending = await client.GetStringAsync("/FollowUps/Pending");
        Assert.Contains("Overdue (1)", pending);
        Assert.Contains("(2)", pending);
        foreach (var shown in new[] { "overdue", "today", "day7" }) Assert.Contains($"{tag} {shown}", pending);
        foreach (var hidden in new[] { "day8", "done", "someone else" }) Assert.DoesNotContain($"{tag} {hidden}", pending);

        var bell = await client.GetStringAsync("/Dashboard");
        Assert.Contains("1 overdue, 2 upcoming follow-ups", bell);
        Assert.Matches(new Regex(@"text-bg-danger"">\s*3<"), bell);
        Assert.Contains($"{tag} overdue", bell);
        Assert.DoesNotContain($"{tag} day8", bell);
    }

    [Fact]
    public async Task Managers_get_reminders_for_their_team() // FUP-09
    {
        var (manager, managerClient) = await UserAsync(Roles.Manager);
        var (member, _) = await UserAsync(managerId: manager.Id);
        var tag = $"T{DbFixture.Next()}";
        await InsertAsync(member.Id, await CustomerOfAsync(member.Id), Today.AddDays(1), $"{tag} team");

        Assert.Contains($"{tag} team", await managerClient.GetStringAsync("/FollowUps/Pending"));
    }

    [Fact]
    public async Task Deleting_a_lead_removes_its_follow_ups_from_reminders()
    {
        var (me, client) = await UserAsync();
        var lead = await LeadOfAsync(me.Id);
        var subject = $"Lead reminder {DbFixture.Next()}";
        await InsertAsync(me.Id, 0, Today, subject, leadId: lead);
        Assert.Contains(subject, await client.GetStringAsync("/FollowUps/Pending"));

        await PostFormAsync(client, $"/Leads/Delete/{lead}", new(), $"/Leads/Details/{lead}");
        Assert.DoesNotContain(subject, await client.GetStringAsync("/FollowUps/Pending"));
    }

    [Fact]
    public async Task Other_users_follow_ups_cannot_be_seen_or_changed() // FUP-09, RBAC-04
    {
        var (owner, ownerClient) = await UserAsync();
        var id = await CreateAsync(ownerClient, Form(customerId: await CustomerOfAsync(owner.Id)));
        var (_, intruder) = await UserAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await intruder.GetAsync($"/FollowUps/Details/{id}")).StatusCode);
        foreach (var action in new[] { "Complete", "Missed", "Cancel", "Delete" })
            Assert.Equal(HttpStatusCode.NotFound, (await PostFormAsync(intruder, $"/FollowUps/{action}/{id}", new(), "/FollowUps")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostFormAsync(intruder, $"/FollowUps/Reschedule/{id}",
            new() { ["newDate"] = Iso(Today.AddDays(3)) }, "/FollowUps")).StatusCode);

        var after = await LoadAsync(id);
        Assert.Equal(FollowUpStatus.Planned, after.Status);
        Assert.False(after.IsDeleted);
    }

    [Fact]
    public async Task Assignment_follows_D6()
    {
        var (me, client) = await UserAsync();
        var other = await fx.CreateIdentityUserAsync();
        var response = await PostFormAsync(client, "/FollowUps/Create",
            Form(customerId: await CustomerOfAsync(me.Id), assignedTo: other.Id));
        Assert.Contains("You cannot assign follow-ups to this user.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Search_filters_by_date_status_assigned_user_and_related_record() // FUP-08
    {
        var (manager, client) = await UserAsync(Roles.Manager);
        var (member, _) = await UserAsync(managerId: manager.Id);
        var tag = $"S{DbFixture.Next()}";
        int customer;
        using (var db = fx.NewContext())
        {
            var c = DbFixture.NewCustomer(member.Id);
            c.CustomerName = $"{tag} Industries";
            db.Customers.Add(c);
            await db.SaveChangesAsync();
            customer = c.CustomerId;
        }
        await InsertAsync(member.Id, customer, Today.AddDays(1), "first");
        await InsertAsync(member.Id, customer, Today.AddDays(4), "second", FollowUpStatus.Completed);
        await InsertAsync(manager.Id, await CustomerOfAsync(manager.Id), Today.AddDays(1), "mine");

        var byRelated = await client.GetStringAsync($"/FollowUps?related={tag}");
        Assert.Contains("Showing 1–2 of 2", byRelated);
        Assert.Contains("Showing 1–1 of 1", await client.GetStringAsync($"/FollowUps?related={tag}&status=Completed"));
        Assert.Contains("Showing 1–1 of 1", await client.GetStringAsync(
            $"/FollowUps?related={tag}&from={Iso(Today.AddDays(3))}&to={Iso(Today.AddDays(5))}"));
        Assert.Contains("Showing 1–2 of 2", await client.GetStringAsync($"/FollowUps?related={tag}&assignedTo={member.Id}"));
        Assert.Contains("No follow-ups found.", await client.GetStringAsync($"/FollowUps?related={tag}&assignedTo={manager.Id}"));
    }

    [Fact]
    public async Task Delete_is_a_soft_delete_and_is_audited()
    {
        var (me, client) = await UserAsync();
        var id = await CreateAsync(client, Form(customerId: await CustomerOfAsync(me.Id)));

        await ActAsync(client, id, "Delete");

        Assert.True((await LoadAsync(id)).IsDeleted);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/FollowUps/Details/{id}")).StatusCode);
        Assert.Contains(await AuditAsync(id), a => a.Action == "Delete");
    }
}
