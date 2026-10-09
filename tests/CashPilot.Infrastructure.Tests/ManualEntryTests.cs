using CashPilot.Domain.Descriptions;
using CashPilot.Domain.Transactions;
using CashPilot.Infrastructure.Importing;
using CashPilot.Infrastructure.Persistence;

namespace CashPilot.Infrastructure.Tests;

public class ManualEntryTests
{
    private static readonly DateOnly Day = new(2026, 10, 8);

    [Fact]
    public void AnExpenseIsStoredNegativeAndStaysPendingWhenNothingIsKnown()
    {
        using var store = new CashPilotStore(":memory:");

        var result = ManualEntries.Add(store, Day, "Dinheiro", "  Padaria   Alfa ", 12.50m, income: false);

        var saved = Assert.Single(store.GetAll());
        Assert.Equal(-12.50m, saved.Amount);
        Assert.Equal("Padaria Alfa", saved.RawDescription);
        Assert.Equal(TransactionType.Expense, saved.Type);
        Assert.Null(saved.Category);
        Assert.False(result.AutoClassified);
        Assert.Single(store.GetPending());
    }

    [Fact]
    public void AnIncomeIsStoredPositive()
    {
        using var store = new CashPilotStore(":memory:");

        ManualEntries.Add(store, Day, "Conta A", "Venda de bicicleta", 300m, income: true);

        var saved = Assert.Single(store.GetAll());
        Assert.Equal(300m, saved.Amount);
        Assert.Equal(TransactionType.Income, saved.Type);
    }

    [Fact]
    public void AChosenCategoryIsSavedAndLearnedForTheNextEntries()
    {
        using var store = new CashPilotStore(":memory:");
        var food = new Classification("Alimentação", "Padaria");

        ManualEntries.Add(store, Day, "Dinheiro", "Padaria Alfa", 10m, income: false, chosen: food);
        var second = ManualEntries.Add(store, Day.AddDays(1), "Dinheiro", "Padaria Alfa", 11m, income: false);

        Assert.All(store.GetAll(), t => Assert.Equal("Alimentação", t.Category));
        Assert.True(second.AutoClassified);
        Assert.Empty(store.GetPending());
    }

    [Fact]
    public void ACategoryChosenWithoutLearningDoesNotTeachTheRule()
    {
        using var store = new CashPilotStore(":memory:");

        ManualEntries.Add(store, Day, "Dinheiro", "Padaria Alfa", 10m, income: false,
            chosen: new Classification("Alimentação", ""), learn: false);
        var second = ManualEntries.Add(store, Day, "Dinheiro", "Padaria Alfa", 11m, income: false);

        Assert.False(second.AutoClassified);
        Assert.Single(store.GetPending());
    }

    [Fact]
    public void TwoIdenticalEntriesTypedOnPurposeAreBothKept()
    {
        using var store = new CashPilotStore(":memory:");

        ManualEntries.Add(store, Day, "Dinheiro", "Café", 8m, income: false);
        ManualEntries.Add(store, Day, "Dinheiro", "Café", 8m, income: false);

        Assert.Equal(2, store.GetAll().Count);
    }

    [Theory]
    [InlineData("", "Café", 8)]
    [InlineData("Dinheiro", "  ", 8)]
    [InlineData("Dinheiro", "Café", 0)]
    [InlineData("Dinheiro", "Café", -3)]
    public void InvalidEntriesAreRejected(string account, string description, double amount)
    {
        using var store = new CashPilotStore(":memory:");

        Assert.Throws<ArgumentException>(() =>
            ManualEntries.Add(store, Day, account, description, (decimal)amount, income: false));
        Assert.Empty(store.GetAll());
    }
}
