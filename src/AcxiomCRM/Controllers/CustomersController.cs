using AcxiomCRM.Data;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

// Customer Management (CUS-01..08). Every action is limited to the user's scope; out-of-scope ids answer 404.
[Authorize(Policy = Policies.CrmUser)]
public class CustomersController(CustomerService customers, ScopeService scope, AuditService audit, AppDbContext db) : Controller
{
    public async Task<IActionResult> Index(string? search, CustomerStatus? status, string? sort, bool desc = false, int page = 1)
    {
        var query = (await customers.VisibleAsync()).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(c => c.CustomerName.Contains(s) || c.Email.Contains(s) || c.Phone.Contains(s)
                                     || (c.CompanyName != null && c.CompanyName.Contains(s)));
        }
        if (status is not null)
            query = query.Where(c => c.Status == status);

        query = (sort?.ToLowerInvariant(), desc) switch
        {
            ("code", false) => query.OrderBy(c => c.CustomerId),
            ("code", true) => query.OrderByDescending(c => c.CustomerId),
            ("company", false) => query.OrderBy(c => c.CompanyName),
            ("company", true) => query.OrderByDescending(c => c.CompanyName),
            ("owner", false) => query.OrderBy(c => c.Owner!.Name),
            ("owner", true) => query.OrderByDescending(c => c.Owner!.Name),
            (_, true) => query.OrderByDescending(c => c.CustomerName),
            _ => query.OrderBy(c => c.CustomerName),
        };

        var list = await PagedList<CustomerListItem>.CreateAsync(query.Select(c => new CustomerListItem(
            c.CustomerId, c.CustomerCode, c.CustomerName, c.CompanyName, c.Email, c.Phone, c.Owner!.Name, c.Status)), page);

        return View(new CustomerIndexViewModel { Search = search, Status = status, Customers = list });
    }

    public async Task<IActionResult> Details(int id)
    {
        var customer = await customers.FindAsync(id);
        return customer is null ? NotFound() : View(await DetailsModelAsync(customer));
    }

    [HttpGet]
    public async Task<IActionResult> Create() =>
        View(await WithOwnersAsync(new CustomerFormViewModel { OwnerId = scope.UserId }, currentOwnerId: null));

    [HttpPost]
    public async Task<IActionResult> Create(CustomerFormViewModel model)
    {
        if (!ModelState.IsValid) return View(await WithOwnersAsync(model, null));

        var (customer, errors) = await customers.CreateAsync(model);
        if (ModelState.AddErrors(errors)) return View(await WithOwnersAsync(model, null));

        TempData["Success"] = $"Customer {customer!.CustomerCode} created.";
        return RedirectToAction(nameof(Details), new { id = customer.CustomerId });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var c = await customers.FindAsync(id);
        if (c is null) return NotFound();
        return View(await WithOwnersAsync(new CustomerFormViewModel
        {
            CustomerName = c.CustomerName, Email = c.Email, Phone = c.Phone, CompanyName = c.CompanyName,
            Address = c.Address, City = c.City, State = c.State, Status = c.Status, Notes = c.Notes, OwnerId = c.OwnerId,
        }, c.OwnerId));
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, CustomerFormViewModel model)
    {
        var customer = await customers.FindAsync(id);
        if (customer is null) return NotFound();
        if (!ModelState.IsValid) return View(await WithOwnersAsync(model, customer.OwnerId));

        if (ModelState.AddErrors(await customers.UpdateAsync(customer, model)))
            return View(await WithOwnersAsync(model, customer.OwnerId));

        TempData["Success"] = "Customer updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // §16 #8: Delete marks the customer Inactive.
    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var customer = await customers.FindAsync(id);
        if (customer is null) return NotFound();

        await customers.DeactivateAsync(customer);
        TempData["Success"] = "Customer deactivated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    async Task<CustomerDetailsViewModel> DetailsModelAsync(Customer c)
    {
        // ACT-05: related activities, limited to the activities this user may see (ACT-06).
        var activities = db.Activities.AsNoTracking().Where(a => a.CustomerId == c.CustomerId);
        if (await scope.VisibleUserIdsAsync() is { } ids)
            activities = activities.Where(a => ids.Contains(a.AssignedTo));

        var names = await db.Users.Where(u => u.Id == c.OwnerId || u.Id == c.CreatedBy)
            .ToDictionaryAsync(u => u.Id, u => u.Name);

        return new CustomerDetailsViewModel
        {
            Customer = c,
            OwnerName = names.GetValueOrDefault(c.OwnerId),
            CreatedByName = names.GetValueOrDefault(c.CreatedBy),
            History = await audit.HistoryAsync(nameof(Customer), c.CustomerId.ToString()),
            Activities = await activities.OrderByDescending(a => a.ActivityDate).Take(20)
                .Select(a => new RelatedActivity(a.ActivityId, a.ActivityType, a.Subject, a.ActivityDate, a.Status))
                .ToListAsync(),
        };
    }

    async Task<CustomerFormViewModel> WithOwnersAsync(CustomerFormViewModel model, string? currentOwnerId)
    {
        model.Owners = await scope.AssigneeOptionsAsync(currentOwnerId);
        return model;
    }
}
