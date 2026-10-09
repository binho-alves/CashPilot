using System.Globalization;
using System.Text.RegularExpressions;
using CashPilot.Domain.Reports;
using CashPilot.Domain.Transactions;

namespace CashPilot.Domain.Payments;

/// <summary>
/// A monthly entry found in the history. <see cref="Amount"/> is signed: negative = expense, positive = income.
/// <see cref="Remaining"/> is set when the description counts installments ("09/12", "10/12"): only that many are left.
/// </summary>
public sealed record RecurringEntry(
    string Account, string Description, decimal Amount, int DayOfMonth, DateOnly LastDate, int Months, int? Remaining = null)
{
    public bool IsIncome => Amount > 0;
}

/// <summary>
/// Finds entries that repeat every month (rent, subscriptions, salary...). One entry per month, same account and same
/// description (installments excluded), in at least <see cref="MinMonths"/> of the last <see cref="WindowMonths"/> months,
/// with amounts within <see cref="AmountTolerance"/> of the median and days of the month within
/// <see cref="DayTolerance"/> days of the median, and still active: the latest one is at most
/// <see cref="ActiveDays"/> days old. Months with two entries of the same description (a shop visited twice) disqualify it.
/// A description ending in "NN/MM" whose NN goes up by one each month is an installment the bank wrote without "PARC":
/// it is projected only for the installments that are left (and not at all once the last one was charged).
/// </summary>
public static class RecurringEntries
{
    public const int MinMonths = 3;
    public const int WindowMonths = 6;
    public const decimal AmountTolerance = 0.15m;
    public const int DayTolerance = 5;
    public const int ActiveDays = 45;

    public static IReadOnlyList<RecurringEntry> Detect(IEnumerable<Transaction> transactions, DateOnly today)
    {
        var windowStart = new DateOnly(today.Year, today.Month, 1).AddMonths(-(WindowMonths - 1));
        var result = new List<RecurringEntry>();

        var groups = transactions
            .Where(t => t.Date >= windowStart && t.Date <= today && Qualifies(t))
            .GroupBy(t => (Account: t.Account.Trim().ToUpperInvariant(), t.NormalizedDescription, Income: t.Amount > 0));

        foreach (var group in groups)
        {
            if (group.Key.NormalizedDescription.Length == 0) continue;

            var entries = group.OrderBy(t => t.Date).ToList();
            var months = entries.GroupBy(t => (t.Date.Year, t.Date.Month)).ToList();
            if (months.Count < MinMonths || months.Any(m => m.Count() > 1)) continue;

            var latest = entries[^1];
            if (latest.Date < today.AddDays(-ActiveDays)) continue;

            var amounts = entries.Select(t => Math.Abs(t.Amount)).OrderBy(a => a).ToList();
            var median = amounts[amounts.Count / 2];
            if (amounts.Any(a => Math.Abs(a - median) > median * AmountTolerance)) continue;

            var days = entries.Select(t => t.Date.Day).OrderBy(d => d).ToList();
            var medianDay = days[days.Count / 2];
            if (days.Any(d => Math.Abs(d - medianDay) > DayTolerance)) continue;

            // The amount to expect: median of the last three, so a price change is followed quickly.
            var recent = entries.TakeLast(3).Select(t => Math.Abs(t.Amount)).OrderBy(a => a).ToList();
            var expected = recent[recent.Count / 2];

            var remaining = RemainingInstallments(entries);
            if (remaining == 0) continue;

            result.Add(new RecurringEntry(
                latest.Account, latest.RawDescription, group.Key.Income ? expected : -expected,
                medianDay, latest.Date, months.Count, remaining));
        }

        return result
            .OrderBy(r => r.DayOfMonth)
            .ThenBy(r => r.Description, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Dates the entry is expected on after <paramref name="after"/> (exclusive) up to <paramref name="until"/>.</summary>
    public static IEnumerable<DateOnly> Occurrences(RecurringEntry entry, DateOnly after, DateOnly until)
    {
        var month = new DateOnly(entry.LastDate.Year, entry.LastDate.Month, 1).AddMonths(1);
        var counted = 0;
        while (month <= until)
        {
            if (++counted > (entry.Remaining ?? int.MaxValue)) yield break;
            var date = CardBilling.DayOfMonth(month.Year, month.Month, entry.DayOfMonth);
            if (date > until) yield break;
            if (date > after && date > entry.LastDate) yield return date;
            month = month.AddMonths(1);
        }
    }

    private static readonly Regex TrailingCounter = new(@"(\d{1,2})\s*/\s*(\d{1,2})\s*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Installments left when every entry ends in "NN/MM" with the same MM and NN rising by one from one entry to the
    /// next (and NN not above MM); null when the description is not a counter (a date like "05/10" repeats or jumps).
    /// </summary>
    private static int? RemainingInstallments(IReadOnlyList<Transaction> entries)
    {
        var counters = new List<(int Number, int Total)>();
        foreach (var entry in entries)
        {
            var match = TrailingCounter.Match(entry.RawDescription.Trim());
            if (!match.Success) return null;
            counters.Add((int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                          int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)));
        }

        for (var i = 0; i < counters.Count; i++)
        {
            if (counters[i].Total < 2 || counters[i].Number < 1 || counters[i].Number > counters[i].Total) return null;
            if (i > 0 && (counters[i].Total != counters[i - 1].Total || counters[i].Number != counters[i - 1].Number + 1)) return null;
        }

        var last = counters[^1];
        return last.Total - last.Number;
    }

    private static bool Qualifies(Transaction t)
    {
        if (t.InstallmentCount is > 1) return false;
        return t.Amount < 0
            ? SpendingReport.CountsAsSpending(t)
            : t.Amount > 0 && t.Type == TransactionType.Income;
    }
}
