using AcxiomCRM.Data;
using AcxiomCRM.Models;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Services;

// Customer business rules (CUS-01..08). Shared by the MVC controller and, in Step 11, the REST API.
// Format/length rules are checked at the boundary (ModelState); this adds uniqueness, ownership and scope.
public class CustomerService(AppDbContext db, ScopeService scope, AuditService audit)
{
    const string Module = "Customers";
    const string DuplicateEmail = "A customer with this email already exists.";
    const string DuplicatePhone = "A customer with this phone number already exists.";

    public async Task<IQueryable<Customer>> VisibleAsync()
    {
        var query = db.Customers.AsQueryable();
        return await scope.VisibleUserIdsAsync() is { } ids ? query.Where(c => ids.Contains(c.OwnerId)) : query;
    }

    // Null when the customer doesn't exist or is outside the user's scope (callers answer 404 either way).
    public async Task<Customer?> FindAsync(int id) =>
        await (await VisibleAsync()).FirstOrDefaultAsync(c => c.CustomerId == id);

    // Dropdown of active customers in scope for linking follow-ups/activities; keeps the current link listed.
    public async Task<List<SelectListItem>> OptionsAsync(int? currentId) =>
        await (await VisibleAsync())
            .Where(c => c.Status == CustomerStatus.Active || c.CustomerId == currentId)
            .OrderBy(c => c.CustomerName)
            .Select(c => new SelectListItem(c.CustomerName + " (" + c.CustomerCode + ")", c.CustomerId.ToString()))
            .ToListAsync();

    public async Task<(Customer? Customer, List<FieldError> Errors)> CreateAsync(CustomerInput input)
    {
        Normalize(input);
        if (string.IsNullOrEmpty(input.OwnerId)) input.OwnerId = scope.UserId;

        var errors = await ValidateAsync(input, existing: null);
        if (errors.Count > 0) return (null, errors);

        var customer = new Customer { CreatedBy = scope.UserId, CreatedDate = DateTime.UtcNow };
        Apply(input, customer);
        db.Customers.Add(customer);
        if (await SaveAsync() is { } duplicate) return (null, [duplicate]);

        await audit.LogAsync(Module, "Create", nameof(Customer), customer.CustomerId.ToString(), newValue: Snapshot(customer));
        return (customer, []);
    }

    public async Task<List<FieldError>> UpdateAsync(Customer customer, CustomerInput input)
    {
        Normalize(input);
        if (string.IsNullOrEmpty(input.OwnerId)) input.OwnerId = customer.OwnerId;

        var errors = await ValidateAsync(input, customer);
        if (errors.Count > 0) return errors;

        var before = Snapshot(customer);
        var oldStatus = customer.Status;
        Apply(input, customer);
        customer.ModifiedDate = DateTime.UtcNow;
        if (await SaveAsync() is { } duplicate) return [duplicate];

        var id = customer.CustomerId.ToString();
        await audit.LogAsync(Module, "Update", nameof(Customer), id, before, Snapshot(customer));
        if (oldStatus != customer.Status)
            await audit.LogAsync(Module, "StatusChanged", nameof(Customer), id, new { Status = oldStatus }, new { customer.Status });
        return [];
    }

    // §16 #8: deleting a customer marks it Inactive; the record and its history stay.
    public async Task DeactivateAsync(Customer customer)
    {
        if (customer.Status == CustomerStatus.Inactive) return;
        customer.Status = CustomerStatus.Inactive;
        customer.ModifiedDate = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.LogAsync(Module, "Deactivate", nameof(Customer), customer.CustomerId.ToString(),
            new { Status = CustomerStatus.Active }, new { customer.Status });
    }

    async Task<List<FieldError>> ValidateAsync(CustomerInput input, Customer? existing)
    {
        var errors = new List<FieldError>();
        var id = existing?.CustomerId ?? 0;

        // §16 #9: checked across all customers, including inactive ones and those outside the user's scope.
        // The Email column is case-insensitive, so this comparison is too.
        if (await db.Customers.AnyAsync(c => c.CustomerId != id && c.Email == input.Email))
            errors.Add(new(nameof(input.Email), DuplicateEmail, Conflict: true));
        if (await db.Customers.AnyAsync(c => c.CustomerId != id && c.Phone == input.Phone))
            errors.Add(new(nameof(input.Phone), DuplicatePhone, Conflict: true));

        // Keeping the current owner is always allowed; choosing a new one must follow D6.
        if (input.OwnerId != existing?.OwnerId && !await scope.CanAssignToAsync(input.OwnerId))
            errors.Add(new(nameof(input.OwnerId), "You cannot assign customers to this user."));

        return errors;
    }

    // The unique indexes are the last line of defence if two saves race past ValidateAsync.
    async Task<FieldError?> SaveAsync()
    {
        try
        {
            await db.SaveChangesAsync();
            return null;
        }
        catch (DbUpdateException e) when (e.InnerException?.Message.Contains("IX_Customers_Email") == true)
        {
            return new(nameof(CustomerInput.Email), DuplicateEmail, Conflict: true);
        }
        catch (DbUpdateException e) when (e.InnerException?.Message.Contains("IX_Customers_Phone") == true)
        {
            return new(nameof(CustomerInput.Phone), DuplicatePhone, Conflict: true);
        }
    }

    static void Normalize(CustomerInput input)
    {
        input.CustomerName = input.CustomerName.Trim();
        input.Email = input.Email.Trim();
        input.Phone = input.Phone.Trim();
        input.CompanyName = Clean(input.CompanyName);
        input.Address = Clean(input.Address);
        input.City = Clean(input.City);
        input.State = Clean(input.State);
        input.Notes = Clean(input.Notes);
    }

    static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    static void Apply(CustomerInput input, Customer c)
    {
        c.CustomerName = input.CustomerName;
        c.Email = input.Email;
        c.Phone = input.Phone;
        c.CompanyName = input.CompanyName;
        c.Address = input.Address;
        c.City = input.City;
        c.State = input.State;
        c.Status = input.Status;
        c.Notes = input.Notes;
        c.OwnerId = input.OwnerId!;
    }

    static object Snapshot(Customer c) => new
    {
        c.CustomerCode, c.CustomerName, c.Email, c.Phone, c.CompanyName, c.Address, c.City, c.State,
        c.Status, c.Notes, c.OwnerId,
    };
}
