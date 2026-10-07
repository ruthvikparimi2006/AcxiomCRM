using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AcxiomCRM.Models;

public class Opportunity
{
    public int OpportunityId { get; set; }

    [MaxLength(150)]
    public string OpportunityName { get; set; } = "";

    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public int? LeadId { get; set; }
    public Lead? Lead { get; set; }

    public decimal Amount { get; set; }

    public OpportunityStage Stage { get; set; } = OpportunityStage.Qualification;

    public int Probability { get; set; }

    public DateOnly ExpectedCloseDate { get; set; }

    public OpportunityStatus Status { get; set; } = OpportunityStatus.Open;

    public LeadSource? Source { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    public string AssignedTo { get; set; } = "";
    [ForeignKey(nameof(AssignedTo))]
    public ApplicationUser? AssignedUser { get; set; }

    // Set when the stage becomes Won or Lost (§16 #12).
    public DateTime? ClosedDate { get; set; }

    public DateTime? ModifiedDate { get; set; }

    public bool IsDeleted { get; set; }
}
