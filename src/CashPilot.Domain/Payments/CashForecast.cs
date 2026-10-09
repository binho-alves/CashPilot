using CashPilot.Domain.Accounts;
using CashPilot.Domain.Transactions;

namespace CashPilot.Domain.Payments;

public sealed record ForecastDay(DateOnly Date, IReadOnlyList<UpcomingPayment> Items, decimal Outflow, decimal Inflow, decimal Balance);

/// <param name="Lowest">Day with the lowest projected balance (null when nothing happens in the period).</param>
/// <param name="FirstNegative">First day the balance goes below zero.</param>
/// <param name="FirstBeyondLis">First day the overdraft needed is above the LIS limit.</param>
public sealed record ForecastSummary(IReadOnlyList<ForecastDay> Days, ForecastDay? Lowest, DateOnly? FirstNegative, DateOnly? FirstBeyondLis);

/// <summary>
/// Cash forecast: <see cref="PaymentSchedule"/> plus what is already known to come and what repeats.
/// <list type="bullet">
/// <item>future installments and recurring card charges are added to the card bill of their cycle (a bill that has
/// no entry yet is created, marked as projected);</item>
/// <item>recurring expenses and incomes on bank accounts (<see cref="RecurringEntries"/>) go on their expected day;
/// an expense is skipped when a registered boleto or a future entry of about the same amount is already within
/// <see cref="CoverDays"/> days, so it is not counted twice.</item>
/// </list>
/// It is an estimate: recurring entries assume the next months look like the last ones.
/// </summary>
public static class CashForecast
{
    public const int CoverDays = 5;
    public const decimal CoverTolerance = 0.15m;

    public static IReadOnlyList<UpcomingPayment> Build(
        IEnumerable<Account> accounts,
        IEnumerable<Transaction> transactions,
        IEnumerable<Payable> payables,
        DateOnly today,
        DateOnly until,
        IEnumerable<CardBillSettlement>? settlements = null,
        bool includeIncome = true)
    {
        var accountList = accounts.ToList();
        var all = transactions.ToList();
        var items = PaymentSchedule.Build(accountList, all, payables, today, settlements).ToList();

        var cards = accountList
            .Where(a => a.Kind == AccountKind.CreditCard && a.ClosingDay is not null && a.DueDay is not null)
            .ToDictionary(a => a.Name.Trim().ToUpperInvariant());
        var banks = accountList
            .Where(a => a.Kind == AccountKind.BankAccount)
            .Select(a => a.Name.Trim().ToUpperInvariant())
            .ToHashSet();

        var extras = new Dictionary<(string Card, DateOnly Closing), (Account Card, DateOnly Due, decimal Amount)>();

        void AddExtra(Account card, DateOnly closing, DateOnly due, decimal amount)
        {
            var key = (card.Name.Trim().ToUpperInvariant(), closing);
            extras[key] = extras.TryGetValue(key, out var current)
                ? (card, due, current.Amount + amount)
                : (card, due, amount);
        }

        foreach (var installment in FutureInstallments.Build(accountList, all, today).SelectMany(p => p.Future))
        {
            if (installment.Due > until) continue;
            if (cards.TryGetValue(installment.Account.Trim().ToUpperInvariant(), out var card))
                AddExtra(card, installment.Closing, installment.Due, installment.Amount);
        }

        var covering = items.Where(i => i.Kind is UpcomingKind.Payable or UpcomingKind.FutureEntry && !i.Overdue).ToList();

        foreach (var entry in RecurringEntries.Detect(all, today))
        {
            var key = entry.Account.Trim().ToUpperInvariant();
            foreach (var date in RecurringEntries.Occurrences(entry, today, until))
            {
                if (cards.TryGetValue(key, out var card))
                {
                    if (entry.IsIncome) continue; // refunds on a card are not predictable
                    var closing = CardBilling.ClosingFor(date, card.ClosingDay!.Value);
                    var due = CardBilling.DueFor(closing, card.DueDay!.Value);
                    if (due <= until) AddExtra(card, closing, due, -entry.Amount);
                }
                else if (banks.Contains(key))
                {
                    if (entry.IsIncome)
                    {
                        if (includeIncome)
                            items.Add(new UpcomingPayment(UpcomingKind.RecurringIncome, entry.Description, date, entry.Amount, entry.Account, null, false));
                    }
                    else if (!IsCovered(covering, date, -entry.Amount))
                    {
                        items.Add(new UpcomingPayment(UpcomingKind.RecurringExpense, entry.Description, date, -entry.Amount, entry.Account, null, false));
                    }
                }
            }
        }

        foreach (var ((_, closing), extra) in extras.OrderBy(e => e.Key.Closing))
        {
            var index = items.FindIndex(i => i.Kind == UpcomingKind.CardBill
                                             && i.Closing == closing
                                             && string.Equals(i.Account, extra.Card.Name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                items[index] = items[index] with { Amount = items[index].Amount + extra.Amount, ProjectedPart = items[index].ProjectedPart + extra.Amount };
            else
                items.Add(new UpcomingPayment(UpcomingKind.CardBill, $"Fatura {extra.Card.Name} (fecha {closing:dd/MM})",
                    extra.Due, extra.Amount, extra.Card.Name, null, false, closing, extra.Amount));
        }

        return items.OrderBy(i => i.DueDate).ThenBy(i => i.Description, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Day by day balance. Overdue items come off the starting balance at once; inflows (<see cref="UpcomingKind.RecurringIncome"/>)
    /// add to it. The lowest balance can only happen on a day with entries, so only those days are listed.
    /// </summary>
    public static ForecastSummary Project(
        decimal startBalance, IEnumerable<UpcomingPayment> items, decimal lisLimit, DateOnly today, int horizonDays)
    {
        var list = items.ToList();
        var limit = today.AddDays(horizonDays);
        var balance = startBalance - list.Where(i => i.Overdue).Sum(i => i.Amount);

        var days = new List<ForecastDay>();
        foreach (var group in list.Where(i => !i.Overdue && i.DueDate <= limit).GroupBy(i => i.DueDate).OrderBy(g => g.Key))
        {
            var outflow = group.Where(i => i.Kind != UpcomingKind.RecurringIncome).Sum(i => i.Amount);
            var inflow = group.Where(i => i.Kind == UpcomingKind.RecurringIncome).Sum(i => i.Amount);
            balance += inflow - outflow;
            days.Add(new ForecastDay(group.Key, group.ToList(), outflow, inflow, balance));
        }

        ForecastDay? lowest = null;
        foreach (var day in days)
            if (lowest is null || day.Balance < lowest.Balance) lowest = day;

        return new ForecastSummary(
            days,
            lowest,
            days.FirstOrDefault(d => d.Balance < 0)?.Date,
            days.FirstOrDefault(d => d.Balance < 0 && -d.Balance > lisLimit)?.Date);
    }

    private static bool IsCovered(IEnumerable<UpcomingPayment> existing, DateOnly date, decimal amount) =>
        existing.Any(i => Math.Abs(i.DueDate.DayNumber - date.DayNumber) <= CoverDays
                          && Math.Abs(i.Amount - amount) <= amount * CoverTolerance);
}
