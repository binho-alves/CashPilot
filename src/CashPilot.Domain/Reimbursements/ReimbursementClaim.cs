using CashPilot.Domain.Transactions;

namespace CashPilot.Domain.Reimbursements;

public enum ClaimStatus
{
    /// <summary>Claim sent to the health plan, waiting for the payment.</summary>
    Requested = 1,
    /// <summary>The plan asked for more documents: the wait starts over.</summary>
    AwaitingDocuments = 2,
    /// <summary>The user gave up on the rest (denied or paid in part): the difference is now a final cost.</summary>
    Closed = 3,
}

/// <summary>Part of one incoming Pix (<see cref="PixId"/>) that paid part or all of one claim.</summary>
public sealed record ReimbursementPayment(Guid ClaimId, Guid PixId, decimal Amount);

/// <summary>A suggested or chosen split of a Pix over a claim.</summary>
public sealed record Allocation(Guid ClaimId, decimal Amount);

/// <summary>
/// An expense paid to a provider that was sent to the health plan for reimbursement. The plan pays the whole amount
/// or less, never more, by Pix, sometimes several claims in one Pix. The expense stays a normal expense; the money that
/// comes back is the Pix(es) linked to it, so the real cost is <see cref="Difference"/>.
/// </summary>
public sealed record ReimbursementClaim(
    Transaction Expense,
    DateOnly RequestedOn,
    DateOnly ExpectedOn,
    ClaimStatus Status,
    string? Note,
    IReadOnlyList<ReimbursementPayment> Payments)
{
    public Guid Id => Expense.Id;

    /// <summary>What was paid to the provider (positive).</summary>
    public decimal Paid => -Expense.Amount;

    public decimal Received => Payments.Sum(p => p.Amount);

    /// <summary>Still to come if the plan pays everything.</summary>
    public decimal Remaining => Math.Max(0m, Paid - Received);

    /// <summary>Paid in full by the plan.</summary>
    public bool Settled => Remaining <= 0m;

    /// <summary>Still waiting for money.</summary>
    public bool IsOpen => !Settled && Status != ClaimStatus.Closed;

    /// <summary>What it really cost so far: paid minus received. Final once the claim is settled or closed.</summary>
    public decimal Difference => Paid - Received;

    public bool IsLate(DateOnly today) => IsOpen && ExpectedOn < today;

    public int DaysWaiting(DateOnly today) => Math.Max(0, today.DayNumber - RequestedOn.DayNumber);
}

public sealed record ReimbursementTotals(
    int OpenCount, int LateCount, decimal Waiting, decimal Paid, decimal Received, decimal FinalDifference);

/// <summary>Claims of one month (the month of the expense): what was paid, what came back and what is still open.</summary>
public sealed record ReimbursementMonth(int Year, int Month, int Claims, decimal Paid, decimal Received, decimal Waiting, decimal Lost)
{
    /// <summary>What the month really cost so far: paid minus received (still counts what is waiting).</summary>
    public decimal NetCost => Paid - Received;

    /// <summary>Share of the paid amount that came back, 0 to 1.</summary>
    public decimal? ReimbursedFraction => Paid > 0 ? Received / Paid : null;
}

public sealed record AllocationSuggestion(IReadOnlyList<Allocation> Items, bool Exact);

public static class ReimbursementRules
{
    /// <summary>Days the plan takes to pay, counted from the request (or from the last documents sent).</summary>
    public const int ExpectedDays = 30;

    /// <summary>Claims looked at when searching for a combination that adds up to a Pix.</summary>
    public const int MaxCombination = 16;

    public static ReimbursementTotals Summarize(IEnumerable<ReimbursementClaim> claims, DateOnly today)
    {
        var list = claims.ToList();
        var open = list.Where(c => c.IsOpen).ToList();
        return new ReimbursementTotals(
            open.Count,
            open.Count(c => c.IsLate(today)),
            open.Sum(c => c.Remaining),
            list.Sum(c => c.Paid),
            list.Sum(c => c.Received),
            // Cost that will not come back: claims already settled (zero) or closed with something missing.
            list.Where(c => !c.IsOpen).Sum(c => c.Difference));
    }

    /// <summary>By month of the expense, newest first. <c>Waiting</c> is what open claims still expect; <c>Lost</c> is what
    /// settled or closed claims did not get back.</summary>
    public static IReadOnlyList<ReimbursementMonth> ByMonth(IEnumerable<ReimbursementClaim> claims) =>
        claims
            .GroupBy(c => (c.Expense.Date.Year, c.Expense.Date.Month))
            .OrderByDescending(g => g.Key.Year).ThenByDescending(g => g.Key.Month)
            .Select(g => new ReimbursementMonth(
                g.Key.Year, g.Key.Month, g.Count(),
                g.Sum(c => c.Paid),
                g.Sum(c => c.Received),
                g.Where(c => c.IsOpen).Sum(c => c.Remaining),
                g.Where(c => !c.IsOpen).Sum(c => c.Difference)))
            .ToList();

    /// <summary>
    /// Suggests how a Pix of <paramref name="pixAmount"/> splits over the open claims. First looks for the smallest
    /// group of claims, oldest first, whose remaining amounts add up exactly to the Pix (the plan paid in full).
    /// Without one, fills the oldest claims in order until the Pix runs out (a partial payment); the user adjusts.
    /// </summary>
    public static AllocationSuggestion Suggest(decimal pixAmount, IEnumerable<ReimbursementClaim> openClaims)
    {
        var claims = openClaims
            .Where(c => c.IsOpen)
            .OrderBy(c => c.RequestedOn).ThenBy(c => c.Expense.Date)
            .ToList();

        var target = ToCents(pixAmount);
        var cents = claims.Take(MaxCombination).Select(c => ToCents(c.Remaining)).ToList();

        for (var size = 1; size <= cents.Count; size++)
        {
            var chosen = new List<int>();
            if (Search(cents, target, size, 0, 0, chosen))
                return new AllocationSuggestion(chosen.Select(i => new Allocation(claims[i].Id, claims[i].Remaining)).ToList(), true);
        }

        var items = new List<Allocation>();
        var left = pixAmount;
        foreach (var claim in claims)
        {
            if (left <= 0m) break;
            var amount = Math.Min(left, claim.Remaining);
            items.Add(new Allocation(claim.Id, amount));
            left -= amount;
        }
        return new AllocationSuggestion(items, false);
    }

    private static bool Search(IReadOnlyList<long> cents, long target, int size, int start, long sum, List<int> chosen)
    {
        if (chosen.Count == size) return sum == target;
        for (var i = start; i < cents.Count; i++)
        {
            if (sum + cents[i] > target) continue;
            chosen.Add(i);
            if (Search(cents, target, size, i + 1, sum + cents[i], chosen)) return true;
            chosen.RemoveAt(chosen.Count - 1);
        }
        return false;
    }

    private static long ToCents(decimal value) => (long)Math.Round(value * 100m, MidpointRounding.AwayFromZero);
}
