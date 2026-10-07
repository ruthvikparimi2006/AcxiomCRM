using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static AcxiomCRM.Tests.TestHttp;

namespace AcxiomCRM.Tests;

// Step 3: RBAC-01..06 and USR-01..08. Every check goes through the server, including crafted requests.
[Collection("db")]
public partial class AuthorizationTests(DbFixture fx)
{
    [GeneratedRegex("value=\"(https://localhost/Account/ResetPassword[^\"]+)\"")]
    private static partial Regex ResetLinkField();

    Task<HttpClient> AdminAsync() => SignedInAsync(fx, DbFixture.AdminEmail, DbFixture.AdminPassword);
    Task<HttpClient> AsAsync(ApplicationUser u) => SignedInAsync(fx, u.UserName!, DbFixture.Password);

    async Task<ApplicationUser> ReloadAsync(string id)
    {
        using var scope = fx.App.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(id))!;
    }

    async Task<string?> RoleOfAsync(string id)
    {
        using var scope = fx.App.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return (await users.GetRolesAsync((await users.FindByIdAsync(id))!)).SingleOrDefault();
    }

    async Task<List<AuditLog>> AuditAbout(string recordId)
    {
        using var db = fx.NewContext();
        return await db.AuditLogs.Where(a => a.RecordId == recordId).ToListAsync();
    }

    static void AssertAccessDenied(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("/Account/AccessDenied", r.Headers.Location!.ToString());
    }

    static Dictionary<string, string> Form(string name, string userName, string role, string? managerId = null) => new()
    {
        ["Name"] = name, ["UserName"] = userName, ["Email"] = userName.Contains('@') ? userName : $"{userName}@example.com",
        ["Role"] = role, ["ManagerId"] = managerId ?? "",
    };

    [Theory]
    [InlineData("/Users")]
    [InlineData("/Users/Create")]
    [InlineData("/Roles")]
    [InlineData("/Roles/Permissions")]
    public async Task Anonymous_users_are_sent_to_login(string url)
    {
        var response = await fx.NewClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("/Users")]
    [InlineData("/Users/Create")]
    [InlineData("/Roles")]
    [InlineData("/Roles/Permissions")]
    public async Task SalesExecutive_has_no_user_or_role_administration(string url)
    {
        var client = await AsAsync(await fx.CreateIdentityUserAsync());
        AssertAccessDenied(await client.GetAsync(url));
    }

    [Fact]
    public async Task Manager_sees_only_their_team_and_read_only()
    {
        var manager = await fx.CreateIdentityUserAsync(Roles.Manager);
        var member = await fx.CreateIdentityUserAsync(managerId: manager.Id);
        var outsider = await fx.CreateIdentityUserAsync();
        var client = await AsAsync(manager);

        var list = await client.GetStringAsync("/Users");
        Assert.Contains(member.UserName!, list);
        Assert.Contains(manager.UserName!, list);
        Assert.DoesNotContain(outsider.UserName!, list);
        Assert.DoesNotContain("New user", list);

        var details = await client.GetStringAsync($"/Users/Details/{member.Id}");
        Assert.Contains(member.Email!, details);
        Assert.DoesNotContain("Generate reset link", details);
        Assert.DoesNotContain("Deactivate", details);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Users/Details/{outsider.Id}")).StatusCode);
        AssertAccessDenied(await client.GetAsync("/Users/Create"));
        AssertAccessDenied(await client.GetAsync($"/Users/Edit/{member.Id}"));
        AssertAccessDenied(await client.GetAsync("/Roles"));
    }

    [Fact]
    public async Task Manager_cannot_change_team_users_with_crafted_posts()
    {
        var manager = await fx.CreateIdentityUserAsync(Roles.Manager);
        var member = await fx.CreateIdentityUserAsync(managerId: manager.Id);
        using (var scope = fx.App.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            await users.SetLockoutEndDateAsync((await users.FindByIdAsync(member.Id))!, DateTimeOffset.UtcNow.AddMinutes(10));
        }
        var client = await AsAsync(manager);

        AssertAccessDenied(await PostFormAsync(client, $"/Users/Unlock/{member.Id}", new(), "/Users"));
        AssertAccessDenied(await PostFormAsync(client, $"/Users/SetActive/{member.Id}", new() { ["active"] = "false" }, "/Users"));
        AssertAccessDenied(await PostFormAsync(client, $"/Users/ResetLink/{member.Id}", new(), "/Users"));
        AssertAccessDenied(await PostFormAsync(client, $"/Users/Edit/{member.Id}",
            Form(member.Name, member.UserName!, Roles.Admin), "/Users"));
        AssertAccessDenied(await PostFormAsync(client, "/Users/Create", Form("Sneaky", $"sneaky{DbFixture.Next()}", Roles.Admin), "/Users"));

        var after = await ReloadAsync(member.Id);
        Assert.True(after.LockoutEnd > DateTimeOffset.UtcNow);
        Assert.True(after.IsActive);
        Assert.Equal(Roles.SalesExecutive, await RoleOfAsync(member.Id));
        Assert.Empty(await AuditAbout(member.Id));
    }

    [Fact]
    public async Task Admin_creates_a_user_who_sets_their_password_through_the_one_time_link()
    {
        var manager = await fx.CreateIdentityUserAsync(Roles.Manager);
        var admin = await AdminAsync();
        var userName = $"created{DbFixture.Next()}";

        var response = await PostFormAsync(admin, "/Users/Create", Form("Created User", userName, Roles.SalesExecutive, manager.Id));
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var link = WebUtility.HtmlDecode(ResetLinkField().Match(html).Groups[1].Value);
        Assert.NotEmpty(link);

        using var db = fx.NewContext();
        var created = await db.Users.SingleAsync(u => u.UserName == userName);
        Assert.Equal(manager.Id, created.ManagerId);
        Assert.Null(created.PasswordHash);
        Assert.Equal(Roles.SalesExecutive, await RoleOfAsync(created.Id));

        // The user sets a password with the link, then logs in.
        var user = fx.NewClient();
        var token = Uri.UnescapeDataString(Regex.Match(link, "token=([^&]+)").Groups[1].Value);
        var reset = await PostFormAsync(user, "/Account/ResetPassword", new()
        {
            ["UserId"] = created.Id, ["Token"] = token,
            ["NewPassword"] = DbFixture.Password, ["ConfirmPassword"] = DbFixture.Password,
        }, formPage: new Uri(link).PathAndQuery);
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(fx.NewClient(), userName, DbFixture.Password)).StatusCode);

        var audit = await AuditAbout(created.Id);
        Assert.Contains(audit, a => a.Action == "Create");
        Assert.Contains(audit, a => a.Action == "PasswordResetLinkCreated");
        Assert.All(audit, a => Assert.DoesNotContain(token, $"{a.OldValue}{a.NewValue}{a.Details}"));
    }

    [Fact]
    public async Task Role_change_is_applied_and_audited()
    {
        var user = await fx.CreateIdentityUserAsync();
        var admin = await AdminAsync();

        var response = await PostFormAsync(admin, $"/Users/Edit/{user.Id}", Form(user.Name, user.UserName!, Roles.Manager),
            $"/Users/Edit/{user.Id}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(Roles.Manager, await RoleOfAsync(user.Id));
        var change = Assert.Single(await AuditAbout(user.Id), a => a.Action == "RoleChanged");
        Assert.Contains(Roles.SalesExecutive, change.OldValue);
        Assert.Contains(Roles.Manager, change.NewValue);
    }

    [Theory]
    [InlineData("SuperUser", false, "Select a valid role.")]
    [InlineData(Roles.Manager, true, "Only Sales Executives can report to a manager.")]
    [InlineData(Roles.SalesExecutive, false, "Select an active manager.")] // "manager" is really a SalesExecutive
    public async Task Invalid_role_or_manager_is_rejected_on_the_server(string role, bool useRealManager, string message)
    {
        var realManager = await fx.CreateIdentityUserAsync(Roles.Manager);
        var notAManager = await fx.CreateIdentityUserAsync();
        var admin = await AdminAsync();
        var userName = $"bad{DbFixture.Next()}";

        var response = await PostFormAsync(admin, "/Users/Create",
            Form("Bad", userName, role, useRealManager ? realManager.Id : notAManager.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(message, await response.Content.ReadAsStringAsync());
        using var db = fx.NewContext();
        Assert.False(await db.Users.AnyAsync(u => u.UserName == userName));
    }

    [Fact]
    public async Task Manager_with_a_team_cannot_be_demoted()
    {
        var manager = await fx.CreateIdentityUserAsync(Roles.Manager);
        await fx.CreateIdentityUserAsync(managerId: manager.Id);
        var admin = await AdminAsync();

        var response = await PostFormAsync(admin, $"/Users/Edit/{manager.Id}",
            Form(manager.Name, manager.UserName!, Roles.SalesExecutive), $"/Users/Edit/{manager.Id}");

        Assert.Contains("Reassign this manager&#x27;s team", await response.Content.ReadAsStringAsync());
        Assert.Equal(Roles.Manager, await RoleOfAsync(manager.Id));
    }

    [Fact]
    public async Task The_last_active_admin_cannot_be_demoted_or_deactivated()
    {
        var admin = await AdminAsync(); // also starts the app, which seeds the Admin
        using var db = fx.NewContext();
        var adminUser = await db.Users.SingleAsync(u => u.Email == DbFixture.AdminEmail);

        var demote = await PostFormAsync(admin, $"/Users/Edit/{adminUser.Id}",
            Form(adminUser.Name, adminUser.UserName!, Roles.Manager), $"/Users/Edit/{adminUser.Id}");
        Assert.Contains("At least one active Admin is required.", await demote.Content.ReadAsStringAsync());

        await PostFormAsync(admin, $"/Users/SetActive/{adminUser.Id}", new() { ["active"] = "false" }, "/Users");
        Assert.True((await ReloadAsync(adminUser.Id)).IsActive);
        Assert.Equal(Roles.Admin, await RoleOfAsync(adminUser.Id));
    }

    [Fact]
    public async Task Deactivated_users_cannot_log_in_until_reactivated()
    {
        var user = await fx.CreateIdentityUserAsync();
        var admin = await AdminAsync();
        var stampBefore = user.SecurityStamp;

        await PostFormAsync(admin, $"/Users/SetActive/{user.Id}", new() { ["active"] = "false" }, "/Users");
        var deactivated = await ReloadAsync(user.Id);
        Assert.False(deactivated.IsActive);
        Assert.NotEqual(stampBefore, deactivated.SecurityStamp); // existing sessions are invalidated
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(fx.NewClient(), user.UserName!, DbFixture.Password)).StatusCode);

        await PostFormAsync(admin, $"/Users/SetActive/{user.Id}", new() { ["active"] = "true" }, "/Users");
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(fx.NewClient(), user.UserName!, DbFixture.Password)).StatusCode);

        var audit = await AuditAbout(user.Id);
        Assert.Contains(audit, a => a.Action == "Deactivate");
        Assert.Contains(audit, a => a.Action == "Activate");
    }

    [Fact]
    public async Task Admin_unlocks_a_locked_account()
    {
        var user = await fx.CreateIdentityUserAsync();
        for (var i = 0; i < 5; i++) await LoginAsync(fx.NewClient(), user.UserName!, "Wrong#Pass1");
        var admin = await AdminAsync();
        Assert.Contains("Locked until", await admin.GetStringAsync($"/Users/Details/{user.Id}"));

        await PostFormAsync(admin, $"/Users/Unlock/{user.Id}", new(), $"/Users/Details/{user.Id}");

        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(fx.NewClient(), user.UserName!, DbFixture.Password)).StatusCode);
        Assert.Contains(await AuditAbout(user.Id), a => a.Action == "Unlock");
    }

    [Fact]
    public async Task User_pages_never_expose_password_hashes_or_security_stamps()
    {
        var user = await ReloadAsync((await fx.CreateIdentityUserAsync()).Id);
        var admin = await AdminAsync();

        var html = await admin.GetStringAsync($"/Users/Details/{user.Id}") + await admin.GetStringAsync("/Users");
        Assert.DoesNotContain(user.PasswordHash!, html);
        Assert.DoesNotContain(user.SecurityStamp!, html);
    }

    [Fact]
    public async Task Admin_can_view_roles_and_the_permission_matrix()
    {
        var admin = await AdminAsync();
        var roles = await admin.GetStringAsync("/Roles");
        Assert.All(Roles.All, r => Assert.Contains(r, roles));
        Assert.Contains("Role Management", await admin.GetStringAsync("/Roles/Permissions"));
    }

    [Theory]
    [InlineData(Roles.Admin)]
    [InlineData(Roles.Manager)]
    [InlineData(Roles.SalesExecutive)]
    public async Task ScopeService_limits_visible_owners_by_role(string role)
    {
        // Role comes from the claim; the stored role doesn't matter (and creating extra Admins would skew other tests).
        var me = await fx.CreateIdentityUserAsync();
        var teamMember = await fx.CreateIdentityUserAsync(managerId: role == Roles.Manager ? me.Id : null);
        var stranger = await fx.CreateIdentityUserAsync();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, me.Id), new Claim(ClaimTypes.Role, role)], "test"));
        using var db = fx.NewContext();
        var scope = new ScopeService(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } }, db);

        Assert.True(await scope.CanSeeAsync(me.Id));
        Assert.True(await scope.CanSeeAsync(stranger.Id) == (role == Roles.Admin));
        Assert.True(await scope.CanSeeAsync(teamMember.Id) == (role != Roles.SalesExecutive));
    }
}
