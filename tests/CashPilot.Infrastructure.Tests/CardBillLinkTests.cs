using CashPilot.Domain.Accounts;
using CashPilot.Domain.Transactions;
using CashPilot.Infrastructure.Importing;
using CashPilot.Infrastructure.Persistence;

namespace CashPilot.Infrastructure.Tests;

public class CardBillLinkTests
{
    private static readonly DateOnly Day = new(2026, 9, 27);
    private static readonly DateOnly Closing = new(2026, 9, 20);

    private static Guid AddBillPayment(CashPilotStore store, decimal amount) =>
        ManualEntries.Add(store, Day, "Banco A", "Pagamento fatura", amount, income: false,
            type: TransactionType.CardBillPayment).Transaction.Id;

    [Fact]
    public void LinksAccumulateAndCanBeRemoved()
    {
        using var store = new CashPilotStore(":memory:");
        var payment = AddBillPayment(store, 100m);

        store.LinkCardBillPayment(payment, "Cartão X", Closing, 30m);
        store.LinkCardBillPayment(payment, "Cartão X", Closing, 20m);
        store.LinkCardBillPayment(payment, "Cartão Y", Closing, 50m);

        var links = store.GetCardBillLinks();
        Assert.Equal(2, links.Count);
        Assert.Equal(50m, links.Single(l => l.Card == "Cartão X").Amount);

        Assert.True(store.UnlinkCardBillPayment(payment, "Cartão X", Closing));
        Assert.Single(store.GetCardBillLinks());
        Assert.False(store.UnlinkCardBillPayment(payment, "Cartão X", Closing));
    }

    [Fact]
    public void ACannotApplyMoreThanThePaymentHas()
    {
        using var store = new CashPilotStore(":memory:");
        var payment = AddBillPayment(store, 100m);
        store.LinkCardBillPayment(payment, "Cartão X", Closing, 80m);

        Assert.Throws<ArgumentException>(() => store.LinkCardBillPayment(payment, "Cartão Y", Closing, 30m));
        Assert.Throws<ArgumentException>(() => store.LinkCardBillPayment(payment, "Cartão Y", Closing, 0m));
    }

    [Fact]
    public void OnlyBillPaymentEntriesCanBeLinked()
    {
        using var store = new CashPilotStore(":memory:");
        var expense = ManualEntries.Add(store, Day, "Dinheiro", "Padaria", 10m, income: false).Transaction.Id;

        Assert.Throws<ArgumentException>(() => store.LinkCardBillPayment(expense, "Cartão X", Closing, 10m));
        Assert.Throws<ArgumentException>(() => store.LinkCardBillPayment(Guid.NewGuid(), "Cartão X", Closing, 10m));
    }

    [Fact]
    public void RenamingACardMovesItsLinks()
    {
        using var store = new CashPilotStore(":memory:");
        store.SaveAccount(new Account { Name = "Cartão Velho", Kind = AccountKind.CreditCard, ClosingDay = 20, DueDay = 28 });
        var payment = AddBillPayment(store, 100m);
        store.LinkCardBillPayment(payment, "Cartão Velho", Closing, 100m);

        store.RenameAccount("Cartão Velho", "Cartão Novo");

        Assert.Equal("Cartão Novo", Assert.Single(store.GetCardBillLinks()).Card);
    }

    [Fact]
    public void AdjustmentsRoundTripReplaceAndClear()
    {
        using var store = new CashPilotStore(":memory:");

        store.SetCardBillAdjustment("Cartão X", Closing, -0.03m);
        store.SetCardBillAdjustment("Cartão X", Closing, 1.20m);
        store.SetCardBillAdjustment("Cartão Y", Closing, -0.50m);

        var all = store.GetCardBillAdjustments();
        Assert.Equal(2, all.Count);
        Assert.Equal(1.20m, all.Single(a => a.Card == "Cartão X").Amount);

        store.SetCardBillAdjustment("Cartão X", Closing, 0m);
        Assert.Equal("Cartão Y", Assert.Single(store.GetCardBillAdjustments()).Card);
    }

    [Fact]
    public void RenamingACardMovesItsAdjustments()
    {
        using var store = new CashPilotStore(":memory:");
        store.SaveAccount(new Account { Name = "Cartão Velho", Kind = AccountKind.CreditCard, ClosingDay = 20, DueDay = 28 });
        store.SetCardBillAdjustment("Cartão Velho", Closing, -0.03m);

        store.RenameAccount("Cartão Velho", "Cartão Novo");

        Assert.Equal("Cartão Novo", Assert.Single(store.GetCardBillAdjustments()).Card);
    }
}
