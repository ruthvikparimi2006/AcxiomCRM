using AcxiomCRM.Data;
using AcxiomCRM.Models;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Services;

// Lead business rules (LEAD-01..05, LEAD-07..09). Shared by the MVC controller and, in Step 11, the REST API.
// Conversion (LEAD-06) is completed in Step 8 once Opportunity Management exists; the pieces it needs are here.
public class LeadService(AppDbContext db, ScopeService scope, AuditService audit)
{
    const string Module = "Leads";

    // §16 #4 workflow. Converted is only reached through conversion (Step 8), never by editing.
    static readonly Dictionary<LeadStatus, LeadStatus[]> Transitions = new()
    {
        [LeadStatus.New] = [LeadStatus.Contacted, LeadStatus.Unqualified, LeadStatus.Lost],
        [LeadStatus.Contacted] = [LeadStatus.Qualified, LeadStatus.Unqualified, LeadStatus.Lost],
        [LeadStatus.Qualified] = [LeadStatus.Converted, LeadStatus.Lost],
    };

    // Statuses a user may pick when editing: the current one plus its manual next steps.
    public static List<LeadStatus> EditableStatuses(LeadStatus current) =>
        [current, .. Transitions.GetValueOrDefault(current, []).Where(s => s != LeadStatus.Converted)];

    public static bool CanConvert(Lead lead) => lead.Status == LeadStatus.Qualified;

    public async Task<IQueryable<Lead>> VisibleAsync()
    {
        var query = db.Leads.AsQueryable();
        return await scope.VisibleUserIdsAsync() is { } ids ? query.Where(l => ids.Contains(l.AssignedTo)) : query;
    }

    // Null when the lead doesn't exist, is deleted, or is outside the user's scope (callers answer 404).
    public async Task<Lead?> FindAsync(int id) => await (await VisibleAsync()).FirstOrDefaultAsync(l => l.LeadId == id);

    // Dropdown of leads in scope for linking follow-ups/activities.
    public async Task<List<SelectListItem>> OptionsAsync() =>
        await (await VisibleAsync())
            .OrderBy(l => l.LeadName)
            .Select(l => new SelectListItem(l.LeadName + " (" + l.LeadCode + ")", l.LeadId.ToString()))
            .ToListAsync();

    public async Task<(Lead? Lead, List<FieldError> Errors)> CreateAsync(LeadInput input)
    {
        Normalize(input);
        if (string.IsNullOrEmpty(input.AssignedTo)) input.AssignedTo = scope.UserId;

        var errors = new List<FieldError>();
        if (input.Status != LeadStatus.New)
            errors.Add(new(nameof(input.Status), "New leads start with status New."));
        if (!await scope.CanAssignToAsync(input.AssignedTo))
            errors.Add(new(nameof(input.AssignedTo), "You cannot assign leads to this user."));
        if (errors.Count > 0) return (null, errors);

        var lead = new Lead { CreatedDate = DateTime.UtcNow };
        Apply(input, lead);
        db.Leads.Add(lead);
        await db.SaveChangesAsync();

        await audit.LogAsync(Module, "Create", nameof(Lead), lead.LeadId.ToString(), newValue: Snapshot(lead));
        return (lead, []);
    }

    public async Task<List<FieldError>> UpdateAsync(Lead lead, LeadInput input)
    {
        Normalize(input);
        if (string.IsNullOrEmpty(input.AssignedTo)) input.AssignedTo = lead.AssignedTo;

        var errors = new List<FieldError>();
        if (input.Status != lead.Status)
        {
            if (input.Status == LeadStatus.Converted)
                errors.Add(new(nameof(input.Status), "Only qualified leads can be converted, using Convert."));
            else if (!EditableStatuses(lead.Status).Contains(input.Status!.Value))
                errors.Add(new(nameof(input.Status), $"A {lead.Status} lead cannot be changed to {input.Status}."));
        }
        if (input.AssignedTo != lead.AssignedTo && !await scope.CanAssignToAsync(input.AssignedTo))
            errors.Add(new(nameof(input.AssignedTo), "You cannot assign leads to this user."));
        if (errors.Count > 0) return errors;

        var before = Snapshot(lead);
        var oldStatus = lead.Status;
        Apply(input, lead);
        lead.ModifiedDate = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var id = lead.LeadId.ToString();
        await audit.LogAsync(Module, "Update", nameof(Lead), id, before, Snapshot(lead));
        if (oldStatus != lead.Status)
            await audit.LogAsync(Module, "StatusChanged", nameof(Lead), id, new { Status = oldStatus }, new { lead.Status });
        return [];
    }

    // §16 #8: soft delete; the row stays for history and is hidden by the global query filter.
    public async Task DeleteAsync(Lead lead)
    {
        lead.IsDeleted = true;
        lead.ModifiedDate = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.LogAsync(Module, "Delete", nameof(Lead), lead.LeadId.ToString(), Snapshot(lead));
    }

    // §16 #7 (used by conversion in Step 8): reuse an existing customer with the same Email or Phone
    // instead of creating a duplicate. Searches all customers, as the uniqueness rule does.
    public Task<Customer?> FindMatchingCustomerAsync(Lead lead) =>
        db.Customers.FirstOrDefaultAsync(c =>
            (lead.Email != null && c.Email == lead.Email) || (lead.Phone != null && c.Phone == lead.Phone));

    static void Normalize(LeadInput input)
    {
        input.LeadName = input.LeadName.Trim();
        input.Email = Clean(input.Email);
        input.Phone = Clean(input.Phone);
        input.CompanyName = Clean(input.CompanyName);
        input.Notes = Clean(input.Notes);
    }

    static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    static void Apply(LeadInput input, Lead l)
    {
        l.LeadName = input.LeadName;
        l.Email = input.Email;
        l.Phone = input.Phone;
        l.CompanyName = input.CompanyName;
        l.Source = input.Source;
        l.Status = input.Status!.Value;
        l.Priority = input.Priority;
        l.ExpectedValue = input.ExpectedValue;
        l.Notes = input.Notes;
        l.AssignedTo = input.AssignedTo!;
    }

    static object Snapshot(Lead l) => new
    {
        l.LeadCode, l.LeadName, l.Email, l.Phone, l.CompanyName, l.Source, l.Status, l.Priority,
        l.ExpectedValue, l.Notes, l.AssignedTo,
    };
}
