using Microsoft.AspNetCore.Mvc.Rendering;

namespace AcxiomCRM.ViewModels;

// One generic table for all eight reports (RPT-01..03). Cells keep their raw values (number, date, text)
// so sorting is correct; the view formats them.
public record ReportInfo(string Action, string Title, string Description);

public class ReportViewModel
{
    public ReportInfo Info { get; set; } = null!;
    public string[] Columns { get; set; } = [];
    public PagedList<object?[]> Rows { get; set; } = new();
    public object?[]? Totals { get; set; }

    // Filters (RPT-02): period, assigned user, and for the pipeline report the grouping.
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public string? OwnerId { get; set; }
    public string? GroupBy { get; set; }
    public bool ShowGroupBy { get; set; }
    public string DateLabel { get; set; } = "Date";
    public List<SelectListItem> Owners { get; set; } = [];
}
