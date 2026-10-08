using CashPilot.Domain.Accounts;
using CashPilot.Domain.Payments;
using CashPilot.Domain.Transactions;

namespace CashPilot.Domain.Tests;

public class PaymentScheduleTests
{
    private static readonly Account Card = new()
    {
        Name = "Cartão X", Kind = AccountKind.CreditCard, ClosingDay = 20, DueDay = 28,
    };

    private static Transaction Tx(string account, string date, decimal amount, string description = "Loja",
        TransactionType type = TransactionType.Expense) => new()
    {
        Account = account,
        Date = DateOnly.Parse(date),
        Amount = amount,
        RawDescription = description,
        Type = type,
    };

    [Theory]
    [InlineData("2026-09-10", "2026-09-20")]
    [InlineData("2026-09-20", "2026-09-20")] // purchase on the closing day stays in that bill
    [InlineData("2026-09-21", "2026-10-20")]
    [InlineData("2026-12-25", "2027-01-20")]
    public void PurchaseBelongsToTheCycleThatClosesOnOrAfterIt(string purchase, string closing) =>
        Assert.Equal(DateOnly.Parse(closing), CardBilling.ClosingFor(DateOnly.Parse(purchase), 20));

    [Theory]
    [InlineData("2026-09-20", 28, "2026-09-28")]  // due later in the same month
    [InlineData("2026-09-20", 5, "2026-10-05")]   // due day before closing day: next month
    [InlineData("2026-09-20", 20, "2026-10-20")]  // same day is not "after" the closing
    public void DueIsTheFirstDueDayAfterClosing(string closing, int dueDay, string due) =>
        Assert.Equal(DateOnly.Parse(due), CardBilling.DueFor(DateOnly.Parse(closing), dueDay));

    [Fact]
    public void ShortMonthsClampTheDay()
    {
        Assert.Equal(new DateOnly(2027, 2, 28), CardBilling.ClosingFor(new DateOnly(2027, 2, 10), 31));
        Assert.Equal(new DateOnly(2028, 2, 29), CardBilling.ClosingFor(new DateOnly(2028, 2, 10), 31));
    }

    [Fact]
    public void CardEntriesBecomeOneBillPerCycleAndBillPaymentsAreIgnored()
    {
        var today = new DateOnly(2026, 10, 8);
        var transactions = new[]
        {
            Tx("Cartão X", "2026-09-10", -100m),
            Tx("Cartão X", "2026-09-18", -50m),
            Tx("Cartão X", "2026-09-19", 30m),                                        // refund reduces the bill
            Tx("Cartão X", "2026-09-25", -200m, "Terminal", TransactionType.CardCashAdvance), // gross counts
            Tx("Cartão X", "2026-09-30", 500m, "Pagamento", TransactionType.CardBillPayment), // never counts
            Tx("Cartão X", "2026-08-10", -999m),                                      // bill due 2026-08-28: past
        };

        var schedule = PaymentSchedule.Build(new[] { Card }, transactions, Array.Empty<Payable>(), today);

        // Bills due before today (08-28, 09-28) are not listed; only the cycle closing 10-20 is.
        var oct = Assert.Single(schedule);
        Assert.Equal(UpcomingKind.CardBill, oct.Kind);
        Assert.Equal(new DateOnly(2026, 10, 28), oct.DueDate);
        Assert.Equal(200m, oct.Amount); // only the 09-25 cash-out is in the cycle closing 10-20
    }

    [Fact]
    public void PayablesAndFutureEntriesAreListed()
    {
        var today = new DateOnly(2026, 10, 8);
        var overdue = new Payable { Description = "Luz", DueDate = new DateOnly(2026, 10, 1), Amount = 150m };
        var paid = new Payable { Description = "Água", DueDate = new DateOnly(2026, 10, 2), Amount = 80m, Paid = true };
        var transactions = new[]
        {
            Tx("Banco Y", "2026-11-05", -300m, "Parcela sofá"),
            Tx("Banco Y", "2026-10-05", -40m, "Já passou"),
            Tx("Banco Y", "2026-11-06", 900m, "Receita futura"),
        };

        var schedule = PaymentSchedule.Build(Array.Empty<Account>(), transactions, new[] { overdue, paid }, today);

        Assert.Equal(2, schedule.Count);
        Assert.Equal("Luz", schedule[0].Description);
        Assert.True(schedule[0].Overdue);
        Assert.Equal(UpcomingKind.FutureEntry, schedule[1].Kind);
        Assert.Equal(300m, schedule[1].Amount);
    }

    [Fact]
    public void CardWithoutBillDaysListsItsFutureEntriesIndividually()
    {
        var today = new DateOnly(2026, 10, 8);
        var plainCard = new Account { Name = "Cartão Z", Kind = AccountKind.CreditCard };
        var transactions = new[] { Tx("Cartão Z", "2026-11-10", -75m, "Parcela") };

        var schedule = PaymentSchedule.Build(new[] { plainCard }, transactions, Array.Empty<Payable>(), today);

        Assert.Equal(UpcomingKind.FutureEntry, Assert.Single(schedule).Kind);
    }

    [Fact]
    public void BalanceIsTheAnchorPlusEntriesAfterItUpToToday()
    {
        var bank = new Account
        {
            Name = "Banco Y", BalanceAnchor = 1000m, BalanceAnchorDate = new DateOnly(2026, 10, 1),
        };
        var transactions = new[]
        {
            Tx("Banco Y", "2026-09-30", -500m),   // before the anchor: already in the typed balance
            Tx("Banco Y", "2026-10-01", -100m),   // on the anchor date: already in the typed balance
            Tx("Banco Y", "2026-10-03", -200m),
            Tx("Banco Y", "2026-10-05", 50m),
            Tx("Banco Y", "2026-10-20", -999m),   // future
            Tx("Outra", "2026-10-03", -1m),       // other account
        };

        Assert.Equal(850m, AccountBalance.Current(bank, transactions, new DateOnly(2026, 10, 8)));
        Assert.Equal(-500m - 100m - 200m + 50m,
            AccountBalance.Current(new Account { Name = "Banco Y" }, transactions, new DateOnly(2026, 10, 8)));
    }
}
