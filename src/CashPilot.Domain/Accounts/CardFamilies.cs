using CashPilot.Domain.Transactions;

namespace CashPilot.Domain.Accounts;

/// <summary>
/// Additional cards (cartões adicionais) share the main card's bill. Everything that builds bills from cards and entries
/// calls <see cref="Fold"/> first: the additional cards disappear from the list and their entries are moved to the main
/// card, so the bill, its limit cycle, payments and forecasts are those of the main card, with the entries of all of them.
/// Folding twice changes nothing.
/// </summary>
public static class CardFamilies
{
    /// <summary>The main card of an additional one, or null when the account is not an additional card of a registered credit card.</summary>
    public static Account? ParentOf(Account account, IReadOnlyList<Account> accounts)
    {
        if (account.Kind != AccountKind.CreditCard || string.IsNullOrWhiteSpace(account.ParentCard)) return null;
        var parent = accounts.FirstOrDefault(a =>
            a.Kind == AccountKind.CreditCard && string.Equals(a.Name, account.ParentCard!.Trim(), StringComparison.OrdinalIgnoreCase));
        // A card cannot be an additional of itself, and the main card cannot be an additional of another one.
        return parent is null || ReferenceEquals(parent, account) || !string.IsNullOrWhiteSpace(parent.ParentCard) ? null : parent;
    }

    public static (List<Account> Accounts, List<Transaction> Transactions) Fold(
        IEnumerable<Account> accounts, IEnumerable<Transaction> transactions)
    {
        var accountList = accounts.ToList();
        var transactionList = transactions.ToList();

        var moves = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in accountList)
            if (ParentOf(account, accountList) is { } parent) moves[account.Name.Trim()] = parent.Name;
        if (moves.Count == 0) return (accountList, transactionList);

        var kept = accountList.Where(a => !moves.ContainsKey(a.Name.Trim())).ToList();
        var moved = transactionList
            .Select(t => moves.TryGetValue(t.Account.Trim(), out var main) ? t with { Account = main } : t)
            .ToList();
        return (kept, moved);
    }
}
