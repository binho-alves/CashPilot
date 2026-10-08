namespace CashPilot.Domain.Transactions;

/// <summary>
/// Recognizes from the (already normalized) text that an entry is a credit card bill payment.
/// The patterns are Brazilian-bank wording and configurable, since each bank words it differently.
/// </summary>
public sealed class CardBillPaymentDetector
{
    public static readonly string[] DefaultPatterns =
    {
        "PAGAMENTO FATURA",
        "PAG FATURA",
        "PGTO FATURA",
        "PAGTO FATURA",
        "PAGAMENTO DE FATURA",
        "PAGAMENTO EFETUADO",
        "PAGAMENTO RECEBIDO",
    };

    private readonly string[] _patterns;

    public CardBillPaymentDetector(IEnumerable<string>? patterns = null)
    {
        _patterns = (patterns ?? DefaultPatterns).ToArray();
    }

    public bool IsCardBillPayment(string normalizedDescription) =>
        _patterns.Any(p => normalizedDescription.Contains(p, StringComparison.Ordinal));
}
