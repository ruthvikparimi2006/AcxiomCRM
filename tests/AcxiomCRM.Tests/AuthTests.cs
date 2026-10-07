using System.Net;
using AcxiomCRM.Models;
using static AcxiomCRM.Tests.TestHttp;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AcxiomCRM.Tests;

// Step 2: AUTH-01..07, AUTH-09, AUTH-11, AUTH-12 through the real MVC pipeline.
[Collection("db")]
public class AuthTests(DbFixture fx)
{
    async Task<List<AuditLog>> AuditFor(string userId)
    {
        using var db = fx.NewContext();
        return await db.AuditLogs.Where(a => a.UserId == userId).OrderBy(a => a.AuditLogId).ToListAsync();
    }

    UserManager<ApplicationUser> Users(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

    [Fact]
    public async Task Register_creates_an_active_SalesExecutive_with_a_hashed_password()
    {
        var client = fx.NewClient();
        var email = $"new{DbFixture.Next()}@example.com";

        var response = await PostFormAsync(client, "/Account/Register", new()
        {
            ["Name"] = "New User", ["Email"] = email,
            ["Password"] = DbFixture.Password, ["ConfirmPassword"] = DbFixture.Password,
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using var scope = fx.App.Services.CreateScope();
        var user = await Users(scope).FindByEmailAsync(email);
        Assert.NotNull(user);
        Assert.True(user.IsActive);
        Assert.Equal([Roles.SalesExecutive], await Users(scope).GetRolesAsync(user));
        Assert.NotEqual(DbFixture.Password, user.PasswordHash);
        Assert.DoesNotContain(DbFixture.Password, user.PasswordHash);
        Assert.Contains(await AuditFor(user.Id), a => a.Action == "Register");
    }

    [Theory]
    [InlineData("short1!A")]       // valid: exactly 8 chars with all four classes
    [InlineData("alllowercase1!")] // no upper-case
    [InlineData("NoDigits!!")]     // no digit
    [InlineData("NoSymbol123")]    // no symbol
    [InlineData("Sh0rt!")]         // under 8
    public async Task Register_enforces_the_password_policy(string password)
    {
        var client = fx.NewClient();
        var email = $"policy{DbFixture.Next()}@example.com";
        await PostFormAsync(client, "/Account/Register", new()
        {
            ["Name"] = "Policy", ["Email"] = email, ["Password"] = password, ["ConfirmPassword"] = password,
        });

        using var scope = fx.App.Services.CreateScope();
        var created = await Users(scope).FindByEmailAsync(email) is not null;
        Assert.Equal(password == "short1!A", created);
    }

    [Fact]
    public async Task Valid_login_by_username_or_email_succeeds_and_is_audited()
    {
        var user = await fx.CreateIdentityUserAsync();

        foreach (var login in new[] { user.UserName!, user.Email! })
        {
            var response = await LoginAsync(fx.NewClient(), login, DbFixture.Password);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".AspNetCore.Identity.Application="));
        }
        Assert.Equal(2, (await AuditFor(user.Id)).Count(a => a.Action == "LoginSuccess"));
    }

    [Fact]
    public async Task Auth_cookie_is_secure_and_http_only()
    {
        var user = await fx.CreateIdentityUserAsync();
        var response = await LoginAsync(fx.NewClient(), user.UserName!, DbFixture.Password);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(".AspNetCore.Identity.Application="));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_user_are_rejected_with_the_same_message()
    {
        var user = await fx.CreateIdentityUserAsync();

        foreach (var (login, password) in new[] { (user.UserName!, "Wrong#Pass1"), ("nobody-here", DbFixture.Password) })
        {
            var response = await LoginAsync(fx.NewClient(), login, password);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Invalid login attempt.", await response.Content.ReadAsStringAsync());
        }
        Assert.Contains(await AuditFor(user.Id), a => a.Action == "LoginFailed" && a.Result == "Failure");
    }

    [Fact]
    public async Task Inactive_user_cannot_log_in()
    {
        var user = await fx.CreateIdentityUserAsync(active: false);
        var response = await LoginAsync(fx.NewClient(), user.UserName!, DbFixture.Password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(await AuditFor(user.Id), a => a.Details == "Inactive account");
    }

    [Fact]
    public async Task Five_failed_logins_lock_the_account_for_15_minutes()
    {
        var user = await fx.CreateIdentityUserAsync();
        for (var i = 0; i < 5; i++)
            await LoginAsync(fx.NewClient(), user.UserName!, "Wrong#Pass1");

        // Even the correct password is refused while locked.
        var response = await LoginAsync(fx.NewClient(), user.UserName!, DbFixture.Password);
        Assert.Contains("This account is locked.", await response.Content.ReadAsStringAsync());

        using var scope = fx.App.Services.CreateScope();
        var locked = await Users(scope).FindByIdAsync(user.Id);
        Assert.InRange(locked!.LockoutEnd!.Value - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15));
        Assert.Single(await AuditFor(user.Id), a => a.Action == "Lockout");
    }

    [Fact]
    public async Task Successful_login_resets_the_failed_attempt_count()
    {
        var user = await fx.CreateIdentityUserAsync();
        await LoginAsync(fx.NewClient(), user.UserName!, "Wrong#Pass1");
        await LoginAsync(fx.NewClient(), user.UserName!, "Wrong#Pass1");
        await LoginAsync(fx.NewClient(), user.UserName!, DbFixture.Password);

        using var scope = fx.App.Services.CreateScope();
        Assert.Equal(0, (await Users(scope).FindByIdAsync(user.Id))!.AccessFailedCount);
    }

    [Fact]
    public async Task Post_without_antiforgery_token_is_rejected()
    {
        var user = await fx.CreateIdentityUserAsync();
        var response = await fx.NewClient().PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Login"] = user.UserName!, ["Password"] = DbFixture.Password,
        }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Logout_ends_the_session_and_is_audited()
    {
        var user = await fx.CreateIdentityUserAsync();
        var client = fx.NewClient();
        await LoginAsync(client, user.UserName!, DbFixture.Password);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Account/ChangePassword")).StatusCode);

        var logout = await PostFormAsync(client, "/Account/Logout", new(), formPage: "/Account/ChangePassword");
        Assert.Equal("/Account/Login", logout.Headers.Location?.OriginalString);

        var after = await client.GetAsync("/Account/ChangePassword");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.StartsWith("https://localhost/Account/Login", after.Headers.Location?.ToString());
        Assert.Contains(await AuditFor(user.Id), a => a.Action == "Logout");
    }

    [Fact]
    public async Task Change_password_requires_the_current_password()
    {
        var user = await fx.CreateIdentityUserAsync();
        var client = fx.NewClient();
        await LoginAsync(client, user.UserName!, DbFixture.Password);
        const string newPassword = "Changed#Pass2";

        await PostFormAsync(client, "/Account/ChangePassword", new()
        {
            ["CurrentPassword"] = "Wrong#Pass1", ["NewPassword"] = newPassword, ["ConfirmPassword"] = newPassword,
        });
        // Wrong current password: nothing changed, the old password still works.
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(fx.NewClient(), user.UserName!, DbFixture.Password)).StatusCode);

        await PostFormAsync(client, "/Account/ChangePassword", new()
        {
            ["CurrentPassword"] = DbFixture.Password, ["NewPassword"] = newPassword, ["ConfirmPassword"] = newPassword,
        });
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(fx.NewClient(), user.UserName!, newPassword)).StatusCode);
        Assert.Contains(await AuditFor(user.Id), a => a.Action == "PasswordChanged" && a.Result == "Success");
    }

    [Fact]
    public async Task Reset_link_works_once_and_its_token_is_never_audited()
    {
        var user = await fx.CreateIdentityUserAsync();
        string token;
        using (var scope = fx.App.Services.CreateScope())
            token = await Users(scope).GeneratePasswordResetTokenAsync((await Users(scope).FindByIdAsync(user.Id))!);
        var url = $"/Account/ResetPassword?userId={user.Id}&token={Uri.EscapeDataString(token)}";
        const string newPassword = "Reset#Pass3";

        var first = await PostFormAsync(fx.NewClient(), "/Account/ResetPassword", new()
        {
            ["UserId"] = user.Id, ["Token"] = token, ["NewPassword"] = newPassword, ["ConfirmPassword"] = newPassword,
        }, formPage: url);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(fx.NewClient(), user.UserName!, newPassword)).StatusCode);

        // Second use of the same link fails.
        var second = await PostFormAsync(fx.NewClient(), "/Account/ResetPassword", new()
        {
            ["UserId"] = user.Id, ["Token"] = token, ["NewPassword"] = "Other#Pass4", ["ConfirmPassword"] = "Other#Pass4",
        }, formPage: url);
        Assert.Contains("invalid or has expired", await second.Content.ReadAsStringAsync());

        var audit = await AuditFor(user.Id);
        Assert.Contains(audit, a => a.Action == "PasswordReset" && a.Result == "Success");
        Assert.Contains(audit, a => a.Action == "PasswordReset" && a.Result == "Failure");
        Assert.All(audit, a => Assert.DoesNotContain(token, $"{a.OldValue}{a.NewValue}{a.Details}"));
    }

    [Fact]
    public void Reset_tokens_expire_after_30_minutes()
    {
        var options = fx.App.Services.GetRequiredService<IOptions<DataProtectionTokenProviderOptions>>().Value;
        Assert.Equal(TimeSpan.FromMinutes(30), options.TokenLifespan);
    }

    [Fact]
    public async Task Passwords_never_appear_in_the_audit_log()
    {
        var user = await fx.CreateIdentityUserAsync();
        await LoginAsync(fx.NewClient(), user.UserName!, "Wrong#Secret9");
        await LoginAsync(fx.NewClient(), user.UserName!, DbFixture.Password);

        using var db = fx.NewContext();
        var all = await db.AuditLogs.ToListAsync();
        foreach (var secret in new[] { DbFixture.Password, "Wrong#Secret9", DbFixture.AdminPassword })
            Assert.DoesNotContain(all, a => $"{a.OldValue}{a.NewValue}{a.Details}".Contains(secret));
    }

    [Fact]
    public async Task First_admin_is_seeded_once_from_configuration()
    {
        using var scope = fx.App.Services.CreateScope();
        var admins = await Users(scope).GetUsersInRoleAsync(Roles.Admin);
        Assert.Equal(DbFixture.AdminEmail, Assert.Single(admins).Email);

        await AcxiomCRM.Data.SeedData.SeedAsync(fx.App.Services);
        Assert.Single(await Users(scope).GetUsersInRoleAsync(Roles.Admin));

        var response = await LoginAsync(fx.NewClient(), DbFixture.AdminEmail, DbFixture.AdminPassword);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
}
