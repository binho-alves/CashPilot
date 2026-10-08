using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CashPilot.Domain.Descriptions;

namespace CashPilot.Infrastructure.Importing;

/// <summary>
/// Readers for the statements the owner actually receives: Bradesco (CSV and PDF) and Itaú (PDF).
/// The PDF readers work on word positions (<see cref="PdfWord"/>), not on extracted text, because the layout
/// carries the meaning: which column an amount sits in, which line is the payee.
/// </summary>
public static partial class BankStatementParsers
{
    public const string BradescoCsv = "Bradesco CSV";
    public const string BradescoPdf = "Bradesco PDF";
    public const string ItauPdf = "Itaú PDF";

    private static readonly string[] DateFormats = ["dd/MM/yyyy"];

    // ---------------------------------------------------------------- detection

    /// <summary>Returns null when the words are not a statement this reader knows.</summary>
    public static BankStatement? TryParsePdf(IReadOnlyList<PdfWord> words)
    {
        if (words.Any(w => Fold(w.Text) == "DOCTO.")) return ParseBradescoPdf(words);
        if (words.Any(w => Fold(w.Text) == "LANCAMENTOS") && words.Any(w => Fold(w.Text) == "SALDO"))
            return ParseItauPdf(words);
        return null;
    }

    // ---------------------------------------------------------------- Bradesco CSV

    /// <summary>
    /// "Data;Histórico;Docto.;Crédito (R$);Débito (R$);Saldo (R$)". The file has two blocks (the month and
    /// "Últimos Lançamentos"), "COD. LANC. 0" opening-balance rows and a totals row; only dated rows are entries.
    /// Returns null when the header is not found.
    /// </summary>
    public static BankStatement? TryParseBradescoCsv(string text)
    {
        var table = CsvParser.Parse(text);
        var headerIndex = table.FindIndex(r =>
            r.Count >= 6 && Fold(r[0]) == "DATA" && Fold(r[2]).Contains("DOCTO", StringComparison.Ordinal));
        if (headerIndex < 0) return null;

        var entries = new List<BankEntry>();
        var rejected = new List<RejectedRow>();

        for (var i = headerIndex + 1; i < table.Count; i++)
        {
            var cells = table[i];
            if (cells.Count < 6 || !TryDate(cells[0], out var date)) continue;

            var history = Clean(cells[1]);
            var credit = Money(cells[3]);
            var debit = Money(cells[4]);
            var delta = (credit ?? 0m) - (debit ?? 0m);
            var openingRow = Fold(history).StartsWith("COD. LANC", StringComparison.Ordinal);

            if (openingRow) continue;

            if (history.Length == 0 || delta == 0m)
            {
                rejected.Add(new RejectedRow(i + 1, "Linha sem histórico ou com valor zerado."));
                continue;
            }

            entries.Add(new BankEntry(date, history, "", delta, NullIfEmpty(cells[2])));
        }

        return new BankStatement("Bradesco", BradescoCsv, entries, Array.Empty<string>(), rejected);
    }

    // ---------------------------------------------------------------- Bradesco PDF

    private static BankStatement ParseBradescoPdf(IReadOnlyList<PdfWord> words)
    {
        var entries = new List<BankEntry>();
        var rejected = new List<RejectedRow>();
        var warnings = new List<string>();
        DateOnly? currentDate = null;

        foreach (var page in words.GroupBy(w => w.Page).OrderBy(g => g.Key))
        {
            var pageWords = page.ToList();
            var history = pageWords.FirstOrDefault(w => Fold(w.Text).StartsWith("HISTORICO", StringComparison.Ordinal));
            if (history is null) continue; // a page without the table

            var headerLine = pageWords.Where(w => Math.Abs(w.Top - history.Top) <= 2).ToList();
            var edges = headerLine.Where(w => w.Text.Contains("(R$)", StringComparison.Ordinal))
                .Select(w => w.Right).OrderBy(x => x).ToList();
            var docto = headerLine.FirstOrDefault(w => Fold(w.Text).StartsWith("DOCTO", StringComparison.Ordinal));
            if (edges.Count < 3 || docto is null)
            {
                warnings.Add($"Página {page.Key}: não consegui identificar as colunas da tabela.");
                continue;
            }

            // Amounts are right-aligned under "Crédito (R$)", "Débito (R$)" and "Saldo (R$)".
            var creditEdge = edges[0];
            var debitEdge = edges[1];
            var balanceEdge = edges[2];

            var totalRow = pageWords.FirstOrDefault(w =>
                Fold(w.Text) == "TOTAL" && w.Left < history.Left - 1 && w.Top > history.Top);
            var bodyEnd = totalRow is null ? double.MaxValue : totalRow.Top - 1;
            var body = pageWords.Where(w => w.Top > history.Top + 3 && w.Top < bodyEnd).ToList();

            // Every row ends with a balance column: its position anchors the row (the value itself is ignored).
            var anchors = body.Where(w => IsMoney(w.Text) && Math.Abs(w.Right - balanceEdge) <= 8)
                .OrderBy(w => w.Top).ToList();
            if (anchors.Count == 0) continue;

            // Text of the history column sits half a line above (the bank's wording), on the line (short entries)
            // or half a line below (payee or remitter) the anchor. Each word goes to the nearest anchor.
            var above = anchors.Select(_ => new List<PdfWord>()).ToList();
            var inline = anchors.Select(_ => new List<PdfWord>()).ToList();
            var below = anchors.Select(_ => new List<PdfWord>()).ToList();
            foreach (var word in body.Where(w => w.Left >= history.Left - 4 && w.Left < docto.Left - 12))
            {
                var nearest = 0;
                for (var k = 1; k < anchors.Count; k++)
                    if (Math.Abs(word.Top - anchors[k].Top) < Math.Abs(word.Top - anchors[nearest].Top)) nearest = k;

                var dy = word.Top - anchors[nearest].Top;
                if (Math.Abs(dy) > 8) continue;
                (Math.Abs(dy) <= 1.5 ? inline : dy < 0 ? above : below)[nearest].Add(word);
            }

            for (var k = 0; k < anchors.Count; k++)
            {
                var anchor = anchors[k];
                var line = body.Where(w => Math.Abs(w.Top - anchor.Top) <= 1.5).ToList();

                var dateWord = line.FirstOrDefault(w => w.Left < history.Left && TryDate(w.Text, out _));
                if (dateWord is not null && TryDate(dateWord.Text, out var parsedDate)) currentDate = parsedDate;
                if (currentDate is not { } date)
                {
                    rejected.Add(new RejectedRow(page.Key, "Linha sem data."));
                    continue;
                }

                var creditWord = line.FirstOrDefault(w => IsMoney(w.Text) && Math.Abs(w.Right - creditEdge) <= 8);
                var debitWord = line.FirstOrDefault(w => IsMoney(w.Text) && Math.Abs(w.Right - debitEdge) <= 8);
                var docWord = line.FirstOrDefault(w => IsDigits(w.Text) && w.Left >= docto.Left - 12 && w.Right < creditEdge - 40);

                var wording = Join(above[k].Concat(inline[k]));
                var detail = Join(below[k]);
                var delta = (creditWord is null ? 0m : Money(creditWord.Text) ?? 0m)
                            - (debitWord is null ? 0m : Money(debitWord.Text) ?? 0m);
                var openingRow = Fold(wording).StartsWith("COD. LANC", StringComparison.Ordinal);

                if (openingRow) continue;

                if (wording.Length == 0 || delta == 0m)
                {
                    rejected.Add(new RejectedRow(page.Key, $"Linha de {date:dd/MM/yyyy} sem histórico ou com valor zerado."));
                    continue;
                }

                entries.Add(new BankEntry(date, wording, detail, delta, docWord?.Text));
            }
        }

        return new BankStatement("Bradesco", BradescoPdf, entries, warnings, rejected);
    }

    // ---------------------------------------------------------------- Itaú PDF

    private static BankStatement ParseItauPdf(IReadOnlyList<PdfWord> words)
    {
        var lines = Lines(words);
        var entries = new List<BankEntry>();
        var rejected = new List<RejectedRow>();

        for (var i = 0; i < lines.Count; i++)
        {
            var tokens = lines[i].Select(w => w.Text).ToList();
            if (tokens.Count < 2 || !TryDate(tokens[0], out var date)) continue;

            var body = string.Join(' ', tokens.Skip(1));
            if (Fold(body).Contains("SALDO DO DIA", StringComparison.Ordinal)) continue; // daily summary, not a movement

            var last = tokens[^1].Replace('\u2212', '-');
            if (!IsMoney(last) || Money(last) is not { } amount || amount == 0m)
            {
                rejected.Add(new RejectedRow(i + 1, $"Linha de {date:dd/MM/yyyy} sem valor reconhecível: {body}"));
                continue;
            }

            var description = Clean(string.Join(' ', tokens.Skip(1).Take(tokens.Count - 2)));
            if (description.Length == 0)
            {
                rejected.Add(new RejectedRow(i + 1, $"Linha de {date:dd/MM/yyyy} sem descrição."));
                continue;
            }
            entries.Add(new BankEntry(date, description, "", amount));
        }

        // The statement lists newest first. Stored oldest first; the sort is stable, so the order inside a day is kept.
        return new BankStatement("Itaú", ItauPdf, entries.OrderBy(e => e.Date).ToList(), Array.Empty<string>(), rejected);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Rebuilds visual lines: words of one page whose vertical positions are within a few points.</summary>
    private static List<List<PdfWord>> Lines(IReadOnlyList<PdfWord> words)
    {
        var lines = new List<List<PdfWord>>();
        foreach (var word in words.OrderBy(w => w.Page).ThenBy(w => w.Top).ThenBy(w => w.Left))
        {
            if (lines.Count > 0 && lines[^1][0].Page == word.Page && Math.Abs(word.Top - lines[^1][0].Top) <= 2.5)
                lines[^1].Add(word);
            else
                lines.Add([word]);
        }
        return lines.Select(l => l.OrderBy(w => w.Left).ToList()).ToList();
    }

    private static string Join(IEnumerable<PdfWord> words) =>
        Clean(string.Join(' ', words.OrderBy(w => Math.Round(w.Top, 1)).ThenBy(w => w.Left).Select(w => w.Text)));

    private static bool TryDate(string text, out DateOnly date) =>
        DateOnly.TryParseExact(text.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static bool IsMoney(string text) => MoneyRegex().IsMatch(text.Replace('\u2212', '-'));

    private static bool IsDigits(string text) => text.Length > 0 && text.All(char.IsAsciiDigit);

    private static decimal? Money(string text)
    {
        var trimmed = text.Replace('\u2212', '-').Trim();
        return trimmed.Length > 0 && BrazilianCurrencyParser.TryParse(trimmed, out var value) ? value : null;
    }

    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string Clean(string text) => WhitespaceRegex().Replace(text, " ").Trim();

    /// <summary>Upper case without accents, so header names survive a wrong file encoding.</summary>
    private static string Fold(string text)
    {
        var decomposed = text.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        return builder.ToString().Normalize(NormalizationForm.FormC).ToUpperInvariant();
    }

    [GeneratedRegex(@"^-?\d{1,3}(\.\d{3})*,\d{2}$")]
    private static partial Regex MoneyRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
