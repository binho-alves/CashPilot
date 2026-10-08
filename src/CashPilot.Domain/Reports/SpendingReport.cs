using CashPilot.Domain.Transactions;

namespace CashPilot.Domain.Reports;

public sealed record SpendingLine(string Category, string Item, decimal Total, int Count);

public sealed record MonthlySpending(int Year, int Month, IReadOnlyList<SpendingLine> Lines)
{
    public decimal Total => Lines.Sum(l => l.Total);

    public int Count => Lines.Sum(l => l.Count);

    /// <summary>Part of <see cref="Total"/> that still has no category.</summary>
    public decimal Uncategorized => Lines.Where(l => l.Category == SpendingReport.Uncategorized).Sum(l => l.Total);
}

/// <summary>
/// Spending by month, category and item. Internal transfers, credit card bill payments and card cash-out charges
/// (gross amount) are not spending. A categorized inflow (refund, credit) reduces its category.
/// </summary>
public static class SpendingReport
{
    public const string Uncategorized = "(sem categoria)";

    public static bool CountsAsSpending(Transaction transaction) => transaction.Type switch
    {
        TransactionType.Expense => true,
        TransactionType.Undefined => transaction.Amount < 0,
        TransactionType.Income => transaction.Category is not null,
        _ => false,
    };

    /// <summary>One entry per month, oldest first; lines inside a month are ordered by amount, largest first.</summary>
    public static IReadOnlyList<MonthlySpending> ByMonth(IEnumerable<Transaction> transactions) =>
        transactions
            .Where(CountsAsSpending)
            .GroupBy(t => (Year: t.Date.Year, Month: t.Date.Month))
            .OrderBy(month => month.Key.Year).ThenBy(month => month.Key.Month)
            .Select(month => new MonthlySpending(
                month.Key.Year,
                month.Key.Month,
                month.GroupBy(t => (Category: t.Category ?? Uncategorized, Item: t.Item ?? ""))
                     .Select(g => new SpendingLine(g.Key.Category, g.Key.Item, -g.Sum(t => t.Amount), g.Count()))
                     .OrderByDescending(line => line.Total)
                     .ToList()))
            .ToList();
}
