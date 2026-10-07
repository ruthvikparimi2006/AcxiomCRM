using System.Net;
using AcxiomCRM.Models;
using Microsoft.EntityFrameworkCore;
using static AcxiomCRM.Tests.TestHttp;

namespace AcxiomCRM.Tests;

// Step 8 (part 2): LEAD-06 lead conversion (§16 #7) and the §8 Lead-to-Customer workflow.
[Collection("db")]
public class ConversionTests(DbFixture fx)
{
    static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    async Task<(ApplicationUser User, HttpClient Client)> UserAsync(string role = Roles.SalesExecutive, string? managerId = null)
    {
        var user = await fx.CreateIdentityUserAsync(role, managerId: managerId);
        return (user, await SignedInAsync(fx, user.UserName!, DbFixture.Password));
    }

    async Task<int> LeadAsync(string assignedTo, LeadStatus status = LeadStatus.Qualified, string? email = "auto", string? phone = "auto")
    {
        using var db = fx.NewContext();
        var lead = new Lead
        {
            LeadName = $"Lead {DbFixture.Next()}", Status = status, AssignedTo = assignedTo, CompanyName = "Lead Co",
            Email = email == "auto" ? $"conv{DbFixture.Next()}@example.com" : email,
            Phone = phone == "auto" ? DbFixture.NextPhone() : phone,
            Source = LeadSource.Website, ExpectedValue = 75000,
        };
        db.Leads.Add(lead);
        await db.SaveChangesAsync();
        return lead.LeadId;
    }

    async Task<Lead> LoadLeadAsync(int id)
    {
        using var db = fx.NewContext();
        return await db.Leads.IgnoreQueryFilters().SingleAsync(l => l.LeadId == id);
    }

    // Starts from the prefilled Convert form, as a user would.
    static async Task<HttpResponseMessage> ConvertAsync(HttpClient client, int leadId, Lead lead,
        bool createOpportunity = true, string amount = "75000", string probability = "30", Action<Dictionary<string, string>>? change = null)
    {
        var form = new Dictionary<string, string>
        {
            ["Customer.CustomerName"] = lead.LeadName,
            ["Customer.Email"] = lead.Email ?? "",
            ["Customer.Phone"] = lead.Phone ?? "",
            ["Customer.CompanyName"] = lead.CompanyName ?? "",
            ["CreateOpportunity"] = createOpportunity ? "true" : "false",
        };
        if (createOpportunity)
        {
            form["Opportunity.OpportunityName"] = $"{lead.LeadName} deal";
            form["Opportunity.Amount"] = amount;
            form["Opportunity.Stage"] = "Qualification";
            form["Opportunity.Probability"] = probability;
            form["Opportunity.ExpectedCloseDate"] = Today.AddDays(45).ToString("yyyy-MM-dd");
        }
        change?.Invoke(form);
        // Token from the dashboard: the Convert page itself redirects or 404s for leads that can't be converted.
        return await PostFormAsync(client, $"/Leads/Convert/{leadId}", form, "/Dashboard");
    }

    [Fact]
    public async Task Converting_creates_customer_and_opportunity_in_one_audited_step()
    {
        var (me, client) = await UserAsync();
        var leadId = await LeadAsync(me.Id);
        var lead = await LoadLeadAsync(leadId);

        var response = await ConvertAsync(client, leadId, lead);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Opportunities/Details/", response.Headers.Location!.OriginalString);

        var converted = await LoadLeadAsync(leadId);
        Assert.Equal(LeadStatus.Converted, converted.Status);
        using var db = fx.NewContext();
        var customer = await db.Customers.SingleAsync(c => c.CustomerId == converted.ConvertedCustomerId);
        Assert.Equal(lead.Email, customer.Email);
        Assert.Equal(me.Id, customer.OwnerId);
        var opportunity = await db.Opportunities.SingleAsync(o => o.LeadId == leadId);
        Assert.Equal(customer.CustomerId, opportunity.CustomerId);
        Assert.Equal(75000m, opportunity.Amount);
        Assert.Equal(LeadSource.Website, opportunity.Source);
        Assert.Equal(me.Id, opportunity.AssignedTo);

        var audit = await db.AuditLogs.Where(a => a.EntityName == "Lead" && a.RecordId == leadId.ToString() && a.Action == "Converted").SingleAsync();
        Assert.Contains($"\"CustomerId\":{customer.CustomerId}", audit.NewValue);
        Assert.Contains($"\"OpportunityId\":{opportunity.OpportunityId}", audit.NewValue);
        Assert.Contains(await db.AuditLogs.ToListAsync(), a => a.EntityName == "Customer" && a.RecordId == customer.CustomerId.ToString() && a.Action == "Create");
    }

    [Fact]
    public async Task Converting_links_an_existing_customer_with_the_same_email_instead_of_duplicating()
    {
        var (me, client) = await UserAsync();
        var leadId = await LeadAsync(me.Id);
        var lead = await LoadLeadAsync(leadId);
        int existing;
        using (var db = fx.NewContext())
        {
            var c = DbFixture.NewCustomer(me.Id, email: lead.Email!.ToUpperInvariant());
            db.Customers.Add(c);
            await db.SaveChangesAsync();
            existing = c.CustomerId;
        }

        var page = await client.GetStringAsync($"/Leads/Convert/{leadId}");
        Assert.Contains("An existing customer has the same email or phone.", page);

        // The customer fields are not on the form when there is a match.
        var response = await ConvertAsync(client, leadId, lead, createOpportunity: false, change: f =>
        {
            f.Remove("Customer.CustomerName"); f.Remove("Customer.Email"); f.Remove("Customer.Phone");
        });

        Assert.Equal($"/Customers/Details/{existing}", response.Headers.Location?.OriginalString);
        Assert.Equal(existing, (await LoadLeadAsync(leadId)).ConvertedCustomerId);
        using var check = fx.NewContext();
        Assert.Equal(1, await check.Customers.CountAsync(c => c.Email == lead.Email));
        Assert.False(await check.Opportunities.AnyAsync(o => o.LeadId == leadId));
    }

    [Fact]
    public async Task Opportunity_fields_are_ignored_when_no_opportunity_is_requested()
    {
        var (me, client) = await UserAsync();
        var leadId = await LeadAsync(me.Id);
        var lead = await LoadLeadAsync(leadId);

        var response = await ConvertAsync(client, leadId, lead, createOpportunity: false,
            change: f => { f["Opportunity.Amount"] = "0"; f["Opportunity.Probability"] = "500"; });

        Assert.StartsWith("/Customers/Details/", response.Headers.Location!.OriginalString);
        Assert.Equal(LeadStatus.Converted, (await LoadLeadAsync(leadId)).Status);
    }

    [Fact]
    public async Task A_failed_conversion_changes_nothing() // one transaction
    {
        var (me, client) = await UserAsync();
        var leadId = await LeadAsync(me.Id);
        var lead = await LoadLeadAsync(leadId);
        int customersBefore, auditBefore;
        using (var db = fx.NewContext())
        {
            customersBefore = await db.Customers.CountAsync();
            auditBefore = await db.AuditLogs.CountAsync();
        }

        var response = await ConvertAsync(client, leadId, lead, amount: "0");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Opportunity Amount must be greater than 0.", await response.Content.ReadAsStringAsync());
        Assert.Equal(LeadStatus.Qualified, (await LoadLeadAsync(leadId)).Status);
        using var after = fx.NewContext();
        Assert.Equal(customersBefore, await after.Customers.CountAsync());
        Assert.Equal(auditBefore, await after.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task A_failure_inside_the_service_after_the_customer_is_saved_is_rolled_back()
    {
        var (me, client) = await UserAsync();
        var leadId = await LeadAsync(me.Id);
        var lead = await LoadLeadAsync(leadId);

        // A deal created as Won with Amount 0 passes the form rules (they only apply to open stages), so it reaches
        // OpportunityService, which rejects it after the customer has already been saved in the transaction.
        var response = await ConvertAsync(client, leadId, lead, amount: "0", change: f => f["Opportunity.Stage"] = "Won");

        Assert.Contains("Opportunity Amount must be greater than 0.", await response.Content.ReadAsStringAsync());
        using var db = fx.NewContext();
        Assert.False(await db.Customers.AnyAsync(c => c.Email == lead.Email));
        Assert.Equal(LeadStatus.Qualified, (await LoadLeadAsync(leadId)).Status);
    }

    [Fact]
    public async Task Lead_without_contact_details_needs_them_for_the_new_customer()
    {
        var (me, client) = await UserAsync();
        var leadId = await LeadAsync(me.Id, email: null, phone: null);
        var lead = await LoadLeadAsync(leadId);

        var missing = await ConvertAsync(client, leadId, lead, createOpportunity: false);
        var html = await missing.Content.ReadAsStringAsync();
        Assert.Contains("Email is required.", html);
        Assert.Contains("Phone is required.", html);

        var ok = await ConvertAsync(client, leadId, lead, createOpportunity: false, change: f =>
        {
            f["Customer.Email"] = $"filled{DbFixture.Next()}@example.com";
            f["Customer.Phone"] = DbFixture.NextPhone();
        });
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
    }

    [Theory]
    [InlineData(LeadStatus.New)]
    [InlineData(LeadStatus.Contacted)]
    [InlineData(LeadStatus.Lost)]
    [InlineData(LeadStatus.Converted)]
    public async Task Only_qualified_leads_can_be_converted(LeadStatus status)
    {
        var (me, client) = await UserAsync();
        var leadId = await LeadAsync(me.Id, status);
        var lead = await LoadLeadAsync(leadId);

        var response = await ConvertAsync(client, leadId, lead);

        Assert.Equal($"/Leads/Details/{leadId}", response.Headers.Location?.OriginalString);
        Assert.Contains("Only qualified leads can be converted.", await client.GetStringAsync(response.Headers.Location!));
        Assert.Equal(status, (await LoadLeadAsync(leadId)).Status);
    }

    [Fact]
    public async Task A_match_outside_my_scope_blocks_conversion_without_revealing_it()
    {
        var (me, client) = await UserAsync();
        var (other, _) = await UserAsync();
        var leadId = await LeadAsync(me.Id);
        var lead = await LoadLeadAsync(leadId);
        using (var db = fx.NewContext())
        {
            var hidden = DbFixture.NewCustomer(other.Id, phone: lead.Phone);
            hidden.CustomerName = "Secret Customer";
            db.Customers.Add(hidden);
            await db.SaveChangesAsync();
        }

        var response = await ConvertAsync(client, leadId, lead);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("already exists outside your scope", html);
        Assert.DoesNotContain("Secret Customer", html);
        Assert.Equal(LeadStatus.Qualified, (await LoadLeadAsync(leadId)).Status);
    }

    [Fact]
    public async Task Leads_outside_my_scope_cannot_be_converted()
    {
        var (other, _) = await UserAsync();
        var (_, client) = await UserAsync();
        var leadId = await LeadAsync(other.Id);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Leads/Convert/{leadId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ConvertAsync(client, leadId, await LoadLeadAsync(leadId))).StatusCode);
    }

    [Fact]
    public async Task Lead_to_customer_workflow_end_to_end() // §8: create, assign, contact, qualify, convert, audit
    {
        var (manager, managerClient) = await UserAsync(Roles.Manager);
        var (member, memberClient) = await UserAsync(managerId: manager.Id);
        var email = $"flow{DbFixture.Next()}@example.com";
        var form = new Dictionary<string, string>
        {
            ["LeadName"] = "Workflow Lead", ["Email"] = email, ["Phone"] = DbFixture.NextPhone(), ["CompanyName"] = "Flow Ltd",
            ["Source"] = "Referral", ["Status"] = "New", ["Priority"] = "High", ["ExpectedValue"] = "120000",
            ["AssignedTo"] = member.Id,
        };
        var created = await PostFormAsync(managerClient, "/Leads/Create", form);
        var leadId = int.Parse(created.Headers.Location!.OriginalString.Split('/').Last());

        foreach (var status in new[] { "Contacted", "Qualified" })
        {
            form["Status"] = status;
            await PostFormAsync(memberClient, $"/Leads/Edit/{leadId}", form, $"/Leads/Edit/{leadId}");
        }

        var lead = await LoadLeadAsync(leadId);
        Assert.Contains(">Convert</a>", await memberClient.GetStringAsync($"/Leads/Details/{leadId}"));
        Assert.Equal(HttpStatusCode.Redirect, (await ConvertAsync(memberClient, leadId, lead)).StatusCode);

        using var db = fx.NewContext();
        var actions = await db.AuditLogs.Where(a => a.EntityName == "Lead" && a.RecordId == leadId.ToString())
            .OrderBy(a => a.AuditLogId).Select(a => a.Action).ToListAsync();
        Assert.Equal(["Create", "Update", "StatusChanged", "Update", "StatusChanged", "Converted"], actions);
        var opportunity = await db.Opportunities.SingleAsync(o => o.LeadId == leadId);
        Assert.Equal(member.Id, opportunity.AssignedTo);
        Assert.Contains("Converted to", await managerClient.GetStringAsync($"/Leads/Details/{leadId}"));
    }
}
