using System.Globalization;

namespace CashPilot.Contracts;

/// <summary>Reads an amount as typed on a phone keyboard: "12,50", "12.50", "1.250,00", "1,250.00", "R$ 8".</summary>
public static class MoneyInput
{
    public static bool TryParse(string? text, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Replace("R$", "", StringComparison.OrdinalIgnoreCase).Replace(" ", "").Trim();
        if (s.Length == 0 || s.Any(c => !char.IsDigit(c) && c != ',' && c != '.')) return false;

        string integerPart, fraction = "";
        if (!s.Contains(',') && IsThousandsOnly(s))
        {
            // "1.250" or "1.250.300": dots are thousands marks, there are no cents.
            integerPart = s.Replace(".", "");
        }
        else
        {
            var at = Math.Max(s.LastIndexOf(','), s.LastIndexOf('.'));
            if (at < 0)
            {
                integerPart = s;
            }
            else
            {
                var separator = s[at];
                var other = s[..at];
                if (other.Contains(separator)) return false;   // "1,2,3"
                integerPart = other.Replace(".", "").Replace(",", "");
                fraction = s[(at + 1)..];
            }
        }

        if (integerPart.Length == 0) integerPart = "0";
        if (fraction.Length > 2) return false;

        var normalized = fraction.Length == 0 ? integerPart : integerPart + "." + fraction;
        return decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsThousandsOnly(string s)
    {
        if (!s.Contains('.')) return false;
        var groups = s.Split('.');
        return groups[0].Length is >= 1 and <= 3 && groups.Skip(1).All(g => g.Length == 3);
    }
}
