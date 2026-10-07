using AcxiomCRM.Data;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

// Role Management is Admin-only (§7.1). Roles are the fixed three; users are assigned through UsersController.
[Authorize(Policy = Policies.Admin)]
public class RolesController(AppDbContext db) : Controller
{
    public async Task<IActionResult> Index()
    {
        var counts = await db.Roles
            .Select(r => new { r.Name, Users = db.UserRoles.Count(ur => ur.RoleId == r.Id) })
            .ToDictionaryAsync(r => r.Name!, r => r.Users);
        return View(Roles.All.Select(r => (Role: r, Users: counts.GetValueOrDefault(r))).ToList());
    }

    // §16 #11: permissions are fixed role policies in code; this is a read-only view of them.
    public IActionResult Permissions() => View();
}
