using System.Security.Claims;
using AcxiomCRM.Data;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Services;

// Record-level scope for the signed-in user (§7.1, §16 #1). Every module filters through this:
// Admin sees everything, a Manager sees their own and their team's records, a SalesExecutive only their own.
public class ScopeService(IHttpContextAccessor http, AppDbContext db)
{
    List<string>? _visibleUserIds;
    bool _loaded;

    ClaimsPrincipal User => http.HttpContext?.User ?? throw new InvalidOperationException("No signed-in user.");

    public string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("No signed-in user.");

    // Owners/assignees whose records the user may see; null means no restriction (Admin).
    public async Task<List<string>?> VisibleUserIdsAsync()
    {
        if (_loaded) return _visibleUserIds;
        _loaded = true;

        var me = UserId;
        if (User.IsInRole(Roles.Admin))
            _visibleUserIds = null;
        else if (User.IsInRole(Roles.Manager))
            _visibleUserIds = await db.Users.Where(u => u.Id == me || u.ManagerId == me).Select(u => u.Id).ToListAsync();
        else
            _visibleUserIds = [me];
        return _visibleUserIds;
    }

    public async Task<bool> CanSeeAsync(string ownerId) =>
        await VisibleUserIdsAsync() is not { } ids || ids.Contains(ownerId);

    // D6: who records may be assigned to. Admin: any active user. Manager: themselves or an active team member.
    // SalesExecutive: only themselves. Used for Owner/AssignedTo in every CRM module.
    public async Task<List<(string Id, string Name)>> AssignableUsersAsync()
    {
        var me = UserId;
        var query = db.Users.Where(u => u.IsActive);
        if (!User.IsInRole(Roles.Admin))
            query = User.IsInRole(Roles.Manager)
                ? query.Where(u => u.Id == me || u.ManagerId == me)
                : query.Where(u => u.Id == me);
        var users = await query.OrderBy(u => u.Name).Select(u => new { u.Id, u.Name }).ToListAsync();
        return users.Select(u => (u.Id, u.Name)).ToList();
    }

    public async Task<bool> CanAssignToAsync(string? userId) =>
        userId is not null && (await AssignableUsersAsync()).Any(u => u.Id == userId);

    // Dropdown options for Owner/AssignedTo. The current assignee stays listed even if no longer assignable,
    // so editing other fields of an old record doesn't force a reassignment.
    public async Task<List<SelectListItem>> AssigneeOptionsAsync(string? currentId)
    {
        var users = await AssignableUsersAsync();
        if (currentId is not null && users.All(u => u.Id != currentId))
        {
            var name = await db.Users.Where(u => u.Id == currentId).Select(u => u.Name).FirstOrDefaultAsync();
            users.Insert(0, (currentId, $"{name} (current)"));
        }
        return users.Select(u => new SelectListItem(u.Name, u.Id)).ToList();
    }
}
