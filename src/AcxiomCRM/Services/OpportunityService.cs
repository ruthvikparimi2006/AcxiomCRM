using AcxiomCRM.Data;
using AcxiomCRM.Models;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Services;

// Opportunity Management (OPP-01..09). Shared by the MVC controller, lead conversion and, in Step 11, the REST API.
public class OpportunityService(AppDbContext db, ScopeService scope, AuditService audit, CustomerService customers, LeadService leads)
{
    const string Module = "Opportunities";

    // §16 #4/#5: open = active = these stages; Status is derived from Stage and never edited directly.
    public static bool IsOpen(OpportunityStage stage) =>
        stage is OpportunityStage.Qualification or OpportunityStage.Proposal or OpportunityStage.Negotiation;

    public static OpportunityStatus StatusFor(OpportunityStage stage) => stage switch
    {
        OpportunityStage.Won => OpportunityStatus.Won,
        OpportunityStage.Lost => OpportunityStatus.Lost,
        _ => OpportunityStatus.Open,
    };

    static DateOnly Today => NotBeforeTodayAttribute.Today;

    public async Task<IQueryable<Opportunity>> VisibleAsync()
    {
        var query = db.Opportunities.AsQueryable();
        return await scope.VisibleUserIdsAsync() is { } ids ? query.Where(o => ids.Contains(o.AssignedTo)) : query;
    }

    public async Task<Opportunity?> FindAsync(int id) =>
        await (await VisibleAsync()).FirstOrDefaultAsync(o => o.OpportunityId == id);

    // OPP-07: weighted value = Amount x Probability / 100.
    public static IQueryable<OpportunityListItem> Project(IQueryable<Opportunity> query) =>
        query.Select(o => new OpportunityListItem(o.OpportunityId, o.OpportunityName, o.CustomerId, o.Customer!.CustomerName,
            o.Stage, o.Status, o.Amount, o.Probability, o.Amount * o.Probability / 100m, o.ExpectedCloseDate,
            o.AssignedUser!.Name));

    // Dropdown of opportunities in scope for linking follow-ups (FUP-02).
    public async Task<List<SelectListItem>> OptionsAsync() =>
        await (await VisibleAsync())
            .OrderBy(o => o.OpportunityName)
            .Select(o => new SelectListItem(o.OpportunityName + " — " + o.Customer!.CustomerName, o.OpportunityId.ToString()))
            .ToListAsync();

    public async Task<(Opportunity? Opportunity, List<FieldError> Errors)> CreateAsync(OpportunityInput input)
    {
        Normalize(input);
        if (string.IsNullOrEmpty(input.AssignedTo)) input.AssignedTo = scope.UserId;

        var errors = await ValidateAsync(input, existing: null);
        if (errors.Count > 0) return (null, errors);

        var opportunity = new Opportunity { CreatedDate = DateTime.UtcNow };
        Apply(input, opportunity);
        if (!IsOpen(opportunity.Stage)) opportunity.ClosedDate = DateTime.UtcNow;
        db.Opportunities.Add(opportunity);
        await db.SaveChangesAsync();

        await audit.LogAsync(Module, "Create", nameof(Opportunity), opportunity.OpportunityId.ToString(), newValue: Snapshot(opportunity));
        return (opportunity, []);
    }

    public async Task<List<FieldError>> UpdateAsync(Opportunity opportunity, OpportunityInput input)
    {
        Normalize(input);
        if (string.IsNullOrEmpty(input.AssignedTo)) input.AssignedTo = opportunity.AssignedTo;

        var errors = await ValidateAsync(input, opportunity);
        if (errors.Count > 0) return errors;

        var before = Snapshot(opportunity);
        var oldStage = opportunity.Stage;
        Apply(input, opportunity);
        opportunity.ModifiedDate = DateTime.UtcNow;
        // OPP-03: the outcome date is captured when the deal is marked Won/Lost, and cleared if it is reopened.
        if (oldStage != opportunity.Stage)
            opportunity.ClosedDate = IsOpen(opportunity.Stage) ? null : DateTime.UtcNow;
        await db.SaveChangesAsync();

        var id = opportunity.OpportunityId.ToString();
        await audit.LogAsync(Module, "Update", nameof(Opportunity), id, before, Snapshot(opportunity));
        if (oldStage != opportunity.Stage)
            await audit.LogAsync(Module, "StageChanged", nameof(Opportunity), id,
                new { Stage = oldStage, Status = StatusFor(oldStage) }, new { opportunity.Stage, opportunity.Status });
        return [];
    }

    public async Task DeleteAsync(Opportunity opportunity)
    {
        opportunity.IsDeleted = true;
        opportunity.ModifiedDate = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.LogAsync(Module, "Delete", nameof(Opportunity), opportunity.OpportunityId.ToString(), Snapshot(opportunity));
    }

    // Business rules repeated on the server so tampered requests (and the API) cannot skip them.
    async Task<List<FieldError>> ValidateAsync(OpportunityInput input, Opportunity? existing)
    {
        var errors = new List<FieldError>();
        var open = IsOpen(input.Stage!.Value);

        if (input.Amount < 0)
            errors.Add(new(nameof(input.Amount), "Amount cannot be negative."));
        else if ((open || existing is null) && input.Amount <= 0)
            errors.Add(new(nameof(input.Amount), "Opportunity Amount must be greater than 0."));

        if (input.Probability is < 0 or > 100)
            errors.Add(new(nameof(input.Probability), "Probability must be between 0 and 100."));

        if (open && input.ExpectedCloseDate < Today)
            errors.Add(new(nameof(input.ExpectedCloseDate), "Expected Close Date cannot be in the past."));

        // VAL-09: linked records must exist and be visible. Unchanged links are not re-checked.
        if (input.CustomerId != existing?.CustomerId && await customers.FindAsync(input.CustomerId!.Value) is null)
            errors.Add(new(nameof(input.CustomerId), "Select a valid customer."));
        if (input.LeadId is { } leadId && leadId != existing?.LeadId && await leads.FindAsync(leadId) is null)
            errors.Add(new(nameof(input.LeadId), "Select a valid lead."));

        if (input.AssignedTo != existing?.AssignedTo && !await scope.CanAssignToAsync(input.AssignedTo))
            errors.Add(new(nameof(input.AssignedTo), "You cannot assign opportunities to this user."));
        return errors;
    }

    static void Normalize(OpportunityInput input)
    {
        input.OpportunityName = input.OpportunityName.Trim();
        input.Notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim();
    }

    static void Apply(OpportunityInput input, Opportunity o)
    {
        o.OpportunityName = input.OpportunityName;
        o.CustomerId = input.CustomerId!.Value;
        o.LeadId = input.LeadId;
        o.Amount = input.Amount!.Value;
        o.Stage = input.Stage!.Value;
        o.Status = StatusFor(o.Stage);
        o.Probability = input.Probability!.Value;
        o.ExpectedCloseDate = input.ExpectedCloseDate!.Value;
        o.Source = input.Source;
        o.Notes = input.Notes;
        o.AssignedTo = input.AssignedTo!;
    }

    static object Snapshot(Opportunity o) => new
    {
        o.OpportunityName, o.CustomerId, o.LeadId, o.Amount, o.Stage, o.Status, o.Probability, o.ExpectedCloseDate,
        o.Source, o.Notes, o.AssignedTo, o.ClosedDate,
    };
}
