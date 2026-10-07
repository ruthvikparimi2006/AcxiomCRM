using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AcxiomCRM.Models;

public class Activity
{
    public int ActivityId { get; set; }

    public ActivityType ActivityType { get; set; }

    [MaxLength(200)]
    public string Subject { get; set; } = "";

    [MaxLength(2000)]
    public string? Description { get; set; }

    public DateTime ActivityDate { get; set; }

    // At least one of these is set (database check constraint).
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public int? LeadId { get; set; }
    public Lead? Lead { get; set; }

    public string AssignedTo { get; set; } = "";
    [ForeignKey(nameof(AssignedTo))]
    public ApplicationUser? AssignedUser { get; set; }

    public ActivityStatus Status { get; set; } = ActivityStatus.Planned;

    public bool IsDeleted { get; set; }
}
