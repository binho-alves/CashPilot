namespace CashPilot.Domain.Transactions;

public sealed record TransferPair(Guid OutgoingId, Guid IncomingId);

/// <summary>
/// Finds outgoing/incoming pairs between different accounts of the owner
/// (same amount, opposite signs, nearby dates). Such pairs are not spending.
/// </summary>
public static class TransferDetector
{
    public static IReadOnlyList<TransferPair> Detect(
        IEnumerable<Transaction> transactions,
        int windowDays = 3)
    {
        var candidates = transactions
            .Where(t => t.Amount != 0
                        && t.Type is TransactionType.Undefined or TransactionType.Expense or TransactionType.Income)
            .ToList();

        var incoming = candidates.Where(t => t.Amount > 0).ToList();
        var used = new HashSet<Guid>();
        var pairs = new List<TransferPair>();

        foreach (var outgoing in candidates.Where(t => t.Amount < 0).OrderBy(t => t.Date))
        {
            var match = incoming
                .Where(i => !used.Contains(i.Id)
                            && i.Amount == -outgoing.Amount
                            && !string.Equals(i.Account, outgoing.Account, StringComparison.OrdinalIgnoreCase)
                            && Math.Abs(i.Date.DayNumber - outgoing.Date.DayNumber) <= windowDays)
                .OrderBy(i => Math.Abs(i.Date.DayNumber - outgoing.Date.DayNumber))
                .FirstOrDefault();

            if (match is null) continue;

            used.Add(match.Id);
            pairs.Add(new TransferPair(outgoing.Id, match.Id));
        }

        return pairs;
    }
}
