using AcxiomCRM.Data;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

// Dashboard (DASH-01..07). Every figure comes from the same scoped services as the module pages (DASH-07).
// D8 date rules: records count by CreatedDate; won/lost and monthly sales by ClosedDate; follow-ups by FollowUpDate.
[Authorize(Policy = Policies.CrmUser)]
public class DashboardController(
    CustomerService customers, LeadService leads, OpportunityService opportunities, FollowUpService followUps,
    AppDbContext db) : Controller
{
    public async Task<IActionResult> Index(string? range, DateOnly? from, DateOnly? to)
    {
        var p = Period.From(range, from, to);
        DateTime? start = p.StartUtc, end = p.EndUtc;

        var customerQuery = (await customers.VisibleAsync()).AsNoTracking()
            .Where(c => (start == null || c.CreatedDate >= start) && (end == null || c.CreatedDate < end));
        var leadQuery = (await leads.VisibleAsync()).AsNoTracking()
            .Where(l => (start == null || l.CreatedDate >= start) && (end == null || l.CreatedDate < end));
        var allOpportunities = (await opportunities.VisibleAsync()).AsNoTracking();
        var createdOpportunities = allOpportunities
            .Where(o => (start == null || o.CreatedDate >= start) && (end == null || o.CreatedDate < end));
        var closed = allOpportunities
            .Where(o => o.ClosedDate != null && (start == null || o.ClosedDate >= start) && (end == null || o.ClosedDate < end));
        var openOpportunities = createdOpportunities.Where(o => o.Status == OpportunityStatus.Open);

        var model = new DashboardViewModel
        {
            Period = p,
            Scope = User.IsInRole(Roles.Admin) ? "all records" : User.IsInRole(Roles.Manager) ? "your team's records" : "your assigned records",
            TotalCustomers = await customerQuery.CountAsync(),
            TotalLeads = await leadQuery.CountAsync(),
            OpenLeads = await leadQuery.CountAsync(l => l.Status == LeadStatus.New || l.Status == LeadStatus.Contacted || l.Status == LeadStatus.Qualified),
            TotalOpportunities = await createdOpportunities.CountAsync(),
            OpenOpportunities = await openOpportunities.CountAsync(),
            WonOpportunities = await closed.CountAsync(o => o.Status == OpportunityStatus.Won),
            LostOpportunities = await closed.CountAsync(o => o.Status == OpportunityStatus.Lost),
            PipelineValue = await openOpportunities.SumAsync(o => (decimal?)o.Amount) ?? 0,
        };

        // Lead Status chart: all six statuses (§16 #4).
        var byStatus = await leadQuery.GroupBy(l => l.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        var statuses = Enum.GetValues<LeadStatus>();
        model.LeadStatus = new(statuses.Select(s => s.ToString()).ToList(),
            statuses.Select(s => (decimal)(byStatus.FirstOrDefault(b => b.Key == s)?.Count ?? 0)).ToList());

        // Opportunity Pipeline chart: count and amount per stage.
        var byStage = await createdOpportunities.GroupBy(o => o.Stage)
            .Select(g => new { g.Key, Count = g.Count(), Amount = g.Sum(o => o.Amount) }).ToListAsync();
        var stages = Enum.GetValues<OpportunityStage>();
        model.PipelineCounts = new(stages.Select(s => s.ToString()).ToList(),
            stages.Select(s => (decimal)(byStage.FirstOrDefault(b => b.Key == s)?.Count ?? 0)).ToList());
        model.PipelineAmounts = new(model.PipelineCounts.Labels,
            stages.Select(s => byStage.FirstOrDefault(b => b.Key == s)?.Amount ?? 0).ToList());

        // Monthly Sales chart (§16 #12): won amount by month of ClosedDate, last 12 months.
        var firstMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-11);
        var sales = await allOpportunities
            .Where(o => o.Status == OpportunityStatus.Won && o.ClosedDate >= firstMonth)
            .GroupBy(o => new { o.ClosedDate!.Value.Year, o.ClosedDate.Value.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, Amount = g.Sum(o => o.Amount) }).ToListAsync();
        var months = Enumerable.Range(0, 12).Select(firstMonth.AddMonths).ToList();
        model.MonthlySales = new(months.Select(m => m.ToString("MMM yyyy")).ToList(),
            months.Select(m => sales.FirstOrDefault(s => s.Year == m.Year && s.Month == m.Month)?.Amount ?? 0).ToList());

        // Follow-up panel: planned follow-ups dated in the period (the next 7 days when the period is All time).
        var today = NotBeforeTodayAttribute.Today;
        DateOnly? fFrom = p.FromDay ?? today, fTo = p.ToDay ?? today.AddDays(FollowUpService.UpcomingDays);
        var planned = (await followUps.VisibleAsync()).AsNoTracking().Where(f => f.Status == FollowUpStatus.Planned);
        var inPeriod = planned.Where(f => f.FollowUpDate >= fFrom && f.FollowUpDate <= fTo);
        model.FollowUpsInPeriod = await inPeriod.CountAsync();
        model.OverdueFollowUps = await planned.CountAsync(f => f.FollowUpDate < today);
        model.FollowUps = await FollowUpService.Project(inPeriod.OrderBy(f => f.FollowUpDate).ThenBy(f => f.FollowUpId).Take(5)).ToListAsync();

        // Admin and Manager panel: performance per assigned user in scope.
        if (!User.IsInRole(Roles.SalesExecutive))
        {
            var open = await openOpportunities.GroupBy(o => o.AssignedUser!.Name)
                .Select(g => new { Name = g.Key, Count = g.Count(), Value = g.Sum(o => o.Amount) }).ToListAsync();
            var won = await closed.Where(o => o.Status == OpportunityStatus.Won).GroupBy(o => o.AssignedUser!.Name)
                .Select(g => new { Name = g.Key, Count = g.Count(), Amount = g.Sum(o => o.Amount) }).ToListAsync();
            model.Owners = open.Select(o => o.Name).Union(won.Select(w => w.Name)).OrderBy(n => n)
                .Select(n => new OwnerPerformance(n,
                    open.FirstOrDefault(o => o.Name == n)?.Count ?? 0, open.FirstOrDefault(o => o.Name == n)?.Value ?? 0,
                    won.FirstOrDefault(w => w.Name == n)?.Count ?? 0, won.FirstOrDefault(w => w.Name == n)?.Amount ?? 0))
                .ToList();
        }

        // Admin panel: user and security statistics.
        if (User.IsInRole(Roles.Admin))
        {
            var now = DateTimeOffset.UtcNow;
            model.UserStats = (
                await db.Users.CountAsync(u => u.IsActive),
                await db.Users.CountAsync(u => !u.IsActive),
                await db.Users.CountAsync(u => u.LockoutEnd > now),
                await db.AuditLogs.CountAsync(a => (a.Action == "LoginFailed" || a.Action == "Lockout")
                    && (start == null || a.CreatedDate >= start) && (end == null || a.CreatedDate < end)));
        }

        return View(model);
    }
}
