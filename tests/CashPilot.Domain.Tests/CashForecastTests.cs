using CashPilot.Domain.Accounts;
using CashPilot.Domain.Payments;
using CashPilot.Domain.Transactions;

namespace CashPilot.Domain.Tests;

public class CashForecastTests
{
    private static readonly DateOnly Today = new(2026, 10, 8);
    private static readonly DateOnly Until = new(2027, 1, 8);

    private static readonly Account Bank = new() { Name = "Bank A", Kind = AccountKind.BankAccount };

    private static readonly Account Card = new()
    {
        Name = "Card A", Kind = AccountKind.CreditCard, ClosingDay = 10, DueDay = 20,
    };

    private static Transaction Tx(string account, string date, decimal amount, string description,
        TransactionType type = TransactionType.Expense) => new()
    {
        Account = account, Date = DateOnly.Parse(date), Amount = amount, RawDescription = description, Type = type,
    };

    private static IEnumerable<Transaction> Monthly(string account, string description, decimal amount, int day, params int[] months) =>
        months.Select(m => Tx(account, $"2026-{m:00}-{day:00}", amount, description,
            amount < 0 ? TransactionType.Expense : TransactionType.Income));

    private static IReadOnlyList<UpcomingPayment> Build(IEnumerable<Transaction> transactions, IEnumerable<Payable>? payables = null,
        bool includeIncome = true) =>
        CashForecast.Build([Bank, Card], transactions, payables ?? Array.Empty<Payable>(), Today, Until, includeIncome: includeIncome);

    [Fact]
    public void Recurring_bank_expense_and_income_are_placed_on_their_days()
    {
        var items = Build(
            Monthly("Bank A", "Rent flat", -1500m, 5, 7, 8, 9).Concat(Monthly("Bank A", "Salary Co", 5000m, 6, 7, 8, 9)));

        var rent = items.Where(i => i.Kind == UpcomingKind.RecurringExpense).ToList();
        var salary = items.Where(i => i.Kind == UpcomingKind.RecurringIncome).ToList();

        Assert.Equal(new DateOnly(2026, 11, 5), rent[0].DueDate);
        Assert.Equal(1500m, rent[0].Amount);
        Assert.Equal(3, rent.Count);   // Nov, Dec, Jan (until 08/01)
        Assert.Equal(5000m, salary[0].Amount);
        Assert.Equal(new DateOnly(2026, 11, 6), salary[0].DueDate);
    }

    [Fact]
    public void Income_can_be_left_out()
    {
        var items = Build(Monthly("Bank A", "Salary Co", 5000m, 6, 7, 8, 9), includeIncome: false);

        Assert.DoesNotContain(items, i => i.Kind == UpcomingKind.RecurringIncome);
    }

    [Fact]
    public void A_registered_boleto_for_the_same_expense_is_not_counted_twice()
    {
        var november = new Payable { Description = "Rent November", DueDate = new DateOnly(2026, 11, 5), Amount = 1500m };

        var items = Build(Monthly("Bank A", "Rent flat", -1500m, 5, 7, 8, 9), [november]);

        Assert.DoesNotContain(items, i => i.Kind == UpcomingKind.RecurringExpense && i.DueDate.Month == 11);
        Assert.Contains(items, i => i.Kind == UpcomingKind.RecurringExpense && i.DueDate.Month == 12);
    }

    [Fact]
    public void Recurring_card_charge_goes_to_the_bill_of_its_cycle_even_when_that_bill_has_no_entry_yet()
    {
        // Card A closes on the 10th, due on the 20th. Streaming is charged on the 3rd of each month.
        var items = Build(Monthly("Card A", "Streaming", -50m, 3, 7, 8, 9));

        var bills = items.Where(i => i.Kind == UpcomingKind.CardBill).ToList();
        // Charge on 03/11 -> bill closing 10/11, due 20/11.
        var november = bills.Single(b => b.DueDate == new DateOnly(2026, 11, 20));
        Assert.Equal(50m, november.Amount);
        Assert.Equal(50m, november.ProjectedPart);
        Assert.Equal(new DateOnly(2026, 11, 10), november.Closing);
    }

    [Fact]
    public void Projection_is_added_on_top_of_a_bill_that_already_exists()
    {
        var items = Build(Monthly("Card A", "Streaming", -50m, 3, 7, 8, 9)
            .Append(Tx("Card A", "2026-10-02", -200m, "Market")));

        // Open cycle closes 10/10 (due 20/10): has the 200 market entry; the 3rd of October already passed in the data.
        var october = items.Single(i => i.Kind == UpcomingKind.CardBill && i.DueDate == new DateOnly(2026, 10, 20));
        Assert.Equal(200m, october.Amount);
        Assert.Equal(0m, october.ProjectedPart);
    }

    [Fact]
    public void Future_installments_are_added_to_the_following_bills()
    {
        var installment = Tx("Card A", "2026-10-05", -100m, "Shop PARC 01/03") with { InstallmentNumber = 1, InstallmentCount = 3 };

        var items = Build([installment]);

        var bills = items.Where(i => i.Kind == UpcomingKind.CardBill).OrderBy(i => i.DueDate).ToList();
        // 05/10 is in the bill that closes 10/10 (due 20/10) and already has the 1st installment (100).
        Assert.Equal(100m, bills[0].Amount);
        Assert.Equal(0m, bills[0].ProjectedPart);
        Assert.Equal(new DateOnly(2026, 11, 20), bills[1].DueDate);
        Assert.Equal(100m, bills[1].Amount);
        Assert.Equal(100m, bills[1].ProjectedPart);
        Assert.Equal(new DateOnly(2026, 12, 20), bills[2].DueDate);
    }

    [Fact]
    public void Installment_and_streaming_in_the_same_cycle_share_one_bill()
    {
        var installment = Tx("Card A", "2026-10-05", -100m, "Shop PARC 01/03") with { InstallmentNumber = 1, InstallmentCount = 3 };

        var items = Build(Monthly("Card A", "Streaming", -50m, 3, 7, 8, 9).Append(installment));

        var november = items.Single(i => i.Kind == UpcomingKind.CardBill && i.DueDate == new DateOnly(2026, 11, 20));
        Assert.Equal(150m, november.Amount);
    }

    [Fact]
    public void Project_runs_the_balance_day_by_day_with_inflows_and_overdue()
    {
        var items = new[]
        {
            new UpcomingPayment(UpcomingKind.Payable, "Late power bill", new DateOnly(2026, 10, 1), 100m, null, null, true),
            new UpcomingPayment(UpcomingKind.RecurringExpense, "Rent flat", new DateOnly(2026, 10, 20), 1500m, "Bank A", null, false),
            new UpcomingPayment(UpcomingKind.RecurringIncome, "Salary Co", new DateOnly(2026, 11, 5), 2000m, "Bank A", null, false),
            new UpcomingPayment(UpcomingKind.Payable, "Beyond the horizon", new DateOnly(2027, 6, 1), 999m, null, null, false),
        };

        var summary = CashForecast.Project(1000m, items, lisLimit: 500m, Today, horizonDays: 60);

        Assert.Equal(2, summary.Days.Count);
        Assert.Equal(-600m, summary.Days[0].Balance);   // 1000 - 100 overdue - 1500
        Assert.Equal(1400m, summary.Days[1].Balance);   // + 2000
        Assert.Equal(new DateOnly(2026, 10, 20), summary.Lowest!.Date);
        Assert.Equal(new DateOnly(2026, 10, 20), summary.FirstNegative);
        Assert.Equal(new DateOnly(2026, 10, 20), summary.FirstBeyondLis);   // 600 needed > 500 limit
        Assert.Equal(2000m, summary.Days[1].Inflow);
    }

    [Fact]
    public void Project_without_trouble_has_no_negative_day()
    {
        var items = new[]
        {
            new UpcomingPayment(UpcomingKind.Payable, "Water", new DateOnly(2026, 10, 20), 100m, null, null, false),
        };

        var summary = CashForecast.Project(1000m, items, 0m, Today, 30);

        Assert.Null(summary.FirstNegative);
        Assert.Null(summary.FirstBeyondLis);
        Assert.Equal(900m, summary.Lowest!.Balance);
    }

    [Fact]
    public void Project_inside_the_overdraft_limit_is_negative_but_not_beyond_it()
    {
        var items = new[]
        {
            new UpcomingPayment(UpcomingKind.Payable, "Big bill", new DateOnly(2026, 10, 20), 1300m, null, null, false),
        };

        var summary = CashForecast.Project(1000m, items, 500m, Today, 30);

        Assert.Equal(new DateOnly(2026, 10, 20), summary.FirstNegative);
        Assert.Null(summary.FirstBeyondLis);
    }
}
