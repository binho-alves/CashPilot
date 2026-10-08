using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CashPilot.Domain.Descriptions;

namespace CashPilot.Domain.Transactions;

/// <summary>
/// One entry on an account or card. Signed amount: negative = outflow, positive = inflow.
/// </summary>
public sealed record Transaction
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Account { get; init; }
    public required DateOnly Date { get; init; }
    public required decimal Amount { get; init; }
    public required string RawDescription { get; init; }
    public string? Category { get; init; }
    public string? Item { get; init; }
    public TransactionType Type { get; init; } = TransactionType.Undefined;
    public int? InstallmentNumber { get; init; }
    public int? InstallmentCount { get; init; }

    public string NormalizedDescription => DescriptionNormalizer.Normalize(RawDescription);

    /// <summary>
    /// Key for idempotent imports: sending the same file twice does not duplicate entries.
    /// <paramref name="occurrence"/> tells apart genuinely identical entries inside one file
    /// (1 for the first, 2 for the second...).
    /// </summary>
    public string ComputeDedupKey(int occurrence = 1)
    {
        var raw = string.Join('|',
            Account.Trim().ToUpperInvariant(),
            Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            Amount.ToString("F2", CultureInfo.InvariantCulture),
            NormalizedDescription,
            InstallmentNumber?.ToString(CultureInfo.InvariantCulture) ?? "",
            InstallmentCount?.ToString(CultureInfo.InvariantCulture) ?? "",
            occurrence.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
}
