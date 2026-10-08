namespace CashPilot.Domain.Interest;

/// <summary>
/// Cash-out using the credit card on a card terminal, with the fee discounted on the spot.
/// The card is charged the gross amount; the account receives the net; the difference is the expense (interest).
/// E.g. R$ 1,000 at 3.08%: cost R$ 30.80, net R$ 969.20.
/// </summary>
public sealed record CardCashAdvance(decimal Gross, decimal FeePercent, int Installments = 1)
{
    public decimal Cost => Math.Round(Gross * FeePercent / 100m, 2, MidpointRounding.AwayFromZero);

    public decimal Net => Gross - Cost;

    /// <summary>Amount of each installment charged on the card (the fee is already in <see cref="Cost"/>).</summary>
    public decimal InstallmentAmount => Math.Round(Gross / Math.Max(Installments, 1), 2, MidpointRounding.AwayFromZero);
}

/// <summary>Terminal fee by number of installments. Only 1x is filled in; the user registers the rest.</summary>
public sealed class CashAdvanceFeeTable
{
    private readonly Dictionary<int, decimal> _fees = new() { [1] = 3.08m };

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
