using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CashPilot.Infrastructure.Importing;

public sealed record StatementTextResult(string Csv, int Entries, IReadOnlyList<RejectedRow> Rejected);

/// <summary>
/// Turns pasted statement or card-bill text into the "Gastos" CSV the importer already understands, for ONE account
/// chosen by the user. Meant for bank apps that only show the list on screen. Accepted lines:
/// <list type="bullet">
/// <item><c>05/10 MERCADO XYZ 19,56</c> or <c>05/10/2026 ...</c> or <c>05 Out ...</c> (date on the line);</item>
/// <item>a line with only a date (<c>05 Out</c>, <c>05/10</c>) that applies to the entries below it, as the apps group them;</item>
/// <item><c>MERCADO XYZ R$ 19,56</c> (uses the last date line);</item>
/// <item><c>Parcela 1 de 3</c> on its own line, added to the entry above as "PARC 01/03";</item>
/// <item>lines starting with <c>#</c> and blank lines are ignored (use # to park provisional entries).</item>
/// </list>
/// Amounts are positive for spending (card bills); a leading minus is a refund or credit.
/// With <c>positiveAreExpenses: false</c> (bank statements) the signs are flipped, so negative means spending.
/// A date without year takes the current year, or the previous one when that would be more than a month ahead.
/// </summary>
public static partial class StatementText
{
    private static readonly string[] Months = ["JAN", "FEV", "MAR", "ABR", "MAI", "JUN", "JUL", "AGO", "SET", "OUT", "NOV", "DEZ"];

    public static StatementTextResult ToGastosCsv(string text, string account, DateOnly today, bool positiveAreExpenses = true)
    {
        var rejected = new List<RejectedRow>();
        var entries = new List<(DateOnly Date, string Description, decimal Amount)>();
        DateOnly? current = null;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var number = i + 1;
            var line = lines[i].Replace('\u00A0', ' ').Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var dateOnly = DateOnlyLineRegex().Match(line);
            if (dateOnly.Success)
            {
                if (TryDate(dateOnly, today, out var parsedDate)) current = parsedDate;
                else rejected.Add(new RejectedRow(number, $"Invalid date '{line}'."));
                continue;
            }

            var installment = InstallmentLineRegex().Match(line);
            if (installment.Success)
            {
                if (entries.Count == 0)
                {
                    rejected.Add(new RejectedRow(number, "Installment line without an entry above it."));
                    continue;
                }
                var last = entries[^1];
                var n = int.Parse(installment.Groups[1].Value, CultureInfo.InvariantCulture);
                var c = int.Parse(installment.Groups[2].Value, CultureInfo.InvariantCulture);
                entries[^1] = last with { Description = $"{last.Description} PARC {n:00}/{c:00}" };
                continue;
            }

            var entry = EntryRegex().Match(line);
            if (!entry.Success)
            {
                rejected.Add(new RejectedRow(number, $"Could not read '{line}' (expected a description and an amount like 19,56)."));
                continue;
            }

            DateOnly date;
            if (entry.Groups["day"].Success)
            {
                if (!TryDate(entry, today, out date))
                {
                    rejected.Add(new RejectedRow(number, $"Invalid date in '{line}'."));
                    continue;
                }
                current = date;
            }
            else if (current is { } known) date = known;
            else
            {
                rejected.Add(new RejectedRow(number, $"No date for '{line}' (put a date line above it)."));
                continue;
            }

            var amount = decimal.Parse(entry.Groups["num"].Value.Replace(".", "").Replace(',', '.'), CultureInfo.InvariantCulture);
            if (entry.Groups["neg"].Success) amount = -amount;
            if (!positiveAreExpenses) amount = -amount;
            if (amount == 0)
            {
                rejected.Add(new RejectedRow(number, $"Zero amount in '{line}'."));
                continue;
            }

            entries.Add((date, entry.Groups["desc"].Value.Trim(), amount));
        }

        var csv = new StringBuilder("Dia;Categoria;Ítem;Conta;Valor;Obs\n");
        foreach (var (date, description, amount) in entries)
        {
            csv.Append(date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)).Append(";;;")
               .Append(Quote(account.Trim())).Append(';')
               .Append(amount.ToString("0.00", new CultureInfo("pt-BR"))).Append(';')
               .Append(Quote(description)).Append('\n');
        }
        return new StatementTextResult(csv.ToString(), entries.Count, rejected);
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static bool TryDate(Match match, DateOnly today, out DateOnly date)
    {
        date = default;
        var day = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
        int month;
        if (match.Groups["month"].Success)
            month = int.Parse(match.Groups["month"].Value, CultureInfo.InvariantCulture);
        else
        {
            var index = Array.IndexOf(Months, Fold(match.Groups["mon"].Value));
            if (index < 0) return false;
            month = index + 1;
        }
        if (month is < 1 or > 12 || day < 1) return false;

        int year;
        if (match.Groups["year"].Success)
        {
            year = int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
            if (year < 100) year += 2000;
        }
        else
        {
            year = today.Year;
            if (day <= DateTime.DaysInMonth(year, month) && new DateOnly(year, month, day) > today.AddDays(31)) year--;
        }

        if (day > DateTime.DaysInMonth(year, month)) return false;
        date = new DateOnly(year, month, day);
        return true;
    }

    private static string Fold(string text)
    {
        var decomposed = text.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c);
        return builder.ToString().ToUpperInvariant();
    }

    private const string DatePart =
        @"(?<day>\d{1,2})(?:\s*/\s*(?<month>\d{1,2})(?:\s*/\s*(?<year>\d{2,4}))?|\s+(?<mon>(?i:jan|fev|mar|abr|mai|jun|jul|ago|set|out|nov|dez))\b\.?)";

    [GeneratedRegex("^" + DatePart + "$", RegexOptions.CultureInvariant)]
    private static partial Regex DateOnlyLineRegex();

    [GeneratedRegex(@"^Parcela\s+(\d{1,2})\s+de\s+(\d{1,2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstallmentLineRegex();

    [GeneratedRegex(
        @"^(?:" + DatePart + @"\s+)?(?<desc>.+?)\s+(?<neg>-)?\s*(?:R\$\s*)?(?:(?<neg>-)\s*)?(?<num>\d{1,3}(?:\.\d{3})*,\d{2}|\d+,\d{2})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex EntryRegex();
}
