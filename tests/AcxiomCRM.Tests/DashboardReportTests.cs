using System.Net;
using System.Text.RegularExpressions;
using AcxiomCRM.Models;
using static AcxiomCRM.Tests.TestHttp;

namespace AcxiomCRM.Tests;

// Step 12 (lean): DASH-02..07 with the D8 date rules, and RPT-01..03.
[Collection("db")]
public class DashboardReportTests(DbFixture fx)
{
    async Task<(ApplicationUser User, HttpClient Client)> UserAsync(string role = Roles.SalesExecutive, string? managerId = null)
    {
        var user = await fx.CreateIdentityUserAsync(role, managerId: managerId);
        return (user, await SignedInAsync(fx, user.UserName!, DbFixture.Password));
    }

    static string Kpi(string html, string card) =>
        Regex.Match(html, $"data-kpi=\"{Regex.Escape(card)}\">([^<]*)<").Groups[1].Value;

    // A SalesExecutive with known data; one won deal was created long ago but closed today (D8).
    async Task SeedAsync(string userId, string? customerName = null)
    {
        using var db = fx.NewContext();
        var customer = DbFixture.NewCustomer(userId);
        if (customerName is not null) customer.CustomerName = customerName;
        db.Customers.Add(customer);
        db.Leads.AddRange(
            new Lead { LeadName = "L1", AssignedTo = userId, Status = LeadStatus.New },
            new Lead { LeadName = "L2", AssignedTo = userId, Status = LeadStatus.Qualified },
            new Lead { LeadName = "L3", AssignedTo = userId, Status = LeadStatus.Lost });
        await db.SaveChangesAsync();
        var close = DateOnly.FromDateTime(DateTime.Now).AddDays(30);
        db.Opportunities.AddRange(
            new Opportunity { OpportunityName = "Open", CustomerId = customer.CustomerId, AssignedTo = userId, Amount = 1000, Probability = 50, ExpectedCloseDate = close },
            new Opportunity
            {
                OpportunityName = "Old win", CustomerId = customer.CustomerId, AssignedTo = userId, Amount = 5000, Probability = 100,
                ExpectedCloseDate = close, Stage = OpportunityStage.Won, Status = OpportunityStatus.Won,
                CreatedDate = DateTime.UtcNow.AddYears(-2), ClosedDate = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Dashboard_kpis_use_created_dates_and_closed_dates_for_outcomes() // DASH-02, DASH-05, D8
    {
        var (me, client) = await UserAsync();
        await SeedAsync(me.Id);

        var all = await client.GetStringAsync("/Dashboard");
        Assert.Equal("1", Kpi(all, "Total Customers"));
        Assert.Equal("3", Kpi(all, "Total Leads"));
        Assert.Equal("2", Kpi(all, "Open Leads"));
        Assert.Equal("2", Kpi(all, "Total Opportunities"));
        Assert.Equal("1", Kpi(all, "Open Opportunities"));
        Assert.Equal("1", Kpi(all, "Won Opportunities"));
        Assert.Equal("1,000.00", Kpi(all, "Total Pipeline Value"));

        // Today: the old deal no longer counts as created in the period, but its win (closed today) still does.
        var today = await client.GetStringAsync("/Dashboard?range=today");
        Assert.Equal("1", Kpi(today, "Total Opportunities"));
        Assert.Equal("1", Kpi(today, "Won Opportunities"));

        var lastYear = DateTime.Now.AddYears(-1);
        var past = await client.GetStringAsync($"/Dashboard?range=custom&from={lastYear:yyyy-MM-dd}&to={lastYear.AddDays(1):yyyy-MM-dd}");
        Assert.Equal("0", Kpi(past, "Total Customers"));
        Assert.Equal("0", Kpi(past, "Won Opportunities"));
    }

    [Fact]
    public async Task Dashboard_renders_the_three_chartjs_charts_from_scoped_data() // DASH-06, DASH-07
    {
        var (me, client) = await UserAsync();
        await SeedAsync(me.Id);
        var html = await client.GetStringAsync("/Dashboard");

        Assert.Contains("/lib/chart.js/chart.umd.min.js", html);
        foreach (var id in new[] { "leadStatusChart", "pipelineChart", "salesChart" }) Assert.Contains($"id=\"{id}\"", html);
        var data = Regex.Match(html, "<script id=\"chartData\" type=\"application/json\">(.*?)</script>").Groups[1].Value;
        Assert.Contains("\"Unqualified\"", data);           // all six lead statuses
        Assert.Contains("\"Negotiation\"", data);           // all five stages
        Assert.Equal(12, Regex.Matches(data, @"""[A-Z][a-z]{2} \d{4}""").Count); // 12 months
        Assert.Contains("5000", data);                       // this user's won amount, closed this month
    }

    [Fact]
    public async Task Admin_can_open_every_report_with_filters_and_sorting() // RPT-01, RPT-02
    {
        var admin = await SignedInAsync(fx, DbFixture.AdminEmail, DbFixture.AdminPassword);
        var index = await admin.GetStringAsync("/Reports");
        string[] reports = ["Customers", "Leads", "FollowUps", "Opportunities", "Pipeline", "Sales", "UserActivity", "Audit"];
        foreach (var r in reports)
        {
            Assert.Contains($"href=\"/Reports/{r}\"", index);
            var page = await admin.GetAsync($"/Reports/{r}?sort=0&desc=true&from=2000-01-01");
            Assert.True(page.StatusCode == HttpStatusCode.OK, $"{r}: {page.StatusCode}");
        }
        Assert.Contains("Open weighted amount", await admin.GetStringAsync("/Reports/Pipeline?groupBy=owner"));
    }

    [Fact]
    public async Task Report_access_and_data_follow_the_role() // RPT-03, §11
    {
        var (manager, managerClient) = await UserAsync(Roles.Manager);
        var (member, memberClient) = await UserAsync(managerId: manager.Id);
        var (outsider, _) = await UserAsync();
        var tag = $"Rpt{DbFixture.Next()}";
        await SeedAsync(member.Id, $"{tag} Member Co");
        await SeedAsync(outsider.Id, $"{tag} Outsider Co");

        // Sales users: no user activity or audit reports.
        foreach (var r in new[] { "UserActivity", "Audit" })
            Assert.Contains("/Account/AccessDenied", (await memberClient.GetAsync($"/Reports/{r}")).Headers.Location!.ToString());
        Assert.DoesNotContain("/Reports/Audit", await memberClient.GetStringAsync("/Reports"));

        // Managers: user activity yes (team only), audit no.
        Assert.Equal(HttpStatusCode.OK, (await managerClient.GetAsync("/Reports/UserActivity")).StatusCode);
        Assert.Contains("/Account/AccessDenied", (await managerClient.GetAsync("/Reports/Audit")).Headers.Location!.ToString());

        // Data is scoped: own for the member, team for the manager, never the outsider's.
        var mine = await memberClient.GetStringAsync("/Reports/Customers");
        Assert.Contains($"{tag} Member Co", mine);
        Assert.DoesNotContain($"{tag} Outsider Co", mine);
        var team = await managerClient.GetStringAsync("/Reports/Customers");
        Assert.Contains($"{tag} Member Co", team);
        Assert.DoesNotContain($"{tag} Outsider Co", team);
        Assert.Contains(member.Name, await managerClient.GetStringAsync("/Reports/Sales"));
    }
}
