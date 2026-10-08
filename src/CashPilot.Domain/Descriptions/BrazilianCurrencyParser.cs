using System.Globalization;

namespace CashPilot.Domain.Descriptions;

/// <summary>Converts texts such as "R$ 1.234,56", "-R$ 5,00" or "(R$ 5,00)" into decimal.</summary>
public static class BrazilianCurrencyParser
{
    public static decimal Parse(string text)
    {
        if (!TryParse(text, out var value))
            throw new FormatException($"Invalid monetary value: '{text}'.");
        return value;
    }

    public static bool TryParse(string? text, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Trim();
        var negative = s.StartsWith('-') || (s.StartsWith('(') && s.EndsWith(')'));

        s = s.Replace("R$", "", StringComparison.OrdinalIgnoreCase)
             .Replace("(", "").Replace(")", "").Replace("-", "")
             .Replace(" ", "").Replace(" ", "")
             .Replace(".", "").Replace(",", ".");

        if (!decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            return false;

        value = negative ? -parsed : parsed;
        return true;
    }
}
