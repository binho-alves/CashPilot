using CashPilot.Domain.Accounts;
using CashPilot.Domain.Payments;
using CashPilot.Domain.Transactions;

namespace CashPilot.Domain.Tests;

public class FutureInstallmentsTests
{
    private static readonly Account Card = new()
    {
        Name = "Card A", Kind = AccountKind.CreditCard, ClosingDay = 10, DueDay = 20,
    };

    private static Transaction Installment(string description, decimal amount, int number, int count, DateOnly date, string account = "Card A") =>
        new()
        {
            Account = account, Date = date, Amount = -amount, RawDescription = description,
            Type = TransactionType.Expense, InstallmentNumber = number, InstallmentCount = count,
        };

    [Fact]
    public void Remaining_installments_follow_the_card_cycle()
    {
        var today = new DateOnly(2026, 10, 8);
        var plans = FutureInstallments.Build(
            [Card], [Installment("Shop X PARC 03/05", 100m, 3, 5, new DateOnly(2026, 10, 5))], today);

        var plan = Assert.Single(plans);
        Assert.Equal(2, plan.Remaining);
        Assert.Equal(200m, plan.RemainingTotal);
        // Purchase on 05/10 -> bill closing 10/10 (due 20/10). Next installments: closing 10/11 (due 20/11), 10/12 (due 20/12).
        Assert.Equal(new DateOnly(2026, 11, 20), plan.Future[0].Due);
        Assert.Equal(4, plan.Future[0].Number);
        Assert.Equal(new DateOnly(2026, 12, 20), plan.Future[1].Due);
    }

    [Fact]
    public void Only_the_latest_installment_seen_counts()
    {
        var today = new DateOnly(2026, 10, 8);
        var plans = FutureInstallments.Build([Card],
        [
            Installment("Shop X PARC 01/04", 50m, 1, 4, new DateOnly(2026, 8, 5)),
            Installment("Shop X PARC 02/04", 50m, 2, 4, new DateOnly(2026, 9, 5)),
            Installment("Shop X PARC 03/04", 50m, 3, 4, new DateOnly(2026, 10, 5)),
        ], today);

        var plan = Assert.Single(plans);
        Assert.Equal(3, plan.LastNumber);
        Assert.Single(plan.Future);
    }

    [Fact]
    public void Finished_purchases_are_not_listed()
    {
        var plans = FutureInstallments.Build([Card],
            [Installment("Shop X PARC 04/04", 50m, 4, 4, new DateOnly(2026, 10, 5))], new DateOnly(2026, 10, 8));
        Assert.Empty(plans);
    }

    [Fact]
    public void Installments_whose_bill_is_already_past_are_skipped()
    {
        // Last imported installment is old: the following ones should already have been charged.
        var plans = FutureInstallments.Build([Card],
            [Installment("Shop X PARC 01/06", 80m, 1, 6, new DateOnly(2026, 5, 5))], new DateOnly(2026, 10, 8));

        var plan = Assert.Single(plans);
        Assert.All(plan.Future, f => Assert.True(f.Due >= new DateOnly(2026, 10, 8)));
        Assert.Equal(new DateOnly(2026, 10, 20), plan.Future[0].Due);
    }

    [Fact]
    public void Different_purchases_at_the_same_shop_stay_separate()
    {
        var plans = FutureInstallments.Build([Card],
        [
            Installment("Shop X PARC 01/03", 100m, 1, 3, new DateOnly(2026, 10, 5)),
            Installment("Shop X PARC 01/03", 250m, 1, 3, new DateOnly(2026, 10, 6)),
        ], new DateOnly(2026, 10, 8));

        Assert.Equal(2, plans.Count);
    }

    [Fact]
    public void Card_without_cycle_days_projects_month_by_month()
    {
        var plain = new Account { Name = "Card A", Kind = AccountKind.CreditCard };
        var plans = FutureInstallments.Build([plain],
            [Installment("Shop X PARC 01/03", 90m, 1, 3, new DateOnly(2026, 10, 5))], new DateOnly(2026, 10, 8));

        Assert.Equal(new DateOnly(2026, 11, 5), plans[0].Future[0].Due);
    }

    [Fact]
    public void ByMonth_sums_installments_by_due_month()
    {
        var today = new DateOnly(2026, 10, 8);
        var plans = FutureInstallments.Build([Card],
        [
            Installment("Shop X PARC 01/03", 100m, 1, 3, new DateOnly(2026, 10, 5)),
            Installment("Shop Y PARC 02/03", 40m, 2, 3, new DateOnly(2026, 10, 6)),
        ], today);

        var months = FutureInstallments.ByMonth(plans);
        Assert.Equal(2, months.Count);
        Assert.Equal(140m, months[0].Total);   // November: X 2/3 + Y 3/3
        Assert.Equal(100m, months[1].Total);   // December: X 3/3
    }
}
