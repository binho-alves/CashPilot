namespace CashPilot.Domain.Interest;

/// <summary>All of them roll up into one "Juros e encargos" category; the type tells the origin.</summary>
public enum InterestType
{
    /// <summary>Cheque especial / LIS (including IOF).</summary>
    Overdraft = 1,
    LateBill = 2,
    LateCardPayment = 3,
    /// <summary>Fee charged when cashing out through a card terminal.</summary>
    CardCashAdvance = 4,
    BankFee = 5,
}
