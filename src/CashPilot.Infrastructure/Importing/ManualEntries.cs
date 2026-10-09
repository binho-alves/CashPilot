using CashPilot.Domain.Descriptions;
using CashPilot.Domain.Transactions;
using CashPilot.Infrastructure.Persistence;

namespace CashPilot.Infrastructure.Importing;

/// <param name="AutoClassified">True when no category was given and the classifier knew the description.</param>
/// <param name="AlreadyExisted">True when the client id was already stored (a retry from the phone): nothing was added.</param>
public sealed record ManualEntryResult(Transaction Transaction, bool AutoClassified, bool AlreadyExisted = false);

/// <summary>
/// An entry typed by hand (cash, a Pix that is not in the statement yet). Unlike an import it has no dedup key to
/// protect: two identical entries typed on purpose are both kept.
/// </summary>
public static class ManualEntries
{
    /// <param name="amount">Always positive; <paramref name="income"/> decides the sign.</param>
    /// <param name="chosen">The category the user picked; when null the classifier tries, and an unknown entry stays pending.</param>
    /// <param name="learn">With a chosen category: also teach it as the rule for this description.</param>
    /// <param name="type">
    /// Null for a normal expense/income. <see cref="TransactionType.CardBillPayment"/> (paying a card bill from a bank
    /// account, an outflow) and <see cref="TransactionType.InternalTransfer"/> (between own accounts, either side) are
    /// neither spending nor income, so they are never classified and teach nothing.
    /// </param>
    /// <param name="clientId">
    /// An id made by the caller (the phone). It becomes the entry id and the dedup key, so sending the same entry again
    /// (even after the user deleted it) adds nothing and returns <see cref="ManualEntryResult.AlreadyExisted"/>.
    /// </param>
    public static ManualEntryResult Add(
        CashPilotStore store,
        DateOnly date,
        string account,
        string description,
        decimal amount,
        bool income,
        Classification? chosen = null,
        bool learn = true,
        TransactionType? type = null,
        Guid? clientId = null)
    {
        if (type is not (null or TransactionType.Expense or TransactionType.Income
                or TransactionType.CardBillPayment or TransactionType.InternalTransfer))
            throw new ArgumentException("Tipo de lançamento não permitido.", nameof(type));
        var neutral = type is TransactionType.CardBillPayment or TransactionType.InternalTransfer;

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
            Type = neutral ? type!.Value : income ? TransactionType.Income : TransactionType.Expense,
        };
        if (clientId is { } id && id != Guid.Empty) transaction = transaction with { Id = id };

        var auto = false;
        if (neutral)
        {
            // Not spending and not income: no category, nothing to learn.
        }
        else if (chosen is { } picked && picked.Category.Trim().Length > 0)
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

        var inserted = false;
        store.InTransaction(() =>
        {
            inserted = store.TryInsert(transaction, "MANUAL-" + transaction.Id.ToString("N"), "manual");
            if (inserted && !auto && learn && transaction.Category is not null)
                store.Classify(description, new Classification(transaction.Category, transaction.Item ?? ""));
        });

        return new ManualEntryResult(transaction, auto, AlreadyExisted: !inserted);
    }

    private static Transaction WithClassification(Transaction transaction, Classification classification) =>
        transaction with
        {
            Category = classification.Category,
            Item = string.IsNullOrWhiteSpace(classification.Item) ? null : classification.Item,
        };
}
