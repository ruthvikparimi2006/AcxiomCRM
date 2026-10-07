using System.ComponentModel.DataAnnotations;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AcxiomCRM.ViewModels;

// The fields a user may submit for a follow-up (FUP-03). Status changes go through the Complete/Missed/
// Cancel/Reschedule actions, never this form.
public class FollowUpInput
{
    // Exactly one of these (checked in FollowUpService).
    [Display(Name = "Customer")]
    public int? CustomerId { get; set; }

    [Display(Name = "Lead")]
    public int? LeadId { get; set; }

    [Display(Name = "Opportunity")]
    public int? OpportunityId { get; set; }

    [Required(ErrorMessage = "Follow-up date is required.")]
    [NotBeforeToday(ErrorMessage = "Follow-up date cannot be earlier than today.")]
    [Display(Name = "Follow-up date")]
    public DateOnly? FollowUpDate { get; set; }

    [Required(ErrorMessage = "Type is required.")]
    [EnumDataType(typeof(ActivityType), ErrorMessage = "Select a valid type.")]
    [Display(Name = "Type")]
    public ActivityType? FollowUpType { get; set; }

    [Required(ErrorMessage = "Subject is required.")]
    [StringLength(200, ErrorMessage = "Subject cannot exceed 200 characters.")]
    public string Subject { get; set; } = "";

    [StringLength(2000, ErrorMessage = "Remarks cannot exceed 2000 characters.")]
    public string? Remarks { get; set; }

    [Display(Name = "Assigned To")]
    public string? AssignedTo { get; set; }
}

public class FollowUpFormViewModel : FollowUpInput
{
    public List<SelectListItem> Customers { get; set; } = [];
    public List<SelectListItem> Leads { get; set; } = [];
    public List<SelectListItem> Opportunities { get; set; } = [];
    public List<SelectListItem> Assignees { get; set; } = [];
}

public record FollowUpListItem(int Id, DateOnly Date, ActivityType Type, string Subject, FollowUpStatus Status,
    int? CustomerId, int? LeadId, int? OpportunityId, string? RelatedName, string AssignedName)
{
    public bool IsOverdue => Status == FollowUpStatus.Planned && Date < NotBeforeTodayAttribute.Today;
}

public class FollowUpIndexViewModel
{
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public FollowUpStatus? Status { get; set; }
    public string? AssignedTo { get; set; }
    public string? Related { get; set; }
    public List<SelectListItem> Users { get; set; } = [];
    public PagedList<FollowUpListItem> FollowUps { get; set; } = new();
}

// FUP-05 reminders (D7): overdue = Planned and before today; upcoming = Planned, today through 7 days ahead.
public record Reminders(List<FollowUpListItem> Overdue, int OverdueCount, List<FollowUpListItem> Upcoming, int UpcomingCount)
{
    public int Total => OverdueCount + UpcomingCount;
}

public class FollowUpDetailsViewModel
{
    public FollowUp FollowUp { get; set; } = null!;
    public string? RelatedName { get; set; }
    public string? AssignedName { get; set; }
    public List<HistoryEntry> History { get; set; } = [];
}
