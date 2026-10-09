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

    /// <summary>One-off penalty on the unpaid balance of a bill paid late (printed identically on the Itaú, Bradesco, BrasilCard and Digio bills).</summary>
    public const decimal LateFeePercent = 2m;
    /// <summary>Juros de mora, simple, percent per month, from the due date.</summary>
    public const decimal LateInterestMonthlyPercent = 1m;

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

    /// <summary>
    /// Cost of paying <paramref name="amount"/> of a bill <paramref name="daysLate"/> days after the due date, when nothing
    /// (or less than the minimum) was paid: the contract interest at the card's rate, juros de mora (1% a.m.), the 2% multa and
    /// the IOF, all counted from the due date, never more than the amount itself.
    /// </summary>
    public static decimal LateCost(decimal amount, int daysLate, decimal revolvingMonthlyPercent)
    {
        if (amount <= 0 || daysLate <= 0) return 0m;
        var fee = Round(amount * LateFeePercent / 100m);
        var mora = Round(amount * LateInterestMonthlyPercent / 100m * daysLate / 30m);
        var total = fee + mora + Interest(amount, daysLate, revolvingMonthlyPercent) + Iof(amount, daysLate);
        return Math.Min(total, amount);
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
