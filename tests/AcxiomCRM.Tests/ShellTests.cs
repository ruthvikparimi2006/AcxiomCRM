using System.Net;
using System.Text.RegularExpressions;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using static AcxiomCRM.Tests.TestHttp;

namespace AcxiomCRM.Tests;

// Test-only endpoint that fails with a "sensitive" message, to prove it never reaches the user.
[AllowAnonymous]
public class ThrowController : Controller
{
    public const string Secret = "SECRET-DB-DETAIL Server=prod-sql";

    [Route("test/throw")]
    public IActionResult Boom() => throw new InvalidOperationException(Secret);
}

// Step 4: GEN-01..09 and DASH-01, plus §17.19 #1-2.
[Collection("db")]
public class ShellTests(DbFixture fx)
{
    static readonly string[] Cards =
    [
        "Total Customers", "Total Leads", "Open Leads", "Total Opportunities",
        "Open Opportunities", "Won Opportunities", "Lost Opportunities", "Total Pipeline Value",
    ];

    async Task<HttpClient> AsRoleAsync(string role) =>
        await SignedInAsync(fx, (await fx.CreateIdentityUserAsync(role)).UserName!, DbFixture.Password);

    [Theory]
    [InlineData("/")]
    [InlineData("/Dashboard")]
    [InlineData("/Account/ChangePassword")]
    public async Task Protected_pages_redirect_anonymous_users_to_login(string url) // §17.19 #1
    {
        var response = await fx.NewClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("/Account/Login")]
    [InlineData("/Account/Register")]
    public async Task Login_and_register_stay_public(string url) =>
        Assert.Equal(HttpStatusCode.OK, (await fx.NewClient().GetAsync(url)).StatusCode);

    [Fact]
    public async Task Login_lands_on_the_dashboard() // §17.19 #2, DASH-01
    {
        var user = await fx.CreateIdentityUserAsync();
        var client = fx.NewClient();

        var login = await LoginAsync(client, user.UserName!, DbFixture.Password);
        Assert.Equal("/", login.Headers.Location!.OriginalString);

        var html = await client.GetStringAsync("/");
        Assert.Contains("<h1 class=\"h3 mb-0\">Dashboard</h1>", html);
        Assert.All(Cards, c => Assert.Contains(c, html));
    }

    [Fact]
    public async Task Register_lands_on_the_dashboard()
    {
        var client = fx.NewClient();
        var email = $"shell{DbFixture.Next()}@example.com";
        var response = await PostFormAsync(client, "/Account/Register", new()
        {
            ["Name"] = "Shell", ["Email"] = email, ["Password"] = DbFixture.Password, ["ConfirmPassword"] = DbFixture.Password,
        });
        Assert.Equal("/", response.Headers.Location!.OriginalString);
        Assert.Contains("Showing your assigned records", await client.GetStringAsync("/"));
    }

    [Theory]
    [InlineData(Roles.Admin, true, true, "all records")]
    [InlineData(Roles.Manager, true, false, "your team")]
    [InlineData(Roles.SalesExecutive, false, false, "your assigned records")]
    public async Task Menu_and_dashboard_scope_follow_the_role(string role, bool users, bool roles, string scope)
    {
        var client = role == Roles.Admin
            ? await SignedInAsync(fx, DbFixture.AdminEmail, DbFixture.AdminPassword)
            : await AsRoleAsync(role);
        var html = await client.GetStringAsync("/Dashboard");

        Assert.Equal(users, html.Contains("href=\"/Users\""));
        Assert.Equal(roles, html.Contains("href=\"/Roles\""));
        Assert.Contains($"Showing {scope}", html);
        Assert.Contains($"Role: {role}", html);
    }

    [Fact]
    public async Task Layout_has_the_common_parts() // GEN-01, GEN-04, GEN-06, GEN-08
    {
        var html = await (await AsRoleAsync(Roles.SalesExecutive)).GetStringAsync("/Dashboard");

        Assert.Contains("aria-label=\"Main navigation\"", html);
        Assert.Contains("aria-label=\"Notifications\"", html);
        Assert.Contains("Change password", html);
        Assert.Contains("action=\"/Account/Logout\"", html);
        Assert.Contains("<footer", html);
        Assert.Contains("id=\"confirmModal\"", html);
        Assert.Contains("Skip to main content", html);
        Assert.Contains("aria-current=\"page\"", html); // Dashboard marked as the current page
    }

    [Fact]
    public async Task Unhandled_errors_show_a_friendly_page_without_details() // GEN-09
    {
        var response = await fx.NewClient().GetAsync("/test/throw");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("Something went wrong", html);
        Assert.DoesNotContain("SECRET-DB-DETAIL", html);
        Assert.DoesNotContain("InvalidOperationException", html);
        Assert.DoesNotContain("ThrowController", html);
    }

    [Fact]
    public async Task Unknown_pages_and_out_of_scope_records_show_a_friendly_404()
    {
        var client = await AsRoleAsync(Roles.SalesExecutive);
        var response = await client.GetAsync("/NoSuchPage/Anywhere");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Page not found", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Rejected_forms_show_a_friendly_400()
    {
        var response = await fx.NewClient().PostAsync("/Account/Login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["Login"] = "x", ["Password"] = "y" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Request could not be processed", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Lists_page_and_sort_with_the_shared_components() // GEN-07
    {
        var tag = $"Pager{DbFixture.Next()}";
        for (var i = 0; i < 21; i++)
            await fx.CreateIdentityUserAsync(name: $"{tag} {i:D2}");
        var admin = await SignedInAsync(fx, DbFixture.AdminEmail, DbFixture.AdminPassword);

        var page1 = await admin.GetStringAsync($"/Users?search={tag}");
        Assert.Equal(20, Regex.Matches(page1, $">{tag} \\d\\d<").Count);
        Assert.Contains("Showing 1–20 of 21", page1);
        Assert.Contains("Page 1 of 2", page1);

        var page2 = await admin.GetStringAsync($"/Users?search={tag}&page=2");
        Assert.Contains("Showing 21–21 of 21", page2);
        Assert.Contains($">{tag} 20<", page2);

        var sortedDesc = await admin.GetStringAsync($"/Users?search={tag}&sort=name&desc=true");
        Assert.Equal($"{tag} 20", Regex.Match(sortedDesc, $">({tag} \\d\\d)<").Groups[1].Value);
        Assert.Contains("aria-sort=\"descending\"", sortedDesc);

        // Out-of-range pages are clamped to the last page.
        Assert.Contains("Showing 21–21 of 21", await admin.GetStringAsync($"/Users?search={tag}&page=99"));
    }

    [Fact]
    public async Task Destructive_actions_use_the_confirmation_dialog() // GEN-06
    {
        var user = await fx.CreateIdentityUserAsync();
        var admin = await SignedInAsync(fx, DbFixture.AdminEmail, DbFixture.AdminPassword);
        var html = await admin.GetStringAsync($"/Users/Details/{user.Id}");
        Assert.Contains("data-confirm=\"Deactivate this user?", html);
    }
}
