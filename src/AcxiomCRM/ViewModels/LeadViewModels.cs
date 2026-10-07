using System.ComponentModel.DataAnnotations;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AcxiomCRM.ViewModels;

// The fields a user may submit for a lead (LEAD-02, LEAD-07). Code, conversion link and dates are never bound.
public class LeadInput
{
    [Required(ErrorMessage = "Lead Name is required.")]
    [StringLength(150, ErrorMessage = "Lead Name cannot exceed 150 characters.")]
    [Display(Name = "Lead Name")]
    public string LeadName { get; set; } = "";

    [StringLength(256, ErrorMessage = "Email cannot exceed 256 characters.")]
    [RegularExpression(ValidationRules.EmailPattern, ErrorMessage = ValidationRules.EmailMessage)]
    public string? Email { get; set; }

    [RegularExpression(ValidationRules.PhonePattern, ErrorMessage = ValidationRules.PhoneMessage)]
    public string? Phone { get; set; }

    [StringLength(150, ErrorMessage = "Company cannot exceed 150 characters.")]
    [Display(Name = "Company")]
    public string? CompanyName { get; set; }

    [EnumDataType(typeof(LeadSource), ErrorMessage = "Select a valid source.")]
    public LeadSource? Source { get; set; }

    // LEAD-03 / VAL-20: mandatory, and only a valid status reachable from the current one (checked in LeadService).
    [Required(ErrorMessage = "Status is required.")]
    [EnumDataType(typeof(LeadStatus), ErrorMessage = "Select a valid status.")]
    public LeadStatus? Status { get; set; } = LeadStatus.New;

    [EnumDataType(typeof(LeadPriority), ErrorMessage = "Select a valid priority.")]
    public LeadPriority? Priority { get; set; }

    // VAL-21 (§16): 0 to 100,000,000.
    [Range(typeof(decimal), "0", "100000000", ErrorMessage = "Expected Value must be between 0 and 100,000,000.")]
    [RegularExpression(ValidationRules.MoneyPattern, ErrorMessage = ValidationRules.MoneyMessage)]
    [Display(Name = "Expected Value")]
    public decimal? ExpectedValue { get; set; }

    [StringLength(2000, ErrorMessage = "Notes cannot exceed 2000 characters.")]
    public string? Notes { get; set; }

    // Empty means "me". Checked against ScopeService.AssignableUsersAsync (D6).
    [Display(Name = "Assigned To")]
    public string? AssignedTo { get; set; }
}

public class LeadFormViewModel : LeadInput
{
    public List<SelectListItem> Assignees { get; set; } = [];
    public List<LeadStatus> AllowedStatuses { get; set; } = [];
}

public record LeadListItem(int Id, string Code, string Name, string? Company, LeadStatus Status, LeadPriority? Priority,
    LeadSource? Source, decimal? ExpectedValue, string AssignedName);

public class LeadIndexViewModel
{
    public string? Search { get; set; }
    public LeadStatus? Status { get; set; }
    public string? AssignedTo { get; set; }
    public List<SelectListItem> Users { get; set; } = [];
    public PagedList<LeadListItem> Leads { get; set; } = new();
}

public class LeadDetailsViewModel
{
    public Lead Lead { get; set; } = null!;
    public string? AssignedName { get; set; }
    public List<HistoryEntry> History { get; set; } = [];
}
