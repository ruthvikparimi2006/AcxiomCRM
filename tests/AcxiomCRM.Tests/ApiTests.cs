using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static AcxiomCRM.Tests.TestHttp;

namespace AcxiomCRM.Tests;

// Step 11: REST API (API-01..10, §10, §17.14, D4, D9) and §17.19 #13.
[Collection("db")]
public class ApiTests(DbFixture fx)
{
    static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);
    static int _ip;

    // A client with its own source IP, so the per-IP login limit (D4) can be tested and tests don't throttle each other.
    (HttpClient Client, string Ip) Api()
    {
        var n = Interlocked.Increment(ref _ip);
        var ip = $"10.{n / 65000 % 250}.{n / 250 % 250}.{n % 250 + 1}";
        var address = IPAddress.Parse(ip);
        var handler = fx.App.Server.CreateHandler(ctx => ctx.Connection.RemoteIpAddress = address);
        return (new HttpClient(handler) { BaseAddress = new Uri("https://localhost") }, ip);
    }

    static Task<HttpResponseMessage> LoginApiAsync(HttpClient client, string login, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new { login, password });

    async Task<HttpClient> AuthorizedAsync(string login, string password)
    {
        var (client, _) = Api();
        var response = await LoginApiAsync(client, login, password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    async Task<(ApplicationUser User, HttpClient Client)> UserAsync(string role = Roles.SalesExecutive, string? managerId = null)
    {
        var user = await fx.CreateIdentityUserAsync(role, managerId: managerId);
        return (user, await AuthorizedAsync(user.UserName!, DbFixture.Password));
    }

    static object CustomerBody(string? name = null, string? email = null, string? phone = null) => new
    {
        customerName = name ?? $"API Customer {DbFixture.Next()}",
        email = email ?? $"api{DbFixture.Next()}@example.com",
        phone = phone ?? DbFixture.NextPhone(),
        companyName = "API Co",
        status = "Active",
    };

    static async Task<JsonElement> JsonAsync(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string? text = null)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        if (text is not null) Assert.Contains(text, body);
        foreach (var leak in new[] { "System.", "Exception", "   at ", "SqlException", "EntityFramework" })
            Assert.DoesNotContain(leak, body);
    }

    // ---------- authentication ----------

    [Fact]
    public async Task Login_returns_a_60_minute_token_and_is_audited()
    {
        var user = await fx.CreateIdentityUserAsync();
        var (client, ip) = Api();

        var response = await LoginApiAsync(client, user.Email!, DbFixture.Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("token").GetString()));
        Assert.InRange(body.GetProperty("expiresAt").GetDateTime() - DateTime.UtcNow, TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(60));
        Assert.Equal(Roles.SalesExecutive, body.GetProperty("user").GetProperty("role").GetString());

        using var db = fx.NewContext();
        var audit = await db.AuditLogs.Where(a => a.UserId == user.Id && a.Action == "LoginSuccess").SingleAsync();
        Assert.Equal("API", audit.Details);
        Assert.Equal(ip, audit.IpAddress); // AUD-02: IP captured
    }

    [Fact]
    public async Task Bad_credentials_and_inactive_users_get_401_without_a_token()
    {
        var user = await fx.CreateIdentityUserAsync();
        var inactive = await fx.CreateIdentityUserAsync(active: false);

        await AssertProblemAsync(await LoginApiAsync(Api().Client, user.UserName!, "Wrong#Pass1"), HttpStatusCode.Unauthorized, "Invalid login attempt.");
        await AssertProblemAsync(await LoginApiAsync(Api().Client, "nobody-at-all", "Wrong#Pass1"), HttpStatusCode.Unauthorized, "Invalid login attempt.");
        await AssertProblemAsync(await LoginApiAsync(Api().Client, inactive.UserName!, DbFixture.Password), HttpStatusCode.Unauthorized, "Invalid login attempt.");
        await AssertProblemAsync(await Api().Client.PostAsJsonAsync("/api/auth/login", new { login = "" }), HttpStatusCode.BadRequest, "Password is required.");
    }

    [Fact]
    public async Task Api_logins_count_towards_account_lockout() // AUTH-07
    {
        var user = await fx.CreateIdentityUserAsync();
        for (var i = 0; i < 5; i++) await LoginApiAsync(Api().Client, user.UserName!, "Wrong#Pass1");

        await AssertProblemAsync(await LoginApiAsync(Api().Client, user.UserName!, DbFixture.Password), HttpStatusCode.Unauthorized, "This account is locked.");
    }

    [Fact]
    public async Task More_than_5_login_attempts_a_minute_from_one_ip_get_429() // D4, API-08
    {
        var user = await fx.CreateIdentityUserAsync();
        var (client, _) = Api();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginApiAsync(client, user.UserName!, "Wrong#Pass" + i)).StatusCode);

        var limited = await LoginApiAsync(client, user.UserName!, DbFixture.Password);
        await AssertProblemAsync(limited, HttpStatusCode.TooManyRequests);
        Assert.Equal("60", limited.Headers.GetValues("Retry-After").Single());

        // Another address is not affected.
        Assert.Equal(HttpStatusCode.OK, (await LoginApiAsync(Api().Client, DbFixture.AdminEmail, DbFixture.AdminPassword)).StatusCode);
    }

    [Fact]
    public async Task Protected_endpoints_need_a_valid_token_and_ignore_the_web_cookie() // API-04, §17.19 #13
    {
        await AssertProblemAsync(await Api().Client.GetAsync("/api/customers"), HttpStatusCode.Unauthorized);

        var (forged, _) = Api();
        forged.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not.a.token");
        await AssertProblemAsync(await forged.GetAsync("/api/customers"), HttpStatusCode.Unauthorized);

        var user = await fx.CreateIdentityUserAsync();
        var browser = await SignedInAsync(fx, user.UserName!, DbFixture.Password); // cookie only
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/customers")).StatusCode);
    }

    [Fact]
    public async Task Tokens_stop_working_when_the_user_is_deactivated()
    {
        var (user, client) = await UserAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/customers")).StatusCode);

        var admin = await SignedInAsync(fx, DbFixture.AdminEmail, DbFixture.AdminPassword);
        await PostFormAsync(admin, $"/Users/SetActive/{user.Id}", new() { ["active"] = "false" }, "/Users");

        await AssertProblemAsync(await client.GetAsync("/api/customers"), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_user_without_a_crm_role_gets_403() // API-04
    {
        var user = await fx.CreateIdentityUserAsync();
        using (var scope = fx.App.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            await users.RemoveFromRoleAsync((await users.FindByIdAsync(user.Id))!, Roles.SalesExecutive);
        }
        var client = await AuthorizedAsync(user.UserName!, DbFixture.Password);

        await AssertProblemAsync(await client.GetAsync("/api/customers"), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Logout_is_audited()
    {
        var (user, client) = await UserAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        using var db = fx.NewContext();
        Assert.True(await db.AuditLogs.AnyAsync(a => a.UserId == user.Id && a.Action == "Logout" && a.Details!.StartsWith("API")));
    }

    // ---------- customers: the full §17.14 set ----------

    [Fact]
    public async Task Customers_support_create_read_update_delete_with_the_right_status_codes() // API-01, API-05
    {
        var (me, client) = await UserAsync();

        var created = await client.PostAsJsonAsync("/api/customers", CustomerBody(name: "API Original"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = await JsonAsync(created);
        var id = dto.GetProperty("id").GetInt32();
        Assert.Equal($"/api/customers/{id}", created.Headers.Location!.AbsolutePath);
        Assert.Equal(me.Id, dto.GetProperty("ownerId").GetString());
        Assert.StartsWith("CUS-", dto.GetProperty("code").GetString());
        Assert.Equal("Active", dto.GetProperty("status").GetString());

        var got = await client.GetAsync($"/api/customers/{id}");
        Assert.Equal(HttpStatusCode.OK, got.StatusCode);
        Assert.Equal("API Original", (await JsonAsync(got)).GetProperty("name").GetString());

        var list = await JsonAsync(await client.GetAsync("/api/customers?search=API%20Original"));
        Assert.Contains(list.GetProperty("items").EnumerateArray(), c => c.GetProperty("id").GetInt32() == id);

        var body = (JsonElement)JsonSerializer.SerializeToElement(CustomerBody(name: "API Renamed", email: dto.GetProperty("email").GetString(), phone: dto.GetProperty("phone").GetString()));
        var updated = await client.PutAsJsonAsync($"/api/customers/{id}", body);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("API Renamed", (await JsonAsync(updated)).GetProperty("name").GetString());

        var deleted = await client.DeleteAsync($"/api/customers/{id}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal("Inactive", (await JsonAsync(deleted)).GetProperty("status").GetString());

        await AssertProblemAsync(await client.GetAsync("/api/customers/999999"), HttpStatusCode.NotFound);

        using var db = fx.NewContext();
        var actions = await db.AuditLogs.Where(a => a.EntityName == "Customer" && a.RecordId == id.ToString())
            .Select(a => a.Action).ToListAsync();
        Assert.Contains("Create", actions);
        Assert.Contains("Update", actions);
        Assert.Contains("Deactivate", actions);
    }

    [Theory] // API-03, VAL-07: the same rules as the web forms, checked at the API boundary
    [InlineData("email", "not-an-email", "Enter a valid email address.")]
    [InlineData("phone", "12345", "Enter a valid phone number.")]
    [InlineData("customerName", "", "Customer Name is required.")]
    [InlineData("customerName", "LONG", "Customer Name cannot exceed 150 characters.")]
    [InlineData("status", "Bogus", "")]
    public async Task Invalid_customer_payloads_get_400(string field, string value, string message)
    {
        var (_, client) = await UserAsync();
        var body = JsonSerializer.SerializeToNode(CustomerBody())!.AsObject();
        body[field] = value == "LONG" ? new string('x', 151) : value;

        var response = await client.PostAsync("/api/customers", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, message);
    }

    [Fact]
    public async Task Duplicate_customers_get_409()
    {
        var (_, client) = await UserAsync();
        var email = $"dup{DbFixture.Next()}@example.com";
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/customers", CustomerBody(email: email))).StatusCode);

        await AssertProblemAsync(await client.PostAsJsonAsync("/api/customers", CustomerBody(email: email.ToUpperInvariant())),
            HttpStatusCode.Conflict, "A customer with this email already exists.");
    }

    [Fact]
    public async Task Other_users_customers_answer_404_and_stay_unchanged() // RBAC-04
    {
        var (_, owner) = await UserAsync();
        var id = (await JsonAsync(await owner.PostAsJsonAsync("/api/customers", CustomerBody(name: "API Private")))).GetProperty("id").GetInt32();
        var (_, intruder) = await UserAsync();

        await AssertProblemAsync(await intruder.GetAsync($"/api/customers/{id}"), HttpStatusCode.NotFound);
        await AssertProblemAsync(await intruder.PutAsJsonAsync($"/api/customers/{id}", CustomerBody(name: "Hijacked")), HttpStatusCode.NotFound);
        await AssertProblemAsync(await intruder.DeleteAsync($"/api/customers/{id}"), HttpStatusCode.NotFound);
        var list = await JsonAsync(await intruder.GetAsync("/api/customers?search=API%20Private"));
        Assert.Equal(0, list.GetProperty("totalCount").GetInt32());

        using var db = fx.NewContext();
        var c = await db.Customers.SingleAsync(x => x.CustomerId == id);
        Assert.Equal("API Private", c.CustomerName);
        Assert.Equal(CustomerStatus.Active, c.Status);
    }

    [Fact]
    public async Task Responses_never_expose_secrets_or_internal_fields() // API-02, API-07
    {
        var (_, client) = await UserAsync();
        await client.PostAsJsonAsync("/api/customers", CustomerBody());
        var bodies = string.Join("\n", await Task.WhenAll(new[] { "/api/customers", "/api/leads", "/api/opportunities", "/api/followups" }
            .Select(async u => await client.GetStringAsync(u))));
        foreach (var hidden in new[] { "passwordHash", "securityStamp", "concurrencyStamp", "isDeleted", "normalizedEmail", "PasswordHash" })
            Assert.DoesNotContain(hidden, bodies, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- leads, opportunities, follow-ups ----------

    [Fact]
    public async Task Leads_are_listed_in_scope_and_created_with_the_workflow_rules()
    {
        var (_, client) = await UserAsync();
        var name = $"API Lead {DbFixture.Next()}";

        var created = await client.PostAsJsonAsync("/api/leads", new { leadName = name, status = "New", expectedValue = 1000, source = "ColdCall" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = await JsonAsync(created);
        Assert.Equal("New", dto.GetProperty("status").GetString());
        Assert.Equal("ColdCall", dto.GetProperty("source").GetString());

        await AssertProblemAsync(await client.PostAsJsonAsync("/api/leads", new { leadName = "Skip", status = "Qualified" }),
            HttpStatusCode.BadRequest, "New leads start with status New.");
        await AssertProblemAsync(await client.PostAsJsonAsync("/api/leads", new { leadName = "Big", status = "New", expectedValue = 100000001 }),
            HttpStatusCode.BadRequest, "Expected Value must be between 0 and 100,000,000.");

        var list = await JsonAsync(await client.GetAsync($"/api/leads?search={Uri.EscapeDataString(name)}"));
        Assert.Equal(1, list.GetProperty("totalCount").GetInt32());
        var (_, other) = await UserAsync();
        Assert.Equal(0, (await JsonAsync(await other.GetAsync($"/api/leads?search={Uri.EscapeDataString(name)}"))).GetProperty("totalCount").GetInt32());
    }

    async Task<int> CustomerIdAsync(HttpClient client) =>
        (await JsonAsync(await client.PostAsJsonAsync("/api/customers", CustomerBody()))).GetProperty("id").GetInt32();

    [Theory] // §17.19 #5-7 through the API
    [InlineData(0, 50, 10, "Opportunity Amount must be greater than 0.")]
    [InlineData(1000, 101, 10, "Probability must be between 0 and 100.")]
    [InlineData(1000, 50, -1, "Expected Close Date cannot be in the past.")]
    [InlineData(100.555, 50, 10, "Amount can have at most 2 decimal places.")]
    public async Task Opportunity_business_rules_apply_to_the_api(double amount, int probability, int days, string message)
    {
        var (_, client) = await UserAsync();
        var response = await client.PostAsJsonAsync("/api/opportunities", new
        {
            opportunityName = "API Deal", customerId = await CustomerIdAsync(client), amount = (decimal)amount, stage = "Qualification",
            probability, expectedCloseDate = Today.AddDays(days),
        });
        await AssertProblemAsync(response, HttpStatusCode.BadRequest, message);
    }

    [Fact]
    public async Task Opportunities_are_created_and_listed_with_the_weighted_amount()
    {
        var (_, client) = await UserAsync();
        var created = await client.PostAsJsonAsync("/api/opportunities", new
        {
            opportunityName = "API Good Deal", customerId = await CustomerIdAsync(client), amount = 80000m, stage = "Proposal",
            probability = 25, expectedCloseDate = Today.AddDays(30),
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = await JsonAsync(created);
        Assert.Equal(20000m, dto.GetProperty("weightedAmount").GetDecimal());
        Assert.Equal("Open", dto.GetProperty("status").GetString());

        var list = await JsonAsync(await client.GetAsync("/api/opportunities?stage=Proposal"));
        Assert.Contains(list.GetProperty("items").EnumerateArray(), o => o.GetProperty("name").GetString() == "API Good Deal");
    }

    [Fact]
    public async Task Follow_ups_cannot_be_dated_before_today_through_the_api()
    {
        var (_, client) = await UserAsync();
        var customerId = await CustomerIdAsync(client);

        await AssertProblemAsync(await client.PostAsJsonAsync("/api/followups", new
        {
            customerId, followUpDate = Today.AddDays(-1), followUpType = "Call", subject = "Too late",
        }), HttpStatusCode.BadRequest, "Follow-up date cannot be earlier than today.");

        var created = await client.PostAsJsonAsync("/api/followups", new { customerId, followUpDate = Today, followUpType = "Call", subject = "On time" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("Planned", (await JsonAsync(created)).GetProperty("status").GetString());
        var list = await JsonAsync(await client.GetAsync($"/api/followups?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}"));
        Assert.Contains(list.GetProperty("items").EnumerateArray(), f => f.GetProperty("subject").GetString() == "On time");
    }

    // ---------- D9: the pipeline report ----------

    [Fact]
    public async Task Pipeline_report_is_own_for_sales_team_for_managers_and_all_for_admins()
    {
        var (manager, managerClient) = await UserAsync(Roles.Manager);
        var (_, memberClient) = await UserAsync(managerId: manager.Id);
        var (_, outsiderClient) = await UserAsync();
        async Task CreateDeal(HttpClient c, decimal amount) =>
            Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/opportunities", new
            {
                opportunityName = "Pipe", customerId = await CustomerIdAsync(c), amount, stage = "Negotiation", probability = 50,
                expectedCloseDate = Today.AddDays(5),
            })).StatusCode);
        await CreateDeal(memberClient, 1000m);
        await CreateDeal(managerClient, 3000m);
        await CreateDeal(outsiderClient, 7000m);

        async Task<JsonElement> Report(HttpClient c) => await JsonAsync(await c.GetAsync("/api/reports/pipeline"));

        var own = await Report(memberClient);
        Assert.Equal("own", own.GetProperty("scope").GetString());
        Assert.Equal(1000m, own.GetProperty("openAmount").GetDecimal());
        Assert.Equal(500m, own.GetProperty("openWeightedAmount").GetDecimal());

        var team = await Report(managerClient);
        Assert.Equal("team", team.GetProperty("scope").GetString());
        Assert.Equal(4000m, team.GetProperty("openAmount").GetDecimal());
        Assert.Equal(5, team.GetProperty("stages").GetArrayLength());

        var all = await Report(await AuthorizedAsync(DbFixture.AdminEmail, DbFixture.AdminPassword));
        Assert.Equal("all", all.GetProperty("scope").GetString());
        Assert.True(all.GetProperty("openAmount").GetDecimal() >= 11000m);
    }

    // ---------- error handling ----------

    [Fact]
    public async Task Malformed_or_mistyped_json_gets_a_plain_400()
    {
        var (_, client) = await UserAsync();
        foreach (var json in new[] { "{ not json", """{"customerName":"X","email":"x@y.com","phone":"9876543210","status":7}""",
                     """{"customerName":"X","email":"x@y.com","phone":9876543210,"status":"Active"}""" })
            await AssertProblemAsync(await client.PostAsync("/api/customers", new StringContent(json, Encoding.UTF8, "application/json")),
                HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Api_writes_need_no_antiforgery_token_but_web_forms_still_do()
    {
        var (_, client) = await UserAsync();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/customers", CustomerBody())).StatusCode);

        var user = await fx.CreateIdentityUserAsync();
        var browser = await SignedInAsync(fx, user.UserName!, DbFixture.Password);
        var form = await browser.PostAsync("/Customers/Create", new FormUrlEncodedContent(new Dictionary<string, string> { ["CustomerName"] = "x" }));
        Assert.Equal(HttpStatusCode.BadRequest, form.StatusCode);
    }

    [Fact]
    public async Task Unknown_api_routes_get_problem_json_not_html()
    {
        var (_, client) = await UserAsync();
        await AssertProblemAsync(await client.GetAsync("/api/nothing-here"), HttpStatusCode.NotFound);
    }
}
