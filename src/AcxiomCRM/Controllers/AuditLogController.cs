using AcxiomCRM.Data;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

// Audit Log (AUD-06, AUD-07): read-only. There are deliberately no edit or delete actions, and the
// database refuses UPDATE/DELETE on these rows anyway (AUD-04).
[Authorize(Policy = Policies.ViewAuditLog)]
public class AuditLogController(AppDbContext db, ScopeService scope) : Controller
{
    // "action" is also a route value (the MVC action name), so the filter is read explicitly from the query string.
    public async Task<IActionResult> Index(string? userId, string? module, [FromQuery(Name = "action")] string? auditAction,
        DateOnly? from, DateOnly? to, int page = 1)
    {
        var visible = await VisibleAsync();
        var query = visible;
        if (!string.IsNullOrEmpty(userId)) query = query.Where(a => a.UserId == userId);
        if (!string.IsNullOrEmpty(module)) query = query.Where(a => a.Module == module);
        if (!string.IsNullOrEmpty(auditAction)) query = query.Where(a => a.Action == auditAction);
        // Dates are picked in local time; audit times are stored in UTC.
        if (from is not null) query = query.Where(a => a.CreatedDate >= LocalDayStartUtc(from.Value));
        if (to is not null) query = query.Where(a => a.CreatedDate < LocalDayStartUtc(to.Value.AddDays(1)));

        var entries = await PagedList<AuditRow>.CreateAsync(query
            .OrderByDescending(a => a.AuditLogId)
            .Select(a => new AuditRow(a.AuditLogId, a.CreatedDate, a.User != null ? a.User.Name : null, a.Module, a.Action,
                a.EntityName, a.RecordId, a.Result, a.IpAddress, a.OldValue, a.NewValue, a.Details)), page);

        var users = db.Users.AsQueryable();
        if (await scope.VisibleUserIdsAsync() is { } ids) users = users.Where(u => ids.Contains(u.Id));

        return View(new AuditIndexViewModel
        {
            UserId = userId, Module = module, Action = auditAction, From = from, To = to, Entries = entries,
            Users = await users.OrderBy(u => u.Name).Select(u => new SelectListItem(u.Name, u.Id)).ToListAsync(),
            Modules = await visible.Select(a => a.Module).Distinct().OrderBy(m => m).ToListAsync(),
            Actions = await visible.Select(a => a.Action).Distinct().OrderBy(m => m).ToListAsync(),
        });
    }

    public async Task<IActionResult> Details(long id)
    {
        var entry = await (await VisibleAsync())
            .Where(a => a.AuditLogId == id)
            .Select(a => new AuditRow(a.AuditLogId, a.CreatedDate, a.User != null ? a.User.Name : null, a.Module, a.Action,
                a.EntityName, a.RecordId, a.Result, a.IpAddress, a.OldValue, a.NewValue, a.Details))
            .FirstOrDefaultAsync();
        return entry is null ? NotFound() : View(entry);
    }

    // AUD-07: Admin sees everything; a Manager (only when configured) sees entries made by their team.
    async Task<IQueryable<AuditLog>> VisibleAsync()
    {
        var query = db.AuditLogs.AsNoTracking();
        return await scope.VisibleUserIdsAsync() is { } ids
            ? query.Where(a => a.UserId != null && ids.Contains(a.UserId))
            : query;
    }

    static DateTime LocalDayStartUtc(DateOnly day) =>
        day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime();
}
