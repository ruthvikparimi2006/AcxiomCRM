using System.ComponentModel.DataAnnotations;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AcxiomCRM.ViewModels;

// The fields a user may submit for an activity (ACT-01, ACT-02).
public class ActivityInput
{
    [Required(ErrorMessage = "Type is required.")]
    [EnumDataType(typeof(ActivityType), ErrorMessage = "Select a valid type.")]
    [Display(Name = "Type")]
    public ActivityType? ActivityType { get; set; }

    [Required(ErrorMessage = "Subject is required.")]
    [StringLength(200, ErrorMessage = "Subject cannot exceed 200 characters.")]
    public string Subject { get; set; } = "";

    [StringLength(2000, ErrorMessage = "Description cannot exceed 2000 characters.")]
    public string? Description { get; set; }

    // Entered and shown as local wall-clock time.
    [Required(ErrorMessage = "Activity date is required.")]
    [Display(Name = "Activity date")]
    public DateTime? ActivityDate { get; set; }

    // At least one of these (checked in ActivityService).
    [Display(Name = "Customer")]
    public int? CustomerId { get; set; }

    [Display(Name = "Lead")]
    public int? LeadId { get; set; }

    [Required(ErrorMessage = "Status is required.")]
    [EnumDataType(typeof(ActivityStatus), ErrorMessage = "Select a valid status.")]
    public ActivityStatus? Status { get; set; } = Models.ActivityStatus.Planned;

    [Display(Name = "Assigned To")]
    public string? AssignedTo { get; set; }
}

public class ActivityFormViewModel : ActivityInput
{
    public List<SelectListItem> Customers { get; set; } = [];
    public List<SelectListItem> Leads { get; set; } = [];
    public List<SelectListItem> Assignees { get; set; } = [];
}

public record ActivityListItem(int Id, ActivityType Type, string Subject, DateTime Date, ActivityStatus Status,
    string? CustomerName, string? LeadName, string AssignedName);

public class ActivityIndexViewModel
{
    public ActivityType? Type { get; set; }
    public ActivityStatus? Status { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public string? AssignedTo { get; set; }
    public List<SelectListItem> Users { get; set; } = [];
    public PagedList<ActivityListItem> Activities { get; set; } = new();
}

public class ActivityDetailsViewModel
{
    public Activity Activity { get; set; } = null!;
    public string? CustomerName { get; set; }
    public string? LeadName { get; set; }
    public string? AssignedName { get; set; }
    public List<HistoryEntry> History { get; set; } = [];
}
