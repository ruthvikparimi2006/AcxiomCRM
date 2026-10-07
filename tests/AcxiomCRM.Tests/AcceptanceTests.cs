using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AcxiomCRM.Models;
using Microsoft.EntityFrameworkCore;
using static AcxiomCRM.Tests.TestHttp;

namespace AcxiomCRM.Tests;

// Step 13: the §17.19 final acceptance scenario (14 checks, in order) and the four §8 workflows, each run once
// with its appropriate role. Detailed rule-by-rule coverage lives in the module test classes (see docs/ACCEPTANCE.md).
[Collection("db")]
public partial class AcceptanceTests(DbFixture fx)
{
    static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);
    static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd");
    static int IdOf(HttpResponseMessage r) => int.Parse(r.Headers.Location!.OriginalString.Split('/').Last());

    [GeneratedRegex("token=([^&\"]+)")]
    private static partial Regex TokenInLink();

    async Task<(ApplicationUser User, HttpClient Client)> UserAsync(string role = Roles.SalesExecutive, string? managerId = null)
    {
        var user = await fx.CreateIdentityUserAsync(role, managerId: managerId);
        return (user, await SignedInAsync(fx, user.UserName!, DbFixture.Password));
    }

    static Dictionary<string, string> Customer(string? email = null, string? phone = null, string? name = null) => new()
    {
        ["CustomerName"] = name ?? $"Acceptance {DbFixture.Next()}", ["Email"] = email ?? $"acc{DbFixture.Next()}@example.com",
        ["Phone"] = phone ?? DbFixture.NextPhone(), ["Status"] = "Active",
    };

    static Dictionary<string, string> Deal(int customerId, string amount = "50000", string probability = "40", DateOnly? close = null) => new()
    {
        ["OpportunityName"] = $"Acceptance deal {DbFixture.Next()}", ["CustomerId"] = customerId.ToString(), ["Amount"] = amount,
        ["Stage"] = "Qualification", ["Probability"] = probability, ["ExpectedCloseDate"] = Iso(close ?? Today.AddDays(30)),
    };

    // ---------- §17.19 final acceptance scenario ----------

    [Fact]
    public async Task S01_protected_pages_are_not_accessible_without_authentication()
    {
        foreach (var url in new[] { "/", "/Customers", "/Leads", "/Opportunities", "/FollowUps", "/Activities", "/Reports", "/Users", "/AuditLog" })
        {
            var r = await fx.NewClient().GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains("/Account/Login", r.Headers.Location!.ToString());
        }
    }

    [Fact]
    public async Task S02_register_and_login_reach_the_dashboard()
    {
        var client = fx.NewClient();
        var email = $"s02{DbFixture.Next()}@example.com";
        var registered = await PostFormAsync(client, "/Account/Register", new()
        {
            ["Name"] = "Acceptance User", ["Email"] = email, ["Password"] = DbFixture.Password, ["ConfirmPassword"] = DbFixture.Password,
        });
        Assert.Equal("/", registered.Headers.Location!.OriginalString);
        Assert.Contains("<h1 class=\"h3 mb-0\">Dashboard</h1>", await client.GetStringAsync("/"));

        var again = fx.NewClient();
        Assert.Equal("/", (await LoginAsync(again, email, DbFixture.Password)).Headers.Location!.OriginalString);
        Assert.Contains("Total Customers", await again.GetStringAsync("/"));
    }

    [Fact]
    public async Task S03_customer_form_validates_email_and_phone_in_the_browser()
    {
        var (_, client) = await UserAsync();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/Customers/Create"));
        Assert.Contains("jquery.validate.unobtrusive", html);
        Assert.Contains("data-val-regex=\"Enter a valid email address.\"", html);
        Assert.Contains("data-val-regex=\"Enter a valid phone number.\"", html);
        Assert.Contains("data-val-required=\"Phone is required.\"", html);
    }

    [Fact]
    public async Task S04_crafted_requests_bypassing_the_browser_are_rejected_by_the_server()
    {
        var (_, client) = await UserAsync();
        var badEmail = Customer(email: "not-an-email");
        var badPhone = Customer(phone: "12ab");
        Assert.Contains("Enter a valid email address.", await (await PostFormAsync(client, "/Customers/Create", badEmail)).Content.ReadAsStringAsync());
        Assert.Contains("Enter a valid phone number.", await (await PostFormAsync(client, "/Customers/Create", badPhone)).Content.ReadAsStringAsync());
        using var db = fx.NewContext();
        Assert.False(await db.Customers.AnyAsync(c => c.CustomerName == badEmail["CustomerName"] || c.CustomerName == badPhone["CustomerName"]));
    }

    [Theory]
    [InlineData("S05", "Amount", "0", "Opportunity Amount must be greater than 0.")]
    [InlineData("S05", "Amount", "-1", "Amount cannot be negative.")]
    [InlineData("S06", "Probability", "101", "Probability must be between 0 and 100.")]
    [InlineData("S07", "ExpectedCloseDate", "PAST", "Expected Close Date cannot be in the past.")]
    public async Task S05_to_S07_invalid_opportunities_are_rejected(string scenario, string field, string value, string message)
    {
        var (_, client) = await UserAsync();
        var customer = IdOf(await PostFormAsync(client, "/Customers/Create", Customer()));
        var form = Deal(customer);
        form[field] = value == "PAST" ? Iso(Today.AddDays(-1)) : value;

        var response = await PostFormAsync(client, "/Opportunities/Create", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(message, await response.Content.ReadAsStringAsync());
        using var db = fx.NewContext();
        Assert.False(await db.Opportunities.AnyAsync(o => o.OpportunityName == form["OpportunityName"]), scenario);
    }

    [Fact]
    public async Task S08_follow_up_dated_before_today_is_rejected()
    {
        var (_, client) = await UserAsync();
        var customer = IdOf(await PostFormAsync(client, "/Customers/Create", Customer()));
        var response = await PostFormAsync(client, "/FollowUps/Create", new()
        {
            ["CustomerId"] = customer.ToString(), ["FollowUpDate"] = Iso(Today.AddDays(-1)), ["FollowUpType"] = "Call", ["Subject"] = "Late",
        });
        Assert.Contains("Follow-up date cannot be earlier than today.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task S09_to_S11_each_role_gets_its_own_scope()
    {
        var (manager, managerClient) = await UserAsync(Roles.Manager);
        var (_, memberClient) = await UserAsync(managerId: manager.Id);
        var (_, outsiderClient) = await UserAsync();
        var memberCustomer = IdOf(await PostFormAsync(memberClient, "/Customers/Create", Customer()));
        var outsiderCustomer = IdOf(await PostFormAsync(outsiderClient, "/Customers/Create", Customer()));
        var admin = await SignedInAsync(fx, DbFixture.AdminEmail, DbFixture.AdminPassword);

        // S09 SalesExecutive: own records only, no administration.
        Assert.Equal(HttpStatusCode.OK, (await memberClient.GetAsync($"/Customers/Details/{memberCustomer}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await memberClient.GetAsync($"/Customers/Details/{outsiderCustomer}")).StatusCode);
        foreach (var url in new[] { "/Users", "/Roles", "/AuditLog", "/Reports/UserActivity" })
            Assert.Contains("/Account/AccessDenied", (await memberClient.GetAsync(url)).Headers.Location!.ToString());

        // S10 Manager: team records, team pipeline and reports.
        Assert.Equal(HttpStatusCode.OK, (await managerClient.GetAsync($"/Customers/Details/{memberCustomer}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerClient.GetAsync($"/Customers/Details/{outsiderCustomer}")).StatusCode);
        foreach (var url in new[] { "/Opportunities/Pipeline", "/Reports/Pipeline", "/Reports/Sales", "/Reports/UserActivity" })
            Assert.Equal(HttpStatusCode.OK, (await managerClient.GetAsync(url)).StatusCode);
        Assert.Contains("Showing your team", await managerClient.GetStringAsync("/Dashboard"));

        // S11 Admin: everything, including user, role and audit administration.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/Customers/Details/{outsiderCustomer}")).StatusCode);
        foreach (var url in new[] { "/Users", "/Users/Create", "/Roles", "/Roles/Permissions", "/AuditLog", "/Reports/Audit" })
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task S12_create_update_and_delete_generate_audit_entries()
    {
        var (me, client) = await UserAsync();
        var form = Customer();
        var id = IdOf(await PostFormAsync(client, "/Customers/Create", form));
        form["CustomerName"] = "Audited rename";
        await PostFormAsync(client, $"/Customers/Edit/{id}", form, $"/Customers/Edit/{id}");
        await PostFormAsync(client, $"/Customers/Delete/{id}", new(), $"/Customers/Details/{id}");

        using var db = fx.NewContext();
        var actions = await db.AuditLogs.Where(a => a.EntityName == "Customer" && a.RecordId == id.ToString() && a.UserId == me.Id)
            .Select(a => a.Action).ToListAsync();
        Assert.Contains("Create", actions);
        Assert.Contains("Update", actions);
        Assert.Contains("Deactivate", actions);
    }

    [Fact]
    public async Task S13_api_customers_returns_authorized_json()
    {
        var user = await fx.CreateIdentityUserAsync();
        var handler = fx.App.Server.CreateHandler(ctx => ctx.Connection.RemoteIpAddress = IPAddress.Parse("10.250.250.13"));
        var api = new HttpClient(handler) { BaseAddress = new Uri("https://localhost") };

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/customers")).StatusCode);

        var login = await api.PostAsJsonAsync("/api/auth/login", new { login = user.UserName, password = DbFixture.Password });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await api.PostAsJsonAsync("/api/customers", new { customerName = "S13", email = $"s13{DbFixture.Next()}@example.com", phone = DbFixture.NextPhone(), status = "Active" });

        var response = await api.GetAsync("/api/customers");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("totalCount").GetInt32()); // only this user's customer
    }

    [Fact]
    public async Task S14_dashboard_shows_kpi_cards_and_chartjs_charts_from_authorized_data()
    {
        var (_, client) = await UserAsync();
        var customer = IdOf(await PostFormAsync(client, "/Customers/Create", Customer()));
        await PostFormAsync(client, "/Opportunities/Create", Deal(customer, amount: "2500"));

        var html = await client.GetStringAsync("/Dashboard");
        foreach (var card in new[] { "Total Customers", "Total Leads", "Open Leads", "Total Opportunities", "Open Opportunities",
                     "Won Opportunities", "Lost Opportunities", "Total Pipeline Value" })
            Assert.Contains($"data-kpi=\"{card}\"", html);
        Assert.Contains("data-kpi=\"Total Customers\">1<", html);
        Assert.Contains("data-kpi=\"Total Pipeline Value\">2,500.00<", html);
        Assert.Contains("chart.umd.min.js", html);
        foreach (var chart in new[] { "leadStatusChart", "pipelineChart", "salesChart" }) Assert.Contains(chart, html);
    }

    // ---------- §8 workflows, once each with the appropriate role ----------

    [Fact]
    public async Task Workflow_user_administration_by_admin()
    {
        // Create user -> assign role -> set inactive/active -> password and lockout policy -> audit.
        var admin = await SignedInAsync(fx, DbFixture.AdminEmail, DbFixture.AdminPassword);
        var userName = $"wfuser{DbFixture.Next()}";
        var created = await PostFormAsync(admin, "/Users/Create", new()
        {
            ["Name"] = "Workflow User", ["UserName"] = userName, ["Email"] = $"{userName}@example.com", ["Role"] = Roles.SalesExecutive,
        });
        var token = Uri.UnescapeDataString(WebUtility.HtmlDecode(TokenInLink().Match(await created.Content.ReadAsStringAsync()).Groups[1].Value));
        string id;
        using (var db = fx.NewContext()) id = (await db.Users.SingleAsync(u => u.UserName == userName)).Id;

        await PostFormAsync(admin, $"/Users/Edit/{id}", new()
        {
            ["Name"] = "Workflow User", ["UserName"] = userName, ["Email"] = $"{userName}@example.com", ["Role"] = Roles.Manager,
        }, $"/Users/Edit/{id}");
        await PostFormAsync(admin, $"/Users/SetActive/{id}", new() { ["active"] = "false" }, "/Users");
        await PostFormAsync(admin, $"/Users/SetActive/{id}", new() { ["active"] = "true" }, "/Users");

        // The role change refreshed the security stamp, which voids the first link: it no longer works.
        var stale = await PostFormAsync(fx.NewClient(), "/Account/ResetPassword", new()
        {
            ["UserId"] = id, ["Token"] = token, ["NewPassword"] = DbFixture.Password, ["ConfirmPassword"] = DbFixture.Password,
        }, "/Account/Login");
        Assert.Contains("invalid or has expired", await stale.Content.ReadAsStringAsync());

        // A fresh link: the password policy applies, and a strong password is accepted.
        var link = await (await PostFormAsync(admin, $"/Users/ResetLink/{id}", new(), "/Users")).Content.ReadAsStringAsync();
        token = Uri.UnescapeDataString(WebUtility.HtmlDecode(TokenInLink().Match(link).Groups[1].Value));
        var weak = await PostFormAsync(fx.NewClient(), "/Account/ResetPassword", new()
        {
            ["UserId"] = id, ["Token"] = token, ["NewPassword"] = "weakpass", ["ConfirmPassword"] = "weakpass",
        }, "/Account/Login");
        Assert.Contains("Password must be at least 8 characters", WebUtility.HtmlDecode(await weak.Content.ReadAsStringAsync()));
        var strong = await PostFormAsync(fx.NewClient(), "/Account/ResetPassword", new()
        {
            ["UserId"] = id, ["Token"] = token, ["NewPassword"] = DbFixture.Password, ["ConfirmPassword"] = DbFixture.Password,
        }, "/Account/Login");
        Assert.Equal(HttpStatusCode.Redirect, strong.StatusCode);

        // Lockout policy.
        for (var i = 0; i < 5; i++) await LoginAsync(fx.NewClient(), userName, "Wrong#Pass1");
        Assert.Contains("This account is locked.", await (await LoginAsync(fx.NewClient(), userName, DbFixture.Password)).Content.ReadAsStringAsync());
        await PostFormAsync(admin, $"/Users/Unlock/{id}", new(), "/Users");
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(fx.NewClient(), userName, DbFixture.Password)).StatusCode);

        using var check = fx.NewContext();
        var actions = await check.AuditLogs.Where(a => a.RecordId == id).Select(a => a.Action).ToListAsync();
        foreach (var a in new[] { "Create", "PasswordResetLinkCreated", "RoleChanged", "Deactivate", "Activate", "PasswordReset", "Lockout", "Unlock", "LoginSuccess" })
            Assert.Contains(a, actions);
    }

    [Fact]
    public async Task Workflow_opportunity_by_sales_executive()
    {
        // Create -> amount -> probability -> close date -> move through stages -> Won -> outcome captured.
        var (_, client) = await UserAsync();
        var customer = IdOf(await PostFormAsync(client, "/Customers/Create", Customer()));
        var form = Deal(customer);
        var id = IdOf(await PostFormAsync(client, "/Opportunities/Create", form));
        foreach (var stage in new[] { "Proposal", "Negotiation", "Won" })
        {
            form["Stage"] = stage;
            Assert.Equal(HttpStatusCode.Redirect, (await PostFormAsync(client, $"/Opportunities/Edit/{id}", form, $"/Opportunities/Edit/{id}")).StatusCode);
        }
        using var db = fx.NewContext();
        var won = await db.Opportunities.SingleAsync(o => o.OpportunityId == id);
        Assert.Equal(OpportunityStatus.Won, won.Status);
        Assert.NotNull(won.ClosedDate);
        Assert.Equal(3, await db.AuditLogs.CountAsync(a => a.EntityName == "Opportunity" && a.RecordId == id.ToString() && a.Action == "StageChanged"));
    }

    [Fact]
    public async Task Workflow_follow_up_by_manager_for_a_team_member()
    {
        // Create -> set date -> assign user -> complete / missed / reschedule -> related record updated -> audit.
        var (manager, managerClient) = await UserAsync(Roles.Manager);
        var (member, memberClient) = await UserAsync(managerId: manager.Id);
        var customer = IdOf(await PostFormAsync(memberClient, "/Customers/Create", Customer()));

        async Task<int> Schedule(string subject) => IdOf(await PostFormAsync(managerClient, "/FollowUps/Create", new()
        {
            ["CustomerId"] = customer.ToString(), ["FollowUpDate"] = Iso(Today.AddDays(1)), ["FollowUpType"] = "Meeting",
            ["Subject"] = subject, ["AssignedTo"] = member.Id,
        }));
        var a = await Schedule("WF complete");
        var b = await Schedule("WF missed");
        var c = await Schedule("WF reschedule");

        await PostFormAsync(memberClient, $"/FollowUps/Complete/{a}", new(), "/Dashboard");
        await PostFormAsync(memberClient, $"/FollowUps/Missed/{b}", new(), "/Dashboard");
        await PostFormAsync(memberClient, $"/FollowUps/Reschedule/{c}", new() { ["newDate"] = Iso(Today.AddDays(4)) }, "/Dashboard");

        using var db = fx.NewContext();
        Assert.Equal(member.Id, (await db.FollowUps.SingleAsync(f => f.FollowUpId == a)).AssignedTo);
        Assert.Equal(FollowUpStatus.Completed, (await db.FollowUps.SingleAsync(f => f.FollowUpId == a)).Status);
        Assert.Equal(FollowUpStatus.Missed, (await db.FollowUps.SingleAsync(f => f.FollowUpId == b)).Status);
        Assert.Equal(Today.AddDays(4), (await db.FollowUps.SingleAsync(f => f.FollowUpId == c)).FollowUpDate);
        Assert.NotNull((await db.Customers.SingleAsync(x => x.CustomerId == customer)).ModifiedDate);
        var audited = await db.AuditLogs.Where(x => x.EntityName == "FollowUp" && (x.RecordId == a.ToString() || x.RecordId == b.ToString() || x.RecordId == c.ToString()))
            .Select(x => x.Action).ToListAsync();
        foreach (var action in new[] { "Create", "Completed", "Missed", "Rescheduled" }) Assert.Contains(action, audited);
    }

    // Lead-to-Customer workflow: ConversionTests.Lead_to_customer_workflow_end_to_end.

    // ---------- §14: database credentials are not stored in source ----------

    [Fact]
    public void No_credentials_or_secrets_are_committed_in_configuration()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "AcxiomCRM"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var configs = Directory.GetFiles(Path.Combine(dir!.FullName, "src", "AcxiomCRM"), "appsettings*.json");
        Assert.NotEmpty(configs);
        foreach (var file in configs)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("ConnectionStrings", text);
            Assert.DoesNotMatch(new Regex(@"(?i)(password|pwd|user id|secret|jwt""\s*:)"), text);
        }
    }
}
