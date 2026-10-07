using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AcxiomCRM.Models;

public class Lead
{
    public int LeadId { get; set; }

    // Computed by the database: LEAD-000001.
    [MaxLength(20)]
    public string LeadCode { get; set; } = "";

    [MaxLength(150)]
    public string LeadName { get; set; } = "";

    [MaxLength(256)]
    public string? Email { get; set; }

    [MaxLength(10)]
    public string? Phone { get; set; }

    [MaxLength(150)]
    public string? CompanyName { get; set; }

    public LeadSource? Source { get; set; }

    public LeadStatus Status { get; set; } = LeadStatus.New;

    public LeadPriority? Priority { get; set; }

    public decimal? ExpectedValue { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    public string AssignedTo { get; set; } = "";
    [ForeignKey(nameof(AssignedTo))]
    public ApplicationUser? AssignedUser { get; set; }

    // Set when the lead is converted (§16 #7).
    public int? ConvertedCustomerId { get; set; }
    public Customer? ConvertedCustomer { get; set; }

    public DateTime? ModifiedDate { get; set; }

    public bool IsDeleted { get; set; }
}
