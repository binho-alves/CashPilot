namespace CashPilot.Infrastructure.Importing;

/// <summary>One line of a bank statement. Signed amount: credit positive, debit negative.</summary>
/// <param name="History">The bank's own wording ("PIX ENVIADO"); used for the dedup key, so the same entry read from a CSV and from a PDF is the same entry.</param>
/// <param name="Detail">Extra line some statements carry (payee, remitter). Empty when the format has none.</param>
public sealed record BankEntry(DateOnly Date, string History, string Detail, decimal Amount, string? Document = null)
{
    public string Description => Detail.Length > 0 ? $"{History} - {Detail}" : History;
}

/// <summary>Only the movements of a statement: balances and daily summaries are deliberately ignored.</summary>
/// <param name="Bank">"Bradesco" or "Itaú"; used to suggest which registered account the file belongs to.</param>
/// <param name="Warnings">Parts of the file that could not be read (e.g. a table whose columns were not found).</param>
public sealed record BankStatement(
    string Bank,
    string Format,
    IReadOnlyList<BankEntry> Entries,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<RejectedRow> Rejected);

/// <summary>A word read from a PDF, with the position needed to rebuild table rows. Top grows downwards.</summary>
public sealed record PdfWord(int Page, string Text, double Left, double Right, double Top);
