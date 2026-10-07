using Microsoft.AspNetCore.Mvc.Rendering;

namespace AcxiomCRM.ViewModels;

public record AuditRow(long Id, DateTime CreatedDate, string? UserName, string Module, string Action, string EntityName,
    string? RecordId, string Result, string? IpAddress, string? OldValue, string? NewValue, string? Details);

public class AuditIndexViewModel
{
    public string? UserId { get; set; }
    public string? Module { get; set; }
    public string? Action { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public List<SelectListItem> Users { get; set; } = [];
    public List<string> Modules { get; set; } = [];
    public List<string> Actions { get; set; } = [];
    public PagedList<AuditRow> Entries { get; set; } = new();
}
