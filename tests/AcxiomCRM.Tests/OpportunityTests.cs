using System.Net;
using AcxiomCRM.Models;
using Microsoft.EntityFrameworkCore;
using static AcxiomCRM.Tests.TestHttp;

namespace AcxiomCRM.Tests;

// Step 8 (part 1): OPP-01..09 and §17.19 #5-7.
[Collection("db")]
public class OpportunityTests(DbFixture fx)
{
    static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);
    static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd");

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

    static Dictionary<string, string> Form(int customerId, string amount = "50000", string probability = "40",
        string stage = "Qualification", DateOnly? close = null, string? name = null, string? assignedTo = null) => new()
    {
        ["OpportunityName"] = name ?? $"Deal {DbFixture.Next()}",
        ["CustomerId"] = customerId.ToString(),
        ["LeadId"] = "",
        ["Amount"] = amount,
        ["Stage"] = stage,
        ["Probability"] = probability,
        ["ExpectedCloseDate"] = Iso(close ?? Today.AddDays(30)),
        ["Source"] = "Referral",
        ["Notes"] = "",
        ["AssignedTo"] = assignedTo ?? "",
    };

    static async Task<int> CreateAsync(HttpClient client, Dictionary<string, string> form)
    {
        var response = await PostFormAsync(client, "/Opportunities/Create", form);
        Assert.True(response.StatusCode == HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync());
        return int.Parse(response.Headers.Location!.OriginalString.Split('/').Last());
    }

    static Task<HttpResponseMessage> EditAsync(HttpClient client, int id, Dictionary<string, string> form) =>
        PostFormAsync(client, $"/Opportunities/Edit/{id}", form, $"/Opportunities/Edit/{id}");

    async Task<Opportunity> LoadAsync(int id)
    {
        using var db = fx.NewContext();
        return await db.Opportunities.IgnoreQueryFilters().SingleAsync(o => o.OpportunityId == id);
    }

    async Task<List<AuditLog>> AuditAsync(int id)
    {
        using var db = fx.NewContext();
        return await db.AuditLogs.Where(a => a.EntityName == "Opportunity" && a.RecordId == id.ToString()).ToListAsync();
    }

    [Fact]
    public async Task Creating_an_opportunity_derives_status_and_shows_the_weighted_amount() // OPP-01..03, OPP-07
    {
        var (me, client) = await UserAsync();
        var id = await CreateAsync(client, Form(await CustomerOfAsync(me.Id), amount: "80000", probability: "25"));

        var o = await LoadAsync(id);
        Assert.Equal(OpportunityStatus.Open, o.Status);
        Assert.Equal(me.Id, o.AssignedTo);
        Assert.Null(o.ClosedDate);
        Assert.Contains(await AuditAsync(id), a => a.Action == "Create");
        Assert.Contains("20,000.00", await client.GetStringAsync($"/Opportunities/Details/{id}")); // 80,000 x 25%
    }

    [Theory] // §17.19 #5-7, OPP-04..06, VAL-13..15, §5.4 messages
    [InlineData("Amount", "0", "Opportunity Amount must be greater than 0.")]
    [InlineData("Amount", "-5", "Amount cannot be negative.")]
    [InlineData("Amount", "", "Amount is required.")]
    [InlineData("Amount", "12abc", "Enter a valid value for Amount.")]
    [InlineData("Probability", "101", "Probability must be between 0 and 100.")]
    [InlineData("Probability", "-1", "Probability must be between 0 and 100.")]
    [InlineData("Probability", "50.5", "Enter a valid value for Probability (%).")]
    [InlineData("ExpectedCloseDate", "YESTERDAY", "Expected Close Date cannot be in the past.")]
    [InlineData("OpportunityName", "", "Opportunity Name is required.")]
    [InlineData("CustomerId", "", "Customer is required.")]
    [InlineData("Stage", "Closed", "Enter a valid value for Stage.")]
    public async Task Crafted_invalid_opportunities_are_rejected_by_the_server(string field, string value, string message)
    {
        var (me, client) = await UserAsync();
        var form = Form(await CustomerOfAsync(me.Id));
        form[field] = value == "YESTERDAY" ? Iso(Today.AddDays(-1)) : value;

        var response = await PostFormAsync(client, "/Opportunities/Create", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(message, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        using var db = fx.NewContext();
        Assert.False(await db.Opportunities.AnyAsync(o => o.OpportunityName == form["OpportunityName"]));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("100")]
    public async Task Probability_boundaries_are_accepted(string probability)
    {
        var (me, client) = await UserAsync();
        var id = await CreateAsync(client, Form(await CustomerOfAsync(me.Id), probability: probability, close: Today));
        Assert.Equal(int.Parse(probability), (await LoadAsync(id)).Probability);
    }

    [Fact]
    public async Task Create_form_carries_the_client_side_rules() // VAL-05, VAL-06
    {
        var (_, client) = await UserAsync();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/Opportunities/Create"));
        Assert.Contains("data-val-greaterthanzero=\"Opportunity Amount must be greater than 0.\"", html);
        Assert.Contains("data-val-greaterthanzero-dependson=\"Stage\"", html);
        Assert.Contains("data-val-notbeforetoday=\"Expected Close Date cannot be in the past.\"", html);
        Assert.Contains("data-val-notbeforetoday-values=\"Qualification,Proposal,Negotiation\"", html);
        Assert.Contains("data-val-range=\"Probability must be between 0 and 100.\"", html);
    }

    [Fact]
    public async Task Moving_through_stages_captures_the_outcome_and_is_audited() // OPP-03, §8 Opportunity workflow
    {
        var (me, client) = await UserAsync();
        var form = Form(await CustomerOfAsync(me.Id));
        var id = await CreateAsync(client, form);

        foreach (var stage in new[] { "Proposal", "Negotiation", "Won" })
        {
            form["Stage"] = stage;
            Assert.Equal(HttpStatusCode.Redirect, (await EditAsync(client, id, form)).StatusCode);
        }
        var won = await LoadAsync(id);
        Assert.Equal(OpportunityStatus.Won, won.Status);
        Assert.NotNull(won.ClosedDate);
        Assert.Equal(3, (await AuditAsync(id)).Count(a => a.Action == "StageChanged"));

        // A closed deal may keep a past close date and a zero amount; reopening clears the outcome.
        form["Stage"] = "Lost"; form["Amount"] = "0"; form["ExpectedCloseDate"] = Iso(Today.AddDays(-10));
        Assert.Equal(HttpStatusCode.Redirect, (await EditAsync(client, id, form)).StatusCode);
        Assert.Equal(OpportunityStatus.Lost, (await LoadAsync(id)).Status);

        form["Stage"] = "Proposal";
        var reopenInPast = await EditAsync(client, id, form);
        Assert.Contains("Expected Close Date cannot be in the past.", await reopenInPast.Content.ReadAsStringAsync());
        Assert.Contains("Opportunity Amount must be greater than 0.", await reopenInPast.Content.ReadAsStringAsync());

        form["Amount"] = "1000"; form["ExpectedCloseDate"] = Iso(Today.AddDays(5));
        Assert.Equal(HttpStatusCode.Redirect, (await EditAsync(client, id, form)).StatusCode);
        var reopened = await LoadAsync(id);
        Assert.Equal(OpportunityStatus.Open, reopened.Status);
        Assert.Null(reopened.ClosedDate);
    }

    [Fact]
    public async Task A_new_opportunity_needs_an_amount_above_zero_even_when_created_closed()
    {
        var (me, client) = await UserAsync();
        var response = await PostFormAsync(client, "/Opportunities/Create",
            Form(await CustomerOfAsync(me.Id), amount: "0", stage: "Lost", close: Today.AddDays(-3)));
        Assert.Contains("Opportunity Amount must be greater than 0.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Status_cannot_be_posted_directly()
    {
        var (me, client) = await UserAsync();
        var form = Form(await CustomerOfAsync(me.Id));
        form["Status"] = "Won";
        form["ClosedDate"] = "2020-01-01";
        var o = await LoadAsync(await CreateAsync(client, form));
        Assert.Equal(OpportunityStatus.Open, o.Status);
        Assert.Null(o.ClosedDate);
    }

    [Fact]
    public async Task Linked_customer_and_lead_must_be_in_scope_and_assignment_follows_D6() // VAL-09, D6
    {
        var (me, client) = await UserAsync();
        var (other, _) = await UserAsync();
        int otherLead;
        using (var db = fx.NewContext())
        {
            var lead = new Lead { LeadName = "Not mine", AssignedTo = other.Id };
            db.Leads.Add(lead);
            await db.SaveChangesAsync();
            otherLead = lead.LeadId;
        }

        var foreignCustomer = await PostFormAsync(client, "/Opportunities/Create", Form(await CustomerOfAsync(other.Id)));
        Assert.Contains("Select a valid customer.", await foreignCustomer.Content.ReadAsStringAsync());

        var withForeignLead = Form(await CustomerOfAsync(me.Id));
        withForeignLead["LeadId"] = otherLead.ToString();
        Assert.Contains("Select a valid lead.", await (await PostFormAsync(client, "/Opportunities/Create", withForeignLead)).Content.ReadAsStringAsync());

        var assigned = await PostFormAsync(client, "/Opportunities/Create", Form(await CustomerOfAsync(me.Id), assignedTo: other.Id));
        Assert.Contains("You cannot assign opportunities to this user.", await assigned.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Users_cannot_see_or_change_opportunities_outside_their_scope() // OPP-09
    {
        var (manager, managerClient) = await UserAsync(Roles.Manager);
        var (member, memberClient) = await UserAsync(managerId: manager.Id);
        var (_, outsider) = await UserAsync();
        var form = Form(await CustomerOfAsync(member.Id));
        var id = await CreateAsync(memberClient, form);

        Assert.Equal(HttpStatusCode.OK, (await managerClient.GetAsync($"/Opportunities/Details/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/Opportunities/Details/{id}")).StatusCode);
        form["Stage"] = "Won";
        Assert.Equal(HttpStatusCode.NotFound, (await PostFormAsync(outsider, $"/Opportunities/Edit/{id}", form, "/Opportunities")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostFormAsync(outsider, $"/Opportunities/Delete/{id}", new(), "/Opportunities")).StatusCode);

        var after = await LoadAsync(id);
        Assert.Equal(OpportunityStage.Qualification, after.Stage);
        Assert.False(after.IsDeleted);
    }

    [Fact]
    public async Task Search_filters_by_name_customer_stage_and_status() // OPP-08
    {
        var (me, client) = await UserAsync();
        var tag = $"O{DbFixture.Next()}";
        var customer = await CustomerOfAsync(me.Id, $"{tag} Holdings");
        await CreateAsync(client, Form(customer, name: $"{tag} Alpha"));
        var won = Form(customer, name: $"{tag} Beta");
        var wonId = await CreateAsync(client, won);
        won["Stage"] = "Won";
        await EditAsync(client, wonId, won);

        Assert.Contains("Showing 1–2 of 2", await client.GetStringAsync($"/Opportunities?search={tag}"));
        Assert.Contains("Showing 1–2 of 2", await client.GetStringAsync($"/Opportunities?customer={tag}%20Holdings"));
        Assert.Contains($"{tag} Beta", await client.GetStringAsync($"/Opportunities?search={tag}&stage=Won"));
        Assert.Contains("Showing 1–1 of 1", await client.GetStringAsync($"/Opportunities?search={tag}&status=Open"));
    }

    [Fact]
    public async Task Pipeline_totals_amount_and_weighted_value_per_stage() // OPP-01, OPP-07
    {
        var (me, client) = await UserAsync();
        var customer = await CustomerOfAsync(me.Id);
        await CreateAsync(client, Form(customer, amount: "1000", probability: "25"));
        await CreateAsync(client, Form(customer, amount: "2000", probability: "50", stage: "Proposal"));
        var won = Form(customer, amount: "5000", probability: "90");
        var wonId = await CreateAsync(client, won);
        won["Stage"] = "Won";
        await EditAsync(client, wonId, won);

        var html = await client.GetStringAsync("/Opportunities/Pipeline");
        Assert.Contains("3,000.00", html);   // open amount
        Assert.Contains("1,250.00", html);   // open weighted: 250 + 1,000
        Assert.Contains("4,500.00", html);   // won weighted
    }

    [Fact]
    public async Task Delete_is_a_soft_delete_and_is_audited()
    {
        var (me, client) = await UserAsync();
        var id = await CreateAsync(client, Form(await CustomerOfAsync(me.Id)));

        await PostFormAsync(client, $"/Opportunities/Delete/{id}", new(), $"/Opportunities/Details/{id}");

        Assert.True((await LoadAsync(id)).IsDeleted);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Opportunities/Details/{id}")).StatusCode);
        Assert.Contains(await AuditAsync(id), a => a.Action == "Delete");
    }
}
