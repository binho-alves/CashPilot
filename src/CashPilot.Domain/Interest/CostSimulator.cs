using CashPilot.Domain.Accounts;
using CashPilot.Domain.Payments;

namespace CashPilot.Domain.Interest;

public enum FundingKind
{
    Overdraft = 1,
    CardCashOut = 2,
    LateBoleto = 3,
    CardRevolving = 4,
    LateCardBill = 5,
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
/// A boleto whose multa and juros de mora are registered can also be left unpaid for a while (its extra cost is the
/// option's cost). Leaving part of a card bill unpaid (revolving credit, then bill installments) is compared for each card that has a revolving rate.
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

    /// <summary>
    /// What a boleto costs on top of its amount when paid <paramref name="daysLate"/> days after the due date: the one-off
    /// multa plus juros de mora, simple and pro rata by day (monthly rate / 30). Nothing is due when it is not late.
    /// </summary>
    public static decimal BoletoLateCost(decimal amount, decimal feePercent, decimal monthlyInterestPercent, int daysLate)
    {
        if (daysLate <= 0) return 0m;
        var fee = amount * feePercent / 100m;
        var interest = amount * monthlyInterestPercent / 100m * daysLate / 30m;
        return Math.Round(fee + interest, 2, MidpointRounding.AwayFromZero);
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
        CashAdvanceFeeTable? feeTable = null,
        IEnumerable<Payable>? payables = null,
        DateOnly? today = null)
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

        // Not paying part of a card bill: interest at the card's own rate plus IOF, capped at the amount itself.
        foreach (var card in accounts.Where(a => a.Kind == AccountKind.CreditCard && a.RevolvingMonthlyRatePercent is not null))
        {
            var revolving = card.RevolvingMonthlyRatePercent!.Value;
            var installment = card.InstallmentMonthlyRatePercent;
            var cost = CardInterest.CarryCost(amount, days, revolving, installment);
            var detail = days <= CardInterest.RevolvingMaxDays || installment is null
                ? $"deixa R$ {amount:N2} da fatura sem pagar: rotativo {revolving:0.##}% ao mês + IOF"
                : $"deixa R$ {amount:N2} da fatura sem pagar: passado o próximo vencimento vira parcelamento da fatura, {installment.Value:0.##}% ao mês + IOF";
            options.Add(new FundingOption(
                FundingKind.CardRevolving,
                $"Rotativo {card.Name}",
                detail,
                amount,
                cost,
                true,
                null));

            // Paying nothing at all (below the minimum) is worse than the revolving: multa and mora come on top.
            options.Add(new FundingOption(
                FundingKind.LateCardBill,
                $"Atrasar a fatura {card.Name}",
                $"não paga R$ {amount:N2} da fatura e passa do mínimo: juros {revolving:0.##}% ao mês + mora {CardInterest.LateInterestMonthlyPercent:0.##}% ao mês + multa {CardInterest.LateFeePercent:0.##}% + IOF, contados do vencimento (além do cartão bloqueável e do nome negativável)",
                amount,
                CardInterest.LateCost(amount, days, revolving),
                true,
                null));
        }

        // Leaving a boleto unpaid until the end of the period: only the extra late cost counts (a multa already
        // incurred because it is overdue today is not a consequence of this choice).
        if (payables is not null)
        {
            var start = today ?? DateOnly.FromDateTime(DateTime.Today);
            var payAt = start.AddDays(days);
            foreach (var bill in payables.Where(p => !p.Paid && p.LateFeePercent is not null && p.LateInterestMonthlyPercent is not null))
            {
                var lateAtPayment = payAt.DayNumber - bill.DueDate.DayNumber;
                if (lateAtPayment <= 0) continue;
                var lateToday = Math.Max(0, start.DayNumber - bill.DueDate.DayNumber);
                var fee = bill.LateFeePercent!.Value;
                var interest = bill.LateInterestMonthlyPercent!.Value;
                var cost = BoletoLateCost(bill.Amount, fee, interest, lateAtPayment)
                           - BoletoLateCost(bill.Amount, fee, interest, lateToday);
                var feasible = bill.Amount >= amount;
                options.Add(new FundingOption(
                    FundingKind.LateBoleto,
                    $"Atrasar boleto: {bill.Description}",
                    $"venceu/vence {bill.DueDate:dd/MM}, pago em {payAt:dd/MM} ({lateAtPayment} dias de atraso): multa {fee:0.##}% + mora {interest:0.##}% ao mês",
                    amount,
                    cost,
                    feasible,
                    feasible ? null : $"o boleto é de R$ {bill.Amount:N2}"));
            }
        }

        return options
            .OrderByDescending(o => o.Feasible)
            .ThenBy(o => o.Cost)
            .ThenBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
