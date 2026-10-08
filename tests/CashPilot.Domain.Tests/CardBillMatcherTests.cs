using CashPilot.Domain.Accounts;
using CashPilot.Domain.Payments;
using CashPilot.Domain.Transactions;

namespace CashPilot.Domain.Tests;

public class CardBillMatcherTests
{
    // Closes on the 20th, due on the 28th. September cycle: closes 20/09, due 28/09.
    private static readonly Account Card = new() { Name = "Cartão X", Kind = AccountKind.CreditCard, ClosingDay = 20, DueDay = 28 };
    private static readonly Account Other = new() { Name = "Cartão Y", Kind = AccountKind.CreditCard, ClosingDay = 20, DueDay = 28 };
    private static readonly Account Bank = new() { Name = "Banco A", Kind = AccountKind.BankAccount };

    private static Transaction Tx(string account, string date, decimal amount, string description = "Loja",
        TransactionType type = TransactionType.Expense) => new()
    {
        Account = account, Date = DateOnly.Parse(date), Amount = amount, RawDescription = description, Type = type,
    };

    private static Transaction BankPayment(string date, decimal amount) =>
        Tx("Banco A", date, -amount, "PAGTO FATURA CARTAO", TransactionType.CardBillPayment);

    private static Transaction CardSidePayment(string card, string date, decimal amount) =>
        Tx(card, date, amount, "Pagamento", TransactionType.CardBillPayment);

    [Fact]
    public void APaymentOfTheSameAmountInsideTheWindowPaysTheBill()
    {
        var transactions = new[] { Tx("Cartão X", "2026-09-10", -300m), BankPayment("2026-09-27", 300m) };

        var matches = CardBillMatcher.Match(new[] { Card, Bank }, transactions);

        var bill = Assert.Single(matches.Cycles);
        Assert.True(bill.Paid);
        Assert.Equal(new DateOnly(2026, 9, 28), bill.Due);
        Assert.Empty(matches.UnmatchedPayments);
    }

    [Theory]
    [InlineData("2026-09-19")]  // before the bill closed
    [InlineData("2026-10-09")]  // more than 10 days after the due date
    public void APaymentOutsideTheWindowDoesNotPayIt(string paymentDate)
    {
        var transactions = new[] { Tx("Cartão X", "2026-09-10", -300m), BankPayment(paymentDate, 300m) };

        var matches = CardBillMatcher.Match(new[] { Card, Bank }, transactions);

        Assert.False(Assert.Single(matches.Cycles).Paid);
        Assert.Single(matches.UnmatchedPayments);
    }

    [Fact]
    public void ADifferentAmountLeavesTheBillOpenAndThePaymentUnmatched()
    {
        var transactions = new[] { Tx("Cartão X", "2026-09-10", -300m), BankPayment("2026-09-27", 250m) };

        var matches = CardBillMatcher.Match(new[] { Card, Bank }, transactions);

        Assert.False(Assert.Single(matches.Cycles).Paid);
        Assert.Equal(250m, -Assert.Single(matches.UnmatchedPayments).Amount);
    }

    [Fact]
    public void TwoBillsWithTheSameAmountEachTakeTheirOwnPayment()
    {
        var transactions = new[]
        {
            Tx("Cartão X", "2026-08-10", -400m),   // bill due 28/08
            Tx("Cartão X", "2026-09-10", -400m),   // bill due 28/09
            BankPayment("2026-08-27", 400m),
        };

        var cycles = CardBillMatcher.Match(new[] { Card, Bank }, transactions).Cycles;

        Assert.True(cycles[0].Paid);    // August: the payment of 27/08
        Assert.False(cycles[1].Paid);   // September: no payment yet
    }

    [Fact]
    public void OnePaymentPaysOnlyOneOfTwoCardsWithTheSameAmount()
    {
        var transactions = new[]
        {
            Tx("Cartão X", "2026-09-10", -300m),
            Tx("Cartão Y", "2026-09-12", -300m),
            BankPayment("2026-09-27", 300m),
        };

        var cycles = CardBillMatcher.Match(new[] { Card, Other, Bank }, transactions).Cycles;

        Assert.Equal(1, cycles.Count(c => c.Paid));
    }

    [Fact]
    public void AnInflowOnTheCardNamesTheCardAndItsBankTwinIsNotLeftUnmatched()
    {
        var transactions = new[]
        {
            Tx("Cartão X", "2026-09-10", -300m),
            Tx("Cartão Y", "2026-09-12", -300m),
            CardSidePayment("Cartão Y", "2026-09-27", 300m),
            BankPayment("2026-09-27", 300m),
        };

        var matches = CardBillMatcher.Match(new[] { Card, Other, Bank }, transactions);

        Assert.False(matches.Cycles.Single(c => c.Card.Name == "Cartão X").Paid);
        Assert.True(matches.Cycles.Single(c => c.Card.Name == "Cartão Y").Paid);
        Assert.Empty(matches.UnmatchedPayments);
    }

    [Fact]
    public void ABillPaidEarlyIsNoLongerListedAsToPay()
    {
        var today = new DateOnly(2026, 9, 24);
        var transactions = new[] { Tx("Cartão X", "2026-09-10", -300m), BankPayment("2026-09-22", 300m) };

        Assert.Empty(PaymentSchedule.Build(new[] { Card, Bank }, transactions, Array.Empty<Payable>(), today));

        var open = new[] { Tx("Cartão X", "2026-09-10", -300m) };
        Assert.Single(PaymentSchedule.Build(new[] { Card, Bank }, open, Array.Empty<Payable>(), today));
    }

    [Fact]
    public void ABillMarkedAsPaidByHandIsPaidEvenWithNoMatchingPayment()
    {
        var today = new DateOnly(2026, 9, 24);
        var transactions = new[] { Tx("Cartão X", "2026-09-10", -300m), BankPayment("2026-09-22", 280m) };
        var settlement = new CardBillSettlement("cartão x", new DateOnly(2026, 9, 20), new DateOnly(2026, 9, 22));

        var matches = CardBillMatcher.Match(new[] { Card, Bank }, transactions, new[] { settlement });

        var bill = Assert.Single(matches.Cycles);
        Assert.True(bill.Paid);
        Assert.Null(bill.Payment);
        Assert.Equal(new DateOnly(2026, 9, 22), bill.ManualPaidOn);

        Assert.Empty(PaymentSchedule.Build(new[] { Card, Bank }, transactions, Array.Empty<Payable>(), today, new[] { settlement }));
        Assert.Single(PaymentSchedule.Build(new[] { Card, Bank }, transactions, Array.Empty<Payable>(), today));
    }

    [Fact]
    public void AManualMarkOnOneBillDoesNotTouchTheOthers()
    {
        var transactions = new[] { Tx("Cartão X", "2026-08-10", -100m), Tx("Cartão X", "2026-09-10", -100m) };
        var settlement = new CardBillSettlement("Cartão X", new DateOnly(2026, 8, 20), new DateOnly(2026, 8, 25));

        var cycles = CardBillMatcher.Match(new[] { Card, Bank }, transactions, new[] { settlement }).Cycles;

        Assert.True(cycles[0].Paid);
        Assert.False(cycles[1].Paid);
    }
}
