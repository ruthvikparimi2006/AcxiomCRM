using AcxiomCRM.Data;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

// User & Role Management (USR-01..08). Admins manage everyone; Managers get a read-only view of their team.
[Authorize(Policy = Policies.ViewUsers)]
public class UsersController(
    AppDbContext db,
    UserManager<ApplicationUser> users,
    ScopeService scope,
    AuditService audit) : Controller
{
    const string Module = "Users";

    bool IsAdmin => User.IsInRole(Roles.Admin);

    public async Task<IActionResult> Index(string? search, string? role, bool? active, string? sort, bool desc = false, int page = 1)
    {
        var query = db.Users.AsNoTracking();
        if (await scope.VisibleUserIdsAsync() is { } ids)
            query = query.Where(u => ids.Contains(u.Id));
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(u => u.Name.Contains(search) || u.Email!.Contains(search) || u.UserName!.Contains(search));
        if (!string.IsNullOrEmpty(role))
            query = query.Where(u => db.UserRoles.Any(ur => ur.UserId == u.Id && db.Roles.Any(r => r.Id == ur.RoleId && r.Name == role)));
        if (active is not null)
            query = query.Where(u => u.IsActive == active);

        query = (sort?.ToLowerInvariant(), desc) switch
        {
            ("username", false) => query.OrderBy(u => u.UserName),
            ("username", true) => query.OrderByDescending(u => u.UserName),
            ("email", false) => query.OrderBy(u => u.Email),
            ("email", true) => query.OrderByDescending(u => u.Email),
            (_, true) => query.OrderByDescending(u => u.Name),
            _ => query.OrderBy(u => u.Name),
        };

        var now = DateTimeOffset.UtcNow;
        var list = await PagedList<UserListItem>.CreateAsync(query
            .Select(u => new UserListItem(u.Id, u.Name, u.UserName!, u.Email!,
                db.UserRoles.Where(ur => ur.UserId == u.Id).Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name).FirstOrDefault(),
                u.IsActive, u.LockoutEnd > now)), page);

        return View(new UserIndexViewModel { Search = search, Role = role, Active = active, Users = list, CanManage = IsAdmin });
    }

    public async Task<IActionResult> Details(string id)
    {
        var user = await FindVisibleAsync(id);
        return user is null ? NotFound() : View(await DetailsModelAsync(user));
    }

    [Authorize(Policy = Policies.Admin), HttpGet]
    public async Task<IActionResult> Create() => View(await WithManagersAsync(new UserFormViewModel { Role = Roles.SalesExecutive }));

    [Authorize(Policy = Policies.Admin), HttpPost]
    public async Task<IActionResult> Create(UserFormViewModel model)
    {
        await ValidateAsync(model, existing: null, currentRole: null);
        if (!ModelState.IsValid) return View(await WithManagersAsync(model));

        // No password: the new user sets one through the one-time reset link shown below (§16 D2).
        var user = new ApplicationUser
        {
            Name = model.Name, UserName = model.UserName, Email = model.Email,
            ManagerId = NullIfEmpty(model.ManagerId), EmailConfirmed = true,
        };

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            if (!Succeeded(await users.CreateAsync(user)) || !Succeeded(await users.AddToRoleAsync(user, model.Role)))
                return View(await WithManagersAsync(model));
            await tx.CommitAsync();
        }

        await audit.LogAsync(Module, "Create", "User", user.Id, newValue: Snapshot(user, model.Role));
        return await ShowResetLinkAsync(user);
    }

    [Authorize(Policy = Policies.Admin), HttpGet]
    public async Task<IActionResult> Edit(string id)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null) return NotFound();
        return View(await WithManagersAsync(new UserFormViewModel
        {
            Name = user.Name, UserName = user.UserName!, Email = user.Email!,
            Role = await RoleOfAsync(user) ?? "", ManagerId = user.ManagerId,
        }));
    }

    [Authorize(Policy = Policies.Admin), HttpPost]
    public async Task<IActionResult> Edit(string id, UserFormViewModel model)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null) return NotFound();

        var oldRole = await RoleOfAsync(user);
        await ValidateAsync(model, user, oldRole);
        if (!ModelState.IsValid) return View(await WithManagersAsync(model));

        var before = Snapshot(user, oldRole);
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            user.Name = model.Name;
            user.ManagerId = NullIfEmpty(model.ManagerId);
            var ok = (user.UserName == model.UserName || Succeeded(await users.SetUserNameAsync(user, model.UserName)))
                && (user.Email == model.Email || Succeeded(await users.SetEmailAsync(user, model.Email)))
                && Succeeded(await users.UpdateAsync(user))
                && (oldRole == model.Role
                    || ((oldRole is null || Succeeded(await users.RemoveFromRoleAsync(user, oldRole)))
                        && Succeeded(await users.AddToRoleAsync(user, model.Role))));
            if (!ok) return View(await WithManagersAsync(model));
            await tx.CommitAsync();
        }

        await audit.LogAsync(Module, "Update", "User", user.Id, before, Snapshot(user, model.Role));
        if (oldRole != model.Role)
            await audit.LogAsync(Module, "RoleChanged", "User", user.Id, new { Role = oldRole }, new { Role = model.Role });

        TempData["Success"] = "User updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Policy = Policies.Admin), HttpPost]
    public async Task<IActionResult> SetActive(string id, bool active)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null) return NotFound();

        if (!active && user.Id == users.GetUserId(User))
            TempData["Error"] = "You cannot deactivate your own account.";
        else if (!active && await IsLastActiveAdminAsync(user))
            TempData["Error"] = "At least one active Admin is required.";
        else if (user.IsActive != active)
        {
            user.IsActive = active;
            await users.UpdateAsync(user);
            await users.UpdateSecurityStampAsync(user); // ends the user's existing sessions
            await audit.LogAsync(Module, active ? "Activate" : "Deactivate", "User", user.Id,
                new { IsActive = !active }, new { IsActive = active });
            TempData["Success"] = active ? "User activated." : "User deactivated.";
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    // AUTH-07: unlock only through this controlled, audited Admin action.
    [Authorize(Policy = Policies.Admin), HttpPost]
    public async Task<IActionResult> Unlock(string id)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null) return NotFound();

        var lockedUntil = user.LockoutEnd;
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        await audit.LogAsync(Module, "Unlock", "User", user.Id, new { LockoutEnd = lockedUntil }, new { LockoutEnd = (DateTimeOffset?)null });

        TempData["Success"] = "Account unlocked.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Policy = Policies.Admin), HttpPost]
    public async Task<IActionResult> ResetLink(string id)
    {
        var user = await users.FindByIdAsync(id);
        return user is null ? NotFound() : await ShowResetLinkAsync(user);
    }

    // §16 D2: one-time link the Admin passes to the user. Rendered once, never stored or logged.
    async Task<IActionResult> ShowResetLinkAsync(ApplicationUser user)
    {
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var model = await DetailsModelAsync(user);
        model.ResetLink = Url.Action("ResetPassword", "Account", new { userId = user.Id, token }, Request.Scheme);
        await audit.LogAsync(Module, "PasswordResetLinkCreated", "User", user.Id, details: "Link expires in 30 minutes");

        Response.Headers.CacheControl = "no-store";
        return View(nameof(Details), model);
    }

    async Task ValidateAsync(UserFormViewModel model, ApplicationUser? existing, string? currentRole)
    {
        if (!Roles.All.Contains(model.Role))
            ModelState.AddModelError(nameof(model.Role), "Select a valid role.");

        if (!string.IsNullOrEmpty(model.ManagerId))
        {
            var manager = await users.FindByIdAsync(model.ManagerId);
            if (model.Role != Roles.SalesExecutive)
                ModelState.AddModelError(nameof(model.ManagerId), "Only Sales Executives can report to a manager.");
            else if (manager is null || !manager.IsActive || manager.Id == existing?.Id
                     || !await users.IsInRoleAsync(manager, Roles.Manager))
                ModelState.AddModelError(nameof(model.ManagerId), "Select an active manager.");
        }

        if (existing is null) return;
        if (currentRole == Roles.Manager && model.Role != Roles.Manager
            && await db.Users.AnyAsync(u => u.ManagerId == existing.Id))
            ModelState.AddModelError(nameof(model.Role), "Reassign this manager's team before changing their role.");
        if (currentRole == Roles.Admin && model.Role != Roles.Admin && await IsLastActiveAdminAsync(existing))
            ModelState.AddModelError(nameof(model.Role), "At least one active Admin is required.");
    }

    async Task<bool> IsLastActiveAdminAsync(ApplicationUser user) =>
        user.IsActive && await users.IsInRoleAsync(user, Roles.Admin)
        && (await users.GetUsersInRoleAsync(Roles.Admin)).Count(u => u.IsActive) <= 1;

    async Task<ApplicationUser?> FindVisibleAsync(string id)
    {
        var user = await users.FindByIdAsync(id);
        return user is not null && await scope.CanSeeAsync(user.Id) ? user : null;
    }

    async Task<string?> RoleOfAsync(ApplicationUser user) => (await users.GetRolesAsync(user)).FirstOrDefault();

    async Task<UserDetailsViewModel> DetailsModelAsync(ApplicationUser user) => new()
    {
        Id = user.Id, Name = user.Name, UserName = user.UserName!, Email = user.Email!,
        Role = await RoleOfAsync(user),
        ManagerName = user.ManagerId is null ? null
            : await db.Users.Where(u => u.Id == user.ManagerId).Select(u => u.Name).FirstOrDefaultAsync(),
        IsActive = user.IsActive, LockoutEnd = user.LockoutEnd, AccessFailedCount = user.AccessFailedCount,
        CreatedDate = user.CreatedDate, CanManage = IsAdmin,
    };

    async Task<UserFormViewModel> WithManagersAsync(UserFormViewModel model)
    {
        model.Managers = (await users.GetUsersInRoleAsync(Roles.Manager))
            .Where(u => u.IsActive).OrderBy(u => u.Name)
            .Select(u => new SelectListItem(u.Name, u.Id)).ToList();
        return model;
    }

    static object Snapshot(ApplicationUser u, string? role) =>
        new { u.Name, u.UserName, u.Email, Role = role, u.ManagerId, u.IsActive };

    static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    bool Succeeded(IdentityResult result)
    {
        foreach (var error in result.Errors)
            ModelState.AddModelError("", error.Description);
        return result.Succeeded;
    }
}
