using CashPilot.Domain.Accounts;
using CashPilot.Domain.Transactions;

namespace CashPilot.Domain.Payments;

public enum UpcomingKind
{
    CardBill = 1,
    Payable = 2,
    FutureEntry = 3,
    /// <summary>Forecast only (<see cref="CashForecast"/>): a monthly expense repeated from the history.</summary>
    RecurringExpense = 4,
    /// <summary>Forecast only: a monthly income repeated from the history. <see cref="UpcomingPayment.Amount"/> is the inflow.</summary>
    RecurringIncome = 5,
    /// <summary>Forecast only: money still to come from a health-plan reimbursement claim. The amount is the inflow.</summary>
    ExpectedReimbursement = 6,
}

/// <summary>
/// One thing to pay. <see cref="Amount"/> is positive. <see cref="PayableId"/> is set for hand-registered ones.
/// <see cref="ProjectedPart"/> is the part of the amount that is a forecast rather than an entry (future installments and
/// recurring card charges added to a card bill); it equals the amount for a bill that has no entry yet.
/// </summary>
public sealed record UpcomingPayment(
    UpcomingKind Kind,
    string Description,
    DateOnly DueDate,
    decimal Amount,
    string? Account,
    Guid? PayableId,
    bool Overdue,
    DateOnly? Closing = null,
    decimal ProjectedPart = 0m);

public static class PaymentSchedule
{
    /// <summary>
    /// Everything still to pay, by due date:
    /// <list type="bullet">
    /// <item>card bills: entries on registered cards with closing and due days, grouped by cycle, bills due today or later
    /// (the gross cash-out charge counts, bill payments do not) that no payment was found for
    /// (see <see cref="CardBillLedger"/>: a bill already paid, early or by links, or marked as paid by hand, is not listed; one paid in part lists what is left);</item>
    /// <item>unpaid hand-registered payables, overdue ones included;</item>
    /// <item>entries dated in the future on any other account (installments, scheduled boletos).</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<UpcomingPayment> Build(
        IEnumerable<Account> accounts,
        IEnumerable<Transaction> transactions,
        IEnumerable<Payable> payables,
        DateOnly today,
        IEnumerable<CardBillSettlement>? settlements = null,
        IEnumerable<CardBillPaymentLink>? cardBillLinks = null,
        IEnumerable<CardBillAdjustment>? cardBillAdjustments = null)
    {
        var result = new List<UpcomingPayment>();
        var all = transactions.ToList();

        var accountList = accounts.ToList();
        var billedNames = accountList
            .Where(a => a.Kind == AccountKind.CreditCard && a.ClosingDay is not null && a.DueDay is not null)
            .Select(a => a.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var bill in CardBillLedger.Build(accountList, all, settlements, cardBillLinks, today, cardBillAdjustments).Statements)
        {
            if (bill.Due < today || bill.IsPaid) continue;
            // A bill paid in part is still due for what is left.
            result.Add(new UpcomingPayment(UpcomingKind.CardBill,
                $"Fatura {bill.Card.Name} (fecha {bill.Closing:dd/MM})" + (bill.Paid > 0m ? " — restante" : ""),
                bill.Due, bill.Remaining, bill.Card.Name, null, false, bill.Closing));
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
