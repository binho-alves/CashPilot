using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace CashPilot.Infrastructure.Importing;

/// <param name="Text">One entry per line ("08/10 CIZI MERCADO EXPRESS R$ 35,05"), ready for <see cref="StatementText"/>.</param>
/// <param name="Entries">How many entries were read.</param>
/// <param name="Skipped">Lines left out on purpose (payments of the previous bill, which are not purchases).</param>
public sealed record CardBillPdfResult(string Text, int Entries, int Skipped, int Repaired = 0);

/// <summary>
/// Reads a card bill PDF that has text (the "Extrato da fatura" a bank app exports: Bradesco layout with the columns
/// Data, Histórico, Moeda de origem, US$, Cotação US$, R$). The description is what sits before the "Moeda de origem"
/// column and the amount is the last figure of the line, so the foreign-currency columns never get mixed in.
/// The result is plain text that goes to the same box as pasted text, so the user still sees it before importing.
/// </summary>
public static partial class CardBillPdf
{
    public static CardBillPdfResult? TryRead(IReadOnlyList<PdfWord> words)
    {
        var historico = words.FirstOrDefault(w => Fold(w.Text) == "HISTORICO");
        var moeda = words.FirstOrDefault(w => Fold(w.Text) == "MOEDA");
        var hasQuote = words.Any(w => Fold(w.Text).StartsWith("COTACAO", StringComparison.Ordinal));
        if (historico is null || moeda is null || !hasQuote) return null;

        var descriptionEnd = moeda.Left - 2;
        var builder = new StringBuilder();
        int entries = 0, skipped = 0;

        foreach (var line in Lines(words))
        {
            var first = line[0];
            if (!DateRegex().IsMatch(first.Text)) continue;

            var description = string.Join(' ', line.Skip(1).Where(w => w.Left < descriptionEnd).Select(w => w.Text)).Trim();
            var figures = line.Where(w => w.Left >= descriptionEnd).ToList();
            var amountWord = figures.LastOrDefault(w => TryAmount(w.Text, out _));
            if (description.Length == 0 || amountWord is null) continue;

            TryAmount(amountWord.Text, out var amount);
            var negative = figures.Any(w => w.Text.Contains('-') || w.Text.Contains('−'));

            // "Pagamento efetuado" and the like: the payment of the previous bill shows up as a credit on the bill.
            // It is not a purchase (the bank account side already records it), so it is not imported.
            if (IsNotAPurchase(description))
            {
                skipped++;
                continue;
            }

            builder.Append(first.Text).Append(' ').Append(description).Append(' ')
                .Append(negative ? "-R$ " : "R$ ").Append(amount).Append('\n');
            entries++;
        }

        return entries == 0 && skipped == 0 ? null : new CardBillPdfResult(builder.ToString(), entries, skipped);
    }


    /// <summary>"Pagamento efetuado", "PAGTO. POR DEB EM C/C" (payment of the previous bill) and "SALDO ANTERIOR" are not purchases.</summary>
    private static bool IsNotAPurchase(string description)
    {
        var folded = Fold(description);
        return folded.StartsWith("PAGAMENTO", StringComparison.Ordinal)
               || folded.StartsWith("PAGTO", StringComparison.Ordinal)
               || folded.StartsWith("SALDO ANTERIOR", StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads a card bill PDF. Tries the text first. Many bank apps export the bill as a PDF that is only a picture of
    /// the screen (nothing to copy): then the pictures are read with <paramref name="ocr"/>. Returns the lines for the
    /// text box, or an explanation of what went wrong.
    /// </summary>
    public static (CardBillPdfResult? Result, string? Error) FromPdf(byte[] pdf, Func<byte[], string> ocr)
    {
        IReadOnlyList<PdfWord> words;
        try { words = PdfWordReader.Read(pdf); }
        catch (Exception ex) { return (null, "Não consegui abrir o PDF: " + ex.Message); }

        var direct = TryRead(words);
        if (direct is not null) return (direct, null);

        var pictures = ReadPictures(pdf);
        if (pictures.Count == 0)
        {
            return words.Count == 0
                ? (null, "O PDF não tem texto nem imagens que eu consiga ler.")
                : (null, "O PDF tem texto, mas não reconheci as colunas da fatura (Data, Histórico, Moeda de origem, Cotação). Primeiras palavras lidas: "
                         + string.Join(' ', words.Take(15).Select(w => w.Text)));
        }

        var text = new StringBuilder();
        foreach (var picture in pictures)
            text.AppendLine(ocr(picture));   // InvalidOperationException (missing language file) goes to the caller

        var fromOcr = FromOcrText(text.ToString());
        if (fromOcr is not null) return (fromOcr, null);

        var sample = text.ToString().Trim();
        if (sample.Length > 300) sample = sample[..300] + "…";
        return (null, "O PDF é uma imagem e eu a li, mas não achei lançamentos no formato da fatura (data, descrição, valor). Texto lido: " + sample);
    }

    /// <summary>
    /// Lines like "08/10 CIZI MERCADO EXPRESS USD 0,00 R$ 0,00 R$ 35,05" (what OCR gives for the bill): the foreign
    /// currency columns are dropped and the last figure is the amount. Section titles ("Total para: ...") have no date and
    /// are ignored; a minus before or after "R$" makes a credit.
    /// </summary>
    public static CardBillPdfResult? FromOcrText(string text)
    {
        var entries = new List<OcrEntry>();
        int skipped = 0;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            // Vertical bars come from the row separators of the screenshot, never from a merchant name.
            var line = Regex.Replace(raw.Replace('|', ' ').Trim().TrimStart('[', ']', ' '), @"\s+", " ");
            line = FixDate(line);

            var match = OcrLineRegex().Match(line);
            string? date = null;
            if (match.Success) date = match.Groups["date"].Value;
            else
            {
                // The date came out garbled ("08M0": the slash and the 1 of the month were lost): the day is still the
                // first two characters, the month is rebuilt from the neighbors below.
                var garbled = GarbledDateRegex().Match(line);
                if (!garbled.Success) continue;
                match = OcrLineRegex().Match("00/00 " + garbled.Groups["rest"].Value);
                if (!match.Success) continue;
                date = null;
                line = garbled.Groups["d"].Value.Replace('O', '0').Replace('o', '0');
            }

            var description = Regex.Replace(match.Groups["desc"].Value.Trim(), @"\s+", " ");
            if (description.Length == 0) continue;
            if (IsNotAPurchase(description)) { skipped++; continue; }

            var negative = match.Groups["neg1"].Success || match.Groups["neg2"].Success;
            var day = date is null ? int.Parse(line, CultureInfo.InvariantCulture) : int.Parse(date.Split('/')[0], CultureInfo.InvariantCulture);
            var month = date?.Split('/')[1];
            entries.Add(new OcrEntry(date, day, month, description, negative, match.Groups["amt"].Value));
        }

        // No line with a readable date: maybe the list layout that groups purchases by day (see FromListText).
        if (entries.All(e => e.Date is null) && FromListText(text) is { } list) return list;

        var repaired = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Date is not null) continue;
            var neighbor = NearestKnown(entries, i);
            if (neighbor is { } known && Math.Abs(known.Day - entries[i].Day) <= 15)
            {
                entries[i] = entries[i] with { Month = known.Month };
                repaired++;
            }
        }

        var builder = new StringBuilder();
        foreach (var e in entries)
        {
            var date = e.Date ?? (e.Month is null ? $"{e.Day:00}/??" : $"{e.Day:00}/{e.Month}");
            builder.Append(date).Append(' ').Append(e.Description).Append(' ')
                .Append(e.Negative ? "-R$ " : "R$ ").Append(e.Amount).Append('\n');
        }
        return entries.Count == 0 && skipped == 0 ? null : new CardBillPdfResult(builder.ToString(), entries.Count, skipped, repaired);
    }

    /// <summary>
    /// The other layout, used by apps that group purchases by day (a header "Data, Descrição, Valor"; the day number on the
    /// first purchase of the day with the month name under it, a timeline dot before each description, "R$ 15,99 &gt;" at the end):
    ///   10 O CIZI MERCADO EXPR R$ 15,99 &gt;
    ///   Out
    ///   O CAFE SOL GELATERIA R$ 88,99 &gt;
    /// A purchase without a day belongs to the day above it; the month comes from the month name of its group (or the group above).
    /// </summary>
    private static CardBillPdfResult? FromListText(string text)
    {
        var items = new List<(int Group, string Desc, bool Negative, string Amount)>();
        var groupDay = new List<int?>();
        var groupMonth = new List<int?>();
        int? pendingMonth = null;
        int skipped = 0;

        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = Regex.Replace(raw.Replace('|', ' ').Trim(), @"\s+", " ");
            if (line.Length == 0) continue;

            var monthName = MonthLineRegex().Match(line.Replace('0', 'o').Replace('O', 'o'));
            if (monthName.Success)
            {
                var number = Array.IndexOf(MonthNames, monthName.Groups["m"].Value.ToLowerInvariant()) + 1;
                if (groupMonth.Count > 0 && groupMonth[^1] is null) groupMonth[^1] = number;
                else pendingMonth = number;
                continue;
            }

            var match = ListLineRegex().Match(line);
            if (!match.Success) continue;
            var description = Regex.Replace(match.Groups["desc"].Value.Trim(), @"\s+", " ");
            if (description.Length == 0) continue;
            if (IsNotAPurchase(description)) { skipped++; continue; }

            if (match.Groups["day"].Success && int.TryParse(match.Groups["day"].Value, out var day) && day is >= 1 and <= 31)
            {
                groupDay.Add(day);
                groupMonth.Add(pendingMonth);
                pendingMonth = null;
            }
            else if (groupDay.Count == 0)
            {
                groupDay.Add(null);   // purchases before any day header: the day is unknown
                groupMonth.Add(pendingMonth);
                pendingMonth = null;
            }

            items.Add((groupDay.Count - 1, description, match.Groups["neg1"].Success || match.Groups["neg2"].Success, match.Groups["amt"].Value));
        }
        if (items.Count == 0 && skipped == 0) return null;

        var repaired = 0;
        var builder = new StringBuilder();
        foreach (var item in items)
        {
            var month = groupMonth[item.Group];
            var inherited = false;
            for (var g = item.Group - 1; month is null && g >= 0; g--) { month = groupMonth[g]; inherited = month is not null; }
            if (inherited) repaired++;

            var day = groupDay[item.Group];
            var date = (day is null ? "??" : $"{day:00}") + "/" + (month is null ? "??" : $"{month:00}");
            builder.Append(date).Append(' ').Append(item.Desc).Append(' ')
                .Append(item.Negative ? "-R$ " : "R$ ").Append(item.Amount).Append('\n');
        }
        return new CardBillPdfResult(builder.ToString(), items.Count, skipped, repaired);
    }

    private static readonly string[] MonthNames = ["jan", "fev", "mar", "abr", "mai", "jun", "jul", "ago", "set", "out", "nov", "dez"];

    [GeneratedRegex(@"^(?<m>jan|fev|mar|abr|mai|jun|jul|ago|set|out|nov|dez)[a-z]*\.?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MonthLineRegex();

    // [day + dot] [dot] DESCRIPTION [-]R$ amount [>]. The dot of the timeline is read as O, o, ©, 0, a bullet...; the arrow as > or something alike.
    [GeneratedRegex(
        @"^(?:(?<day>\d{1,2})\s+[OoÓ©°•@Q0(]{1,2}\s+|[OoÓ©°•@Q0(]{1,2}\s+)?(?<desc>.+?)\s+(?<neg1>-\s*)?R[\$S§]?\s*(?<neg2>-\s*)?(?<amt>(?:\d{1,3}(?:\.\d{3})+|\d+),\d{2})\s*[>›»)\]]?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ListLineRegex();

    private sealed record OcrEntry(string? Date, int Day, string? Month, string Description, bool Negative, string Amount);

    /// <summary>The closest entry with a readable date (the one above first), for rebuilding a damaged month.</summary>
    private static (int Day, string Month)? NearestKnown(List<OcrEntry> entries, int index)
    {
        for (var distance = 1; distance < entries.Count; distance++)
        {
            foreach (var j in new[] { index - distance, index + distance })
                if (j >= 0 && j < entries.Count && entries[j].Date is not null)
                    return (entries[j].Day, entries[j].Month!);
        }
        return null;
    }

    [GeneratedRegex(@"^(?<d>[0-3Oo][\dOo])[^\s]{0,4}\s+(?<rest>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex GarbledDateRegex();

    /// <summary>
    /// The page pictures of a PDF, each distinct picture once (an app that prints a long screenshot over several pages
    /// repeats the same image on every page, and reading it three times would triple the entries).
    /// </summary>
    public static List<byte[]> ReadPictures(byte[] pdf)
    {
        var result = new List<byte[]>();
        var seen = new HashSet<string>();
        try
        {
            using var document = PdfDocument.Open(pdf);
            foreach (var page in document.GetPages())
            {
                foreach (var image in page.GetImages())
                {
                    var raw = image.RawBytes.ToArray();
                    if (raw.Length == 0 || !seen.Add(Convert.ToHexString(SHA256.HashData(raw)))) continue;

                    if (image.TryGetPng(out var png) && png is { Length: > 0 }) result.Add(png);
                    else if (raw.Length > 3 && raw[0] == 0xFF && raw[1] == 0xD8) result.Add(raw);   // a JPEG stored as is
                }
            }
        }
        catch (Exception)
        {
            // An unusual image encoding: the caller reports that nothing readable was found.
        }
        return result;
    }

    /// <summary>OCR often drops the slash or reads a zero as the letter O: "0810", "O8/1O" become "08/10".</summary>
    private static string FixDate(string line)
    {
        var m = DamagedDateRegex().Match(line);
        if (!m.Success) return line;
        string Digits(string v) => v.Replace('O', '0').Replace('o', '0');
        var day = Digits(m.Groups["d"].Value);
        var month = Digits(m.Groups["m"].Value);
        if (!int.TryParse(day, out var d) || !int.TryParse(month, out var mo) || d is < 1 or > 31 || mo is < 1 or > 12) return line;
        return day + "/" + month + line[m.Length..];
    }

    [GeneratedRegex(@"^(?<d>[0-3Oo][\dOo])[/\\|]?(?<m>[01Oo][\dOo])(?=\s)", RegexOptions.CultureInvariant)]
    private static partial Regex DamagedDateRegex();

    [GeneratedRegex(
        @"^(?<date>\d{1,2}/\d{2})\s+(?<desc>.+?)\s+(?:[A-Z]{3}\s+[\d.,]+\s+R[\$S§]?\s*[\d.,]+\s+)?(?<neg1>-\s*)?R[\$S§]?\s*(?<neg2>-\s*)?(?<amt>(?:\d{1,3}(?:\.\d{3})+|\d+),\d{2})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex OcrLineRegex();

    private static bool TryAmount(string text, out string amount)
    {
        var cleaned = text.Replace("R$", "", StringComparison.OrdinalIgnoreCase).Trim('-', '−', ' ');
        amount = cleaned;
        return MoneyRegex().IsMatch(cleaned);
    }

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

    private static string Fold(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c);
        return builder.ToString().ToUpperInvariant();
    }

    [GeneratedRegex(@"^\d{1,2}/\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"^(?:\d{1,3}(?:\.\d{3})+|\d+),\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex MoneyRegex();
}
