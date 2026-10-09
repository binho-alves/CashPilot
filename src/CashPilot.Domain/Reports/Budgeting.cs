namespace CashPilot.Domain.Reports;

public enum BudgetStatus
{
    /// <summary>No limit set for the category.</summary>
    NoLimit = 0,
    Ok = 1,
    /// <summary>80% or more of the limit spent.</summary>
    Near = 2,
    /// <summary>Spent more than the limit.</summary>
    Over = 3,
}

/// <summary>One category in a month: what was spent against its monthly limit.</summary>
public sealed record BudgetLine(string Category, decimal? Limit, decimal Spent, decimal? Suggested, decimal? Projected)
{
    public decimal? Remaining => Limit - Spent;

    public decimal? Fraction => Limit is > 0 ? Spent / Limit : null;

    public BudgetStatus Status =>
        Limit is not > 0 ? BudgetStatus.NoLimit :
        Spent > Limit ? BudgetStatus.Over :
        Spent >= Limit * Budgeting.NearFraction ? BudgetStatus.Near :
        BudgetStatus.Ok;

    /// <summary>The month is still running and, at this pace, the category will end above its limit.</summary>
    public bool ProjectedOver => Limit is > 0 && Projected > Limit && Status != BudgetStatus.Over;
}

public sealed record BudgetReport(int Year, int Month, IReadOnlyList<BudgetLine> Lines, decimal Uncategorized)
{
    public decimal TotalLimit => Lines.Sum(l => l.Limit ?? 0m);

    /// <summary>Spent in the categories that have a limit.</summary>
    public decimal SpentInBudgeted => Lines.Where(l => l.Limit is > 0).Sum(l => l.Spent);
}

/// <summary>
/// Monthly limits per category. Spending follows <see cref="SpendingReport"/> (transfers and card-bill payments do not
/// count). "(sem categoria)" cannot have a limit: it is reported apart.
/// </summary>
public static class Budgeting
{
    public const decimal NearFraction = 0.8m;

    /// <summary>Projection only starts after this many days, before that the pace says nothing.</summary>
    public const int MinDaysForProjection = 7;

    public static BudgetReport Build(
        IEnumerable<MonthlySpending> months, IReadOnlyDictionary<string, decimal> limits, int year, int month, DateOnly today)
    {
        var all = months.ToList();
        var spent = SpentByCategory(all.FirstOrDefault(m => m.Year == year && m.Month == month));

        var isCurrent = today.Year == year && today.Month == month;
        var daysInMonth = DateTime.DaysInMonth(year, month);

        var lines = limits.Keys.Concat(spent.Keys)
            .Where(c => c != SpendingReport.Uncategorized)
            .Distinct()
            .Select(category =>
            {
                limits.TryGetValue(category, out var limit);
                var value = spent.GetValueOrDefault(category);
                decimal? projected = isCurrent && today.Day >= MinDaysForProjection ? value / today.Day * daysInMonth : null;
                return new BudgetLine(category, limit > 0 ? limit : (decimal?)null, value, Suggest(all, category, year, month), projected);
            })
            .Where(l => l.Limit is not null || l.Spent != 0)
            .OrderByDescending(l => (int)l.Status)
            .ThenByDescending(l => l.Fraction ?? 0m)
            .ThenByDescending(l => l.Spent)
            .ThenBy(l => l.Category, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new BudgetReport(year, month, lines, spent.GetValueOrDefault(SpendingReport.Uncategorized));
    }

    /// <summary>
    /// Suggested limit: average of the category in up to three earlier months that have data, rounded up to the next
    /// R$ 10. Null when there is no history.
    /// </summary>
    public static decimal? Suggest(IReadOnlyCollection<MonthlySpending> months, string category, int year, int month)
    {
        var key = year * 12 + (month - 1);
        var earlier = months
            .Where(m => m.Year * 12 + (m.Month - 1) < key)
            .OrderByDescending(m => m.Year * 12 + m.Month)
            .Take(3)
            .ToList();
        if (earlier.Count == 0) return null;

        var average = earlier.Sum(m => SpentByCategory(m).GetValueOrDefault(category)) / earlier.Count;
        return average <= 0 ? null : Math.Ceiling(average / 10m) * 10m;
    }

    private static Dictionary<string, decimal> SpentByCategory(MonthlySpending? month) =>
        month is null
            ? new Dictionary<string, decimal>()
            : month.Lines.GroupBy(l => l.Category).ToDictionary(g => g.Key, g => g.Sum(l => l.Total));
}
