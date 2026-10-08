using CashPilot.Domain.Accounts;
using CashPilot.Domain.Descriptions;
using CashPilot.Domain.Transactions;
using CashPilot.Infrastructure.Persistence;

namespace CashPilot.Infrastructure.Importing;

public enum CandidateStatus
{
    /// <summary>Not stored yet.</summary>
    New,
    /// <summary>Already imported (same dedup key), possibly deleted by the user since. Never inserted again.</summary>
    Duplicate,
    /// <summary>
    /// Same account, same amount and a date within two days of an entry that is already stored, but not imported
    /// from a statement (typically typed in the spreadsheet). Probably the same movement: off by default.
    /// </summary>
    Similar,
}

public sealed record BankCandidate(
    int Index,
    BankEntry Entry,
    Transaction Transaction,
    string DedupKey,
    CandidateStatus Status,
    string? Note);

public sealed record BankPreview(string Account, BankStatement Statement, IReadOnlyList<BankCandidate> Candidates)
{
    public int Count(CandidateStatus status) => Candidates.Count(c => c.Status == status);
}

/// <param name="TransferPairs">Pairs between accounts found after the import (marked as internal transfers).</param>
public sealed record BankImportResult(int Inserted, int Skipped, int TransferPairs);

/// <summary>
/// Turns a parsed statement into entries of one account, in two steps: <see cref="Preview"/> decides what each line
/// is and whether it is already there (nothing is written), then <see cref="Import"/> stores the lines the user kept.
/// </summary>
public sealed class BankStatementImporter
{
    public const string InterestCategory = "Juros";
    public const string OverdraftItem = "Cheque Especial";
    public const string FeeItem = "Tarifa Bancária";

    // Wording that bank statements use for card bill payments, besides the generic ones in the detector.
    private static readonly string[] BankBillPatterns = ["FATURA PAGA", "GASTOS CARTAO DE CREDITO", "PGTO CARTAO", "PAGTO CARTAO"];

    private readonly CashPilotStore _store;
    private readonly CardBillPaymentDetector _billDetector =
        new(CardBillPaymentDetector.DefaultPatterns.Concat(BankBillPatterns));

    public BankStatementImporter(CashPilotStore store)
    {
        _store = store;
    }

    public BankPreview Preview(BankStatement statement, string account)
    {
        account = account.Trim();
        var classifier = _store.BuildClassifier();
        var ownTransfers = _store.GetOwnTransferPatterns();
        var stored = _store.GetAll()
            .Where(t => string.Equals(t.Account, account, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var matched = new HashSet<Guid>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var candidates = new List<BankCandidate>();

        for (var index = 0; index < statement.Entries.Count; index++)
        {
            var entry = statement.Entries[index];
            var transaction = Build(entry, account, classifier, ownTransfers);

            // The key uses only the bank's wording: the same movement read from the CSV (no payee) and from the PDF
            // (with payee) is one entry. Identical movements in one file are told apart by their order.
            var keyed = transaction with { RawDescription = entry.History };
            var baseKey = keyed.ComputeDedupKey(1);
            var occurrence = occurrences.GetValueOrDefault(baseKey) + 1;
            occurrences[baseKey] = occurrence;
            var key = keyed.ComputeDedupKey(occurrence);

            if (_store.DedupKeyExists(key))
            {
                candidates.Add(new BankCandidate(index, entry, transaction, key, CandidateStatus.Duplicate, "já importado"));
                continue;
            }

            var similar = stored.FirstOrDefault(t => !matched.Contains(t.Id)
                                                     && t.Amount == entry.Amount
                                                     && Math.Abs(t.Date.DayNumber - entry.Date.DayNumber) <= 2);
            if (similar is not null)
            {
                matched.Add(similar.Id);
                candidates.Add(new BankCandidate(index, entry, transaction, key, CandidateStatus.Similar,
                    $"parece \"{similar.RawDescription}\" de {similar.Date:dd/MM}"));
                continue;
            }

            candidates.Add(new BankCandidate(index, entry, transaction, key, CandidateStatus.New, null));
        }

        return new BankPreview(account, statement, candidates);
    }

    /// <summary>Stores the lines whose index is in <paramref name="include"/>, then pairs up transfers between accounts.</summary>
    public BankImportResult Import(BankPreview preview, IEnumerable<int> include, string? sourceName)
    {
        var wanted = include.ToHashSet();
        var inserted = 0;
        var skipped = 0;
        var pairs = 0;

        _store.InTransaction(() =>
        {
            foreach (var candidate in preview.Candidates)
            {
                if (candidate.Status == CandidateStatus.Duplicate || !wanted.Contains(candidate.Index)) { skipped++; continue; }
                if (_store.TryInsert(candidate.Transaction, candidate.DedupKey, sourceName)) inserted++;
                else skipped++;
            }
            pairs = _store.MarkInternalTransfers();
        });

        return new BankImportResult(inserted, skipped, pairs);
    }

    /// <summary>Registered bank accounts whose name mentions the statement's bank (to suggest the account).</summary>
    public IReadOnlyList<string> SuggestAccounts(BankStatement statement)
    {
        var bank = DescriptionNormalizer.Normalize(statement.Bank);
        return _store.GetAccounts()
            .Where(a => a.Kind == AccountKind.BankAccount
                        && DescriptionNormalizer.Normalize(a.Name).Contains(bank, StringComparison.Ordinal))
            .Select(a => a.Name)
            .ToList();
    }

    private Transaction Build(BankEntry entry, string account, Classifier classifier, HashSet<string> ownTransfers)
    {
        var normalized = DescriptionNormalizer.Normalize(entry.History);
        var transaction = new Transaction
        {
            Account = account,
            Date = entry.Date,
            Amount = entry.Amount,
            RawDescription = entry.Description,
            Type = entry.Amount < 0 ? TransactionType.Expense : TransactionType.Deposit,
        };

        // A description the user already said is "between my own accounts" (it has a payee, so it is safe to remember).
        if (ownTransfers.Contains(DescriptionNormalizer.Normalize(entry.Description)))
            return transaction with { Type = TransactionType.InternalTransfer };

        // Credits stay uncategorized and pending: the user says whether it is a transfer between own accounts.
        if (entry.Amount > 0) return transaction;

        if (_billDetector.IsCardBillPayment(normalized))
            return transaction with { Type = TransactionType.CardBillPayment };

        if (IsOverdraftCost(normalized))
            return transaction with { Category = InterestCategory, Item = OverdraftItem };

        if (IsBankFee(normalized))
            return transaction with { Category = InterestCategory, Item = FeeItem };

        // "PIX ENVIADO" alone (no payee, as in the CSV) says nothing about where the money went: leave it for the user
        // instead of applying one rule to every Pix.
        if (entry.Detail.Length == 0 && TransferDetector.IsGeneric(normalized)) return transaction;

        var result = classifier.Classify(entry.Description);
        if (!result.IsClassified) return transaction;
        return transaction with
        {
            Category = result.Classification!.Category,
            Item = string.IsNullOrEmpty(result.Classification.Item) ? null : result.Classification.Item,
        };
    }

    // Cheque especial / LIS: interest ("ENCARGOS LIMITE DE CRED", "JUROS LIMITE DA CONTA", "JUROS EXCESSO LIM CONTA")
    // and the IOF charged on its use ("IOF S/ UTILIZACAO LIMITE", "IOF").
    private static bool IsOverdraftCost(string normalized) =>
        normalized.Contains("ENCARGOS LIMITE", StringComparison.Ordinal)
        || normalized.Contains("ENCARGOS EXCESSO", StringComparison.Ordinal)
        || normalized.Contains("JUROS LIMITE", StringComparison.Ordinal)
        || normalized.Contains("JUROS EXCESSO", StringComparison.Ordinal)
        || normalized.Contains("UTILIZACAO LIMITE", StringComparison.Ordinal)
        || normalized == "IOF"
        || normalized.StartsWith("IOF ", StringComparison.Ordinal);

    private static bool IsBankFee(string normalized) =>
        normalized.StartsWith("TARIFA", StringComparison.Ordinal)
        || normalized.Contains("TARIFA BANCARIA", StringComparison.Ordinal);
}
