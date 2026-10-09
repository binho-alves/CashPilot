using System.Globalization;
using System.Text;
using CashPilot.Domain.Transactions;

namespace CashPilot.Infrastructure.Backup;

/// <summary>
/// The entries as a CSV to open in Excel (semicolon separated, comma decimals, Brazilian dates). It is a readable
/// copy of the data, not a restore format: restoring is done with a copy of the database.
/// </summary>
public static class TransactionExport
{
    private static readonly CultureInfo Br = new("pt-BR");

    public const string Header = "Data;Conta;Descrição;Valor;Tipo;Categoria;Item;Parcela";

    public static string ToCsv(IEnumerable<Transaction> transactions)
    {
        var builder = new StringBuilder(Header).Append("\r\n");
        foreach (var t in transactions.OrderBy(t => t.Date).ThenBy(t => t.Account, StringComparer.CurrentCultureIgnoreCase))
        {
            var installment = t.InstallmentCount is > 1 ? $"{t.InstallmentNumber}/{t.InstallmentCount}" : "";
            builder
                .Append(t.Date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)).Append(';')
                .Append(Text(t.Account)).Append(';')
                .Append(Text(t.RawDescription)).Append(';')
                .Append(t.Amount.ToString("F2", Br)).Append(';')
                .Append(TypeLabel(t.Type)).Append(';')
                .Append(Text(t.Category ?? "")).Append(';')
                .Append(Text(t.Item ?? "")).Append(';')
                .Append(installment)
                .Append("\r\n");
        }
        return builder.ToString();
    }

    /// <summary>Quotes a text field when needed and defuses a leading character a spreadsheet would run as a formula.</summary>
    private static string Text(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        return value.IndexOfAny([';', '"', '\n', '\r']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    private static string TypeLabel(TransactionType type) => type switch
    {
        TransactionType.Expense => "Gasto",
        TransactionType.Income => "Receita",
        TransactionType.InternalTransfer => "Transferência própria",
        TransactionType.CardBillPayment => "Pagamento de fatura",
        TransactionType.CardCashAdvance => "Saque maquininha",
        TransactionType.Deposit => "Entrada",
        _ => "Indefinido",
    };
}
