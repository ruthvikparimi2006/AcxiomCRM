namespace AcxiomCRM.ViewModels;

// DASH-05: Today, This Week, This Month, Custom Range (plus All time). Created/closed dates are stored in UTC,
// so local day boundaries are converted; follow-up dates are plain dates.
public record Period(string Key, string Label, DateOnly? FromDay, DateOnly? ToDay)
{
    public DateTime? StartUtc => FromDay?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime();
    public DateTime? EndUtc => ToDay?.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime();

    public static Period From(string? key, DateOnly? from, DateOnly? to)
    {
        var today = NotBeforeTodayAttribute.Today;
        return key switch
        {
            "today" => new("today", "Today", today, today),
            "week" => new("week", "This week", today.AddDays(-(((int)today.DayOfWeek + 6) % 7)), today.AddDays(6 - ((int)today.DayOfWeek + 6) % 7)),
            "month" => new("month", "This month", new DateOnly(today.Year, today.Month, 1), new DateOnly(today.Year, today.Month, 1).AddMonths(1).AddDays(-1)),
            "custom" when from is not null || to is not null => new("custom", $"{from:d} – {to:d}", from, to),
            _ => new("all", "All time", null, null),
        };
    }
}

public record ChartSeries(List<string> Labels, List<decimal> Values);

public record OwnerPerformance(string Name, int OpenCount, decimal PipelineValue, int WonCount, decimal WonAmount);

public class DashboardViewModel
{
    public Period Period { get; set; } = Period.From(null, null, null);
    public string Scope { get; set; } = "";

    // DASH-02 / §17.11 cards.
    public int TotalCustomers { get; set; }
    public int TotalLeads { get; set; }
    public int OpenLeads { get; set; }
    public int TotalOpportunities { get; set; }
    public int OpenOpportunities { get; set; }
    public int WonOpportunities { get; set; }
    public int LostOpportunities { get; set; }
    public decimal PipelineValue { get; set; }

    // DASH-06 / §17.12 charts.
    public ChartSeries LeadStatus { get; set; } = new([], []);
    public ChartSeries PipelineCounts { get; set; } = new([], []);
    public ChartSeries PipelineAmounts { get; set; } = new([], []);
    public ChartSeries MonthlySales { get; set; } = new([], []);

    // DASH-04 role panels.
    public List<FollowUpListItem> FollowUps { get; set; } = [];
    public int FollowUpsInPeriod { get; set; }
    public int OverdueFollowUps { get; set; }
    public List<OwnerPerformance> Owners { get; set; } = [];
    public (int Active, int Inactive, int Locked, int FailedLogins)? UserStats { get; set; }
}
