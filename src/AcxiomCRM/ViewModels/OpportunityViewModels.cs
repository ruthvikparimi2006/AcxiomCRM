using System.ComponentModel.DataAnnotations;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AcxiomCRM.ViewModels;

// The fields a user may submit for an opportunity (OPP-02, §16 #6). Status, ClosedDate and dates are derived.
public class OpportunityInput
{
    const string OpenStages = "Qualification,Proposal,Negotiation";

    [Required(ErrorMessage = "Opportunity Name is required.")]
    [StringLength(150, ErrorMessage = "Opportunity Name cannot exceed 150 characters.")]
    [Display(Name = "Opportunity Name")]
    public string OpportunityName { get; set; } = "";

    [Required(ErrorMessage = "Customer is required.")]
    [Display(Name = "Customer")]
    public int? CustomerId { get; set; }

    [Display(Name = "Lead")]
    public int? LeadId { get; set; }

    // OPP-04 / VAL-13: never negative; greater than 0 while open (and always on create, checked in OpportunityService).
    [Required(ErrorMessage = "Amount is required.")]
    [Range(typeof(decimal), "0", "9999999999999999.99", ErrorMessage = "Amount cannot be negative.")]
    [RegularExpression(ValidationRules.MoneyPattern, ErrorMessage = ValidationRules.MoneyMessage)]
    [GreaterThanZero(ErrorMessage = "Opportunity Amount must be greater than 0.", DependsOn = nameof(Stage), Values = OpenStages)]
    public decimal? Amount { get; set; }

    [Required(ErrorMessage = "Stage is required.")]
    [EnumDataType(typeof(OpportunityStage), ErrorMessage = "Select a valid stage.")]
    public OpportunityStage? Stage { get; set; } = OpportunityStage.Qualification;

    // OPP-05 / VAL-14.
    [Required(ErrorMessage = "Probability is required.")]
    [Range(0, 100, ErrorMessage = "Probability must be between 0 and 100.")]
    [Display(Name = "Probability (%)")]
    public int? Probability { get; set; }

    // OPP-06 / VAL-15: only while the opportunity is open.
    [Required(ErrorMessage = "Expected Close Date is required.")]
    [NotBeforeToday(ErrorMessage = "Expected Close Date cannot be in the past.", DependsOn = nameof(Stage), Values = OpenStages)]
    [Display(Name = "Expected Close Date")]
    public DateOnly? ExpectedCloseDate { get; set; }

    [EnumDataType(typeof(LeadSource), ErrorMessage = "Select a valid source.")]
    public LeadSource? Source { get; set; }

    [StringLength(2000, ErrorMessage = "Notes cannot exceed 2000 characters.")]
    public string? Notes { get; set; }

    [Display(Name = "Assigned To")]
    public string? AssignedTo { get; set; }
}

public class OpportunityFormViewModel : OpportunityInput
{
    public List<SelectListItem> Customers { get; set; } = [];
    public List<SelectListItem> Leads { get; set; } = [];
    public List<SelectListItem> Assignees { get; set; } = [];
}

public record OpportunityListItem(int Id, string Name, int CustomerId, string CustomerName, OpportunityStage Stage,
    OpportunityStatus Status, decimal Amount, int Probability, decimal Weighted, DateOnly ExpectedCloseDate, string AssignedName);

public class OpportunityIndexViewModel
{
    public string? Search { get; set; }
    public string? Customer { get; set; }
    public OpportunityStage? Stage { get; set; }
    public OpportunityStatus? Status { get; set; }
    public PagedList<OpportunityListItem> Opportunities { get; set; } = new();
}

public record PipelineStage(OpportunityStage Stage, int Count, decimal Amount, decimal Weighted, List<OpportunityListItem> Items);

public class OpportunityDetailsViewModel
{
    public Opportunity Opportunity { get; set; } = null!;
    public string? CustomerName { get; set; }
    public string? LeadName { get; set; }
    public string? AssignedName { get; set; }
    public List<HistoryEntry> History { get; set; } = [];
}

// LEAD-06 / §16 #7. Customer fields are used only when no existing customer matches the lead;
// opportunity fields only when CreateOpportunity is ticked. The controller drops the unused part's validation.
public class ConvertLeadViewModel
{
    public CustomerInput Customer { get; set; } = new();
    [Display(Name = "Also create an opportunity")]
    [Required(ErrorMessage = "Choose whether to create an opportunity.")]
    public bool CreateOpportunity { get; set; } = true;
    public OpportunityInput Opportunity { get; set; } = new();

    // Display only: never bound or validated (a non-nullable reference would otherwise be implicitly required).
    [BindNever, ValidateNever] public Lead Lead { get; set; } = null!;
    [BindNever, ValidateNever] public Customer? MatchedCustomer { get; set; }
}
