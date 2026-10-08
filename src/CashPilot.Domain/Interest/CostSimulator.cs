using CashPilot.Domain.Accounts;

namespace CashPilot.Domain.Interest;

public enum FundingKind
{
    Overdraft = 1,
    CardCashOut = 2,
}

/// <summary>
/// One way of getting money into an account. <see cref="Cost"/> is what it costs on top of the amount received.
/// <see cref="Feasible"/> is false when the option cannot cover the amount (<see cref="Note"/> says why).
/// </summary>
public sealed record FundingOption(
    FundingKind Kind,
    string Name,
    string Detail,
    decimal Amount,
    decimal Cost,
    bool Feasible,
    string? Note = null);

/// <summary>
/// "I need R$ X for N days: what is the cheapest way?" Compares the options whose terms are registered:
/// overdraft (LIS) of each bank account that has a rate, and the card-terminal cash-out at 1x to 12x.
/// Boleto late fees and revolving credit are not modelled yet (no rates registered).
/// </summary>
public static class CostSimulator
{
    /// <summary>
    /// Simple interest, pro rata by day: Amount x monthly rate x (days beyond the free days) / 30. IOF and other
    /// taxes are not included, so treat the figure as a floor and compare it with the statement.
    /// </summary>
    public static decimal OverdraftCost(decimal amount, int days, int freeDays, decimal monthlyRatePercent)
    {
        var chargedDays = Math.Max(0, days - Math.Max(0, freeDays));
        return Math.Round(amount * monthlyRatePercent / 100m * chargedDays / 30m, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>Smallest gross charge on the card whose net, after the terminal fee, is at least <paramref name="net"/>.</summary>
    public static CardCashAdvance CashOutForNet(decimal net, decimal feePercent, int installments)
    {
        if (feePercent < 0 || feePercent >= 100) throw new ArgumentOutOfRangeException(nameof(feePercent));
        var gross = Math.Ceiling(net * 100m / (1m - feePercent / 100m)) / 100m;
        var result = new CardCashAdvance(gross, feePercent, installments);
        while (result.Net < net)
        {
            gross += 0.01m;
            result = new CardCashAdvance(gross, feePercent, installments);
        }
        return result;
    }

    /// <param name="balances">Current balance by bank account name; used to size the overdraft already in use.</param>
    public static IReadOnlyList<FundingOption> Compare(
        decimal amount,
        int days,
        IEnumerable<Account> accounts,
        IReadOnlyDictionary<string, decimal> balances,
        CashAdvanceFeeTable? feeTable = null)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (days < 1) throw new ArgumentOutOfRangeException(nameof(days));
        feeTable ??= new CashAdvanceFeeTable();
        var options = new List<FundingOption>();

        foreach (var bank in accounts.Where(a => a.Kind == AccountKind.BankAccount
                                                 && a.OverdraftMonthlyRatePercent is not null
                                                 && a.OverdraftLimit is not null))
        {
            var balance = balances.TryGetValue(bank.Name, out var b) ? b : 0m;
            var inUse = Math.Max(0m, -balance);
            var available = Math.Max(0m, bank.OverdraftLimit!.Value - inUse);
            var free = bank.OverdraftFreeDays ?? 0;
            var cost = OverdraftCost(amount, days, free, bank.OverdraftMonthlyRatePercent!.Value);
            var feasible = amount <= available;
            options.Add(new FundingOption(
                FundingKind.Overdraft,
                $"LIS {bank.Name}",
                $"{free} dias sem juros, {bank.OverdraftMonthlyRatePercent.Value:0.##}% ao mês",
                amount,
                cost,
                feasible,
                feasible ? null : $"LIS disponível R$ {available:N2}"));
        }

        for (var installments = 1; installments <= 12; installments++)
        {
            if (!feeTable.TryGet(installments, out var fee)) continue;
            var cashOut = CashOutForNet(amount, fee, installments);
            options.Add(new FundingOption(
                FundingKind.CardCashOut,
                $"Saque na maquininha {installments}x",
                $"cobra R$ {cashOut.Gross:N2} no cartão ({installments}x de R$ {cashOut.InstallmentAmount:N2}), taxa {fee:0.##}%",
                amount,
                cashOut.Gross - amount,
                true));
        }

        return options
            .OrderByDescending(o => o.Feasible)
            .ThenBy(o => o.Cost)
            .ThenBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
