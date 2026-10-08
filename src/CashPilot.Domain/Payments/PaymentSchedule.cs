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
    bool Overdue,
    DateOnly? Closing = null);

public static class PaymentSchedule
{
    /// <summary>
    /// Everything still to pay, by due date:
    /// <list type="bullet">
    /// <item>card bills: entries on registered cards with closing and due days, grouped by cycle, bills due today or later
    /// (the gross cash-out charge counts, bill payments do not) that no payment was found for
    /// (see <see cref="CardBillMatcher"/>: a bill already paid early, or marked as paid by hand, is not listed);</item>
    /// <item>unpaid hand-registered payables, overdue ones included;</item>
    /// <item>entries dated in the future on any other account (installments, scheduled boletos).</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<UpcomingPayment> Build(
        IEnumerable<Account> accounts,
        IEnumerable<Transaction> transactions,
        IEnumerable<Payable> payables,
        DateOnly today,
        IEnumerable<CardBillSettlement>? settlements = null)
    {
        var result = new List<UpcomingPayment>();
        var all = transactions.ToList();

        var accountList = accounts.ToList();
        var billedNames = accountList
            .Where(a => a.Kind == AccountKind.CreditCard && a.ClosingDay is not null && a.DueDay is not null)
            .Select(a => a.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var bill in CardBillMatcher.Match(accountList, all, settlements).Cycles)
        {
            if (bill.Due < today || bill.Paid) continue;
            result.Add(new UpcomingPayment(UpcomingKind.CardBill,
                $"Fatura {bill.Card.Name} (fecha {bill.Closing:dd/MM})", bill.Due, bill.Total, bill.Card.Name, null, false, bill.Closing));
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
