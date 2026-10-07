using AcxiomCRM.Data;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

// Reports (RPT-01..03, §11). Every report reads through the scoped services, so each role sees its own scope:
// Admin all, Manager team, SalesExecutive own. Filters: period (D8 date per report), assigned user; sorting; paging.
// No export (§16 #13).
// Limitation: rows are built in memory before sorting/paging; move sorting into SQL if reports grow large.
[Authorize(Policy = Policies.CrmUser)]
public class ReportsController(
    CustomerService customers, LeadService leads, OpportunityService opportunities, FollowUpService followUps,
    ScopeService scope, AppDbContext db) : Controller
{
    public static readonly ReportInfo[] All =
    [
        new("Customers", "Customer Report", "Customer list with status, owner and created date."),
        new("Leads", "Lead Report", "Leads by source and status, with conversion information."),
        new("FollowUps", "Follow-Up Report", "Planned, completed, missed and overdue follow-ups."),
        new("Opportunities", "Opportunity Report", "Stage, amount, probability and expected close date."),
        new("Pipeline", "Pipeline Report", "Stage-wise or owner-wise pipeline amount and weighted pipeline."),
        new("Sales", "Sales/Conversion Report", "Leads converted vs not converted and opportunity outcomes per user."),
        new("UserActivity", "User Activity Report", "Audited actions per user (Admin and Manager)."),
        new("Audit", "Audit Report", "Security and business audit events (Admin only)."),
    ];

    bool CanSee(string action) => action switch
    {
        "UserActivity" => User.IsInRole(Roles.Admin) || User.IsInRole(Roles.Manager),
        "Audit" => User.IsInRole(Roles.Admin),
        _ => true,
    };

    public IActionResult Index() => View(All.Where(r => CanSee(r.Action)).ToList());

    public async Task<IActionResult> Customers(DateOnly? from, DateOnly? to, string? ownerId, string? sort, bool desc = false, int page = 1)
    {
        var (start, end) = Utc(from, to);
        var rows = await (await customers.VisibleAsync()).AsNoTracking()
            .Where(c => (start == null || c.CreatedDate >= start) && (end == null || c.CreatedDate < end))
            .Where(c => ownerId == null || c.OwnerId == ownerId)
            .Select(c => new object?[] { c.CustomerCode, c.CustomerName, c.CompanyName, c.Status.ToString(), c.Owner!.Name, c.CreatedDate.ToLocalTime() })
            .ToListAsync();
        return await ReportAsync("Customers", ["Code", "Customer", "Company", "Status", "Owner", "Created"], rows, from, to, ownerId, sort, desc, page, "Created");
    }

    public async Task<IActionResult> Leads(DateOnly? from, DateOnly? to, string? ownerId, string? sort, bool desc = false, int page = 1)
    {
        var (start, end) = Utc(from, to);
        var rows = await (await leads.VisibleAsync()).AsNoTracking()
            .Where(l => (start == null || l.CreatedDate >= start) && (end == null || l.CreatedDate < end))
            .Where(l => ownerId == null || l.AssignedTo == ownerId)
            .Select(l => new object?[]
            {
                l.LeadCode, l.LeadName, l.Source.ToString(), l.Status.ToString(), l.AssignedUser!.Name,
                l.Status == LeadStatus.Converted ? "Yes" : "No", l.ConvertedCustomer != null ? l.ConvertedCustomer.CustomerCode : null,
                l.CreatedDate.ToLocalTime(),
            })
            .ToListAsync();
        return await ReportAsync("Leads", ["Code", "Lead", "Source", "Status", "Owner", "Converted", "Customer", "Created"],
            rows, from, to, ownerId, sort, desc, page, "Created");
    }

    public async Task<IActionResult> FollowUps(DateOnly? from, DateOnly? to, string? ownerId, string? sort, bool desc = false, int page = 1)
    {
        var items = await FollowUpService.Project((await followUps.VisibleAsync()).AsNoTracking()
                .Where(f => (from == null || f.FollowUpDate >= from) && (to == null || f.FollowUpDate <= to))
                .Where(f => ownerId == null || f.AssignedTo == ownerId))
            .ToListAsync();
        var rows = items.Select(f => new object?[]
        {
            f.Date, f.Subject, f.Type.ToString(), f.IsOverdue ? "Overdue" : f.Status.ToString(), f.RelatedName, f.AssignedName,
        }).ToList();
        var totals = new object?[]
        {
            $"{items.Count} total", null, null,
            $"{items.Count(f => f.Status == FollowUpStatus.Completed)} completed · {items.Count(f => f.Status == FollowUpStatus.Missed)} missed · {items.Count(f => f.IsOverdue)} overdue",
            null, null,
        };
        return await ReportAsync("FollowUps", ["Date", "Subject", "Type", "Status", "Related to", "Owner"], rows, from, to, ownerId, sort, desc, page,
            "Follow-up date", totals);
    }

    public async Task<IActionResult> Opportunities(DateOnly? from, DateOnly? to, string? ownerId, string? sort, bool desc = false, int page = 1)
    {
        var (start, end) = Utc(from, to);
        var rows = await (await opportunities.VisibleAsync()).AsNoTracking()
            .Where(o => (start == null || o.CreatedDate >= start) && (end == null || o.CreatedDate < end))
            .Where(o => ownerId == null || o.AssignedTo == ownerId)
            .Select(o => new object?[]
            {
                o.OpportunityName, o.Customer!.CustomerName, o.Stage.ToString(), o.Amount, o.Probability, o.ExpectedCloseDate, o.AssignedUser!.Name,
            })
            .ToListAsync();
        return await ReportAsync("Opportunities", ["Opportunity", "Customer", "Stage", "Amount", "Probability %", "Expected close", "Owner"],
            rows, from, to, ownerId, sort, desc, page, "Created");
    }

    // §11: stage-wise and owner-wise pipeline amount with weighted pipeline (OPP-07).
    public async Task<IActionResult> Pipeline(DateOnly? from, DateOnly? to, string? ownerId, string? groupBy, string? sort, bool desc = false, int page = 1)
    {
        var (start, end) = Utc(from, to);
        var query = (await opportunities.VisibleAsync()).AsNoTracking()
            .Where(o => (start == null || o.CreatedDate >= start) && (end == null || o.CreatedDate < end))
            .Where(o => ownerId == null || o.AssignedTo == ownerId);
        var byOwner = groupBy == "owner";
        List<object?[]> rows;
        if (byOwner)
        {
            var open = query.Where(o => o.Status == OpportunityStatus.Open);
            rows = (await open.GroupBy(o => o.AssignedUser!.Name)
                    .Select(g => new { g.Key, Count = g.Count(), Amount = g.Sum(o => o.Amount), Weighted = g.Sum(o => o.Amount * o.Probability / 100m) })
                    .ToListAsync())
                .Select(d => new object?[] { d.Key, d.Count, d.Amount, d.Weighted }).ToList();
        }
        else
        {
            var data = await query.GroupBy(o => o.Stage)
                .Select(g => new { g.Key, Count = g.Count(), Amount = g.Sum(o => o.Amount), Weighted = g.Sum(o => o.Amount * o.Probability / 100m) })
                .ToListAsync();
            // Every stage, in pipeline order.
            rows = Enum.GetValues<OpportunityStage>().Select(stage => data.FirstOrDefault(d => d.Key == stage) is { } d
                ? new object?[] { stage.ToString(), d.Count, d.Amount, d.Weighted }
                : new object?[] { stage.ToString(), 0, 0m, 0m }).ToList();
        }
        var totals = new object?[] { "Total", rows.Sum(r => (int)r[1]!), rows.Sum(r => (decimal)r[2]!), rows.Sum(r => (decimal)r[3]!) };
        var columns = byOwner ? new[] { "Owner", "Opportunities", "Open amount", "Open weighted amount" } : ["Stage", "Opportunities", "Amount", "Weighted amount"];
        return await ReportAsync("Pipeline", columns, rows, from, to, ownerId, sort, desc, page, "Created", totals, groupBy ?? "stage");
    }

    // §11: leads converted vs not converted, and opportunity outcomes, per assigned user.
    public async Task<IActionResult> Sales(DateOnly? from, DateOnly? to, string? ownerId, string? sort, bool desc = false, int page = 1)
    {
        var (start, end) = Utc(from, to);
        var leadStats = await (await leads.VisibleAsync()).AsNoTracking()
            .Where(l => (start == null || l.CreatedDate >= start) && (end == null || l.CreatedDate < end))
            .Where(l => ownerId == null || l.AssignedTo == ownerId)
            .GroupBy(l => l.AssignedUser!.Name)
            .Select(g => new { Name = g.Key, Total = g.Count(), Converted = g.Count(l => l.Status == LeadStatus.Converted) })
            .ToListAsync();
        var outcomes = await (await opportunities.VisibleAsync()).AsNoTracking()
            .Where(o => o.ClosedDate != null && (start == null || o.ClosedDate >= start) && (end == null || o.ClosedDate < end))
            .Where(o => ownerId == null || o.AssignedTo == ownerId)
            .GroupBy(o => o.AssignedUser!.Name)
            .Select(g => new
            {
                Name = g.Key, Won = g.Count(o => o.Status == OpportunityStatus.Won), Lost = g.Count(o => o.Status == OpportunityStatus.Lost),
                WonAmount = g.Where(o => o.Status == OpportunityStatus.Won).Sum(o => (decimal?)o.Amount) ?? 0,
            })
            .ToListAsync();

        object?[] Row(string name, int total, int converted, int won, int lost, decimal amount) =>
            [name, total, converted, total - converted, total == 0 ? 0m : Math.Round(100m * converted / total, 1),
             won, lost, won + lost == 0 ? 0m : Math.Round(100m * won / (won + lost), 1), amount];

        var rows = leadStats.Select(l => l.Name).Union(outcomes.Select(o => o.Name)).Select(n =>
        {
            var l = leadStats.FirstOrDefault(x => x.Name == n);
            var o = outcomes.FirstOrDefault(x => x.Name == n);
            return Row(n, l?.Total ?? 0, l?.Converted ?? 0, o?.Won ?? 0, o?.Lost ?? 0, o?.WonAmount ?? 0);
        }).ToList();
        var totals = Row("Total", leadStats.Sum(l => l.Total), leadStats.Sum(l => l.Converted),
            outcomes.Sum(o => o.Won), outcomes.Sum(o => o.Lost), outcomes.Sum(o => o.WonAmount));
        return await ReportAsync("Sales",
            ["User", "Leads", "Converted", "Not converted", "Conversion %", "Won", "Lost", "Win rate %", "Won amount"],
            rows, from, to, ownerId, sort, desc, page, "Lead created / deal closed", totals);
    }

    // §11: Admin, or Manager for their team.
    public async Task<IActionResult> UserActivity(DateOnly? from, DateOnly? to, string? ownerId, string? sort, bool desc = false, int page = 1)
    {
        if (!CanSee("UserActivity")) return Forbid();
        var rows = (await AuditQueryAsync(from, to, ownerId))
            .GroupBy(a => new { a.UserId, a.User!.Name })
            .Select(g => new
            {
                g.Key.Name, Total = g.Count(), Logins = g.Count(a => a.Action == "LoginSuccess"),
                Creates = g.Count(a => a.Action == "Create"), Updates = g.Count(a => a.Action == "Update" || a.Action == "StatusChanged" || a.Action == "StageChanged"),
                Deletes = g.Count(a => a.Action == "Delete" || a.Action == "Deactivate"), Failures = g.Count(a => a.Result == "Failure"),
            });
        var list = (await rows.ToListAsync())
            .Select(r => new object?[] { r.Name, r.Total, r.Logins, r.Creates, r.Updates, r.Deletes, r.Failures }).ToList();
        return await ReportAsync("UserActivity", ["User", "Actions", "Logins", "Creates", "Updates", "Deletes", "Failed"],
            list, from, to, ownerId, sort ?? "1", sort is null || desc, page, "Action date");
    }

    // §11: Admin only.
    public async Task<IActionResult> Audit(DateOnly? from, DateOnly? to, string? ownerId, string? sort, bool desc = false, int page = 1)
    {
        if (!CanSee("Audit")) return Forbid();
        var rows = await (await AuditQueryAsync(from, to, ownerId))
            .OrderByDescending(a => a.AuditLogId).Take(5000)
            .Select(a => new object?[] { a.CreatedDate.ToLocalTime(), a.User != null ? a.User.Name : null, a.Module, a.Action, a.EntityName + " " + a.RecordId, a.Result })
            .ToListAsync();
        return await ReportAsync("Audit", ["When", "User", "Module", "Action", "Record", "Result"],
            rows, from, to, ownerId, sort ?? "0", sort is null || desc, page, "Event date");
    }

    async Task<IQueryable<AuditLog>> AuditQueryAsync(DateOnly? from, DateOnly? to, string? ownerId)
    {
        var (start, end) = Utc(from, to);
        var query = db.AuditLogs.AsNoTracking().Where(a => a.UserId != null)
            .Where(a => (start == null || a.CreatedDate >= start) && (end == null || a.CreatedDate < end))
            .Where(a => ownerId == null || a.UserId == ownerId);
        return await scope.VisibleUserIdsAsync() is { } ids ? query.Where(a => ids.Contains(a.UserId!)) : query;
    }

    async Task<IActionResult> ReportAsync(string action, string[] columns, List<object?[]> rows, DateOnly? from, DateOnly? to,
        string? ownerId, string? sort, bool desc, int page, string dateLabel, object?[]? totals = null, string? groupBy = null)
    {
        if (int.TryParse(sort, out var col) && col >= 0 && col < columns.Length)
            rows = (desc ? rows.OrderByDescending(r => r[col], Comparer<object?>.Default) : rows.OrderBy(r => r[col], Comparer<object?>.Default)).ToList();

        var users = db.Users.AsQueryable();
        if (await scope.VisibleUserIdsAsync() is { } ids) users = users.Where(u => ids.Contains(u.Id));

        return View("Report", new ReportViewModel
        {
            Info = All.Single(r => r.Action == action), Columns = columns, Rows = PagedList<object?[]>.FromList(rows, page),
            Totals = totals, From = from, To = to, OwnerId = ownerId, GroupBy = groupBy, ShowGroupBy = groupBy is not null,
            DateLabel = dateLabel,
            Owners = await users.OrderBy(u => u.Name).Select(u => new SelectListItem(u.Name, u.Id)).ToListAsync(),
        });
    }

    static (DateTime? Start, DateTime? End) Utc(DateOnly? from, DateOnly? to) =>
        (from?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime(),
         to?.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime());
}
