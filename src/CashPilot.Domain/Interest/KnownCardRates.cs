using System.Globalization;
using System.Text;

namespace CashPilot.Domain.Interest;

/// <summary>Monthly rates of a card. <see cref="Estimated"/> is true when no bill of that card gave the figure.</summary>
public sealed record KnownCardRate(string Key, decimal RevolvingPercent, decimal? InstallmentPercent, bool Estimated);

/// <summary>
/// Rates printed on real bills (September 2026), found by words in the account name. Only a starting point: the bill of
/// each card is the source of truth and the figures change, so they are filled in only where the card has none yet.
/// </summary>
public static class KnownCardRates
{
    public const decimal GenericEstimate = 15.00m;

    // Order matters: the first key contained in the normalized name wins, so the specific ones come first.
    private static readonly KnownCardRate[] Table =
    [
        new("flm", 15.99m, 13.30m, false),            // Bradesco PJ bill
        new("bradesco", 15.99m, 13.30m, true),        // Bradesco PF: statement had no rates; PJ figures as a guess
        new("brasilcard", 19.99m, 12.99m, false),
        new("digio", 18.99m, null, false),
        new("azulinfinite", 16.10m, null, false),
        new("azulplatinum", 16.10m, null, false),
        new("latam", 16.10m, 10.50m, false),
        new("paodeacucar", 17.00m, null, false),
        new("uniclassmult", 15.23m, null, false),
        new("uniclass", 15.23m, 13.40m, false),
        new("mercadopago", 17.90m, 15.90m, false),
        new("mercadolivre", 17.90m, 15.90m, false),
        new("picpay", 14.90m, null, false),
        new("nubank", GenericEstimate, null, true),
        new("neon", GenericEstimate, null, true),
        new("inter", GenericEstimate, null, true),
    ];

    public static KnownCardRate? Find(string accountName)
    {
        var normalized = Normalize(accountName);
        return Table.FirstOrDefault(rate => normalized.Contains(rate.Key, StringComparison.Ordinal));
    }

    private static string Normalize(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }
}
