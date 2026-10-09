namespace CashPilot.Domain.Reports;

/// <summary>Spending of one category in a month against the previous month and the recent average.</summary>
public sealed record CategoryChange(string Category, decimal Current, decimal Previous, decimal? RecentAverage)
{
    public decimal Delta => Current - Previous;

    /// <summary>Change against the previous month, as a fraction (0.25 = +25%); null when there was no spending before.</summary>
    public decimal? DeltaFraction => Previous > 0 ? Delta / Previous : null;

    public bool IsNew => Previous <= 0 && Current > 0;

    /// <summary>A rise worth looking at: at least R$ 50 more, and 20% or more (or a category that was not there before).</summary>
    public bool IsBigRise => Delta >= MonthlyComparison.BigRiseAmount && (IsNew || DeltaFraction >= MonthlyComparison.BigRiseFraction);
}

public sealed record MonthComparison(int Year, int Month, decimal CurrentTotal, decimal PreviousTotal, IReadOnlyList<CategoryChange> Rows)
{
    public decimal Delta => CurrentTotal - PreviousTotal;
}

/// <summary>
/// Month-over-month spending by category. The comparison month is the calendar month right before; the recent
/// average covers up to the three calendar months before it that have any data (a month with no entries at all,
/// such as before the first import, is not counted as zero).
/// </summary>
public static class MonthlyComparison
{
    public const decimal BigRiseAmount = 50m;
    public const decimal BigRiseFraction = 0.20m;

    /// <summary>Rows ordered by the biggest rise first, then by the biggest fall.</summary>
    public static MonthComparison Compare(IEnumerable<MonthlySpending> months, int year, int month)
    {
        var byMonth = months.ToDictionary(m => m.Year * 12 + (m.Month - 1), CategoryTotals);
        var key = year * 12 + (month - 1);

        var current = byMonth.GetValueOrDefault(key) ?? new Dictionary<string, decimal>();
        var previous = byMonth.GetValueOrDefault(key - 1) ?? new Dictionary<string, decimal>();
        var recent = Enumerable.Range(1, 3).Select(back => byMonth.GetValueOrDefault(key - back)).Where(m => m is not null).ToList();

        var categories = current.Keys.Concat(previous.Keys).Concat(recent.SelectMany(m => m!.Keys)).Distinct();
        var rows = categories
            .Select(category => new CategoryChange(
                category,
                current.GetValueOrDefault(category),
                previous.GetValueOrDefault(category),
                recent.Count == 0 ? null : recent.Sum(m => m!.GetValueOrDefault(category)) / recent.Count))
            .Where(r => r.Current != 0 || r.Previous != 0)
            .OrderByDescending(r => r.Delta)
            .ThenBy(r => r.Category, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new MonthComparison(year, month, current.Values.Sum(), previous.Values.Sum(), rows);
    }

    private static Dictionary<string, decimal> CategoryTotals(MonthlySpending month) =>
        month.Lines.GroupBy(l => l.Category).ToDictionary(g => g.Key, g => g.Sum(l => l.Total));
}
