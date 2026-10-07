using System.Net;
using AcxiomCRM.Models;
using Microsoft.EntityFrameworkCore;
using static AcxiomCRM.Tests.TestHttp;

namespace AcxiomCRM.Tests;

// Step 7: ACT-01..06.
[Collection("db")]
public class ActivityTests(DbFixture fx)
{
    async Task<(ApplicationUser User, HttpClient Client)> UserAsync(string role = Roles.SalesExecutive, string? managerId = null)
    {
        var user = await fx.CreateIdentityUserAsync(role, managerId: managerId);
        return (user, await SignedInAsync(fx, user.UserName!, DbFixture.Password));
    }

    async Task<int> CustomerOfAsync(string ownerId, string? name = null)
    {
        using var db = fx.NewContext();
        var c = DbFixture.NewCustomer(ownerId);
        if (name is not null) c.CustomerName = name;
        db.Customers.Add(c);
        await db.SaveChangesAsync();
        return c.CustomerId;
    }

    static Dictionary<string, string> Form(int? customerId = null, int? leadId = null, string type = "Meeting",
        string status = "Planned", string? subject = null, string date = "2030-03-15T10:30") => new()
    {
        ["ActivityType"] = type,
        ["Subject"] = subject ?? $"Activity {DbFixture.Next()}",
        ["Description"] = "Demo of the product",
        ["ActivityDate"] = date,
        ["CustomerId"] = customerId?.ToString() ?? "",
        ["LeadId"] = leadId?.ToString() ?? "",
        ["Status"] = status,
        ["AssignedTo"] = "",
    };

    static async Task<int> CreateAsync(HttpClient client, Dictionary<string, string> form)
    {
        var response = await PostFormAsync(client, "/Activities/Create", form);
        Assert.True(response.StatusCode == HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync());
        return int.Parse(response.Headers.Location!.OriginalString.Split('/').Last());
    }

    async Task<Activity> LoadAsync(int id)
    {
        using var db = fx.NewContext();
        return await db.Activities.IgnoreQueryFilters().SingleAsync(a => a.ActivityId == id);
    }

    async Task<List<AuditLog>> AuditAsync(int id)
    {
        using var db = fx.NewContext();
        return await db.AuditLogs.Where(a => a.EntityName == "Activity" && a.RecordId == id.ToString()).ToListAsync();
    }

    [Theory] // ACT-01, ACT-02
    [InlineData("Call")]
    [InlineData("Meeting")]
    [InlineData("Email")]
    [InlineData("Task")]
    public async Task Each_activity_type_can_be_logged_and_is_audited(string type)
    {
        var (me, client) = await UserAsync();
        var id = await CreateAsync(client, Form(customerId: await CustomerOfAsync(me.Id), type: type));

        var activity = await LoadAsync(id);
        Assert.Equal(Enum.Parse<ActivityType>(type), activity.ActivityType);
        Assert.Equal(new DateTime(2030, 3, 15, 10, 30, 0), activity.ActivityDate);
        Assert.Equal(me.Id, activity.AssignedTo);
        Assert.Contains(await AuditAsync(id), a => a.Action == "Create");
    }

    [Fact]
    public async Task Activities_appear_in_the_customer_history() // ACT-05, CUS-07
    {
        var (me, client) = await UserAsync();
        var customer = await CustomerOfAsync(me.Id);
        var form = Form(customerId: customer, subject: $"Quarterly review {DbFixture.Next()}");
        await CreateAsync(client, form);

        var details = await client.GetStringAsync($"/Customers/Details/{customer}");
        Assert.Contains(form["Subject"], details);
        Assert.Contains("Related activities", details);
    }

    [Fact]
    public async Task A_visible_related_customer_or_lead_is_required() // VAL-09
    {
        var (me, client) = await UserAsync();
        var (other, _) = await UserAsync();
        int myLead;
        using (var db = fx.NewContext())
        {
            var lead = new Lead { LeadName = "Mine", AssignedTo = me.Id };
            db.Leads.Add(lead);
            await db.SaveChangesAsync();
            myLead = lead.LeadId;
        }

        var none = await PostFormAsync(client, "/Activities/Create", Form());
        Assert.Contains("Choose a customer, a lead, or both.", await none.Content.ReadAsStringAsync());
        var foreign = await PostFormAsync(client, "/Activities/Create", Form(customerId: await CustomerOfAsync(other.Id)));
        Assert.Contains("Select a valid customer.", await foreign.Content.ReadAsStringAsync());

        var both = await LoadAsync(await CreateAsync(client, Form(customerId: await CustomerOfAsync(me.Id), leadId: myLead)));
        Assert.Equal(myLead, both.LeadId);
    }

    [Theory] // VAL-07
    [InlineData("Subject", "", "Subject is required.")]
    [InlineData("ActivityType", "Fax", "Enter a valid value for Type.")]
    [InlineData("ActivityType", "", "Type is required.")]
    [InlineData("Status", "Missed", "Enter a valid value for Status.")]
    [InlineData("ActivityDate", "", "Activity date is required.")]
    [InlineData("Description", "LONG", "Description cannot exceed 2000 characters.")]
    public async Task Invalid_input_is_rejected_by_the_server(string field, string value, string message)
    {
        var (me, client) = await UserAsync();
        var form = Form(customerId: await CustomerOfAsync(me.Id));
        form[field] = value == "LONG" ? new string('x', 2001) : value;

        var response = await PostFormAsync(client, "/Activities/Create", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(message, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Activities_are_scoped_to_assigned_users_and_teams() // ACT-06
    {
        var (manager, managerClient) = await UserAsync(Roles.Manager);
        var (member, memberClient) = await UserAsync(managerId: manager.Id);
        var (_, outsiderClient) = await UserAsync();
        var id = await CreateAsync(memberClient, Form(customerId: await CustomerOfAsync(member.Id)));

        Assert.Equal(HttpStatusCode.OK, (await managerClient.GetAsync($"/Activities/Details/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsiderClient.GetAsync($"/Activities/Details/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostFormAsync(outsiderClient, $"/Activities/Delete/{id}", new(), "/Activities")).StatusCode);
        Assert.False((await LoadAsync(id)).IsDeleted);
    }

    [Fact]
    public async Task Search_filters_by_type_date_status_and_assigned_user() // ACT-04
    {
        var (manager, client) = await UserAsync(Roles.Manager);
        var (member, memberClient) = await UserAsync(managerId: manager.Id);
        var customer = await CustomerOfAsync(manager.Id);
        var memberCustomer = await CustomerOfAsync(member.Id);
        await CreateAsync(client, Form(customerId: customer, type: "Call", date: "2031-01-10T09:00"));
        await CreateAsync(client, Form(customerId: customer, type: "Email", status: "Completed", date: "2031-01-20T09:00"));
        await CreateAsync(memberClient, Form(customerId: memberCustomer, type: "Call", date: "2031-01-10T15:00"));

        const string range = "from=2031-01-10&to=2031-01-20";
        Assert.Contains("Showing 1–3 of 3", await client.GetStringAsync($"/Activities?{range}"));
        Assert.Contains("Showing 1–2 of 2", await client.GetStringAsync($"/Activities?{range}&type=Call"));
        Assert.Contains("Showing 1–1 of 1", await client.GetStringAsync($"/Activities?{range}&status=Completed"));
        Assert.Contains("Showing 1–2 of 2", await client.GetStringAsync("/Activities?from=2031-01-10&to=2031-01-10"));
        Assert.Contains("Showing 1–1 of 1", await client.GetStringAsync($"/Activities?{range}&assignedTo={member.Id}"));
    }

    [Fact]
    public async Task Status_changes_are_audited_and_delete_is_soft()
    {
        var (me, client) = await UserAsync();
        var form = Form(customerId: await CustomerOfAsync(me.Id));
        var id = await CreateAsync(client, form);

        form["Status"] = "Completed";
        await PostFormAsync(client, $"/Activities/Edit/{id}", form, $"/Activities/Edit/{id}");
        Assert.Equal(ActivityStatus.Completed, (await LoadAsync(id)).Status);
        Assert.Contains(await AuditAsync(id), a => a.Action == "StatusChanged");

        await PostFormAsync(client, $"/Activities/Delete/{id}", new(), $"/Activities/Details/{id}");
        Assert.True((await LoadAsync(id)).IsDeleted);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Activities/Details/{id}")).StatusCode);
        Assert.Contains(await AuditAsync(id), a => a.Action == "Delete");
    }
}
