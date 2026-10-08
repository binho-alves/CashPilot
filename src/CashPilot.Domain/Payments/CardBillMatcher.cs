using CashPilot.Domain.Accounts;
using CashPilot.Domain.Transactions;

namespace CashPilot.Domain.Payments;

/// <summary>The user said "this bill is paid" by hand (the payment entry is missing or has a different amount).</summary>
public sealed record CardBillSettlement(string Card, DateOnly Closing, DateOnly PaidOn);

/// <summary>
/// One card bill (a closing cycle). <see cref="Payment"/> is the payment entry found for it, if any;
/// <see cref="ManualPaidOn"/> is set when the user marked it as paid by hand.
/// </summary>
public sealed record CardBillCycle(
    Account Card, DateOnly Closing, DateOnly Due, decimal Total, Transaction? Payment, DateOnly? ManualPaidOn = null)
{
    public bool Paid => Payment is not null || ManualPaidOn is not null;
}

/// <param name="Cycles">Every cycle of every registered card (with closing and due days), by due date.</param>
/// <param name="UnmatchedPayments">Money that left a bank account as a bill payment but fits no bill.</param>
public sealed record CardBillMatches(IReadOnlyList<CardBillCycle> Cycles, IReadOnlyList<Transaction> UnmatchedPayments);

/// <summary>
/// Pairs bill payments with the card bills they paid. A payment fits a bill when its amount equals the bill (to the cent)
/// and it was made from the closing date up to <see cref="DaysAfterDue"/> days after the due date. A payment is either an
/// outflow from a bank account (it does not say which card) or an inflow on the card itself (it does). When both were
/// recorded for the same payment, they count as one. Each payment pays at most one bill; when several fit, the one
/// closest to the due date wins. Partial payments are not recognized: that bill stays open, unless the user marked it as
/// paid by hand (a <see cref="CardBillSettlement"/>), which wins over any matching.
/// </summary>
public static class CardBillMatcher
{
    public const int DaysAfterDue = 10;
    private const int TwinDays = 5;

    public static CardBillMatches Match(
        IEnumerable<Account> accounts,
        IEnumerable<Transaction> transactions,
        IEnumerable<CardBillSettlement>? settlements = null)
    {
        var all = transactions.ToList();
        var cards = accounts
            .Where(a => a.Kind == AccountKind.CreditCard && a.ClosingDay is not null && a.DueDay is not null)
            .ToList();
        var cardNames = cards.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var cycles = new List<(Account Card, DateOnly Closing, DateOnly Due, decimal Total)>();
        foreach (var card in cards)
        {
            var groups = all
                .Where(t => string.Equals(t.Account, card.Name, StringComparison.OrdinalIgnoreCase)
                            && t.Type != TransactionType.CardBillPayment)
                .GroupBy(t => CardBilling.ClosingFor(t.Date, card.ClosingDay!.Value));
            foreach (var group in groups)
            {
                var total = -group.Sum(t => t.Amount);
                if (total <= 0) continue;
                cycles.Add((card, group.Key, CardBilling.DueFor(group.Key, card.DueDay!.Value), total));
            }
        }

        var payments = all.Where(t => t.Type == TransactionType.CardBillPayment).ToList();
        var used = new HashSet<Guid>();
        var ordered = cycles.OrderBy(c => c.Due).ThenBy(c => c.Card.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var paidBy = new Transaction?[ordered.Count];
        var settled = (settlements ?? Array.Empty<CardBillSettlement>())
            .GroupBy(s => (s.Card.ToUpperInvariant(), s.Closing))
            .ToDictionary(g => g.Key, g => g.First().PaidOn);
        var manual = ordered
            .Select(c => settled.TryGetValue((c.Card.Name.ToUpperInvariant(), c.Closing), out var paidOn) ? (DateOnly?)paidOn : null)
            .ToArray();

        // Two passes: payments that name the card (inflow on the card) first, so an anonymous bank outflow is not
        // taken by the wrong card when another card has its own, named payment of the same amount.
        foreach (var named in new[] { true, false })
        {
            for (var i = 0; i < ordered.Count; i++)
            {
                if (paidBy[i] is not null || manual[i] is not null) continue;
                var cycle = ordered[i];
                var best = payments
                    .Where(p => !used.Contains(p.Id)
                                && (p.Amount > 0) == named
                                && Fits(p, cycle.Card, cycle.Closing, cycle.Due, cycle.Total, cardNames))
                    .OrderBy(p => Math.Abs(p.Date.DayNumber - cycle.Due.DayNumber))
                    .FirstOrDefault();
                if (best is null) continue;

                used.Add(best.Id);
                paidBy[i] = best;

                // The same real payment recorded on the other side (bank outflow and card inflow) counts once.
                var twin = payments.FirstOrDefault(p => !used.Contains(p.Id)
                                                        && Math.Abs(Math.Abs(p.Amount) - Math.Abs(best.Amount)) <= 0.01m
                                                        && Math.Sign(p.Amount) != Math.Sign(best.Amount)
                                                        && Math.Abs(p.Date.DayNumber - best.Date.DayNumber) <= TwinDays
                                                        && (best.Amount < 0
                                                            ? string.Equals(p.Account, cycle.Card.Name, StringComparison.OrdinalIgnoreCase)
                                                            : !cardNames.Contains(p.Account)));
                if (twin is not null) used.Add(twin.Id);
            }
        }

        var result = ordered
            .Select((c, i) => new CardBillCycle(c.Card, c.Closing, c.Due, c.Total, paidBy[i], manual[i]))
            .ToList();

        var unmatched = payments
            .Where(p => !used.Contains(p.Id) && p.Amount < 0 && !cardNames.Contains(p.Account))
            .OrderBy(p => p.Date)
            .ToList();

        return new CardBillMatches(result, unmatched);
    }

    private static bool Fits(Transaction payment, Account card, DateOnly closing, DateOnly due, decimal total, HashSet<string> cardNames)
    {
        if (Math.Abs(Math.Abs(payment.Amount) - total) > 0.01m) return false;
        if (payment.Date < closing || payment.Date > due.AddDays(DaysAfterDue)) return false;

        return payment.Amount < 0
            ? !cardNames.Contains(payment.Account)
            : string.Equals(payment.Account, card.Name, StringComparison.OrdinalIgnoreCase);
    }
}
