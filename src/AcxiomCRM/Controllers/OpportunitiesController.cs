using AcxiomCRM.Data;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

// Opportunity Management and the Sales Pipeline (OPP-01..09). Scoped like every CRM module.
[Authorize(Policy = Policies.CrmUser)]
public class OpportunitiesController(
    OpportunityService opportunities, CustomerService customers, LeadService leads, ScopeService scope,
    AuditService audit, AppDbContext db) : Controller
{
    // OPP-08: search by opportunity name, customer, stage and status.
    public async Task<IActionResult> Index(string? search, string? customer, OpportunityStage? stage, OpportunityStatus? status,
        string? sort, bool desc = false, int page = 1)
    {
        var query = (await opportunities.VisibleAsync()).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(o => o.OpportunityName.Contains(search.Trim()));
        if (!string.IsNullOrWhiteSpace(customer)) query = query.Where(o => o.Customer!.CustomerName.Contains(customer.Trim()));
        if (stage is not null) query = query.Where(o => o.Stage == stage);
        if (status is not null) query = query.Where(o => o.Status == status);

        query = (sort?.ToLowerInvariant(), desc) switch
        {
            ("name", false) => query.OrderBy(o => o.OpportunityName),
            ("name", true) => query.OrderByDescending(o => o.OpportunityName),
            ("amount", false) => query.OrderBy(o => o.Amount),
            ("amount", true) => query.OrderByDescending(o => o.Amount),
            ("close", true) => query.OrderByDescending(o => o.ExpectedCloseDate),
            _ => query.OrderBy(o => o.ExpectedCloseDate).ThenBy(o => o.OpportunityId),
        };

        return View(new OpportunityIndexViewModel
        {
            Search = search, Customer = customer, Stage = stage, Status = status,
            Opportunities = await PagedList<OpportunityListItem>.CreateAsync(OpportunityService.Project(query), page),
        });
    }

    // OPP-01 Sales Pipeline / OPP-07: count, amount and weighted amount per stage, with the open deals listed.
    public async Task<IActionResult> Pipeline()
    {
        // Limitation: loads every in-scope opportunity; switch to a GROUP BY plus open-only list if volumes grow.
        var items = await OpportunityService.Project(
            (await opportunities.VisibleAsync()).AsNoTracking().OrderBy(o => o.ExpectedCloseDate)).ToListAsync();
        var stages = Enum.GetValues<OpportunityStage>().Select(stage =>
        {
            var inStage = items.Where(i => i.Stage == stage).ToList();
            return new PipelineStage(stage, inStage.Count, inStage.Sum(i => i.Amount), inStage.Sum(i => i.Weighted), inStage);
        }).ToList();
        return View(stages);
    }

    public async Task<IActionResult> Details(int id)
    {
        var o = await opportunities.FindAsync(id);
        if (o is null) return NotFound();
        return View(new OpportunityDetailsViewModel
        {
            Opportunity = o,
            CustomerName = await db.Customers.Where(c => c.CustomerId == o.CustomerId).Select(c => c.CustomerName).FirstOrDefaultAsync(),
            LeadName = await db.Leads.Where(l => l.LeadId == o.LeadId).Select(l => l.LeadName).FirstOrDefaultAsync(),
            AssignedName = await db.Users.Where(u => u.Id == o.AssignedTo).Select(u => u.Name).FirstOrDefaultAsync(),
            History = await audit.HistoryAsync(nameof(Opportunity), o.OpportunityId.ToString()),
        });
    }

    [HttpGet]
    public async Task<IActionResult> Create(int? customerId) =>
        View(await WithOptionsAsync(new OpportunityFormViewModel { CustomerId = customerId, AssignedTo = scope.UserId }, null));

    [HttpPost]
    public async Task<IActionResult> Create(OpportunityFormViewModel model)
    {
        if (!ModelState.IsValid) return View(await WithOptionsAsync(model, null));

        var (opportunity, errors) = await opportunities.CreateAsync(model);
        if (ModelState.AddErrors(errors)) return View(await WithOptionsAsync(model, null));

        TempData["Success"] = "Opportunity created.";
        return RedirectToAction(nameof(Details), new { id = opportunity!.OpportunityId });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var o = await opportunities.FindAsync(id);
        if (o is null) return NotFound();
        return View(await WithOptionsAsync(new OpportunityFormViewModel
        {
            OpportunityName = o.OpportunityName, CustomerId = o.CustomerId, LeadId = o.LeadId, Amount = o.Amount,
            Stage = o.Stage, Probability = o.Probability, ExpectedCloseDate = o.ExpectedCloseDate, Source = o.Source,
            Notes = o.Notes, AssignedTo = o.AssignedTo,
        }, o));
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, OpportunityFormViewModel model)
    {
        var o = await opportunities.FindAsync(id);
        if (o is null) return NotFound();
        if (!ModelState.IsValid) return View(await WithOptionsAsync(model, o));

        if (ModelState.AddErrors(await opportunities.UpdateAsync(o, model)))
            return View(await WithOptionsAsync(model, o));

        TempData["Success"] = "Opportunity updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var o = await opportunities.FindAsync(id);
        if (o is null) return NotFound();

        await opportunities.DeleteAsync(o);
        TempData["Success"] = "Opportunity deleted.";
        return RedirectToAction(nameof(Index));
    }

    async Task<OpportunityFormViewModel> WithOptionsAsync(OpportunityFormViewModel model, Opportunity? existing)
    {
        model.Customers = await customers.OptionsAsync(existing?.CustomerId);
        model.Leads = await leads.OptionsAsync();
        model.Assignees = await scope.AssigneeOptionsAsync(existing?.AssignedTo);
        return model;
    }
}
