namespace CashPilot.Domain.Interest;

/// <summary>
/// Cost of leaving part of a card bill unpaid (revolving credit, then bill installments). Rates are per card (each issuer
/// has its own, printed on the bill). The IOF is the same for every issuer: 0.38% plus 0.0082% per day, up to 365 days.
/// </summary>
public static class CardInterest
{
    public const decimal IofFixedPercent = 0.38m;
    public const decimal IofDailyPercent = 0.0082m;
    public const int IofMaxDays = 365;

    /// <summary>Days the revolving credit lasts: after the next due date the balance becomes a bill installment plan.</summary>
    public const int RevolvingMaxDays = 30;

    public static decimal Iof(decimal amount, int days) =>
        Round(amount * (IofFixedPercent + IofDailyPercent * Math.Min(Math.Max(days, 0), IofMaxDays)) / 100m);

    /// <summary>
    /// Interest on <paramref name="amount"/> for <paramref name="days"/> at a monthly rate, compounded daily
    /// (monthly rate spread over the days of the cycle, as the issuers describe it). A full 30 days costs exactly the monthly rate.
    /// </summary>
    public static decimal Interest(decimal amount, int days, decimal monthlyRatePercent)
    {
        if (days <= 0 || monthlyRatePercent <= 0) return 0m;
        var factor = Math.Pow(1.0 + (double)monthlyRatePercent / 100.0, days / 30.0) - 1.0;
        return Round(amount * (decimal)factor);
    }

    /// <summary>
    /// What it costs to carry <paramref name="amount"/> of the bill for <paramref name="days"/> days: interest plus IOF, never
    /// more than the amount itself (the 100% ceiling on interest and charges since 2024). Up to <see cref="RevolvingMaxDays"/> it is
    /// revolving credit; beyond that the installment rate applies when the card has one (otherwise the revolving rate is kept).
    /// </summary>
    public static decimal CarryCost(decimal amount, int days, decimal revolvingMonthlyPercent, decimal? installmentMonthlyPercent = null)
    {
        if (amount <= 0 || days <= 0) return 0m;
        var rate = days > RevolvingMaxDays && installmentMonthlyPercent is { } installment ? installment : revolvingMonthlyPercent;
        var total = Interest(amount, days, rate) + Iof(amount, days);
        return Math.Min(total, amount);
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
