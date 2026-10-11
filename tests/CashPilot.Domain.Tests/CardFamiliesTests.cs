using CashPilot.Domain.Accounts;
using CashPilot.Domain.Payments;
using CashPilot.Domain.Transactions;

namespace CashPilot.Domain.Tests;

public class CardFamiliesTests
{
    private static readonly Account Main = new() { Name = "Cartão X", Kind = AccountKind.CreditCard, ClosingDay = 20, DueDay = 28 };
    private static readonly Account Extra = new() { Name = "Cartão X Adicional", Kind = AccountKind.CreditCard, ParentCard = "Cartão X" };

    private static Transaction Tx(string account, decimal amount) => new()
    {
        Account = account, Date = new DateOnly(2026, 9, 5), Amount = amount, RawDescription = "Item", Type = TransactionType.Expense,
    };

    [Fact]
    public void FoldMovesAdditionalEntriesToTheMainCardAndDropsTheAdditional()
    {
        var (accounts, transactions) = CardFamilies.Fold(new[] { Main, Extra }, new[] { Tx("Cartão X", -10m), Tx("Cartão X Adicional", -5m) });

        Assert.Equal("Cartão X", Assert.Single(accounts).Name);
        Assert.All(transactions, t => Assert.Equal("Cartão X", t.Account));
        Assert.Equal(2, transactions.Count);
    }

    [Fact]
    public void TheMainCardBillSumsBothCardsAndTheAdditionalHasNoBill()
    {
        var result = CardBillLedger.Build(new[] { Main, Extra }, new[] { Tx("Cartão X", -100m), Tx("Cartão X Adicional", -40m) },
            null, null, new DateOnly(2026, 9, 10));

        var bill = Assert.Single(result.Statements);
        Assert.Equal("Cartão X", bill.Card.Name);
        Assert.Equal(140m, bill.Total);
    }

    [Fact]
    public void ACardWithAnUnknownOrAdditionalParentStaysAMainCard()
    {
        var orphan = Extra with { ParentCard = "Não existe" };
        var chained = new Account { Name = "Cartão Z", Kind = AccountKind.CreditCard, ParentCard = "Cartão X Adicional" };

        var (accounts, _) = CardFamilies.Fold(new[] { Main, Extra, orphan with { Name = "Órfão" }, chained }, Array.Empty<Transaction>());

        Assert.Contains(accounts, a => a.Name == "Órfão");
        Assert.Contains(accounts, a => a.Name == "Cartão Z");
    }
}
