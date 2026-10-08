using System.Globalization;
using CashPilot.Domain.Accounts;
using CashPilot.Domain.Descriptions;
using CashPilot.Domain.Payments;
using CashPilot.Domain.Transactions;
using Microsoft.Data.Sqlite;

namespace CashPilot.Infrastructure.Persistence;

public enum RuleKind
{
    /// <summary>Taught from a classified description (exact normalized match, also feeds the similarity layer).</summary>
    Exact = 1,
    /// <summary>Manual "contains" rule.</summary>
    Contains = 2,
    /// <summary>The description was seen with different classifications, so it is asked about instead of guessed.</summary>
    Ambiguous = 3,
    /// <summary>Normalized description of a card-terminal cash-out charge (gross amount): not spending.</summary>
    CashOut = 4,
    /// <summary>Normalized description of a transfer between the owner's own accounts (has a payee, so it is safe to remember).</summary>
    OwnTransfer = 5,
}

/// <summary>
/// SQLite store. Owns one open connection (use ":memory:" in tests).
/// Money is stored as integer cents and dates as ISO text, to avoid floating point surprises.
/// </summary>
/// <summary>A rule as stored, with its row id so the UI can delete it.</summary>
public sealed record StoredRule(long Id, RuleKind Kind, string Pattern, Classification Classification);

public sealed class CashPilotStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private SqliteTransaction? _transaction;

    public CashPilotStore(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();

        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        Schema.Apply(_connection);
    }

    /// <summary>Runs the work in a single database transaction (fast bulk import, all-or-nothing).</summary>
    public void InTransaction(Action work)
    {
        if (_transaction is not null)
            throw new InvalidOperationException("A transaction is already in progress.");

        _transaction = _connection.BeginTransaction();
        try
        {
            work();
            _transaction.Commit();
        }
        catch
        {
            _transaction.Rollback();
            throw;
        }
        finally
        {
            _transaction.Dispose();
            _transaction = null;
        }
    }

    /// <summary>Inserts the entry unless one with the same dedup key exists. Returns whether it was inserted.</summary>
    public bool TryInsert(Transaction transaction, string dedupKey, string? source = null)
    {
        using var command = CreateCommand("""
            INSERT OR IGNORE INTO transactions
                (id, dedup_key, account, date, amount_cents, raw_description, category, item, type,
                 installment_number, installment_count, source, imported_at)
            VALUES
                ($id, $key, $account, $date, $cents, $description, $category, $item, $type,
                 $number, $count, $source, $importedAt);
            """);
        command.Parameters.AddWithValue("$id", transaction.Id.ToString());
        command.Parameters.AddWithValue("$key", dedupKey);
        command.Parameters.AddWithValue("$account", transaction.Account);
        command.Parameters.AddWithValue("$date", transaction.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$cents", ToCents(transaction.Amount));
        command.Parameters.AddWithValue("$description", transaction.RawDescription);
        command.Parameters.AddWithValue("$category", (object?)transaction.Category ?? DBNull.Value);
        command.Parameters.AddWithValue("$item", (object?)transaction.Item ?? DBNull.Value);
        command.Parameters.AddWithValue("$type", (int)transaction.Type);
        command.Parameters.AddWithValue("$number", (object?)transaction.InstallmentNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("$count", (object?)transaction.InstallmentCount ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", (object?)source ?? DBNull.Value);
        command.Parameters.AddWithValue("$importedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// Whether an entry with this dedup key was ever stored, including ones the user deleted
    /// (a deleted entry must not come back when the same file is imported again).
    /// </summary>
    public bool DedupKeyExists(string dedupKey)
    {
        using var command = CreateCommand("SELECT 1 FROM transactions WHERE dedup_key = $key LIMIT 1;");
        command.Parameters.AddWithValue("$key", dedupKey);
        return command.ExecuteScalar() is not null;
    }

    private static readonly HashSet<string> TransferWords =
        new(StringComparer.Ordinal) { "PIX", "TED", "DOC", "TRANSF", "TRANSFERENCIA" };

    /// <summary>
    /// Finds Pix/TED/transfer entries that leave one account and arrive in another with the same amount within
    /// a few days, and marks both as internal transfers (neither spending nor income). Returns how many pairs were found.
    /// </summary>
    public int MarkInternalTransfers()
    {
        var candidates = GetAll()
            .Where(t => t.NormalizedDescription.Split(' ').Any(word => TransferWords.Contains(word)))
            .ToList();

        var pairs = TransferDetector.Detect(candidates);
        foreach (var id in pairs.SelectMany(p => new[] { p.OutgoingId, p.IncomingId }))
        {
            using var command = CreateCommand(
                "UPDATE transactions SET type = $type, category = NULL, item = NULL WHERE id = $id;");
            command.Parameters.AddWithValue("$type", (int)TransactionType.InternalTransfer);
            command.Parameters.AddWithValue("$id", id.ToString());
            command.ExecuteNonQuery();
        }
        return pairs.Count;
    }

    /// <summary>
    /// The user says an entry only moved money between their own accounts: it stops being pending and is neither
    /// spending nor income. Returns false when nothing matched.
    /// </summary>
    public bool MarkEntryInternalTransfer(Guid id)
    {
        using var command = CreateCommand(
            "UPDATE transactions SET type = $type, category = NULL, item = NULL WHERE id = $id AND deleted = 0;");
        command.Parameters.AddWithValue("$type", (int)TransactionType.InternalTransfer);
        command.Parameters.AddWithValue("$id", id.ToString());
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Marks every pending entry with this description as a transfer between the owner's accounts. A description with
    /// a payee is remembered, so future imports do the same; a bare "PIX ENVIADO" is not. Returns how many changed.
    /// </summary>
    public int MarkInternalTransfer(string description)
    {
        var normalized = DescriptionNormalizer.Normalize(description);
        if (normalized.Length == 0) return 0;
        if (!TransferDetector.IsGeneric(normalized)) SaveRule(RuleKind.OwnTransfer, normalized, new Classification("", ""));

        var updated = 0;
        foreach (var pending in GetPending().Where(t => t.NormalizedDescription == normalized))
            if (MarkEntryInternalTransfer(pending.Id)) updated++;
        return updated;
    }

    public HashSet<string> GetOwnTransferPatterns() => GetPatterns(RuleKind.OwnTransfer);

    public int Count()
    {
        using var command = CreateCommand("SELECT COUNT(*) FROM transactions WHERE deleted = 0;");
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<Transaction> GetAll() => Query("SELECT * FROM transactions WHERE deleted = 0 ORDER BY date, rowid;");

    /// <summary>Spending/income without a category yet: the queue the user classifies (and the classifier learns from).</summary>
    public IReadOnlyList<Transaction> GetPending() => Query(
        "SELECT * FROM transactions WHERE deleted = 0 AND category IS NULL AND type IN (0, 1, 2, 6) ORDER BY date, rowid;");

    /// <summary>Records the user's decision for every stored entry with this normalized description, and teaches the rule.</summary>
    public int Classify(string description, Classification classification)
    {
        var normalized = DescriptionNormalizer.Normalize(description);
        SaveRule(RuleKind.Exact, normalized, classification);

        using (var delete = CreateCommand("DELETE FROM classification_rules WHERE kind = $kind AND pattern = $pattern;"))
        {
            delete.Parameters.AddWithValue("$kind", (int)RuleKind.Ambiguous);
            delete.Parameters.AddWithValue("$pattern", normalized);
            delete.ExecuteNonQuery();
        }

        var updated = 0;
        foreach (var pending in GetPending().Where(t => t.NormalizedDescription == normalized))
        {
            SetClassification(pending.Id, classification);
            updated++;
        }
        return updated;
    }

    /// <summary>
    /// Changes the classification of one stored entry (inline correction). With <paramref name="learn"/> the decision
    /// also becomes the rule for that description, so future imports and other pending entries follow it.
    /// </summary>
    public void ClassifyEntry(Guid id, Classification classification, bool learn)
    {
        string? description = null;
        using (var lookup = CreateCommand("SELECT raw_description FROM transactions WHERE id = $id;"))
        {
            lookup.Parameters.AddWithValue("$id", id.ToString());
            description = lookup.ExecuteScalar() as string;
        }
        if (description is null) return;

        SetClassification(id, classification);
        if (learn) Classify(description, classification);
    }

    /// <summary>
    /// Removes an entry from every screen and report (duplicates, mistakes). It is a soft delete: the import key stays,
    /// so importing the same file again does not bring the entry back. Returns false when nothing matched.
    /// </summary>
    public bool DeleteTransaction(Guid id)
    {
        using var command = CreateCommand("UPDATE transactions SET deleted = 1 WHERE id = $id AND deleted = 0;");
        command.Parameters.AddWithValue("$id", id.ToString());
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>Applies the current rules (exact, contains, similarity) to every entry still without a category.</summary>
    public int ReclassifyPending()
    {
        var classifier = BuildClassifier();
        var updated = 0;
        foreach (var pending in GetPending())
        {
            var result = classifier.Classify(pending.RawDescription);
            if (!result.IsClassified) continue;
            SetClassification(pending.Id, result.Classification!);
            updated++;
        }
        return updated;
    }

    private void SetClassification(Guid id, Classification classification)
    {
        using var command = CreateCommand("UPDATE transactions SET category = $category, item = $item WHERE id = $id;");
        command.Parameters.AddWithValue("$category", classification.Category);
        command.Parameters.AddWithValue("$item", string.IsNullOrEmpty(classification.Item) ? DBNull.Value : (object)classification.Item);
        command.Parameters.AddWithValue("$id", id.ToString());
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Marks every stored entry with this description as a card-terminal cash-out (not spending) and remembers
    /// the description, so future imports mark it automatically. Returns how many stored entries changed.
    /// </summary>
    public int MarkCashOut(string description)
    {
        var normalized = DescriptionNormalizer.Normalize(description);
        if (normalized.Length == 0) return 0;

        SaveRule(RuleKind.CashOut, normalized, new Classification("", ""));

        var updated = 0;
        var candidates = GetAll().Where(t => t.NormalizedDescription == normalized
                                             && t.Type is TransactionType.Undefined or TransactionType.Expense or TransactionType.Income);
        foreach (var transaction in candidates)
        {
            using var command = CreateCommand(
                "UPDATE transactions SET type = $type, category = NULL, item = NULL WHERE id = $id;");
            command.Parameters.AddWithValue("$type", (int)TransactionType.CardCashAdvance);
            command.Parameters.AddWithValue("$id", transaction.Id.ToString());
            updated += command.ExecuteNonQuery();
        }
        return updated;
    }

    /// <summary>Every category/item pair already in use (entries and rules), for pick lists.</summary>
    public IReadOnlyList<Classification> GetKnownClassifications()
    {
        var result = new List<Classification>();
        using var command = CreateCommand("""
            SELECT DISTINCT category, COALESCE(item, '') FROM transactions WHERE deleted = 0 AND category IS NOT NULL
            UNION
            SELECT DISTINCT category, item FROM classification_rules WHERE kind IN (1, 2) AND category <> ''
            ORDER BY 1, 2;
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new Classification(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    /// <summary>Every stored rule (exact, contains, ambiguous, cash-out), ordered by kind then pattern.</summary>
    public IReadOnlyList<StoredRule> GetRules()
    {
        var result = new List<StoredRule>();
        using var command = CreateCommand(
            "SELECT id, kind, pattern, category, item FROM classification_rules ORDER BY kind, pattern;");
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new StoredRule(reader.GetInt64(0), (RuleKind)reader.GetInt32(1), reader.GetString(2),
                new Classification(reader.GetString(3), reader.GetString(4))));
        return result;
    }

    /// <summary>Deletes one rule. Entries already classified by it keep their category.</summary>
    public bool DeleteRule(long id)
    {
        using var command = CreateCommand("DELETE FROM classification_rules WHERE id = $id;");
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Renames a category on every stored entry and rule (also merges into an existing category).
    /// Returns how many entries changed.
    /// </summary>
    public int RenameCategory(string from, string to)
    {
        if (string.IsNullOrWhiteSpace(to) || from == to) return 0;
        to = to.Trim();

        int changed;
        using (var command = CreateCommand("UPDATE transactions SET category = $to WHERE category = $from;"))
        {
            command.Parameters.AddWithValue("$from", from);
            command.Parameters.AddWithValue("$to", to);
            changed = command.ExecuteNonQuery();
        }
        using (var command = CreateCommand("UPDATE classification_rules SET category = $to WHERE category = $from;"))
        {
            command.Parameters.AddWithValue("$from", from);
            command.Parameters.AddWithValue("$to", to);
            command.ExecuteNonQuery();
        }
        return changed;
    }

    /// <summary>Distinct account names used by stored entries (to offer them for registration).</summary>
    public IReadOnlyList<string> GetAccountNamesInUse()
    {
        var result = new List<string>();
        using var command = CreateCommand("SELECT DISTINCT account FROM transactions WHERE deleted = 0 ORDER BY account;");
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public IReadOnlyList<Account> GetAccounts()
    {
        var result = new List<Account>();
        using var command = CreateCommand("""
            SELECT name, kind, credit_limit_cents, closing_day, due_day,
                   overdraft_limit_cents, overdraft_free_days, overdraft_monthly_rate,
                   balance_anchor_cents, balance_anchor_date
            FROM accounts ORDER BY kind, name;
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new Account
            {
                Name = reader.GetString(0),
                Kind = (AccountKind)reader.GetInt32(1),
                CreditLimit = reader.IsDBNull(2) ? null : FromCents(reader.GetInt64(2)),
                ClosingDay = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                DueDay = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                OverdraftLimit = reader.IsDBNull(5) ? null : FromCents(reader.GetInt64(5)),
                OverdraftFreeDays = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                OverdraftMonthlyRatePercent = reader.IsDBNull(7)
                    ? null
                    : decimal.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                BalanceAnchor = reader.IsDBNull(8) ? null : FromCents(reader.GetInt64(8)),
                BalanceAnchorDate = reader.IsDBNull(9)
                    ? null
                    : DateOnly.ParseExact(reader.GetString(9), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            });
        }
        return result;
    }

    /// <summary>Creates or updates a registered account (matched by name).</summary>
    public void SaveAccount(Account account)
    {
        if (string.IsNullOrWhiteSpace(account.Name)) throw new ArgumentException("Account name is required.", nameof(account));

        using var command = CreateCommand("""
            INSERT INTO accounts (name, kind, credit_limit_cents, closing_day, due_day,
                                  overdraft_limit_cents, overdraft_free_days, overdraft_monthly_rate)
            VALUES ($name, $kind, $limit, $closing, $due, $odLimit, $odDays, $odRate)
            ON CONFLICT (name) DO UPDATE SET
                kind = excluded.kind,
                credit_limit_cents = excluded.credit_limit_cents,
                closing_day = excluded.closing_day,
                due_day = excluded.due_day,
                overdraft_limit_cents = excluded.overdraft_limit_cents,
                overdraft_free_days = excluded.overdraft_free_days,
                overdraft_monthly_rate = excluded.overdraft_monthly_rate;
            """);
        command.Parameters.AddWithValue("$name", account.Name.Trim());
        command.Parameters.AddWithValue("$kind", (int)account.Kind);
        command.Parameters.AddWithValue("$limit", account.CreditLimit is { } l ? (object)ToCents(l) : DBNull.Value);
        command.Parameters.AddWithValue("$closing", account.ClosingDay is { } c ? (object)c : DBNull.Value);
        command.Parameters.AddWithValue("$due", account.DueDay is { } d ? (object)d : DBNull.Value);
        command.Parameters.AddWithValue("$odLimit", account.OverdraftLimit is { } o ? (object)ToCents(o) : DBNull.Value);
        command.Parameters.AddWithValue("$odDays", account.OverdraftFreeDays is { } f ? (object)f : DBNull.Value);
        command.Parameters.AddWithValue("$odRate", account.OverdraftMonthlyRatePercent is { } r
            ? (object)r.ToString(CultureInfo.InvariantCulture)
            : DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>Sets the hand-typed balance of a registered account as of <paramref name="asOf"/>.</summary>
    public bool SetAccountBalance(string name, decimal balance, DateOnly asOf)
    {
        using var command = CreateCommand(
            "UPDATE accounts SET balance_anchor_cents = $cents, balance_anchor_date = $date WHERE name = $name;");
        command.Parameters.AddWithValue("$cents", ToCents(balance));
        command.Parameters.AddWithValue("$date", asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<Payable> GetPayables()
    {
        var result = new List<Payable>();
        using var command = CreateCommand(
            """
            SELECT id, description, due_date, amount_cents, paid, paid_date, late_fee_percent, late_interest_monthly_percent
            FROM payables ORDER BY due_date, description;
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new Payable
            {
                Id = Guid.Parse(reader.GetString(0)),
                Description = reader.GetString(1),
                DueDate = DateOnly.ParseExact(reader.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Amount = FromCents(reader.GetInt64(3)),
                Paid = reader.GetInt32(4) != 0,
                PaidDate = reader.IsDBNull(5)
                    ? null
                    : DateOnly.ParseExact(reader.GetString(5), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                LateFeePercent = reader.IsDBNull(6) ? null : decimal.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                LateInterestMonthlyPercent = reader.IsDBNull(7) ? null : decimal.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
            });
        }
        return result;
    }

    public void AddPayable(Payable payable)
    {
        if (string.IsNullOrWhiteSpace(payable.Description)) throw new ArgumentException("Description is required.", nameof(payable));
        if (payable.Amount <= 0) throw new ArgumentException("Amount must be positive.", nameof(payable));

        using var command = CreateCommand("""
            INSERT INTO payables (id, description, due_date, amount_cents, paid, paid_date, late_fee_percent, late_interest_monthly_percent)
            VALUES ($id, $description, $due, $cents, 0, NULL, $fee, $interest);
            """);
        command.Parameters.AddWithValue("$id", payable.Id.ToString());
        command.Parameters.AddWithValue("$description", payable.Description.Trim());
        command.Parameters.AddWithValue("$due", payable.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$cents", ToCents(payable.Amount));
        command.Parameters.AddWithValue("$fee", payable.LateFeePercent is { } fee
            ? fee.ToString(CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue("$interest", payable.LateInterestMonthlyPercent is { } interest
            ? interest.ToString(CultureInfo.InvariantCulture) : DBNull.Value);
        command.ExecuteNonQuery();
    }

    public bool SetPayablePaid(Guid id, bool paid, DateOnly? paidOn = null)
    {
        using var command = CreateCommand("UPDATE payables SET paid = $paid, paid_date = $date WHERE id = $id;");
        command.Parameters.AddWithValue("$paid", paid ? 1 : 0);
        command.Parameters.AddWithValue("$date", paid && paidOn is { } d
            ? (object)d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : DBNull.Value);
        command.Parameters.AddWithValue("$id", id.ToString());
        return command.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<CardBillSettlement> GetCardBillSettlements()
    {
        var result = new List<CardBillSettlement>();
        using var command = CreateCommand("SELECT card, closing, paid_on FROM card_bill_settlements;");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new CardBillSettlement(
                reader.GetString(0),
                DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                DateOnly.ParseExact(reader.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }
        return result;
    }

    /// <summary>Marks the bill of <paramref name="card"/> that closes on <paramref name="closing"/> as paid by hand.</summary>
    public void SetCardBillSettled(string card, DateOnly closing, DateOnly paidOn)
    {
        using var command = CreateCommand("""
            INSERT INTO card_bill_settlements (card, closing, paid_on) VALUES ($card, $closing, $paid)
            ON CONFLICT (card, closing) DO UPDATE SET paid_on = excluded.paid_on;
            """);
        command.Parameters.AddWithValue("$card", card);
        command.Parameters.AddWithValue("$closing", closing.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$paid", paidOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public bool ClearCardBillSettled(string card, DateOnly closing)
    {
        using var command = CreateCommand("DELETE FROM card_bill_settlements WHERE card = $card AND closing = $closing;");
        command.Parameters.AddWithValue("$card", card);
        command.Parameters.AddWithValue("$closing", closing.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return command.ExecuteNonQuery() > 0;
    }

    public bool DeletePayable(Guid id)
    {
        using var command = CreateCommand("DELETE FROM payables WHERE id = $id;");
        command.Parameters.AddWithValue("$id", id.ToString());
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Renames a registered account and moves its entries to the new name. Returns false when <paramref name="from"/>
    /// is not registered; throws when another registered account already uses <paramref name="to"/>.
    /// Entries keep their import keys, so re-importing the same sheet still does not duplicate them.
    /// </summary>
    public bool RenameAccount(string from, string to)
    {
        to = to?.Trim() ?? "";
        if (to.Length == 0) throw new ArgumentException("Account name is required.", nameof(to));
        if (from == to) return true;

        var found = false;
        InTransaction(() =>
        {
            if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            {
                using var check = CreateCommand("SELECT COUNT(*) FROM accounts WHERE name = $to COLLATE NOCASE;");
                check.Parameters.AddWithValue("$to", to);
                if (Convert.ToInt32(check.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
                    throw new InvalidOperationException($"Já existe uma conta chamada \"{to}\".");
            }

            using (var update = CreateCommand("UPDATE accounts SET name = $to WHERE name = $from;"))
            {
                update.Parameters.AddWithValue("$to", to);
                update.Parameters.AddWithValue("$from", from);
                found = update.ExecuteNonQuery() > 0;
            }
            if (!found) return;

            using var move = CreateCommand("UPDATE transactions SET account = $to WHERE account = $from;");
            move.Parameters.AddWithValue("$to", to);
            move.Parameters.AddWithValue("$from", from);
            move.ExecuteNonQuery();

            using var settlements = CreateCommand("UPDATE card_bill_settlements SET card = $to WHERE card = $from;");
            settlements.Parameters.AddWithValue("$to", to);
            settlements.Parameters.AddWithValue("$from", from);
            settlements.ExecuteNonQuery();
        });
        return found;
    }

    public bool DeleteAccount(string name)
    {
        using var command = CreateCommand("DELETE FROM accounts WHERE name = $name;");
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteNonQuery() > 0;
    }

    public HashSet<string> GetCashOutPatterns() => GetPatterns(RuleKind.CashOut);

    private HashSet<string> GetPatterns(RuleKind kind)
    {
        var patterns = new HashSet<string>(StringComparer.Ordinal);
        using var command = CreateCommand("SELECT pattern FROM classification_rules WHERE kind = $kind;");
        command.Parameters.AddWithValue("$kind", (int)kind);
        using var reader = command.ExecuteReader();
        while (reader.Read()) patterns.Add(reader.GetString(0));
        return patterns;
    }

    public void SaveRule(RuleKind kind, string pattern, Classification classification)
    {
        var normalized = DescriptionNormalizer.Normalize(pattern);
        if (normalized.Length == 0) return;

        using var command = CreateCommand("""
            INSERT INTO classification_rules (kind, pattern, category, item)
            VALUES ($kind, $pattern, $category, $item)
            ON CONFLICT (kind, pattern) DO UPDATE SET category = excluded.category, item = excluded.item;
            """);
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$pattern", normalized);
        command.Parameters.AddWithValue("$category", classification.Category);
        command.Parameters.AddWithValue("$item", classification.Item);
        command.ExecuteNonQuery();
    }

    /// <summary>Rebuilds the in-memory classifier from every rule saved so far.</summary>
    public Classifier BuildClassifier()
    {
        var rules = new List<(RuleKind Kind, string Pattern, Classification Classification)>();
        using (var command = CreateCommand("SELECT kind, pattern, category, item FROM classification_rules ORDER BY id;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                rules.Add(((RuleKind)reader.GetInt32(0), reader.GetString(1),
                    new Classification(reader.GetString(2), reader.GetString(3))));
        }

        var classifier = new Classifier();
        foreach (var (kind, pattern, classification) in rules)
        {
            if (kind == RuleKind.Exact) classifier.Learn(pattern, classification);
            else if (kind == RuleKind.Contains) classifier.AddContainsRule(pattern, classification);
        }
        // Ambiguity last, so a Learn during the rebuild cannot erase it.
        foreach (var (kind, pattern, _) in rules)
            if (kind == RuleKind.Ambiguous) classifier.MarkAmbiguous(pattern);

        return classifier;
    }

    public void Dispose()
    {
        _transaction?.Dispose();
        _connection.Dispose();
    }

    private SqliteCommand CreateCommand(string sql)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _transaction;
        return command;
    }

    private List<Transaction> Query(string sql)
    {
        var result = new List<Transaction>();
        using var command = CreateCommand(sql);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new Transaction
            {
                Id = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
                Account = reader.GetString(reader.GetOrdinal("account")),
                Date = DateOnly.ParseExact(reader.GetString(reader.GetOrdinal("date")), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Amount = FromCents(reader.GetInt64(reader.GetOrdinal("amount_cents"))),
                RawDescription = reader.GetString(reader.GetOrdinal("raw_description")),
                Category = NullableString(reader, "category"),
                Item = NullableString(reader, "item"),
                Type = (TransactionType)reader.GetInt32(reader.GetOrdinal("type")),
                InstallmentNumber = NullableInt(reader, "installment_number"),
                InstallmentCount = NullableInt(reader, "installment_count"),
            });
        }
        return result;
    }

    private static string? NullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static int? NullableInt(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static long ToCents(decimal amount) =>
        (long)Math.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);

    private static decimal FromCents(long cents) => cents / 100m;
}
