using System.Globalization;
using System.Text.RegularExpressions;

namespace CashPilot.Infrastructure.Importing;

/// <summary>
/// Reads OFX statements (the SGML flavour of OFX 1.x that Brazilian banks still export, and the XML flavour of 2.x).
/// Only the movements (STMTTRN) are read; balances (LEDGERBAL, AVAILBAL) are ignored on purpose.
/// </summary>
public static partial class OfxParser
{
    public const string Format = "OFX";

    // FI codes (COMPE) of the banks the user is likely to have; any other bank falls back to the file's ORG.
    private static readonly Dictionary<int, string> KnownBanks = new()
    {
        [1] = "Banco do Brasil",
        [33] = "Santander",
        [77] = "Inter",
        [104] = "Caixa",
        [237] = "Bradesco",
        [260] = "Nubank",
        [336] = "C6 Bank",
        [341] = "Itaú",
        [756] = "Sicoob",
    };

    public static bool LooksLikeOfx(string text)
    {
        var head = text.Length > 4000 ? text[..4000] : text;
        return head.Contains("OFXHEADER", StringComparison.OrdinalIgnoreCase)
               || head.Contains("<OFX>", StringComparison.OrdinalIgnoreCase);
    }

    public static BankStatement? TryParse(string text)
    {
        if (!LooksLikeOfx(text)) return null;

        var entries = new List<BankEntry>();
        var rejected = new List<RejectedRow>();
        var warnings = new List<string>();
        string? org = null, bankId = null, currency = null;
        var creditCard = false;
        Dictionary<string, string>? current = null;
        var currentLine = 0;

        void Finish(Dictionary<string, string> fields, int line)
        {
            if (!fields.TryGetValue("DTPOSTED", out var posted) || !TryDate(posted, out var date))
            {
                rejected.Add(new RejectedRow(line, "Lançamento sem data válida (DTPOSTED)."));
                return;
            }

            if (!fields.TryGetValue("TRNAMT", out var raw) || !TryAmount(raw, out var amount) || amount == 0m)
            {
                rejected.Add(new RejectedRow(line, "Lançamento sem valor válido ou com valor zerado (TRNAMT)."));
                return;
            }

            var memo = Clean(fields.GetValueOrDefault("MEMO", ""));
            var name = Clean(fields.GetValueOrDefault("NAME", ""));
            var history = memo.Length > 0 ? memo : name;
            if (history.Length == 0)
            {
                rejected.Add(new RejectedRow(line, "Lançamento sem descrição (MEMO e NAME vazios)."));
                return;
            }

            var detail = memo.Length > 0 && name.Length > 0 && !name.Equals(memo, StringComparison.OrdinalIgnoreCase) ? name : "";
            var document = Clean(fields.GetValueOrDefault("CHECKNUM", ""));
            if (document.Length == 0) document = Clean(fields.GetValueOrDefault("FITID", ""));

            entries.Add(new BankEntry(date, history, detail, amount, document.Length > 0 ? document : null));
        }

        foreach (Match match in TagRegex().Matches(text))
        {
            var closing = match.Groups[1].Length > 0;
            var tag = match.Groups[2].Value.ToUpperInvariant();
            var value = Unescape(match.Groups[3].Value.Trim());

            if (tag == "STMTTRN")
            {
                if (current is not null)
                {
                    Finish(current, currentLine);
                    current = null;
                }

                if (!closing)
                {
                    current = new Dictionary<string, string>(StringComparer.Ordinal);
                    currentLine = LineOf(text, match.Index);
                }
                continue;
            }

            if (closing) continue;

            if (current is not null)
            {
                current[tag] = value;
                continue;
            }

            switch (tag)
            {
                case "ORG": org ??= value; break;
                case "BANKID": bankId ??= value; break;
                case "CURDEF": currency ??= value; break;
                case "CCSTMTRS": creditCard = true; break;
            }
        }

        if (current is not null) Finish(current, currentLine);

        var bank = int.TryParse(bankId, NumberStyles.None, CultureInfo.InvariantCulture, out var code)
                   && KnownBanks.TryGetValue(code, out var known)
            ? known
            : string.IsNullOrWhiteSpace(org) ? "OFX" : Clean(org!);

        if (!string.IsNullOrWhiteSpace(currency) && !currency.Equals("BRL", StringComparison.OrdinalIgnoreCase))
            warnings.Add($"O arquivo diz que a moeda é {currency}, não BRL. Confira os valores antes de importar.");
        if (creditCard)
            warnings.Add("Este OFX é de cartão de crédito. Compras entram como gasto; pagamentos de fatura (valores positivos) ficam pendentes para você classificar.");
        if (entries.Count == 0 && rejected.Count == 0)
            warnings.Add("Nenhum lançamento encontrado no arquivo OFX.");

        return new BankStatement(bank, Format, entries, warnings, rejected);
    }

    /// <summary>OFX dates are yyyyMMdd optionally followed by time and time zone; only the day is kept.</summary>
    private static bool TryDate(string value, out DateOnly date)
    {
        date = default;
        return value.Length >= 8
               && DateOnly.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    /// <summary>The spec says "." but some exports use ",": whichever separator comes last is the decimal one.</summary>
    private static bool TryAmount(string value, out decimal amount)
    {
        var s = value.Replace(" ", "");
        var comma = s.LastIndexOf(',');
        var dot = s.LastIndexOf('.');
        if (comma > dot) s = s.Replace(".", "").Replace(',', '.');
        else s = s.Replace(",", "");
        return decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out amount);
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
            if (text[i] == '\n') line++;
        return line;
    }

    private static string Unescape(string value) => value.Contains('&')
        ? value.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&amp;", "&")
        : value;

    private static string Clean(string value) => WhitespaceRegex().Replace(value, " ").Trim();

    // A tag and the text after it up to the next tag (SGML leaf elements have no closing tag).
    [GeneratedRegex(@"<(/?)([A-Za-z][A-Za-z0-9_.]*)>([^<]*)")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
