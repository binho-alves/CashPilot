using CashPilot.Domain.Transactions;

namespace CashPilot.Domain.Accounts;

public static class AccountBalance
{
    /// <summary>
    /// Hand-set balance (zero when none) plus every entry of the account dated after the anchor date and up to
    /// <paramref name="today"/>. Without an anchor date all past entries count.
    /// </summary>
    public static decimal Current(Account account, IEnumerable<Transaction> transactions, DateOnly today)
    {
        var balance = account.BalanceAnchor ?? 0m;
        foreach (var t in transactions)
        {
            if (!string.Equals(t.Account, account.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (t.Date > today) continue;
            if (account.BalanceAnchorDate is { } since && t.Date <= since) continue;
            balance += t.Amount;
        }
        return balance;
    }
}
