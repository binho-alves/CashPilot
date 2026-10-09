using CashPilot.Domain.Accounts;
using CashPilot.Domain.Transactions;

namespace CashPilot.Domain.Payments;

/// <summary>Part of a bill payment entry applied to one card bill (a payment can pay several bills, a bill can get several payments).</summary>
public sealed record CardBillPaymentLink(Guid TransactionId, string Card, DateOnly Closing, decimal Amount);

/// <summary>
/// A hand-set difference added to a bill's total (positive or negative) so it equals the bank's figure to the cent.
/// It is not an entry: reports and balances do not see it.
/// </summary>
public sealed record CardBillAdjustment(string Card, DateOnly Closing, decimal Amount);

public enum CardBillState
{
    /// <summary>The cycle has not closed yet: the total is still growing.</summary>
    Open = 1,
    /// <summary>Closed, nothing paid, due date not reached.</summary>
    ToPay = 2,
    /// <summary>Closed, something paid, the rest not yet due.</summary>
    Partial = 3,
    /// <summary>Past the due date with something still owed.</summary>
    Overdue = 4,
    Paid = 5,
}

/// <param name="Manual">True when the user linked the payment to this bill; false when it was found by amount and date.</param>
public sealed record CardBillPart(Transaction Payment, decimal Amount, bool Manual);

/// <summary>One card bill with what was paid on it. <see cref="SettledByHand"/>: the user said it is paid (no payment entry).</summary>
public sealed record CardBillStatement(
    Account Card,
    DateOnly Closing,
    DateOnly Due,
    decimal Total,
    decimal Paid,
    IReadOnlyList<CardBillPart> Parts,
    DateOnly? SettledByHand,
    CardBillState State,
    decimal Adjustment = 0m)
{
    /// <summary>What the entries on the card add up to, before the adjustment.</summary>
    public decimal EntriesTotal => Total - Adjustment;
    public decimal Remaining => Math.Max(0m, Total - Paid);
    public bool IsPaid => State == CardBillState.Paid;
}

public sealed record CardBillLedgerResult(
    IReadOnlyList<CardBillStatement> Statements,
    IReadOnlyList<Transaction> UnmatchedPayments);

/// <summary>
/// Every card bill with its payments. Payments the user linked by hand (<see cref="CardBillPaymentLink"/>) come first and are
/// kept out of the automatic matching (<see cref="CardBillMatcher"/>: same amount to the cent, near the due date); what
/// the matcher finds is added on top. A bill whose links cover the total counts as paid; one covered in part keeps
/// the rest as <see cref="CardBillStatement.Remaining"/>, which is what still has to be paid. A bill the user marked as paid by hand is
/// paid in full. Links to entries that no longer exist are ignored.
/// </summary>
public static class CardBillLedger
{
    public const decimal Tolerance = 0.01m;

    public static CardBillLedgerResult Build(
        IEnumerable<Account> accounts,
        IEnumerable<Transaction> transactions,
        IEnumerable<CardBillSettlement>? settlements,
        IEnumerable<CardBillPaymentLink>? links,
        DateOnly today,
        IEnumerable<CardBillAdjustment>? adjustments = null)
    {
        var accountList = accounts.ToList();
        var all = transactions.ToList();
        var settleList = (settlements ?? Array.Empty<CardBillSettlement>()).ToList();
        var byId = all.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        var valid = (links ?? Array.Empty<CardBillPaymentLink>())
            .Where(l => l.Amount > 0m && byId.ContainsKey(l.TransactionId))
            .ToList();
        var linkedIds = valid.Select(l => l.TransactionId).ToHashSet();
        var rest = all.Where(t => !linkedIds.Contains(t.Id)).ToList();

        var adjustmentList = (adjustments ?? Array.Empty<CardBillAdjustment>()).ToList();
        decimal AdjustmentFor(string card, DateOnly closing) => adjustmentList
            .Where(a => a.Closing == closing && string.Equals(a.Card, card, StringComparison.OrdinalIgnoreCase))
            .Sum(a => a.Amount);

        decimal LinkedTo(string card, DateOnly closing) => valid
            .Where(l => l.Closing == closing && string.Equals(l.Card, card, StringComparison.OrdinalIgnoreCase))
            .Sum(l => l.Amount);

        // Bills fully covered by links must not take another payment by amount: treat them as settled for the matcher.
        var first = CardBillMatcher.Match(accountList, rest, settleList);
        var extended = settleList.ToList();
        foreach (var cycle in first.Cycles)
        {
            if (cycle.ManualPaidOn is not null) continue;
            var linked = LinkedTo(cycle.Card.Name, cycle.Closing);
            if (linked <= 0m || linked < cycle.Total + AdjustmentFor(cycle.Card.Name, cycle.Closing) - Tolerance) continue;
            var lastPayment = valid
                .Where(l => l.Closing == cycle.Closing && string.Equals(l.Card, cycle.Card.Name, StringComparison.OrdinalIgnoreCase))
                .Max(l => byId[l.TransactionId].Date);
            extended.Add(new CardBillSettlement(cycle.Card.Name, cycle.Closing, lastPayment));
        }
        var matches = CardBillMatcher.Match(accountList, rest, extended);

        var statements = new List<CardBillStatement>();
        foreach (var cycle in matches.Cycles)
        {
            var parts = valid
                .Where(l => l.Closing == cycle.Closing && string.Equals(l.Card, cycle.Card.Name, StringComparison.OrdinalIgnoreCase))
                .Select(l => new CardBillPart(byId[l.TransactionId], l.Amount, true))
                .ToList();
            if (cycle.Payment is not null)
                parts.Add(new CardBillPart(cycle.Payment, Math.Abs(cycle.Payment.Amount), false));

            var byHand = settleList.Any(s => s.Closing == cycle.Closing
                                             && string.Equals(s.Card, cycle.Card.Name, StringComparison.OrdinalIgnoreCase));
            var adjustment = AdjustmentFor(cycle.Card.Name, cycle.Closing);
            var total = cycle.Total + adjustment;
            var paid = byHand ? total : parts.Sum(p => p.Amount);
            var remaining = Math.Max(0m, total - paid);

            var state = remaining <= Tolerance ? CardBillState.Paid
                : cycle.Due < today ? CardBillState.Overdue
                : paid > 0m ? CardBillState.Partial
                : today > cycle.Closing ? CardBillState.ToPay
                : CardBillState.Open;

            statements.Add(new CardBillStatement(
                cycle.Card, cycle.Closing, cycle.Due, total, paid, parts,
                byHand ? settleList.First(s => s.Closing == cycle.Closing
                                               && string.Equals(s.Card, cycle.Card.Name, StringComparison.OrdinalIgnoreCase)).PaidOn : null,
                state,
                adjustment));
        }

        return new CardBillLedgerResult(statements, matches.UnmatchedPayments);
    }
}
