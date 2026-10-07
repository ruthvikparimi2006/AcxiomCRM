using AcxiomCRM.Data;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

// Lead Management (LEAD-01..09), including conversion of qualified leads (LEAD-06).
// Every action is limited to the user's scope; out-of-scope or deleted ids answer 404.
[Authorize(Policy = Policies.CrmUser)]
public class LeadsController(
    LeadService leads, LeadConversionService conversion, ScopeService scope, AuditService audit, AppDbContext db) : Controller
{
    public async Task<IActionResult> Index(string? search, LeadStatus? status, string? assignedTo,
        string? sort, bool desc = false, int page = 1)
    {
        var query = (await leads.VisibleAsync()).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(l => l.LeadName.Contains(s) || (l.CompanyName != null && l.CompanyName.Contains(s)));
        }
        if (status is not null)
            query = query.Where(l => l.Status == status);
        if (!string.IsNullOrEmpty(assignedTo))
            query = query.Where(l => l.AssignedTo == assignedTo);

        query = (sort?.ToLowerInvariant(), desc) switch
        {
            ("code", false) => query.OrderBy(l => l.LeadId),
            ("code", true) => query.OrderByDescending(l => l.LeadId),
            ("company", false) => query.OrderBy(l => l.CompanyName),
            ("company", true) => query.OrderByDescending(l => l.CompanyName),
            ("value", false) => query.OrderBy(l => l.ExpectedValue),
            ("value", true) => query.OrderByDescending(l => l.ExpectedValue),
            ("assigned", false) => query.OrderBy(l => l.AssignedUser!.Name),
            ("assigned", true) => query.OrderByDescending(l => l.AssignedUser!.Name),
            (_, true) => query.OrderByDescending(l => l.LeadName),
            _ => query.OrderBy(l => l.LeadName),
        };

        var list = await PagedList<LeadListItem>.CreateAsync(query.Select(l => new LeadListItem(
            l.LeadId, l.LeadCode, l.LeadName, l.CompanyName, l.Status, l.Priority, l.Source, l.ExpectedValue,
            l.AssignedUser!.Name)), page);

        return View(new LeadIndexViewModel
        {
            Search = search, Status = status, AssignedTo = assignedTo, Leads = list, Users = await FilterUsersAsync(),
        });
    }

    public async Task<IActionResult> Details(int id)
    {
        var lead = await leads.FindAsync(id);
        if (lead is null) return NotFound();
        return View(new LeadDetailsViewModel
        {
            Lead = lead,
            AssignedName = await db.Users.Where(u => u.Id == lead.AssignedTo).Select(u => u.Name).FirstOrDefaultAsync(),
            History = await audit.HistoryAsync(nameof(Lead), lead.LeadId.ToString()),
        });
    }

    [HttpGet]
    public async Task<IActionResult> Create() =>
        View(await WithOptionsAsync(new LeadFormViewModel { AssignedTo = scope.UserId }, null));

    [HttpPost]
    public async Task<IActionResult> Create(LeadFormViewModel model)
    {
        if (!ModelState.IsValid) return View(await WithOptionsAsync(model, null));

        var (lead, errors) = await leads.CreateAsync(model);
        if (ModelState.AddErrors(errors)) return View(await WithOptionsAsync(model, null));

        TempData["Success"] = $"Lead {lead!.LeadCode} created.";
        return RedirectToAction(nameof(Details), new { id = lead.LeadId });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var l = await leads.FindAsync(id);
        if (l is null) return NotFound();
        return View(await WithOptionsAsync(new LeadFormViewModel
        {
            LeadName = l.LeadName, Email = l.Email, Phone = l.Phone, CompanyName = l.CompanyName, Source = l.Source,
            Status = l.Status, Priority = l.Priority, ExpectedValue = l.ExpectedValue, Notes = l.Notes, AssignedTo = l.AssignedTo,
        }, l));
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, LeadFormViewModel model)
    {
        var lead = await leads.FindAsync(id);
        if (lead is null) return NotFound();
        if (!ModelState.IsValid) return View(await WithOptionsAsync(model, lead));

        if (ModelState.AddErrors(await leads.UpdateAsync(lead, model)))
            return View(await WithOptionsAsync(model, lead));

        TempData["Success"] = "Lead updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var lead = await leads.FindAsync(id);
        if (lead is null) return NotFound();

        await leads.DeleteAsync(lead);
        TempData["Success"] = $"Lead {lead.LeadCode} deleted.";
        return RedirectToAction(nameof(Index));
    }

    // LEAD-06: convert a Qualified lead into a customer (or link the matching one) and optionally an opportunity.
    [HttpGet]
    public async Task<IActionResult> Convert(int id)
    {
        var lead = await leads.FindAsync(id);
        if (lead is null) return NotFound();
        if (!LeadService.CanConvert(lead)) return NotConvertible(id);

        return View(await WithConversionInfoAsync(new ConvertLeadViewModel
        {
            Customer = new CustomerInput
            {
                CustomerName = lead.LeadName, Email = lead.Email ?? "", Phone = lead.Phone ?? "", CompanyName = lead.CompanyName,
            },
            Opportunity = new OpportunityInput
            {
                OpportunityName = lead.LeadName, Amount = lead.ExpectedValue, Stage = OpportunityStage.Qualification,
                Source = lead.Source,
            },
        }, lead));
    }

    [HttpPost]
    public async Task<IActionResult> Convert(int id, ConvertLeadViewModel model)
    {
        var lead = await leads.FindAsync(id);
        if (lead is null) return NotFound();
        if (!LeadService.CanConvert(lead)) return NotConvertible(id);

        await WithConversionInfoAsync(model, lead);
        // Only validate the parts that will be used; the links and assignee are set by the conversion itself.
        DropValidation("Opportunity.CustomerId", "Opportunity.LeadId", "Opportunity.AssignedTo", "Customer.OwnerId");
        if (model.MatchedCustomer is not null) DropValidation("Customer.");
        if (!model.CreateOpportunity) DropValidation("Opportunity.");
        if (!ModelState.IsValid) return View(model);

        var result = await conversion.ConvertAsync(lead, model);
        if (ModelState.AddErrors(result.Errors)) return View(model);

        TempData["Success"] = model.MatchedCustomer is null
            ? $"Lead converted. Customer created{(result.OpportunityId is null ? "" : " with a new opportunity")}."
            : $"Lead converted and linked to existing customer {model.MatchedCustomer.CustomerName}{(result.OpportunityId is null ? "" : " with a new opportunity")}.";
        return result.OpportunityId is { } opportunityId
            ? RedirectToAction("Details", "Opportunities", new { id = opportunityId })
            : RedirectToAction("Details", "Customers", new { id = result.CustomerId });
    }

    IActionResult NotConvertible(int id)
    {
        TempData["Error"] = "Only qualified leads can be converted.";
        return RedirectToAction(nameof(Details), new { id });
    }

    async Task<ConvertLeadViewModel> WithConversionInfoAsync(ConvertLeadViewModel model, Lead lead)
    {
        var match = await conversion.FindMatchAsync(lead);
        model.Lead = lead;
        model.MatchedCustomer = match.Customer;
        if (match.OutsideScope)
            ModelState.AddModelError("", "A customer with this lead's email or phone already exists outside your scope. Ask an administrator to convert this lead.");
        return model;
    }

    void DropValidation(params string[] keysOrPrefixes)
    {
        foreach (var key in ModelState.Keys.Where(k => keysOrPrefixes.Any(p => p.EndsWith('.') ? k.StartsWith(p) : k == p)).ToList())
            ModelState.Remove(key);
    }

    // New leads always start as New; existing leads offer their current status plus the allowed next steps.
    async Task<LeadFormViewModel> WithOptionsAsync(LeadFormViewModel model, Lead? existing)
    {
        model.Assignees = await scope.AssigneeOptionsAsync(existing?.AssignedTo);
        model.AllowedStatuses = existing is null ? [LeadStatus.New] : LeadService.EditableStatuses(existing.Status);
        return model;
    }

    // "Assigned user" filter (LEAD-08): only users whose records this user can see.
    async Task<List<SelectListItem>> FilterUsersAsync()
    {
        var users = db.Users.AsQueryable();
        if (await scope.VisibleUserIdsAsync() is { } ids)
            users = users.Where(u => ids.Contains(u.Id));
        return await users.OrderBy(u => u.Name).Select(u => new SelectListItem(u.Name, u.Id)).ToListAsync();
    }
}
