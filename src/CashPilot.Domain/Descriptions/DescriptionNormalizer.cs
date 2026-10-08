using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CashPilot.Domain.Descriptions;

/// <summary>
/// Turns a bank statement description into a stable form for comparison:
/// uppercase, no accents, and without dates, installments and codes that change on every entry.
/// </summary>
public static partial class DescriptionNormalizer
{
    // "PARC02/03", "PARCELA 3/6"
    [GeneratedRegex(@"PARC(ELA)?\s*\d{1,2}\s*/\s*\d{1,2}")]
    private static partial Regex Installment();

    // "31/08", "01/09/2026"
    [GeneratedRegex(@"\d{1,2}/\d{1,2}(/\d{2,4})?")]
    private static partial Regex Date();

    // long codes such as "SDAU15811467"
    [GeneratedRegex(@"[A-Z]*\d{5,}[A-Z0-9]*")]
    private static partial Regex Code();

    [GeneratedRegex(@"\b\d+\b")]
    private static partial Regex LooseNumber();

    [GeneratedRegex(@"[^A-Z0-9]+")]
    private static partial Regex NonAlphanumeric();

    public static string Normalize(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return string.Empty;

        var s = FoldLookalikes(RemoveAccents(description).ToUpperInvariant());
        s = Installment().Replace(s, " ");
        s = Date().Replace(s, " ");
        s = Code().Replace(s, " ");
        s = LooseNumber().Replace(s, " ");
        s = NonAlphanumeric().Replace(s, " ");
        return s.Trim();
    }

    // Card exports sometimes spell merchants with Greek/Cyrillic letters that look Latin
    // (e.g. "ΑΜΑΖΟΝ" with a Greek alpha), which would otherwise split one merchant in two.
    private static readonly Dictionary<char, char> Lookalikes = new()
    {
        ['\u0391'] = 'A', ['\u0392'] = 'B', ['\u0395'] = 'E', ['\u0396'] = 'Z', ['\u0397'] = 'H',
        ['\u0399'] = 'I', ['\u039A'] = 'K', ['\u039C'] = 'M', ['\u039D'] = 'N', ['\u039F'] = 'O',
        ['\u03A1'] = 'P', ['\u03A4'] = 'T', ['\u03A5'] = 'Y', ['\u03A7'] = 'X',
        ['\u0410'] = 'A', ['\u0412'] = 'B', ['\u0415'] = 'E', ['\u041A'] = 'K', ['\u041C'] = 'M',
        ['\u041D'] = 'H', ['\u041E'] = 'O', ['\u0420'] = 'P', ['\u0421'] = 'C', ['\u0422'] = 'T',
        ['\u0423'] = 'Y', ['\u0425'] = 'X',
    };

    private static string FoldLookalikes(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(Lookalikes.TryGetValue(c, out var latin) ? latin : c);
        return sb.ToString();
    }

    private static string RemoveAccents(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
