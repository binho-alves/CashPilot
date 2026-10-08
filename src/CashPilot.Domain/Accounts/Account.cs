namespace CashPilot.Domain.Accounts;

public enum AccountKind
{
    BankAccount = 1,
    CreditCard = 2,
    Cash = 3,
    Other = 4,
}

/// <summary>
/// A registered account. Credit cards use limit and closing/due days; bank accounts can carry overdraft (LIS) terms:
/// the number of interest-free days and the monthly interest rate, used later by the cost simulator.
/// </summary>
public sealed record Account
{
    public required string Name { get; init; }
    public AccountKind Kind { get; init; } = AccountKind.BankAccount;

    /// <summary>Credit card limit.</summary>
    public decimal? CreditLimit { get; init; }
    public int? ClosingDay { get; init; }
    public int? DueDay { get; init; }

    /// <summary>Overdraft (LIS) limit on a bank account.</summary>
    public decimal? OverdraftLimit { get; init; }
    /// <summary>Days of overdraft without interest.</summary>
    public int? OverdraftFreeDays { get; init; }
    /// <summary>Overdraft interest, percent per month (e.g. 8 for 8%).</summary>
    public decimal? OverdraftMonthlyRatePercent { get; init; }

    /// <summary>
    /// Hand-set balance and the date it was set. The current balance is this amount plus the entries dated after it,
    /// so a wrong figure is fixed by typing the real balance again.
    /// </summary>
    public decimal? BalanceAnchor { get; init; }
    public DateOnly? BalanceAnchorDate { get; init; }
}
