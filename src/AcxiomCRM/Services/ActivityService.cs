using AcxiomCRM.Data;
using AcxiomCRM.Models;
using AcxiomCRM.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Services;

// Activity Management (ACT-01..06): calls, meetings, emails and tasks against customers and leads.
public class ActivityService(AppDbContext db, ScopeService scope, AuditService audit, CustomerService customers, LeadService leads)
{
    const string Module = "Activities";

    public async Task<IQueryable<Activity>> VisibleAsync()
    {
        // An activity that only belonged to a soft-deleted lead disappears with it.
        var query = db.Activities.Where(a => a.CustomerId != null || a.Lead != null);
        return await scope.VisibleUserIdsAsync() is { } ids ? query.Where(a => ids.Contains(a.AssignedTo)) : query;
    }

    public async Task<Activity?> FindAsync(int id) =>
        await (await VisibleAsync()).FirstOrDefaultAsync(a => a.ActivityId == id);

    public async Task<(Activity? Activity, List<FieldError> Errors)> CreateAsync(ActivityInput input)
    {
        Normalize(input);
        if (string.IsNullOrEmpty(input.AssignedTo)) input.AssignedTo = scope.UserId;

        var errors = await ValidateAsync(input, existing: null);
        if (errors.Count > 0) return (null, errors);

        var activity = new Activity();
        Apply(input, activity);
        db.Activities.Add(activity);
        await db.SaveChangesAsync();

        await audit.LogAsync(Module, "Create", nameof(Activity), activity.ActivityId.ToString(), newValue: Snapshot(activity));
        return (activity, []);
    }

    public async Task<List<FieldError>> UpdateAsync(Activity activity, ActivityInput input)
    {
        Normalize(input);
        if (string.IsNullOrEmpty(input.AssignedTo)) input.AssignedTo = activity.AssignedTo;

        var errors = await ValidateAsync(input, activity);
        if (errors.Count > 0) return errors;

        var before = Snapshot(activity);
        var oldStatus = activity.Status;
        Apply(input, activity);
        await db.SaveChangesAsync();

        var id = activity.ActivityId.ToString();
        await audit.LogAsync(Module, "Update", nameof(Activity), id, before, Snapshot(activity));
        if (oldStatus != activity.Status)
            await audit.LogAsync(Module, "StatusChanged", nameof(Activity), id, new { Status = oldStatus }, new { activity.Status });
        return [];
    }

    public async Task DeleteAsync(Activity activity)
    {
        activity.IsDeleted = true;
        await db.SaveChangesAsync();
        await audit.LogAsync(Module, "Delete", nameof(Activity), activity.ActivityId.ToString(), Snapshot(activity));
    }

    async Task<List<FieldError>> ValidateAsync(ActivityInput input, Activity? existing)
    {
        var errors = new List<FieldError>();

        // VAL-09: at least one related record; each one given must be visible. Unchanged links are not re-checked.
        if (input.CustomerId is null && input.LeadId is null)
            errors.Add(new(nameof(input.CustomerId), "Choose a customer, a lead, or both."));
        if (input.CustomerId is { } c && c != existing?.CustomerId && await customers.FindAsync(c) is null)
            errors.Add(new(nameof(input.CustomerId), "Select a valid customer."));
        if (input.LeadId is { } l && l != existing?.LeadId && await leads.FindAsync(l) is null)
            errors.Add(new(nameof(input.LeadId), "Select a valid lead."));

        if (input.AssignedTo != existing?.AssignedTo && !await scope.CanAssignToAsync(input.AssignedTo))
            errors.Add(new(nameof(input.AssignedTo), "You cannot assign activities to this user."));
        return errors;
    }

    static void Normalize(ActivityInput input)
    {
        input.Subject = input.Subject.Trim();
        input.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
    }

    static void Apply(ActivityInput input, Activity a)
    {
        a.ActivityType = input.ActivityType!.Value;
        a.Subject = input.Subject;
        a.Description = input.Description;
        a.ActivityDate = input.ActivityDate!.Value;
        a.CustomerId = input.CustomerId;
        a.LeadId = input.LeadId;
        a.Status = input.Status!.Value;
        a.AssignedTo = input.AssignedTo!;
    }

    static object Snapshot(Activity a) => new
    {
        a.ActivityType, a.Subject, a.Description, a.ActivityDate, a.CustomerId, a.LeadId, a.Status, a.AssignedTo,
    };
}
