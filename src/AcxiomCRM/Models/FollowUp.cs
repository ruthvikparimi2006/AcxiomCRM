using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AcxiomCRM.Models;

public class FollowUp
{
    public int FollowUpId { get; set; }

    // Exactly one of these is set (database check constraint).
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public int? LeadId { get; set; }
    public Lead? Lead { get; set; }

    public int? OpportunityId { get; set; }
    public Opportunity? Opportunity { get; set; }

    public DateOnly FollowUpDate { get; set; }

    public ActivityType FollowUpType { get; set; }

    [MaxLength(200)]
    public string Subject { get; set; } = "";

    [MaxLength(2000)]
    public string? Remarks { get; set; }

    public FollowUpStatus Status { get; set; } = FollowUpStatus.Planned;

    public string AssignedTo { get; set; } = "";
    [ForeignKey(nameof(AssignedTo))]
    public ApplicationUser? AssignedUser { get; set; }

    public bool IsDeleted { get; set; }
}
