namespace CashPilot.Domain.Payments;

/// <summary>A bill the user registered by hand (boleto, utility, anything with a due date). Amount is positive.</summary>
public sealed record Payable
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Description { get; init; }
    public required DateOnly DueDate { get; init; }
    public required decimal Amount { get; init; }
    public bool Paid { get; init; }
    public DateOnly? PaidDate { get; init; }

    /// <summary>One-off penalty (multa) charged when paid after the due date, in percent of the amount. Printed on the boleto.</summary>
    public decimal? LateFeePercent { get; init; }

    /// <summary>Late interest (juros de mora) in percent per month, charged pro rata by day of delay. Printed on the boleto.</summary>
    public decimal? LateInterestMonthlyPercent { get; init; }
}
