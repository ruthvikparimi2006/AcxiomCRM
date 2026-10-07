using System.ComponentModel.DataAnnotations;

namespace AcxiomCRM.Models;

// Append-only: AppDbContext refuses to update or delete these rows.
public class AuditLog
{
    public long AuditLogId { get; set; }

    // Null for anonymous events (e.g. a failed login for an unknown user).
    public string? UserId { get; set; }
    public ApplicationUser? User { get; set; }

    [MaxLength(50)]
    public string Action { get; set; } = "";

    [MaxLength(100)]
    public string EntityName { get; set; } = "";

    [MaxLength(50)]
    public string? RecordId { get; set; }

    public string? OldValue { get; set; }

    public string? NewValue { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    [MaxLength(45)]
    public string? IpAddress { get; set; }

    [MaxLength(50)]
    public string Module { get; set; } = "";

    [MaxLength(20)]
    public string Result { get; set; } = "Success";

    [MaxLength(2000)]
    public string? Details { get; set; }
}
