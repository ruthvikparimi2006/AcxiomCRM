using AcxiomCRM.Data;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

// Follow-Up Management: Schedule, Complete and Pending Follow-Ups (FUP-01..09) for customers, leads and opportunities.
// Every action is limited to the user's scope; out-of-scope or deleted ids answer 404.
[Authorize(Policy = Policies.CrmUser)]
public class FollowUpsController(
    FollowUpService followUps, CustomerService customers, LeadService leads, OpportunityService opportunities,
    ScopeService scope,
    AuditService audit, AppDbContext db) : Controller
{
    // FUP-08: search by date range, status, assigned user and related customer/lead (or opportunity) name.
    public async Task<IActionResult> Index(DateOnly? from, DateOnly? to, FollowUpStatus? status, string? assignedTo,
        string? related, string? sort, bool desc = false, int page = 1)
    {
        var query = (await followUps.VisibleAsync()).AsNoTracking();
        if (from is not null) query = query.Where(f => f.FollowUpDate >= from);
        if (to is not null) query = query.Where(f => f.FollowUpDate <= to);
        if (status is not null) query = query.Where(f => f.Status == status);
        if (!string.IsNullOrEmpty(assignedTo)) query = query.Where(f => f.AssignedTo == assignedTo);
        if (!string.IsNullOrWhiteSpace(related))
        {
            var r = related.Trim();
            query = query.Where(f => (f.Customer != null && f.Customer.CustomerName.Contains(r))
                                     || (f.Lead != null && f.Lead.LeadName.Contains(r))
                                     || (f.Opportunity != null && f.Opportunity.OpportunityName.Contains(r)));
        }

        query = (sort?.ToLowerInvariant(), desc) switch
        {
            ("subject", false) => query.OrderBy(f => f.Subject),
            ("subject", true) => query.OrderByDescending(f => f.Subject),
            ("assigned", false) => query.OrderBy(f => f.AssignedUser!.Name),
            ("assigned", true) => query.OrderByDescending(f => f.AssignedUser!.Name),
            (_, true) => query.OrderByDescending(f => f.FollowUpDate).ThenByDescending(f => f.FollowUpId),
            _ => query.OrderBy(f => f.FollowUpDate).ThenBy(f => f.FollowUpId),
        };

        return View(new FollowUpIndexViewModel
        {
            From = from, To = to, Status = status, AssignedTo = assignedTo, Related = related,
            Users = await VisibleUsersAsync(),
            FollowUps = await PagedList<FollowUpListItem>.CreateAsync(FollowUpService.Project(query), page),
        });
    }

    // FUP-01 Pending Follow-Ups / FUP-05 reminders: every overdue and upcoming planned follow-up in scope.
    public async Task<IActionResult> Pending() => View(await followUps.RemindersAsync());

    public async Task<IActionResult> Details(int id)
    {
        var f = await followUps.FindAsync(id);
        if (f is null) return NotFound();
        return View(new FollowUpDetailsViewModel
        {
            FollowUp = f,
            RelatedName = f.CustomerId is { } c
                ? await db.Customers.Where(x => x.CustomerId == c).Select(x => x.CustomerName).FirstOrDefaultAsync()
                : f.LeadId is { } l
                    ? await db.Leads.Where(x => x.LeadId == l).Select(x => x.LeadName).FirstOrDefaultAsync()
                    : await db.Opportunities.Where(x => x.OpportunityId == f.OpportunityId)
                        .Select(x => x.OpportunityName).FirstOrDefaultAsync(),
            AssignedName = await db.Users.Where(u => u.Id == f.AssignedTo).Select(u => u.Name).FirstOrDefaultAsync(),
            History = await audit.HistoryAsync(nameof(FollowUp), f.FollowUpId.ToString()),
        });
    }

    [HttpGet]
    public async Task<IActionResult> Create(int? customerId, int? leadId, int? opportunityId) =>
        View(await WithOptionsAsync(new FollowUpFormViewModel
        {
            CustomerId = customerId, LeadId = customerId is null ? leadId : null,
            OpportunityId = customerId is null && leadId is null ? opportunityId : null,
            FollowUpDate = NotBeforeTodayAttribute.Today, AssignedTo = scope.UserId,
        }, null));

    [HttpPost]
    public async Task<IActionResult> Create(FollowUpFormViewModel model)
    {
        if (!ModelState.IsValid) return View(await WithOptionsAsync(model, null));

        var (followUp, errors) = await followUps.CreateAsync(model);
        if (ModelState.AddErrors(errors)) return View(await WithOptionsAsync(model, null));

        TempData["Success"] = "Follow-up scheduled.";
        return RedirectToAction(nameof(Details), new { id = followUp!.FollowUpId });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var f = await followUps.FindAsync(id);
        if (f is null) return NotFound();
        if (f.Status != FollowUpStatus.Planned) return RedirectToAction(nameof(Details), new { id });
        return View(await WithOptionsAsync(new FollowUpFormViewModel
        {
            CustomerId = f.CustomerId, LeadId = f.LeadId, OpportunityId = f.OpportunityId, FollowUpDate = f.FollowUpDate, FollowUpType = f.FollowUpType,
            Subject = f.Subject, Remarks = f.Remarks, AssignedTo = f.AssignedTo,
        }, f));
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, FollowUpFormViewModel model)
    {
        var f = await followUps.FindAsync(id);
        if (f is null) return NotFound();
        if (!ModelState.IsValid) return View(await WithOptionsAsync(model, f));

        if (ModelState.AddErrors(await followUps.UpdateAsync(f, model)))
            return View(await WithOptionsAsync(model, f));

        TempData["Success"] = "Follow-up updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public Task<IActionResult> Complete(int id) => SetStatusAsync(id, FollowUpStatus.Completed, "Follow-up completed.");

    [HttpPost]
    public Task<IActionResult> Missed(int id) => SetStatusAsync(id, FollowUpStatus.Missed, "Follow-up marked as missed.");

    [HttpPost]
    public Task<IActionResult> Cancel(int id) => SetStatusAsync(id, FollowUpStatus.Cancelled, "Follow-up cancelled.");

    [HttpPost]
    public async Task<IActionResult> Reschedule(int id, DateOnly? newDate)
    {
        var f = await followUps.FindAsync(id);
        if (f is null) return NotFound();

        var error = await followUps.RescheduleAsync(f, newDate);
        if (error is null) TempData["Success"] = $"Follow-up rescheduled to {f.FollowUpDate:d}.";
        else TempData["Error"] = error.Message;
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var f = await followUps.FindAsync(id);
        if (f is null) return NotFound();

        await followUps.DeleteAsync(f);
        TempData["Success"] = "Follow-up deleted.";
        return RedirectToAction(nameof(Index));
    }

    async Task<IActionResult> SetStatusAsync(int id, FollowUpStatus status, string success)
    {
        var f = await followUps.FindAsync(id);
        if (f is null) return NotFound();

        var error = await followUps.SetStatusAsync(f, status);
        if (error is null) TempData["Success"] = success;
        else TempData["Error"] = error.Message;
        return RedirectToAction(nameof(Details), new { id });
    }

    async Task<FollowUpFormViewModel> WithOptionsAsync(FollowUpFormViewModel model, FollowUp? existing)
    {
        model.Customers = await customers.OptionsAsync(existing?.CustomerId);
        model.Leads = await leads.OptionsAsync();
        model.Opportunities = await opportunities.OptionsAsync();
        model.Assignees = await scope.AssigneeOptionsAsync(existing?.AssignedTo);
        return model;
    }

    async Task<List<SelectListItem>> VisibleUsersAsync()
    {
        var users = db.Users.AsQueryable();
        if (await scope.VisibleUserIdsAsync() is { } ids) users = users.Where(u => ids.Contains(u.Id));
        return await users.OrderBy(u => u.Name).Select(u => new SelectListItem(u.Name, u.Id)).ToListAsync();
    }
}
