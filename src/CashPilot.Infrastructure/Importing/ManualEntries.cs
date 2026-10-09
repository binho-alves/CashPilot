using CashPilot.Domain.Descriptions;
using CashPilot.Domain.Transactions;
using CashPilot.Infrastructure.Persistence;

namespace CashPilot.Infrastructure.Importing;

/// <param name="AutoClassified">True when no category was given and the classifier knew the description.</param>
public sealed record ManualEntryResult(Transaction Transaction, bool AutoClassified);

/// <summary>
/// An entry typed by hand (cash, a Pix that is not in the statement yet). Unlike an import it has no dedup key to
/// protect: two identical entries typed on purpose are both kept.
/// </summary>
public static class ManualEntries
{
    /// <param name="amount">Always positive; <paramref name="income"/> decides the sign.</param>
    /// <param name="chosen">The category the user picked; when null the classifier tries, and an unknown entry stays pending.</param>
    /// <param name="learn">With a chosen category: also teach it as the rule for this description.</param>
    public static ManualEntryResult Add(
        CashPilotStore store,
        DateOnly date,
        string account,
        string description,
        decimal amount,
        bool income,
        Classification? chosen = null,
        bool learn = true)
    {
        account = (account ?? "").Trim();
        description = string.Join(' ', (description ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (account.Length == 0) throw new ArgumentException("Informe a conta.", nameof(account));
        if (description.Length == 0) throw new ArgumentException("Informe a descrição.", nameof(description));
        if (amount <= 0) throw new ArgumentException("O valor deve ser maior que zero.", nameof(amount));

        var transaction = new Transaction
        {
            Account = account,
            Date = date,
            Amount = income ? amount : -amount,
            RawDescription = description,
            Type = income ? TransactionType.Income : TransactionType.Expense,
        };

        var auto = false;
        if (chosen is { } picked && picked.Category.Trim().Length > 0)
        {
            transaction = WithClassification(transaction, new Classification(picked.Category.Trim(), picked.Item?.Trim() ?? ""));
        }
        else
        {
            var result = store.BuildClassifier().Classify(description);
            if (result.IsClassified)
            {
                transaction = WithClassification(transaction, result.Classification!);
                auto = true;
            }
        }

        store.InTransaction(() =>
        {
            store.TryInsert(transaction, "MANUAL-" + transaction.Id.ToString("N"), "manual");
            if (!auto && learn && transaction.Category is not null)
                store.Classify(description, new Classification(transaction.Category, transaction.Item ?? ""));
        });

        return new ManualEntryResult(transaction, auto);
    }

    private static Transaction WithClassification(Transaction transaction, Classification classification) =>
        transaction with
        {
            Category = classification.Category,
            Item = string.IsNullOrWhiteSpace(classification.Item) ? null : classification.Item,
        };
}
