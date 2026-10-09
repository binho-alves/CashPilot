using CashPilot.Domain.Accounts;
using CashPilot.Domain.Reports;
using CashPilot.Domain.Transactions;

namespace CashPilot.Domain.Payments;

/// <summary>An installment purchase that still has installments to come.</summary>
public sealed record InstallmentPlan(
    string Account,
    string Description,
    decimal Amount,
    int LastNumber,
    int Count,
    DateOnly LastDate,
    IReadOnlyList<FutureInstallment> Future)
{
    public int Remaining => Count - LastNumber;

    public decimal RemainingTotal => Amount * Future.Count;
}

/// <summary>One installment not yet charged, with the date its bill is due.</summary>
public sealed record FutureInstallment(string Account, string Description, decimal Amount, int Number, int Count, DateOnly Closing, DateOnly Due);

/// <summary>
/// Installments already committed: for every card purchase paid in installments, the installments after the latest one
/// seen. Each lands on the bill that follows the previous one (same closing day), due on the card's due day.
/// Purchases are told apart by account, description, installment count and amount (rounded to whole reais, so a
/// one-cent difference in the first installment does not split a purchase in two).
/// </summary>
public static class FutureInstallments
{
    public static IReadOnlyList<InstallmentPlan> Build(
        IEnumerable<Account> accounts, IEnumerable<Transaction> transactions, DateOnly today)
    {
        var cards = accounts.ToDictionary(a => a.Name.Trim().ToUpperInvariant());

        var plans = new List<InstallmentPlan>();
        var groups = transactions
            .Where(t => t.InstallmentCount is > 1 && t.InstallmentNumber is >= 1 && t.Amount < 0 && SpendingReport.CountsAsSpending(t))
            .GroupBy(t => (
                Account: t.Account.Trim().ToUpperInvariant(),
                t.NormalizedDescription,
                Count: t.InstallmentCount!.Value,
                Amount: Math.Round(Math.Abs(t.Amount), 0, MidpointRounding.AwayFromZero)));

        foreach (var group in groups)
        {
            var latest = group.OrderByDescending(t => t.InstallmentNumber).ThenByDescending(t => t.Date).First();
            var last = latest.InstallmentNumber!.Value;
            var count = latest.InstallmentCount!.Value;
            if (last >= count) continue;

            cards.TryGetValue(group.Key.Account, out var card);
            var amount = -latest.Amount;
            var future = new List<FutureInstallment>();
            for (var k = 1; k <= count - last; k++)
            {
                var (closing, due) = Project(card, latest.Date, k);
                if (due < today) continue; // an installment that should already have been charged: the bill is not imported yet
                future.Add(new FutureInstallment(latest.Account, latest.RawDescription, amount, last + k, count, closing, due));
            }

            if (future.Count > 0)
                plans.Add(new InstallmentPlan(latest.Account, latest.RawDescription, amount, last, count, latest.Date, future));
        }

        return plans
            .OrderBy(p => p.Future[0].Due)
            .ThenBy(p => p.Description, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Total of the future installments by month of the due date, oldest first.</summary>
    public static IReadOnlyList<InstallmentMonth> ByMonth(IEnumerable<InstallmentPlan> plans) =>
        plans.SelectMany(p => p.Future)
            .GroupBy(f => (f.Due.Year, f.Due.Month))
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .Select(g => new InstallmentMonth(g.Key.Year, g.Key.Month, g.OrderBy(f => f.Due).ThenBy(f => f.Description).ToList()))
            .ToList();

    private static (DateOnly Closing, DateOnly Due) Project(Account? card, DateOnly purchase, int monthsAhead)
    {
        if (card is { ClosingDay: { } closingDay, DueDay: { } dueDay })
        {
            var closing = CardBilling.ClosingFor(purchase, closingDay);
            var month = new DateOnly(closing.Year, closing.Month, 1).AddMonths(monthsAhead);
            var next = CardBilling.DayOfMonth(month.Year, month.Month, closingDay);
            return (next, CardBilling.DueFor(next, dueDay));
        }

        // Card without closing/due days registered: same day, month by month.
        var date = purchase.AddMonths(monthsAhead);
        return (date, date);
    }
}

public sealed record InstallmentMonth(int Year, int Month, IReadOnlyList<FutureInstallment> Items)
{
    public decimal Total => Items.Sum(i => i.Amount);
}
