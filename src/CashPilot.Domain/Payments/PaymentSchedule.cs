using CashPilot.Domain.Accounts;
using CashPilot.Domain.Transactions;

namespace CashPilot.Domain.Payments;

public enum UpcomingKind
{
    CardBill = 1,
    Payable = 2,
    FutureEntry = 3,
}

/// <summary>One thing to pay. <see cref="Amount"/> is positive. <see cref="PayableId"/> is set for hand-registered ones.</summary>
public sealed record UpcomingPayment(
    UpcomingKind Kind,
    string Description,
    DateOnly DueDate,
    decimal Amount,
    string? Account,
    Guid? PayableId,
    bool Overdue);

public static class PaymentSchedule
{
    /// <summary>
    /// Everything still to pay, by due date:
    /// <list type="bullet">
    /// <item>card bills: entries on registered cards with closing and due days, grouped by cycle, bills due today or later
    /// (the gross cash-out charge counts, bill payments do not);</item>
    /// <item>unpaid hand-registered payables, overdue ones included;</item>
    /// <item>entries dated in the future on any other account (installments, scheduled boletos).</item>
    /// </list>
    /// Card bills are not matched against bill payments: one due today or later is listed even if already paid early.
    /// </summary>
    public static IReadOnlyList<UpcomingPayment> Build(
        IEnumerable<Account> accounts,
        IEnumerable<Transaction> transactions,
        IEnumerable<Payable> payables,
        DateOnly today)
    {
        var result = new List<UpcomingPayment>();
        var all = transactions.ToList();

        var billedCards = accounts
            .Where(a => a.Kind == AccountKind.CreditCard && a.ClosingDay is not null && a.DueDay is not null)
            .ToList();
        var billedNames = billedCards.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var card in billedCards)
        {
            var cycles = all
                .Where(t => string.Equals(t.Account, card.Name, StringComparison.OrdinalIgnoreCase)
                            && t.Type != TransactionType.CardBillPayment)
                .GroupBy(t => CardBilling.ClosingFor(t.Date, card.ClosingDay!.Value));
            foreach (var cycle in cycles)
            {
                var due = CardBilling.DueFor(cycle.Key, card.DueDay!.Value);
                var total = -cycle.Sum(t => t.Amount);
                if (due < today || total <= 0) continue;
                result.Add(new UpcomingPayment(UpcomingKind.CardBill,
                    $"Fatura {card.Name} (fecha {cycle.Key:dd/MM})", due, total, card.Name, null, false));
            }
        }

        foreach (var payable in payables.Where(p => !p.Paid))
            result.Add(new UpcomingPayment(UpcomingKind.Payable, payable.Description, payable.DueDate,
                payable.Amount, null, payable.Id, payable.DueDate < today));

        foreach (var t in all.Where(t => t.Date > today
                                        && t.Amount < 0
                                        && t.Type is TransactionType.Undefined or TransactionType.Expense
                                        && !billedNames.Contains(t.Account)))
            result.Add(new UpcomingPayment(UpcomingKind.FutureEntry, t.RawDescription, t.Date, -t.Amount,
                t.Account, null, false));

        return result.OrderBy(p => p.DueDate).ThenBy(p => p.Description, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
