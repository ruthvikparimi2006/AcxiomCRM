using AcxiomCRM.Data;
using AcxiomCRM.Models;
using AcxiomCRM.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Services;

// Follow-up rules (FUP-01..09): follow-ups against customers, leads and opportunities.
public class FollowUpService(
    AppDbContext db, ScopeService scope, AuditService audit, CustomerService customers, LeadService leads,
    OpportunityService opportunities)
{
    const string Module = "FollowUps";
    const string RelatedMessage = "Choose one related record: a customer, a lead or an opportunity.";
    public const int UpcomingDays = 7; // D7

    static DateOnly Today => NotBeforeTodayAttribute.Today;

    public async Task<IQueryable<FollowUp>> VisibleAsync()
    {
        // Follow-ups of a soft-deleted lead or opportunity disappear with it (the navigation is filtered out).
        var query = db.FollowUps.Where(f => (f.LeadId == null || f.Lead != null)
                                            && (f.OpportunityId == null || f.Opportunity != null));
        return await scope.VisibleUserIdsAsync() is { } ids ? query.Where(f => ids.Contains(f.AssignedTo)) : query;
    }

    public async Task<FollowUp?> FindAsync(int id) =>
        await (await VisibleAsync()).FirstOrDefaultAsync(f => f.FollowUpId == id);

    public static IQueryable<FollowUpListItem> Project(IQueryable<FollowUp> query) =>
        query.Select(f => new FollowUpListItem(f.FollowUpId, f.FollowUpDate, f.FollowUpType, f.Subject, f.Status,
            f.CustomerId, f.LeadId, f.OpportunityId,
            f.Customer != null ? f.Customer.CustomerName
                : f.Lead != null ? f.Lead.LeadName
                : f.Opportunity != null ? f.Opportunity.OpportunityName : null,
            f.AssignedUser!.Name));

    // FUP-05 / D7: planned follow-ups that are overdue, or due today through UpcomingDays ahead, in the user's scope.
    public async Task<Reminders> RemindersAsync(int? take = null)
    {
        var planned = (await VisibleAsync()).Where(f => f.Status == FollowUpStatus.Planned);
        var today = Today;
        var horizon = today.AddDays(UpcomingDays);
        var overdue = planned.Where(f => f.FollowUpDate < today).OrderBy(f => f.FollowUpDate).ThenBy(f => f.FollowUpId);
        var upcoming = planned.Where(f => f.FollowUpDate >= today && f.FollowUpDate <= horizon)
            .OrderBy(f => f.FollowUpDate).ThenBy(f => f.FollowUpId);

        return new Reminders(
            await Project(take is { } t ? overdue.Take(t) : overdue).ToListAsync(), await overdue.CountAsync(),
            await Project(take is { } u ? upcoming.Take(u) : upcoming).ToListAsync(), await upcoming.CountAsync());
    }

    public async Task<(FollowUp? FollowUp, List<FieldError> Errors)> CreateAsync(FollowUpInput input)
    {
        Normalize(input);
        if (string.IsNullOrEmpty(input.AssignedTo)) input.AssignedTo = scope.UserId;

        var errors = await ValidateAsync(input, existing: null);
        if (errors.Count > 0) return (null, errors);

        var followUp = new FollowUp { Status = FollowUpStatus.Planned };
        Apply(input, followUp);
        db.FollowUps.Add(followUp);
        await db.SaveChangesAsync();

        await audit.LogAsync(Module, "Create", nameof(FollowUp), followUp.FollowUpId.ToString(), newValue: Snapshot(followUp));
        return (followUp, []);
    }

    // Only planned follow-ups can be edited. A date change is a reschedule and is audited as one (FUP-07).
    public async Task<List<FieldError>> UpdateAsync(FollowUp followUp, FollowUpInput input)
    {
        if (followUp.Status != FollowUpStatus.Planned)
            return [new("", $"This follow-up is {followUp.Status} and can no longer be edited.")];

        Normalize(input);
        if (string.IsNullOrEmpty(input.AssignedTo)) input.AssignedTo = followUp.AssignedTo;

        var errors = await ValidateAsync(input, followUp);
        if (errors.Count > 0) return errors;

        var before = Snapshot(followUp);
        var oldDate = followUp.FollowUpDate;
        Apply(input, followUp);
        if (oldDate != followUp.FollowUpDate) await TouchRelatedAsync(followUp);
        await db.SaveChangesAsync();

        var id = followUp.FollowUpId.ToString();
        await audit.LogAsync(Module, "Update", nameof(FollowUp), id, before, Snapshot(followUp));
        if (oldDate != followUp.FollowUpDate)
            await audit.LogAsync(Module, "Rescheduled", nameof(FollowUp), id,
                new { FollowUpDate = oldDate }, new { followUp.FollowUpDate });
        return [];
    }

    // FUP-04: Planned -> Completed, Missed or Cancelled. The related customer/lead is updated too.
    public async Task<FieldError?> SetStatusAsync(FollowUp followUp, FollowUpStatus status)
    {
        if (status == FollowUpStatus.Planned)
            return new("", "Use Reschedule to plan a follow-up again.");
        if (followUp.Status != FollowUpStatus.Planned)
            return new("", $"This follow-up is already {followUp.Status}.");

        followUp.Status = status;
        await TouchRelatedAsync(followUp);
        await db.SaveChangesAsync();
        await audit.LogAsync(Module, status.ToString(), nameof(FollowUp), followUp.FollowUpId.ToString(),
            new { Status = FollowUpStatus.Planned }, new { followUp.Status });
        return null;
    }

    public async Task<FieldError?> RescheduleAsync(FollowUp followUp, DateOnly? date)
    {
        if (followUp.Status != FollowUpStatus.Planned)
            return new("", $"This follow-up is {followUp.Status} and cannot be rescheduled.");
        if (date is null)
            return new("newDate", "Follow-up date is required.");
        if (date < Today)
            return new("newDate", "Follow-up date cannot be earlier than today.");
        if (date == followUp.FollowUpDate)
            return new("newDate", "Choose a different date to reschedule.");

        var oldDate = followUp.FollowUpDate;
        followUp.FollowUpDate = date.Value;
        await TouchRelatedAsync(followUp);
        await db.SaveChangesAsync();
        await audit.LogAsync(Module, "Rescheduled", nameof(FollowUp), followUp.FollowUpId.ToString(),
            new { FollowUpDate = oldDate }, new { followUp.FollowUpDate });
        return null;
    }

    public async Task DeleteAsync(FollowUp followUp)
    {
        followUp.IsDeleted = true;
        await db.SaveChangesAsync();
        await audit.LogAsync(Module, "Delete", nameof(FollowUp), followUp.FollowUpId.ToString(), Snapshot(followUp));
    }

    async Task<List<FieldError>> ValidateAsync(FollowUpInput input, FollowUp? existing)
    {
        var errors = new List<FieldError>();

        // VAL-09: exactly one related record, and one the user can see. An unchanged link is not re-checked.
        if (new[] { input.CustomerId, input.LeadId, input.OpportunityId }.Count(id => id is not null) != 1)
            errors.Add(new(nameof(input.CustomerId), RelatedMessage));
        else if (input.CustomerId is { } c && c != existing?.CustomerId && await customers.FindAsync(c) is null)
            errors.Add(new(nameof(input.CustomerId), "Select a valid customer."));
        else if (input.LeadId is { } l && l != existing?.LeadId && await leads.FindAsync(l) is null)
            errors.Add(new(nameof(input.LeadId), "Select a valid lead."));
        else if (input.OpportunityId is { } o && o != existing?.OpportunityId && await opportunities.FindAsync(o) is null)
            errors.Add(new(nameof(input.OpportunityId), "Select a valid opportunity."));

        // FUP-06 / D10: repeated here so a tampered request can't skip it; no role is exempt.
        if (input.FollowUpDate < Today)
            errors.Add(new(nameof(input.FollowUpDate), "Follow-up date cannot be earlier than today."));

        if (input.AssignedTo != existing?.AssignedTo && !await scope.CanAssignToAsync(input.AssignedTo))
            errors.Add(new(nameof(input.AssignedTo), "You cannot assign follow-ups to this user."));
        return errors;
    }

    async Task TouchRelatedAsync(FollowUp f)
    {
        var now = DateTime.UtcNow;
        if (f.CustomerId is { } c && await db.Customers.FindAsync(c) is { } customer) customer.ModifiedDate = now;
        if (f.LeadId is { } l && await db.Leads.FindAsync(l) is { } lead) lead.ModifiedDate = now;
        if (f.OpportunityId is { } o && await db.Opportunities.FindAsync(o) is { } opportunity) opportunity.ModifiedDate = now;
    }

    static void Normalize(FollowUpInput input)
    {
        input.Subject = input.Subject.Trim();
        input.Remarks = string.IsNullOrWhiteSpace(input.Remarks) ? null : input.Remarks.Trim();
    }

    static void Apply(FollowUpInput input, FollowUp f)
    {
        f.CustomerId = input.CustomerId;
        f.LeadId = input.LeadId;
        f.OpportunityId = input.OpportunityId;
        f.FollowUpDate = input.FollowUpDate!.Value;
        f.FollowUpType = input.FollowUpType!.Value;
        f.Subject = input.Subject;
        f.Remarks = input.Remarks;
        f.AssignedTo = input.AssignedTo!;
    }

    static object Snapshot(FollowUp f) => new
    {
        f.CustomerId, f.LeadId, f.OpportunityId, f.FollowUpDate, f.FollowUpType, f.Subject, f.Remarks, f.Status, f.AssignedTo,
    };
}
