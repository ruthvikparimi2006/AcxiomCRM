using AcxiomCRM.Data;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

// Activity Management: Call, Meeting, Email, Task (ACT-01..06). Scoped like every CRM module.
[Authorize(Policy = Policies.CrmUser)]
public class ActivitiesController(
    ActivityService activities, CustomerService customers, LeadService leads, ScopeService scope,
    AuditService audit, AppDbContext db) : Controller
{
    // ACT-04: search by type, date range, status and assigned user.
    public async Task<IActionResult> Index(ActivityType? type, ActivityStatus? status, DateOnly? from, DateOnly? to,
        string? assignedTo, string? sort, bool desc = false, int page = 1)
    {
        var query = (await activities.VisibleAsync()).AsNoTracking();
        if (type is not null) query = query.Where(a => a.ActivityType == type);
        if (status is not null) query = query.Where(a => a.Status == status);
        if (from is not null) query = query.Where(a => a.ActivityDate >= from.Value.ToDateTime(TimeOnly.MinValue));
        if (to is not null) query = query.Where(a => a.ActivityDate < to.Value.AddDays(1).ToDateTime(TimeOnly.MinValue));
        if (!string.IsNullOrEmpty(assignedTo)) query = query.Where(a => a.AssignedTo == assignedTo);

        query = (sort?.ToLowerInvariant(), desc) switch
        {
            ("subject", false) => query.OrderBy(a => a.Subject),
            ("subject", true) => query.OrderByDescending(a => a.Subject),
            ("date", false) => query.OrderBy(a => a.ActivityDate),
            _ => query.OrderByDescending(a => a.ActivityDate),
        };

        var list = await PagedList<ActivityListItem>.CreateAsync(query.Select(a => new ActivityListItem(
            a.ActivityId, a.ActivityType, a.Subject, a.ActivityDate, a.Status,
            a.Customer != null ? a.Customer.CustomerName : null, a.Lead != null ? a.Lead.LeadName : null,
            a.AssignedUser!.Name)), page);

        var users = db.Users.AsQueryable();
        if (await scope.VisibleUserIdsAsync() is { } ids) users = users.Where(u => ids.Contains(u.Id));

        return View(new ActivityIndexViewModel
        {
            Type = type, Status = status, From = from, To = to, AssignedTo = assignedTo, Activities = list,
            Users = await users.OrderBy(u => u.Name).Select(u => new SelectListItem(u.Name, u.Id)).ToListAsync(),
        });
    }

    public async Task<IActionResult> Details(int id)
    {
        var a = await activities.FindAsync(id);
        if (a is null) return NotFound();
        return View(new ActivityDetailsViewModel
        {
            Activity = a,
            CustomerName = await db.Customers.Where(c => c.CustomerId == a.CustomerId).Select(c => c.CustomerName).FirstOrDefaultAsync(),
            LeadName = await db.Leads.Where(l => l.LeadId == a.LeadId).Select(l => l.LeadName).FirstOrDefaultAsync(),
            AssignedName = await db.Users.Where(u => u.Id == a.AssignedTo).Select(u => u.Name).FirstOrDefaultAsync(),
            History = await audit.HistoryAsync(nameof(Activity), a.ActivityId.ToString()),
        });
    }

    [HttpGet]
    public async Task<IActionResult> Create(int? customerId, int? leadId) =>
        View(await WithOptionsAsync(new ActivityFormViewModel
        {
            CustomerId = customerId, LeadId = leadId, AssignedTo = scope.UserId,
            ActivityDate = DateTime.Now.Date.AddHours(DateTime.Now.Hour + 1),
        }, null));

    [HttpPost]
    public async Task<IActionResult> Create(ActivityFormViewModel model)
    {
        if (!ModelState.IsValid) return View(await WithOptionsAsync(model, null));

        var (activity, errors) = await activities.CreateAsync(model);
        if (ModelState.AddErrors(errors)) return View(await WithOptionsAsync(model, null));

        TempData["Success"] = "Activity saved.";
        return RedirectToAction(nameof(Details), new { id = activity!.ActivityId });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var a = await activities.FindAsync(id);
        if (a is null) return NotFound();
        return View(await WithOptionsAsync(new ActivityFormViewModel
        {
            ActivityType = a.ActivityType, Subject = a.Subject, Description = a.Description, ActivityDate = a.ActivityDate,
            CustomerId = a.CustomerId, LeadId = a.LeadId, Status = a.Status, AssignedTo = a.AssignedTo,
        }, a));
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, ActivityFormViewModel model)
    {
        var a = await activities.FindAsync(id);
        if (a is null) return NotFound();
        if (!ModelState.IsValid) return View(await WithOptionsAsync(model, a));

        if (ModelState.AddErrors(await activities.UpdateAsync(a, model)))
            return View(await WithOptionsAsync(model, a));

        TempData["Success"] = "Activity updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var a = await activities.FindAsync(id);
        if (a is null) return NotFound();

        await activities.DeleteAsync(a);
        TempData["Success"] = "Activity deleted.";
        return RedirectToAction(nameof(Index));
    }

    async Task<ActivityFormViewModel> WithOptionsAsync(ActivityFormViewModel model, Activity? existing)
    {
        model.Customers = await customers.OptionsAsync(existing?.CustomerId);
        model.Leads = await leads.OptionsAsync();
        model.Assignees = await scope.AssigneeOptionsAsync(existing?.AssignedTo);
        return model;
    }
}
