using System.Net;
using System.Text.RegularExpressions;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using static AcxiomCRM.Tests.TestHttp;

namespace AcxiomCRM.Tests;

// Step 10: AUD-01..07 completed and verified.
[Collection("db")]
public partial class AuditLogTests(DbFixture fx)
{
    static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);
    static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd");

    [GeneratedRegex("token=([^&\"]+)")]
    private static partial Regex TokenInLink();

    Task<HttpClient> AdminAsync() => SignedInAsync(fx, DbFixture.AdminEmail, DbFixture.AdminPassword);

    static int IdOf(HttpResponseMessage r) => int.Parse(r.Headers.Location!.OriginalString.Split('/').Last());

    // ---------- AUD-04: append-only, enforced by the database ----------

    [Fact]
    public async Task The_database_refuses_to_change_or_delete_audit_rows_even_outside_the_app()
    {
        long id;
        using (var db = fx.NewContext())
        {
            await new AuditService(db, new HttpContextAccessor()).LogAsync("Test", "Probe", "Test", "1");
            id = await db.AuditLogs.MaxAsync(a => a.AuditLogId);
        }

        using var raw = fx.NewContext();
        var update = await Assert.ThrowsAsync<SqlException>(() =>
            raw.Database.ExecuteSqlInterpolatedAsync($"UPDATE AuditLogs SET Action = 'Tampered' WHERE AuditLogId = {id}"));
        Assert.Contains("append-only", update.Message);
        await Assert.ThrowsAsync<SqlException>(() =>
            raw.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM AuditLogs WHERE AuditLogId = {id}"));
        await Assert.ThrowsAsync<SqlException>(() => raw.AuditLogs.Where(a => a.AuditLogId == id).ExecuteDeleteAsync());
        await Assert.ThrowsAsync<SqlException>(() =>
            raw.AuditLogs.Where(a => a.AuditLogId == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Result, "Failure")));

        using var check = fx.NewContext();
        var row = await check.AuditLogs.SingleAsync(a => a.AuditLogId == id);
        Assert.Equal("Probe", row.Action);
        Assert.Equal("Success", row.Result);
    }

    // ---------- AUD-01 / AUD-03 / AUD-02 / AUD-05: every required event, end to end ----------

    [Fact]
    public async Task Every_required_security_business_and_workflow_event_is_audited_without_secrets()
    {
        var admin = await AdminAsync();
        var startId = await MaxAuditIdAsync();

        // Authentication: register (+ login), logout, failed login, lockout, password change.
        var email = $"audit{DbFixture.Next()}@example.com";
        var me = fx.NewClient();
        await PostFormAsync(me, "/Account/Register", new()
        {
            ["Name"] = "Audit User", ["Email"] = email, ["Password"] = DbFixture.Password, ["ConfirmPassword"] = DbFixture.Password,
        });
        await PostFormAsync(me, "/Account/ChangePassword", new()
        {
            ["CurrentPassword"] = DbFixture.Password, ["NewPassword"] = "Changed#Pass9", ["ConfirmPassword"] = "Changed#Pass9",
        });
        await PostFormAsync(me, "/Account/Logout", new(), "/Dashboard");
        await LoginAsync(fx.NewClient(), "no-such-user", "Wrong#Pass1");
        for (var i = 0; i < 5; i++) await LoginAsync(fx.NewClient(), email, "Wrong#Pass1");
        string userId;
        using (var db = fx.NewContext()) userId = (await db.Users.SingleAsync(u => u.Email == email)).Id;

        // User administration: unlock, role change, deactivate/activate, reset link, password reset.
        await PostFormAsync(admin, $"/Users/Unlock/{userId}", new(), "/Users");
        await PostFormAsync(admin, $"/Users/Edit/{userId}", new()
        {
            ["Name"] = "Audit User", ["UserName"] = email, ["Email"] = email, ["Role"] = Roles.Manager,
        }, $"/Users/Edit/{userId}");
        await PostFormAsync(admin, $"/Users/SetActive/{userId}", new() { ["active"] = "false" }, "/Users");
        await PostFormAsync(admin, $"/Users/SetActive/{userId}", new() { ["active"] = "true" }, "/Users");
        var linkPage = await (await PostFormAsync(admin, $"/Users/ResetLink/{userId}", new(), "/Users")).Content.ReadAsStringAsync();
        var token = Uri.UnescapeDataString(WebUtility.HtmlDecode(TokenInLink().Match(linkPage).Groups[1].Value));
        await PostFormAsync(fx.NewClient(), "/Account/ResetPassword", new()
        {
            ["UserId"] = userId, ["Token"] = token, ["NewPassword"] = DbFixture.Password, ["ConfirmPassword"] = DbFixture.Password,
        }, "/Account/Login");
        var user = await SignedInAsync(fx, email, DbFixture.Password);

        // CRM records: create, update, status change and delete/deactivate in every module, plus the workflows.
        var customerForm = new Dictionary<string, string>
        {
            ["CustomerName"] = "Audit Co", ["Email"] = $"auditco{DbFixture.Next()}@example.com", ["Phone"] = DbFixture.NextPhone(),
            ["Status"] = "Active",
        };
        var customer = IdOf(await PostFormAsync(user, "/Customers/Create", customerForm));
        customerForm["CustomerName"] = "Audit Co Renamed";
        customerForm["Status"] = "Inactive";
        await PostFormAsync(user, $"/Customers/Edit/{customer}", customerForm, $"/Customers/Edit/{customer}");
        customerForm["Status"] = "Active";
        await PostFormAsync(user, $"/Customers/Edit/{customer}", customerForm, $"/Customers/Edit/{customer}");
        var keptCustomer = customer;

        var leadForm = new Dictionary<string, string>
        {
            ["LeadName"] = "Audit Lead", ["Email"] = $"auditlead{DbFixture.Next()}@example.com", ["Phone"] = DbFixture.NextPhone(),
            ["Status"] = "New", ["ExpectedValue"] = "5000",
        };
        var lead = IdOf(await PostFormAsync(user, "/Leads/Create", leadForm));
        foreach (var s in new[] { "Contacted", "Qualified" })
        {
            leadForm["Status"] = s;
            await PostFormAsync(user, $"/Leads/Edit/{lead}", leadForm, $"/Leads/Edit/{lead}");
        }
        var converted = await PostFormAsync(user, $"/Leads/Convert/{lead}", new()
        {
            ["Customer.CustomerName"] = "Audit Lead", ["Customer.Email"] = leadForm["Email"], ["Customer.Phone"] = leadForm["Phone"],
            ["CreateOpportunity"] = "true", ["Opportunity.OpportunityName"] = "Audit Deal", ["Opportunity.Amount"] = "5000",
            ["Opportunity.Stage"] = "Qualification", ["Opportunity.Probability"] = "20",
            ["Opportunity.ExpectedCloseDate"] = Iso(Today.AddDays(20)),
        }, "/Dashboard");
        var opportunity = IdOf(converted);
        var deadLead = IdOf(await PostFormAsync(user, "/Leads/Create", new()
        {
            ["LeadName"] = "Audit Lead 2", ["Status"] = "New",
        }));
        await PostFormAsync(user, $"/Leads/Delete/{deadLead}", new(), "/Dashboard");

        await PostFormAsync(user, $"/Opportunities/Edit/{opportunity}", new()
        {
            ["OpportunityName"] = "Audit Deal", ["CustomerId"] = keptCustomer.ToString(), ["Amount"] = "5000", ["Stage"] = "Won",
            ["Probability"] = "100", ["ExpectedCloseDate"] = Iso(Today.AddDays(20)),
        }, $"/Opportunities/Edit/{opportunity}");

        async Task<int> FollowUpAsync(string subject) => IdOf(await PostFormAsync(user, "/FollowUps/Create", new()
        {
            ["CustomerId"] = keptCustomer.ToString(), ["FollowUpDate"] = Iso(Today), ["FollowUpType"] = "Call", ["Subject"] = subject,
        }));
        var f1 = await FollowUpAsync("Audit F1");
        await PostFormAsync(user, $"/FollowUps/Reschedule/{f1}", new() { ["newDate"] = Iso(Today.AddDays(2)) }, "/Dashboard");
        await PostFormAsync(user, $"/FollowUps/Complete/{f1}", new(), "/Dashboard");
        await PostFormAsync(user, $"/FollowUps/Missed/{await FollowUpAsync("Audit F2")}", new(), "/Dashboard");
        await PostFormAsync(user, $"/FollowUps/Cancel/{await FollowUpAsync("Audit F3")}", new(), "/Dashboard");
        await PostFormAsync(user, $"/FollowUps/Delete/{await FollowUpAsync("Audit F4")}", new(), "/Dashboard");

        var activityForm = new Dictionary<string, string>
        {
            ["ActivityType"] = "Meeting", ["Subject"] = "Audit meeting", ["ActivityDate"] = "2030-05-05T10:00",
            ["CustomerId"] = keptCustomer.ToString(), ["Status"] = "Planned",
        };
        var activity = IdOf(await PostFormAsync(user, "/Activities/Create", activityForm));
        activityForm["Status"] = "Completed";
        await PostFormAsync(user, $"/Activities/Edit/{activity}", activityForm, $"/Activities/Edit/{activity}");
        await PostFormAsync(user, $"/Activities/Delete/{activity}", new(), "/Dashboard");
        await PostFormAsync(user, $"/Opportunities/Delete/{opportunity}", new(), "/Dashboard");
        await PostFormAsync(user, $"/Customers/Delete/{keptCustomer}", new(), "/Dashboard");

        // Now check what was recorded.
        using var check = fx.NewContext();
        var rows = await check.AuditLogs.Where(a => a.AuditLogId > startId).ToListAsync();
        var seen = rows.Select(r => $"{r.Module}:{r.Action}").ToHashSet();
        string[] expected =
        [
            "Authentication:Register", "Authentication:LoginSuccess", "Authentication:LoginFailed", "Authentication:Lockout",
            "Authentication:Logout", "Authentication:PasswordChanged", "Authentication:PasswordReset",
            "Users:Unlock", "Users:Update", "Users:RoleChanged", "Users:Deactivate", "Users:Activate", "Users:PasswordResetLinkCreated",
            "Customers:Create", "Customers:Update", "Customers:StatusChanged", "Customers:Deactivate",
            "Leads:Create", "Leads:Update", "Leads:StatusChanged", "Leads:Converted", "Leads:Delete",
            "Opportunities:Create", "Opportunities:Update", "Opportunities:StageChanged", "Opportunities:Delete",
            "FollowUps:Create", "FollowUps:Rescheduled", "FollowUps:Completed", "FollowUps:Missed", "FollowUps:Cancelled", "FollowUps:Delete",
            "Activities:Create", "Activities:Update", "Activities:StatusChanged", "Activities:Delete",
        ];
        var missing = expected.Where(e => !seen.Contains(e)).ToList();
        Assert.True(missing.Count == 0, "Not audited: " + string.Join(", ", missing));

        // AUD-02: the required fields are filled in.
        Assert.All(rows, r =>
        {
            Assert.False(string.IsNullOrEmpty(r.Module));
            Assert.False(string.IsNullOrEmpty(r.Action));
            Assert.False(string.IsNullOrEmpty(r.EntityName));
            Assert.Contains(r.Result, new[] { "Success", "Failure" });
            Assert.True(r.CreatedDate > DateTime.UtcNow.AddMinutes(-10));
        });
        Assert.All(rows.Where(r => r.Module != "Authentication" || r.Action != "LoginFailed" || r.Details?.StartsWith("Unknown") != true),
            r => Assert.NotNull(r.UserId));
        Assert.All(rows.Where(r => r.Module is "Customers" or "Leads" or "Opportunities" or "FollowUps" or "Activities" or "Users"),
            r => Assert.NotNull(r.RecordId));
        Assert.Contains(rows, r => r.Action == "Update" && r.OldValue != null && r.NewValue != null);

        // AUD-05: no passwords, hashes, stamps or tokens anywhere in the audit table.
        var all = string.Join("\n", (await check.AuditLogs.ToListAsync()).Select(r => $"{r.OldValue}{r.NewValue}{r.Details}"));
        foreach (var secret in new[] { DbFixture.Password, DbFixture.AdminPassword, "Changed#Pass9", "Wrong#Pass1", token,
                     "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "AQAAAA" })
            Assert.DoesNotContain(secret, all);
    }

    async Task<long> MaxAuditIdAsync()
    {
        using var db = fx.NewContext();
        return await db.AuditLogs.MaxAsync(a => (long?)a.AuditLogId) ?? 0;
    }

    // ---------- AUD-06 / AUD-07: the audit log page ----------

    [Theory]
    [InlineData(Roles.SalesExecutive)]
    [InlineData(Roles.Manager)]
    public async Task Only_admins_see_the_audit_log_by_default(string role)
    {
        var user = await fx.CreateIdentityUserAsync(role);
        var client = await SignedInAsync(fx, user.UserName!, DbFixture.Password);

        var response = await client.GetAsync("/AuditLog");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/AccessDenied", response.Headers.Location!.ToString());
        Assert.DoesNotContain("href=\"/AuditLog\"", await client.GetStringAsync("/Dashboard"));

        var admin = await AdminAsync();
        Assert.Contains("href=\"/AuditLog\"", await admin.GetStringAsync("/Dashboard"));
    }

    [Fact]
    public async Task Admin_filters_by_user_module_action_and_date_range()
    {
        var user = await fx.CreateIdentityUserAsync();
        var client = await SignedInAsync(fx, user.UserName!, DbFixture.Password);
        var subject = $"Filter customer {DbFixture.Next()}";
        var id = IdOf(await PostFormAsync(client, "/Customers/Create", new()
        {
            ["CustomerName"] = subject, ["Email"] = $"filter{DbFixture.Next()}@example.com", ["Phone"] = DbFixture.NextPhone(), ["Status"] = "Active",
        }));
        var admin = await AdminAsync();

        var byUser = await admin.GetStringAsync($"/AuditLog?userId={user.Id}");
        Assert.Contains("Showing 1–2 of 2", byUser); // LoginSuccess + Customers Create
        Assert.Contains($"Customer {id}", byUser);

        Assert.Contains("Showing 1–1 of 1", await admin.GetStringAsync($"/AuditLog?userId={user.Id}&module=Customers&action=Create"));
        Assert.Contains("Showing 1–1 of 1", await admin.GetStringAsync($"/AuditLog?userId={user.Id}&action=LoginSuccess"));
        Assert.Contains("Showing 1–2 of 2", await admin.GetStringAsync($"/AuditLog?userId={user.Id}&from={Iso(Today)}&to={Iso(Today)}"));
        Assert.Contains("No audit entries found.", await admin.GetStringAsync($"/AuditLog?userId={user.Id}&from={Iso(Today.AddDays(1))}"));
        Assert.Contains("No audit entries found.", await admin.GetStringAsync($"/AuditLog?userId={user.Id}&to={Iso(Today.AddDays(-1))}"));

        using var db = fx.NewContext();
        var entryId = await db.AuditLogs.Where(a => a.EntityName == "Customer" && a.RecordId == id.ToString()).Select(a => a.AuditLogId).SingleAsync();
        var details = await admin.GetStringAsync($"/AuditLog/Details/{entryId}");
        Assert.Contains(subject, details);
        Assert.Contains("cannot be edited or deleted", details);
    }

    [Fact]
    public async Task The_audit_log_has_no_way_to_edit_or_delete_entries()
    {
        var admin = await AdminAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/AuditLog/Edit/1")).StatusCode);
        // No such routes exist. Unknown POSTs answer 405 rather than 404 because the framework's GET-only static-file
        // fallback endpoint ({**path:file}) is a route candidate for every path; either way nothing runs.
        foreach (var action in new[] { "Delete", "Edit", "Create" })
        {
            var r = await PostFormAsync(admin, $"/AuditLog/{action}/1", new(), "/AuditLog");
            Assert.Contains(r.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        }
    }

    [Fact]
    public async Task When_configured_a_manager_gets_a_read_only_view_of_their_team() // §7.1 "limited view if configured"
    {
        using var app = fx.App.WithWebHostBuilder(b => b.UseSetting("Manager:CanViewAuditLog", "true"));
        HttpClient NewClient() => app.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        async Task<HttpClient> As(ApplicationUser u)
        {
            var c = NewClient();
            Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(c, u.UserName!, DbFixture.Password)).StatusCode);
            return c;
        }

        var manager = await fx.CreateIdentityUserAsync(Roles.Manager);
        var member = await fx.CreateIdentityUserAsync(managerId: manager.Id);
        var outsider = await fx.CreateIdentityUserAsync();
        await As(member);
        await As(outsider);
        var client = await As(manager);

        var html = await client.GetStringAsync("/AuditLog");
        Assert.Contains("href=\"/AuditLog\"", html);
        Assert.DoesNotContain(outsider.Name, html);
        // Entries are listed (not just names in the user filter): the member's login and the manager's own.
        Assert.Contains($"<td>{member.Name}</td>", html);
        Assert.Contains($"<td>{manager.Name}</td>", html);
        Assert.Contains("No audit entries found.", await client.GetStringAsync($"/AuditLog?userId={outsider.Id}"));
        Assert.Contains("Showing 1–1 of 1", await client.GetStringAsync($"/AuditLog?userId={member.Id}&action=LoginSuccess"));

        using var db = fx.NewContext();
        var outsiderEntry = await db.AuditLogs.Where(a => a.UserId == outsider.Id).Select(a => a.AuditLogId).FirstAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/AuditLog/Details/{outsiderEntry}")).StatusCode);
    }
}
