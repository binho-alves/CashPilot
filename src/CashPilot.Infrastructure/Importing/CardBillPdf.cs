using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace CashPilot.Infrastructure.Importing;

/// <param name="Text">One entry per line ("08/10 CIZI MERCADO EXPRESS R$ 35,05"), ready for <see cref="StatementText"/>.</param>
/// <param name="Entries">How many entries were read.</param>
/// <param name="Skipped">Lines left out on purpose (payments of the previous bill, which are not purchases).</param>
public sealed record CardBillPdfResult(string Text, int Entries, int Skipped);

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
        var builder = new StringBuilder();
        int entries = 0, skipped = 0;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var match = OcrLineRegex().Match(raw.Trim().TrimStart('|', '[', ']', ' '));
            if (!match.Success) continue;

            var description = Regex.Replace(match.Groups["desc"].Value.Trim(), @"\s+", " ");
            if (description.Length == 0) continue;
            if (IsNotAPurchase(description)) { skipped++; continue; }

            var negative = match.Groups["neg1"].Success || match.Groups["neg2"].Success;
            builder.Append(match.Groups["date"].Value).Append(' ').Append(description).Append(' ')
                .Append(negative ? "-R$ " : "R$ ").Append(match.Groups["amt"].Value).Append('\n');
            entries++;
        }
        return entries == 0 && skipped == 0 ? null : new CardBillPdfResult(builder.ToString(), entries, skipped);
    }

    /// <summary>
    /// The page pictures of a PDF, each distinct picture once (an app that prints a long screenshot over several pages
    /// repeats the same image on every page, and reading it three times would triple the entries).
    /// </summary>
    private static List<byte[]> ReadPictures(byte[] pdf)
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
