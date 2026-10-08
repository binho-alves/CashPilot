namespace CashPilot.Domain.Transactions;

public enum TransactionType
{
    /// <summary>Not classified yet.</summary>
    Undefined = 0,
    Expense = 1,
    Income = 2,
    /// <summary>Pix/TED between the owner's own accounts. Neither spending nor income.</summary>
    InternalTransfer = 3,
    /// <summary>Credit card bill payment. The purchases are already on the card lines; counting it again would double the spending.</summary>
    CardBillPayment = 4,
}
