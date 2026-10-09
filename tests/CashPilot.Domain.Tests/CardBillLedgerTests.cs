using CashPilot.Domain.Accounts;
using CashPilot.Domain.Payments;
using CashPilot.Domain.Transactions;

namespace CashPilot.Domain.Tests;

public class CardBillLedgerTests
{
    // Closes on the 20th, due on the 28th. September cycle: closes 20/09, due 28/09.
    private static readonly Account CardX = new() { Name = "Cartão X", Kind = AccountKind.CreditCard, ClosingDay = 20, DueDay = 28 };
    private static readonly Account CardY = new() { Name = "Cartão Y", Kind = AccountKind.CreditCard, ClosingDay = 20, DueDay = 28 };
    private static readonly Account Bank = new() { Name = "Banco A", Kind = AccountKind.BankAccount };
    private static readonly DateOnly Closing = new(2026, 9, 20);

    private static Transaction Tx(string account, string date, decimal amount, TransactionType type = TransactionType.Expense) => new()
    {
        Account = account, Date = DateOnly.Parse(date), Amount = amount, RawDescription = "Item", Type = type,
    };

    private static Transaction BankPayment(string date, decimal amount) =>
        Tx("Banco A", date, -amount, TransactionType.CardBillPayment);

    private static CardBillResult Build(
        DateOnly today, IEnumerable<Transaction> transactions, IEnumerable<CardBillPaymentLink>? links = null,
        IEnumerable<CardBillSettlement>? settlements = null)
    {
        var result = CardBillLedger.Build(new[] { CardX, CardY, Bank }, transactions, settlements, links, today);
        return new CardBillResult(result);
    }

    private sealed class CardBillResult(CardBillLedgerResult result)
    {
        public CardBillStatement Bill(string card) => result.Statements.Single(s => s.Card.Name == card);
        public CardBillLedgerResult Raw => result;
    }

    [Fact]
    public void ABillBeforeItsClosingIsOpen()
    {
        var ledger = Build(new DateOnly(2026, 9, 10), new[] { Tx("Cartão X", "2026-09-05", -100m) });

        var bill = ledger.Bill("Cartão X");
        Assert.Equal(CardBillState.Open, bill.State);
        Assert.Equal(100m, bill.Total);
        Assert.Equal(100m, bill.Remaining);
    }

    [Fact]
    public void AClosedUnpaidBillIsToPayThenOverdue()
    {
        var transactions = new[] { Tx("Cartão X", "2026-09-05", -100m) };

        Assert.Equal(CardBillState.ToPay, Build(new DateOnly(2026, 9, 25), transactions).Bill("Cartão X").State);
        Assert.Equal(CardBillState.Overdue, Build(new DateOnly(2026, 9, 29), transactions).Bill("Cartão X").State);
    }

    [Fact]
    public void AnExactPaymentIsFoundByAmountAndPaysTheBill()
    {
        var ledger = Build(new DateOnly(2026, 9, 29), new[] { Tx("Cartão X", "2026-09-05", -100m), BankPayment("2026-09-27", 100m) });

        var bill = ledger.Bill("Cartão X");
        Assert.Equal(CardBillState.Paid, bill.State);
        Assert.False(Assert.Single(bill.Parts).Manual);
    }

    [Fact]
    public void LinkedPaymentsCanPayABillInPartsAndLeaveTheRest()
    {
        var first = BankPayment("2026-09-20", 40m);
        var second = BankPayment("2026-09-27", 25m);
        var links = new[]
        {
            new CardBillPaymentLink(first.Id, "Cartão X", Closing, 40m),
            new CardBillPaymentLink(second.Id, "Cartão X", Closing, 25m),
        };

        var bill = Build(new DateOnly(2026, 9, 25), new[] { Tx("Cartão X", "2026-09-05", -100m), first, second }, links).Bill("Cartão X");

        Assert.Equal(65m, bill.Paid);
        Assert.Equal(35m, bill.Remaining);
        Assert.Equal(CardBillState.Partial, bill.State);
        Assert.All(bill.Parts, p => Assert.True(p.Manual));
    }

    [Fact]
    public void ALinkedPartialBillThatIsPastDueIsOverdueForTheRest()
    {
        var payment = BankPayment("2026-09-27", 60m);
        var links = new[] { new CardBillPaymentLink(payment.Id, "Cartão X", Closing, 60m) };

        var bill = Build(new DateOnly(2026, 10, 5), new[] { Tx("Cartão X", "2026-09-05", -100m), payment }, links).Bill("Cartão X");

        Assert.Equal(CardBillState.Overdue, bill.State);
        Assert.Equal(40m, bill.Remaining);
    }

    [Fact]
    public void OnePaymentCanPayTwoCards()
    {
        var payment = BankPayment("2026-09-27", 150m);
        var links = new[]
        {
            new CardBillPaymentLink(payment.Id, "Cartão X", Closing, 100m),
            new CardBillPaymentLink(payment.Id, "Cartão Y", Closing, 50m),
        };

        var ledger = Build(new DateOnly(2026, 9, 29),
            new[] { Tx("Cartão X", "2026-09-05", -100m), Tx("Cartão Y", "2026-09-06", -50m), payment }, links);

        Assert.True(ledger.Bill("Cartão X").IsPaid);
        Assert.True(ledger.Bill("Cartão Y").IsPaid);
        Assert.Empty(ledger.Raw.UnmatchedPayments);
    }

    [Fact]
    public void AFullyLinkedBillTakesNoSecondPaymentByAmount()
    {
        var chosen = BankPayment("2026-09-26", 100m);
        var lookalike = BankPayment("2026-09-27", 100m);
        var links = new[] { new CardBillPaymentLink(chosen.Id, "Cartão X", Closing, 100m) };

        var ledger = Build(new DateOnly(2026, 9, 29), new[] { Tx("Cartão X", "2026-09-05", -100m), chosen, lookalike }, links);

        var bill = ledger.Bill("Cartão X");
        Assert.Equal(chosen.Id, Assert.Single(bill.Parts).Payment.Id);
        Assert.Contains(ledger.Raw.UnmatchedPayments, p => p.Id == lookalike.Id);
    }

    [Fact]
    public void ABillMarkedByHandIsPaidInFull()
    {
        var settlements = new[] { new CardBillSettlement("Cartão X", Closing, new DateOnly(2026, 9, 27)) };

        var bill = Build(new DateOnly(2026, 10, 5), new[] { Tx("Cartão X", "2026-09-05", -100m) }, null, settlements).Bill("Cartão X");

        Assert.True(bill.IsPaid);
        Assert.Equal(new DateOnly(2026, 9, 27), bill.SettledByHand);
    }

    [Fact]
    public void ALinkToAMissingEntryIsIgnored()
    {
        var links = new[] { new CardBillPaymentLink(Guid.NewGuid(), "Cartão X", Closing, 100m) };

        var bill = Build(new DateOnly(2026, 9, 25), new[] { Tx("Cartão X", "2026-09-05", -100m) }, links).Bill("Cartão X");

        Assert.Equal(0m, bill.Paid);
        Assert.Equal(CardBillState.ToPay, bill.State);
    }

    [Fact]
    public void ThePaymentScheduleListsOnlyWhatIsLeftOfAPartlyPaidBill()
    {
        var payment = BankPayment("2026-09-22", 60m);
        var links = new[] { new CardBillPaymentLink(payment.Id, "Cartão X", Closing, 60m) };

        var upcoming = PaymentSchedule.Build(new[] { CardX, Bank }, new[] { Tx("Cartão X", "2026-09-05", -100m), payment },
            Array.Empty<Payable>(), new DateOnly(2026, 9, 25), null, links);

        var item = Assert.Single(upcoming);
        Assert.Equal(40m, item.Amount);
        Assert.Contains("restante", item.Description);
    }

    [Fact]
    public void AnAdjustmentChangesTheTotalAndWhatIsLeft()
    {
        var payment = BankPayment("2026-09-27", 99.97m);
        var links = new[] { new CardBillPaymentLink(payment.Id, "Cartão X", Closing, 99.97m) };
        var transactions = new[] { Tx("Cartão X", "2026-09-05", -100m), payment };
        var today = new DateOnly(2026, 9, 29);

        var without = CardBillLedger.Build(new[] { CardX, Bank }, transactions, null, links, today).Statements.Single();
        Assert.Equal(CardBillState.Overdue, without.State);
        Assert.Equal(0.03m, without.Remaining);

        var adjustments = new[] { new CardBillAdjustment("Cartão X", Closing, -0.03m) };
        var with = CardBillLedger.Build(new[] { CardX, Bank }, transactions, null, links, today, adjustments).Statements.Single();
        Assert.Equal(99.97m, with.Total);
        Assert.Equal(100m, with.EntriesTotal);
        Assert.Equal(-0.03m, with.Adjustment);
        Assert.Equal(CardBillState.Paid, with.State);
    }

    [Fact]
    public void AnAdjustmentUpwardKeepsTheBillOpenForTheDifference()
    {
        var adjustments = new[] { new CardBillAdjustment("Cartão X", Closing, 1.20m) };

        var bill = CardBillLedger.Build(new[] { CardX, Bank }, new[] { Tx("Cartão X", "2026-09-05", -100m) },
            null, null, new DateOnly(2026, 9, 25), adjustments).Statements.Single();

        Assert.Equal(101.20m, bill.Total);
        Assert.Equal(101.20m, bill.Remaining);
    }

    [Fact]
    public void TheScheduleUsesTheAdjustedTotal()
    {
        var adjustments = new[] { new CardBillAdjustment("Cartão X", Closing, -0.03m) };

        var upcoming = PaymentSchedule.Build(new[] { CardX, Bank }, new[] { Tx("Cartão X", "2026-09-05", -100m) },
            Array.Empty<Payable>(), new DateOnly(2026, 9, 25), null, null, adjustments);

        Assert.Equal(99.97m, Assert.Single(upcoming).Amount);
    }
}
