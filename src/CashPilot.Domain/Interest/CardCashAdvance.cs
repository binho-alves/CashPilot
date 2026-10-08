namespace CashPilot.Domain.Interest;

/// <summary>
/// Cash-out using the credit card on a card terminal, with the fee discounted on the spot.
/// The card is charged the gross amount; the account receives the net; the difference is the expense (interest).
/// E.g. R$ 1,000 at 3.09% (1x): cost R$ 30.90, net R$ 969.10.
/// </summary>
public sealed record CardCashAdvance(decimal Gross, decimal FeePercent, int Installments = 1)
{
    public decimal Cost => Math.Round(Gross * FeePercent / 100m, 2, MidpointRounding.AwayFromZero);

    public decimal Net => Gross - Cost;

    /// <summary>Amount of each installment charged on the card (the fee is already in <see cref="Cost"/>).</summary>
    public decimal InstallmentAmount => Math.Round(Gross / Math.Max(Installments, 1), 2, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Terminal fee (percent of the gross amount, charged once) by number of installments.
/// Ships with the credit table read from the terminal app (fees not passed on to the payer).
/// Use <see cref="Set"/> when the terminal changes its fees.
/// </summary>
public sealed class CashAdvanceFeeTable
{
    private static readonly Dictionary<int, decimal> Defaults = new()
    {
        [1] = 3.09m,
        [2] = 5.79m,
        [3] = 6.09m,
        [4] = 7.99m,
        [5] = 8.09m,
        [6] = 8.19m,
        [7] = 9.49m,
        [8] = 9.68m,
        [9] = 10.37m,
        [10] = 11.05m,
        [11] = 12.27m,
        [12] = 12.38m,
    };

    private readonly Dictionary<int, decimal> _fees = new(Defaults);

    public void Set(int installments, decimal feePercent)
    {
        if (installments < 1) throw new ArgumentOutOfRangeException(nameof(installments));
        _fees[installments] = feePercent;
    }

    public bool TryGet(int installments, out decimal feePercent) =>
        _fees.TryGetValue(installments, out feePercent);

    public CardCashAdvance Simulate(decimal gross, int installments = 1)
    {
        if (!_fees.TryGetValue(installments, out var fee))
            throw new InvalidOperationException($"Fee for {installments}x is not registered yet.");
        return new CardCashAdvance(gross, fee, installments);
    }
}
