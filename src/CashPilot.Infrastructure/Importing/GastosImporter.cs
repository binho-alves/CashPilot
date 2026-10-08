using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CashPilot.Domain.Descriptions;
using CashPilot.Domain.Transactions;
using CashPilot.Infrastructure.Persistence;

namespace CashPilot.Infrastructure.Importing;

public sealed record RejectedRow(int Line, string Reason);

public sealed record ImportResult(
    int RowsRead,
    int Inserted,
    int Duplicates,
    int TaughtFromSheet,
    int ClassifiedByRules,
    int Pending,
    int CardCashOut,
    IReadOnlyList<RejectedRow> Rejected);

public sealed class GastosImportOptions
{
    /// <summary>
    /// In the spreadsheet, spending is written as a positive "Valor". When true the amount is stored negative (outflow)
    /// and a negative "Valor" (refund, credit) becomes an inflow.
    /// </summary>
    public bool ValuesArePositiveExpenses { get; init; } = true;
}

/// <summary>
/// Imports the "Gastos" tab exported as CSV. Expected header (any order, accents/case ignored):
/// Dia, Categoria, Ítem, Conta, Valor, Obs. The merchant text lives in Obs; Categoria/Ítem are the user's own labels.
/// Rows that already have a category teach the classifier; rows without one are classified by it, or stay pending.
/// Idempotent: importing the same file again inserts nothing.
/// </summary>
public sealed partial class GastosImporter
{
    private static readonly string[] DateFormats = ["dd/MM/yyyy", "d/M/yyyy", "dd/MM/yy", "d/M/yy"];

    private readonly CashPilotStore _store;
    private readonly GastosImportOptions _options;
    private readonly CardBillPaymentDetector _billDetector = new();

    public GastosImporter(CashPilotStore store, GastosImportOptions? options = null)
    {
        _store = store;
        _options = options ?? new GastosImportOptions();
    }

    public ImportResult Import(TextReader reader, string? sourceName = null)
    {
        var table = CsvParser.Parse(reader.ReadToEnd());
        var rejected = new List<RejectedRow>();
        var parsed = ParseRows(table, rejected);

        var taught = 0;
        var byRules = 0;
        var inserted = 0;
        var duplicates = 0;
        var pending = 0;
        var cashOutCount = 0;

        _store.InTransaction(() =>
        {
            var classifier = _store.BuildClassifier();
            var cashOut = _store.GetCashOutPatterns();

            // 1st pass: the user's own labels teach the classifier (only rows with a real merchant text).
            foreach (var row in parsed.Where(r => r.HasCategory && r.HasMerchantText))
            {
                var classification = new Classification(row.Transaction.Category!, row.Transaction.Item ?? "");
                classifier.Observe(row.Transaction.RawDescription, classification);
                _store.SaveRule(RuleKind.Exact, row.Transaction.RawDescription, classification);
                if (classifier.IsAmbiguous(row.Transaction.RawDescription))
                    _store.SaveRule(RuleKind.Ambiguous, row.Transaction.RawDescription, new Classification("", ""));
                taught++;
            }

            // 2nd pass: classify the unlabelled rows, then insert everything.
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var row in parsed)
            {
                var transaction = row.Transaction;

                if (cashOut.Contains(transaction.NormalizedDescription))
                {
                    transaction = transaction with { Type = TransactionType.CardCashAdvance, Category = null, Item = null };
                    cashOutCount++;
                }
                else if (!row.HasCategory)
                {
                    var result = classifier.Classify(transaction.RawDescription);
                    if (result.IsClassified)
                    {
                        transaction = transaction with
                        {
                            Category = result.Classification!.Category,
                            Item = string.IsNullOrEmpty(result.Classification.Item) ? null : result.Classification.Item,
                        };
                        byRules++;
                    }
                    else if (transaction.Type is TransactionType.Expense or TransactionType.Income)
                    {
                        pending++;
                    }
                }

                var baseKey = transaction.ComputeDedupKey(1);
                var occurrence = occurrences.GetValueOrDefault(baseKey) + 1;
                occurrences[baseKey] = occurrence;

                if (_store.TryInsert(transaction, transaction.ComputeDedupKey(occurrence), sourceName)) inserted++;
                else duplicates++;
            }
        });

        return new ImportResult(parsed.Count, inserted, duplicates, taught, byRules, pending, cashOutCount, rejected);
    }

    private List<ParsedRow> ParseRows(List<List<string>> table, List<RejectedRow> rejected)
    {
        var result = new List<ParsedRow>();
        if (table.Count == 0) return result;

        var header = table[0].Select(Fold).ToList();
        int Column(string name) => header.IndexOf(name);

        var day = Column("DIA");
        // The export of the real sheet has an empty header cell over the date column.
        if (day < 0 && header.Count > 0 && header[0].Length == 0) day = 0;
        var category = Column("CATEGORIA");
        var item = Column("ITEM");
        var account = Column("CONTA");
        var value = Column("VALOR");
        var notes = Column("OBS");

        if (day < 0 || account < 0 || value < 0)
            throw new FormatException("Header must contain at least: Dia, Conta, Valor.");

        for (var i = 1; i < table.Count; i++)
        {
            var line = i + 1;
            var cells = table[i];
            string Cell(int column) => column >= 0 && column < cells.Count ? cells[column].Trim() : "";

            if (cells.All(c => string.IsNullOrWhiteSpace(c))) continue;
            if (Cell(day).Length == 0 && Cell(value).Length == 0) continue;

            if (!DateOnly.TryParseExact(Cell(day), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                rejected.Add(new RejectedRow(line, $"Invalid date '{Cell(day)}'."));
                continue;
            }
            if (!BrazilianCurrencyParser.TryParse(Cell(value), out var amount) || amount == 0)
            {
                rejected.Add(new RejectedRow(line, $"Invalid or zero amount '{Cell(value)}'."));
                continue;
            }
            if (Cell(account).Length == 0)
            {
                rejected.Add(new RejectedRow(line, "Missing account."));
                continue;
            }

            var merchant = Cell(notes);
            var description = merchant.Length > 0 ? merchant : Cell(item).Length > 0 ? Cell(item) : Cell(category);
            if (description.Length == 0)
            {
                rejected.Add(new RejectedRow(line, "No description (Obs, Ítem and Categoria are all empty)."));
                continue;
            }

            var signed = _options.ValuesArePositiveExpenses ? -amount : amount;
            var (number, count) = ParseInstallment(description);
            var categoryText = Cell(category);
            var itemText = Cell(item);

            var transaction = new Transaction
            {
                Account = Cell(account),
                Date = date,
                Amount = signed,
                RawDescription = description,
                Category = categoryText.Length > 0 ? categoryText : null,
                Item = itemText.Length > 0 ? itemText : null,
                Type = signed < 0 ? TransactionType.Expense : TransactionType.Income,
                InstallmentNumber = number,
                InstallmentCount = count,
            };

            if (_billDetector.IsCardBillPayment(transaction.NormalizedDescription))
                transaction = transaction with { Type = TransactionType.CardBillPayment };

            result.Add(new ParsedRow(transaction, categoryText.Length > 0, merchant.Length > 0));
        }

        return result;
    }

    /// <summary>
    /// Only explicit forms such as "PARC 03/06" or the card export's glued "Loja Xparc03/06" count;
    /// a bare "01/09" is more likely a date.
    /// </summary>
    private static (int? Number, int? Count) ParseInstallment(string description)
    {
        var match = InstallmentRegex().Match(description);
        if (!match.Success) return (null, null);

        var number = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var count = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return number >= 1 && number <= count && count <= 99 ? (number, count) : (null, null);
    }

    /// <summary>Upper case, no accents, trimmed: "Ítem" and "ITEM" are the same header.</summary>
    private static string Fold(string text)
    {
        var decomposed = text.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        return builder.ToString().Normalize(NormalizationForm.FormC).ToUpperInvariant();
    }

    [GeneratedRegex(@"PARC(?:ELA)?\.?\s*(\d{1,2})\s*/\s*(\d{1,2})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstallmentRegex();

    private sealed record ParsedRow(Transaction Transaction, bool HasCategory, bool HasMerchantText);
}
