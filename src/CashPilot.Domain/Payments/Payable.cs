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
}
