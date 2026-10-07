using System.Net;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using static AcxiomCRM.Tests.TestHttp;

namespace AcxiomCRM.Tests;

// Step 6: LEAD-01..05, LEAD-07..09 (conversion is Step 8), with the §16 status workflow, D6, scope and audit.
[Collection("db")]
public class LeadTests(DbFixture fx)
{
    static Dictionary<string, string> Form(string? name = null, string status = "New", string? assignedTo = null,
        string? company = null, string expectedValue = "250000") => new()
    {
        ["LeadName"] = name ?? $"Lead {DbFixture.Next()}",
        ["Email"] = $"lead{DbFixture.Next()}@example.com",
        ["Phone"] = DbFixture.NextPhone(),
        ["CompanyName"] = company ?? "Prospect Co",
        ["Source"] = "ColdCall",
        ["Status"] = status,
        ["Priority"] = "High",
        ["ExpectedValue"] = expectedValue,
        ["AssignedTo"] = assignedTo ?? "",
    };

    async Task<(ApplicationUser User, HttpClient Client)> UserAsync(string role = Roles.SalesExecutive, string? managerId = null)
    {
        var user = await fx.CreateIdentityUserAsync(role, managerId: managerId);
        return (user, await SignedInAsync(fx, user.UserName!, DbFixture.Password));
    }

    static async Task<int> CreateAsync(HttpClient client, Dictionary<string, string> form)
    {
        var response = await PostFormAsync(client, "/Leads/Create", form);
        Assert.True(response.StatusCode == HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync());
        return int.Parse(response.Headers.Location!.OriginalString.Split('/').Last());
    }

    static Task<HttpResponseMessage> EditAsync(HttpClient client, int id, Dictionary<string, string> form) =>
        PostFormAsync(client, $"/Leads/Edit/{id}", form, $"/Leads/Edit/{id}");

    async Task<Lead> LoadAsync(int id)
    {
        using var db = fx.NewContext();
        return await db.Leads.IgnoreQueryFilters().SingleAsync(l => l.LeadId == id);
    }

    async Task<List<AuditLog>> AuditAsync(int id)
    {
        using var db = fx.NewContext();
        return await db.AuditLogs.Where(a => a.EntityName == "Lead" && a.RecordId == id.ToString()).ToListAsync();
    }

    [Fact]
    public async Task Creating_a_lead_starts_it_as_New_assigned_to_the_creator_and_audits_it()
    {
        var (me, client) = await UserAsync();
        var id = await CreateAsync(client, Form(name: "Patel Exports"));

        var lead = await LoadAsync(id);
        Assert.Equal(LeadStatus.New, lead.Status);
        Assert.Equal(me.Id, lead.AssignedTo);
        Assert.Matches(@"^LEAD-\d{6}$", lead.LeadCode);
        Assert.Equal(LeadSource.ColdCall, lead.Source);
        Assert.Contains(await AuditAsync(id), a => a.Action == "Create" && a.UserId == me.Id);

        var details = await client.GetStringAsync($"/Leads/Details/{id}");
        Assert.Contains("Patel Exports", details);
        Assert.Contains("Cold Call", details);
    }

    [Theory] // LEAD-03: a new lead cannot skip the workflow
    [InlineData("Contacted")]
    [InlineData("Qualified")]
    [InlineData("Converted")]
    public async Task New_leads_must_start_as_New(string status)
    {
        var (_, client) = await UserAsync();
        var form = Form(status: status);
        var response = await PostFormAsync(client, "/Leads/Create", form);

        Assert.Contains("New leads start with status New.", await response.Content.ReadAsStringAsync());
        using var db = fx.NewContext();
        Assert.False(await db.Leads.AnyAsync(l => l.LeadName == form["LeadName"]));
    }

    [Fact]
    public async Task Leads_move_through_the_allowed_workflow_and_each_change_is_audited() // LEAD-04, §16 #4
    {
        var (_, client) = await UserAsync();
        var form = Form();
        var id = await CreateAsync(client, form);

        foreach (var next in new[] { "Contacted", "Qualified" })
        {
            form["Status"] = next;
            Assert.Equal(HttpStatusCode.Redirect, (await EditAsync(client, id, form)).StatusCode);
        }

        Assert.Equal(LeadStatus.Qualified, (await LoadAsync(id)).Status);
        var changes = (await AuditAsync(id)).Where(a => a.Action == "StatusChanged").ToList();
        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c.OldValue!.Contains("New") && c.NewValue!.Contains("Contacted"));
        Assert.Contains("Status: Contacted → Qualified", WebUtility.HtmlDecode(await client.GetStringAsync($"/Leads/Details/{id}")));
    }

    [Theory] // LEAD-04, VAL-20
    [InlineData(new string[0], "Qualified", "A New lead cannot be changed to Qualified.")]
    [InlineData(new string[0], "Converted", "Only qualified leads can be converted, using Convert.")]
    [InlineData(new[] { "Contacted", "Qualified" }, "Converted", "Only qualified leads can be converted, using Convert.")]
    [InlineData(new[] { "Contacted", "Qualified" }, "New", "A Qualified lead cannot be changed to New.")]
    [InlineData(new[] { "Lost" }, "New", "A Lost lead cannot be changed to New.")]
    [InlineData(new[] { "Unqualified" }, "Contacted", "A Unqualified lead cannot be changed to Contacted.")]
    [InlineData(new string[0], "Bogus", "Enter a valid value for Status.")]
    [InlineData(new string[0], "42", "Enter a valid value.")]
    [InlineData(new string[0], "", "Status is required.")]
    public async Task Invalid_status_changes_are_rejected_by_the_server(string[] path, string target, string message)
    {
        var (_, client) = await UserAsync();
        var form = Form();
        var id = await CreateAsync(client, form);
        foreach (var step in path)
        {
            form["Status"] = step;
            Assert.Equal(HttpStatusCode.Redirect, (await EditAsync(client, id, form)).StatusCode);
        }
        var before = (await LoadAsync(id)).Status;

        form["Status"] = target;
        var response = await EditAsync(client, id, form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(message, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        Assert.Equal(before, (await LoadAsync(id)).Status);
    }

    [Fact]
    public async Task Edit_form_offers_only_the_current_status_and_its_manual_next_steps()
    {
        var (_, client) = await UserAsync();
        var id = await CreateAsync(client, Form());
        var html = await client.GetStringAsync($"/Leads/Edit/{id}");

        foreach (var allowed in new[] { "New", "Contacted", "Unqualified", "Lost" })
            Assert.Contains($"<option value=\"{allowed}\"", html.Replace("selected=\"selected\" ", ""));
        Assert.DoesNotContain("<option value=\"Qualified\"", html);
        Assert.DoesNotContain("<option value=\"Converted\"", html);
    }

    [Theory] // LEAD-07, VAL-21, §5.4
    [InlineData("ExpectedValue", "-1", "Expected Value must be between 0 and 100,000,000.")]
    [InlineData("ExpectedValue", "100000000.01", "Expected Value must be between 0 and 100,000,000.")]
    [InlineData("ExpectedValue", "abc", "Enter a valid value for Expected Value.")]
    [InlineData("LeadName", "", "Lead Name is required.")]
    [InlineData("LeadName", "LONG", "Lead Name cannot exceed 150 characters.")]
    [InlineData("Email", "bad@", "Enter a valid email address.")]
    [InlineData("Phone", "0123456789", "Enter a valid phone number.")]
    [InlineData("Source", "Billboard", "Enter a valid value for Source.")]
    [InlineData("Priority", "9", "Enter a valid value.")]
    public async Task Invalid_lead_input_is_rejected_by_the_server(string field, string value, string message)
    {
        var (_, client) = await UserAsync();
        var form = Form();
        form[field] = value == "LONG" ? new string('x', 151) : value;

        var response = await PostFormAsync(client, "/Leads/Create", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(message, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        using var db = fx.NewContext();
        Assert.False(await db.Leads.AnyAsync(l => l.Phone == form["Phone"] && l.Email == form["Email"]));
    }

    [Fact]
    public async Task Boundary_expected_value_and_optional_contact_details_are_accepted()
    {
        var (_, client) = await UserAsync();
        var form = Form(expectedValue: "100000000");
        form["Email"] = "";
        form["Phone"] = "";
        var id = await CreateAsync(client, form);

        var lead = await LoadAsync(id);
        Assert.Equal(100_000_000m, lead.ExpectedValue);
        Assert.Null(lead.Email);
        Assert.Null(lead.Phone);
    }

    [Fact]
    public async Task Create_form_carries_client_side_validation_rules()
    {
        var (_, client) = await UserAsync();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/Leads/Create"));
        Assert.Contains("data-val-required=\"Lead Name is required.\"", html);
        Assert.Contains("data-val-range=\"Expected Value must be between 0 and 100,000,000.\"", html);
        Assert.Contains("data-val-regex=\"Enter a valid phone number.\"", html);
        Assert.Contains("data-val-regex=\"Enter a valid email address.\"", html);
    }

    [Fact]
    public async Task Assignment_follows_D6() // LEAD-05
    {
        var (manager, managerClient) = await UserAsync(Roles.Manager);
        var (member, _) = await UserAsync(managerId: manager.Id);
        var outsider = await fx.CreateIdentityUserAsync();
        var (_, salesClient) = await UserAsync();

        var assigned = await CreateAsync(managerClient, Form(assignedTo: member.Id));
        Assert.Equal(member.Id, (await LoadAsync(assigned)).AssignedTo);

        foreach (var client in new[] { managerClient, salesClient })
        {
            var refused = await PostFormAsync(client, "/Leads/Create", Form(assignedTo: outsider.Id));
            Assert.Contains("You cannot assign leads to this user.", await refused.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Users_cannot_see_or_change_leads_outside_their_scope() // LEAD-09, RBAC-04
    {
        var (manager, managerClient) = await UserAsync(Roles.Manager);
        var (_, memberClient) = await UserAsync(managerId: manager.Id);
        var (_, outsiderClient) = await UserAsync();
        var form = Form();
        var teamLead = await CreateAsync(memberClient, form);

        Assert.Equal(HttpStatusCode.OK, (await managerClient.GetAsync($"/Leads/Details/{teamLead}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsiderClient.GetAsync($"/Leads/Details/{teamLead}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsiderClient.GetAsync($"/Leads/Edit/{teamLead}")).StatusCode);

        var hijack = form.ToDictionary(); hijack["LeadName"] = "Hijacked"; hijack["Status"] = "Contacted";
        Assert.Equal(HttpStatusCode.NotFound, (await PostFormAsync(outsiderClient, $"/Leads/Edit/{teamLead}", hijack, "/Leads")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostFormAsync(outsiderClient, $"/Leads/Delete/{teamLead}", new(), "/Leads")).StatusCode);

        var after = await LoadAsync(teamLead);
        Assert.Equal(form["LeadName"], after.LeadName);
        Assert.False(after.IsDeleted);
    }

    [Fact]
    public async Task Delete_is_a_soft_delete_that_hides_the_lead_and_is_audited() // §16 #8
    {
        var (_, client) = await UserAsync();
        var form = Form();
        var id = await CreateAsync(client, form);

        var response = await PostFormAsync(client, $"/Leads/Delete/{id}", new(), $"/Leads/Details/{id}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True((await LoadAsync(id)).IsDeleted);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Leads/Details/{id}")).StatusCode);
        Assert.Contains("No leads found.", await client.GetStringAsync($"/Leads?search={form["LeadName"]}"));
        Assert.Contains(await AuditAsync(id), a => a.Action == "Delete");
    }

    [Fact]
    public async Task Search_and_filters_cover_name_company_status_and_assigned_user() // LEAD-08
    {
        var (manager, managerClient) = await UserAsync(Roles.Manager);
        var (member, memberClient) = await UserAsync(managerId: manager.Id);
        var tag = $"Q{DbFixture.Next()}";
        await CreateAsync(managerClient, Form(name: $"{tag} Alpha", company: $"{tag} Corp"));
        await CreateAsync(memberClient, Form(name: $"{tag} Beta"));
        var contacted = Form(name: $"{tag} Gamma");
        var gamma = await CreateAsync(memberClient, contacted);
        contacted["Status"] = "Contacted";
        await EditAsync(memberClient, gamma, contacted);

        Assert.Contains("Showing 1–3 of 3", await managerClient.GetStringAsync($"/Leads?search={tag}"));
        Assert.Contains($"{tag} Alpha", await managerClient.GetStringAsync($"/Leads?search={tag}%20Corp"));

        var byStatus = await managerClient.GetStringAsync($"/Leads?search={tag}&status=Contacted");
        Assert.Contains($"{tag} Gamma", byStatus);
        Assert.Contains("Showing 1–1 of 1", byStatus);

        var byUser = await managerClient.GetStringAsync($"/Leads?search={tag}&assignedTo={member.Id}");
        Assert.Contains("Showing 1–2 of 2", byUser);
        Assert.DoesNotContain($"{tag} Alpha<", byUser);
    }

    [Fact]
    public async Task Only_qualified_leads_offer_conversion() // LEAD-06 eligibility (conversion itself: ConversionTests)
    {
        var (_, client) = await UserAsync();
        var form = Form();
        var id = await CreateAsync(client, form);
        var refused = await client.GetAsync($"/Leads/Convert/{id}");
        Assert.Equal($"/Leads/Details/{id}", refused.Headers.Location?.OriginalString);
        Assert.DoesNotContain(">Convert</a>", await client.GetStringAsync($"/Leads/Details/{id}"));

        // Eligibility and duplicate matching used by conversion.
        var lead = await LoadAsync(id);
        Assert.False(LeadService.CanConvert(lead));
        Assert.True(LeadService.CanConvert(new Lead { Status = LeadStatus.Qualified }));

        var customer = await fx.AddCustomerAsync(email: form["Email"].ToUpperInvariant());
        using var db = fx.NewContext();
        var service = new LeadService(db, new ScopeService(new HttpContextAccessor(), db), new AuditService(db, new HttpContextAccessor()));
        Assert.Equal(customer.CustomerId, (await service.FindMatchingCustomerAsync(lead))?.CustomerId);
        Assert.Null(await service.FindMatchingCustomerAsync(new Lead { Email = "nobody@nowhere.test", Phone = "6000000000" }));
    }
}
