using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using static AcxiomCRM.Tests.TestHttp;

namespace AcxiomCRM.Tests;

// Step 14: production settings (§14, AUTH-08, AUTH-13, API-09), host-neutral.
[Collection("db")]
public class DeploymentTests(DbFixture fx)
{
    // https_port is what a host provides through its HTTPS binding or ASPNETCORE_HTTPS_PORT (see docs/DEPLOYMENT.md).
    WebApplicationFactory<Program> Production() => fx.App.WithWebHostBuilder(b =>
    {
        b.UseEnvironment("Production");
        b.UseSetting("https_port", "443");
    });

    static HttpClient Client(WebApplicationFactory<Program> app) =>
        app.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("https://crm.example.com") });

    [Fact]
    public async Task Production_sends_hsts_and_only_secure_http_only_cookies()
    {
        using var app = Production();
        var client = Client(app);

        var loginPage = await client.GetAsync("/Account/Login");
        Assert.Equal("max-age=31536000; includeSubDomains", loginPage.Headers.GetValues("Strict-Transport-Security").Single());

        var user = await fx.CreateIdentityUserAsync();
        var signedIn = await LoginAsync(client, user.UserName!, DbFixture.Password);
        var cookies = loginPage.Headers.GetValues("Set-Cookie").Concat(signedIn.Headers.GetValues("Set-Cookie")).ToList();
        Assert.Contains(cookies, c => c.StartsWith(".AspNetCore.Antiforgery."));
        Assert.Contains(cookies, c => c.StartsWith(".AspNetCore.Identity.Application="));
        Assert.All(cookies, c =>
        {
            Assert.Contains("secure", c, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("httponly", c, StringComparison.OrdinalIgnoreCase);
        });

        // Plain HTTP is redirected to HTTPS.
        var http = app.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("http://crm.example.com") });
        var redirect = await http.GetAsync("/Account/Login");
        Assert.True(redirect.StatusCode is HttpStatusCode.RedirectKeepVerb or HttpStatusCode.Redirect or HttpStatusCode.MovedPermanently or HttpStatusCode.PermanentRedirect,
            $"HTTP answered {redirect.StatusCode}");
        Assert.Equal("https", redirect.Headers.Location!.Scheme);
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short-key")]
    public void The_app_refuses_to_start_without_a_strong_signing_key(string key)
    {
        using var app = fx.App.WithWebHostBuilder(b => b.UseSetting("Jwt:Key", key));
        var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());
        Assert.Contains("Jwt:Key", error.ToString());
    }

    [Fact]
    public async Task Forwarded_client_addresses_are_trusted_only_from_a_known_proxy()
    {
        async Task<string?> AuditedIpAsync(string remote, string forwardedFor)
        {
            var user = await fx.CreateIdentityUserAsync();
            var handler = fx.App.Server.CreateHandler(ctx => ctx.Connection.RemoteIpAddress = IPAddress.Parse(remote));
            var api = new HttpClient(handler) { BaseAddress = new Uri("https://localhost") };
            api.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
            Assert.Equal(HttpStatusCode.OK, (await api.PostAsJsonAsync("/api/auth/login", new { login = user.UserName, password = DbFixture.Password })).StatusCode);
            using var db = fx.NewContext();
            return await db.AuditLogs.Where(a => a.UserId == user.Id && a.Action == "LoginSuccess").Select(a => a.IpAddress).SingleAsync();
        }

        // A proxy on the same machine (the default trusted proxy) passes on the real client address...
        Assert.Equal("203.0.113.9", await AuditedIpAsync("127.0.0.1", "203.0.113.9"));
        // ...but a client cannot spoof its address by sending the header itself.
        Assert.Equal("198.51.100.7", await AuditedIpAsync("198.51.100.7", "203.0.113.9"));
    }
}
