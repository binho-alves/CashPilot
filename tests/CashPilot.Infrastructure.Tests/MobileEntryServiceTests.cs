using CashPilot.Contracts;
using CashPilot.Domain.Accounts;
using CashPilot.Domain.Descriptions;
using CashPilot.Domain.Transactions;
using CashPilot.Infrastructure.Importing;
using CashPilot.Infrastructure.Mobile;
using CashPilot.Infrastructure.Persistence;

namespace CashPilot.Infrastructure.Tests;

public class MobileEntryServiceTests
{
    private static readonly DateOnly Day = new(2026, 10, 8);

    private static CashPilotStore NewStore()
    {
        var store = new CashPilotStore(":memory:");
        store.SaveAccount(new Account { Name = "Carteira", Kind = AccountKind.Cash });
        store.SaveAccount(new Account { Name = "Banco Exemplo", Kind = AccountKind.BankAccount });
        store.SaveAccount(new Account { Name = "Cartão Exemplo", Kind = AccountKind.CreditCard });
        return store;
    }

    private static NewEntryRequest Request(Guid? id = null, string kind = EntryKinds.Expense, decimal amount = 12.5m,
        string account = "Carteira", string description = "Padaria Alfa", string? category = null) =>
        new(id ?? Guid.NewGuid(), Day, account, description, amount, kind, category);

    [Fact]
    public void SendingTheSameClientIdTwiceStoresOneEntry()
    {
        using var store = NewStore();
        var request = Request();

        var first = MobileEntryService.Create(store, request);
        var second = MobileEntryService.Create(store, request);

        Assert.False(first.AlreadyExisted);
        Assert.True(second.AlreadyExisted);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(request.ClientId, first.Id);
        Assert.Single(store.GetAll());
    }

    [Fact]
    public void ARetryAfterTheUserDeletedTheEntryDoesNotBringItBack()
    {
        using var store = NewStore();
        var request = Request();
        MobileEntryService.Create(store, request);
        Assert.True(MobileEntryService.DeleteManual(store, request.ClientId));

        var again = MobileEntryService.Create(store, request);

        Assert.True(again.AlreadyExisted);
        Assert.Empty(store.GetAll());
    }

    [Theory]
    [InlineData(EntryKinds.Expense, -12.5, TransactionType.Expense)]
    [InlineData(EntryKinds.Income, 12.5, TransactionType.Income)]
    [InlineData(EntryKinds.BillPayment, -12.5, TransactionType.CardBillPayment)]
    [InlineData(EntryKinds.TransferOut, -12.5, TransactionType.InternalTransfer)]
    [InlineData(EntryKinds.TransferIn, 12.5, TransactionType.InternalTransfer)]
    public void TheKindDecidesSignAndType(string kind, double signed, TransactionType type)
    {
        using var store = NewStore();

        MobileEntryService.Create(store, Request(kind: kind));

        var saved = Assert.Single(store.GetAll());
        Assert.Equal((decimal)signed, saved.Amount);
        Assert.Equal(type, saved.Type);
    }

    [Fact]
    public void AChosenCategoryIsSavedAndLearned()
    {
        using var store = NewStore();

        MobileEntryService.Create(store, Request(category: "Mercado"));
        var next = MobileEntryService.Create(store, Request(id: Guid.NewGuid()));

        Assert.Equal("Mercado", next.Category);
        Assert.True(next.AutoClassified);
    }

    [Fact]
    public void ARetryDoesNotTeachAgain()
    {
        using var store = NewStore();
        var id = Guid.NewGuid();
        MobileEntryService.Create(store, Request(id, category: "Mercado"));

        // Same client id, other category: it is a retry, so it neither adds an entry nor changes what was learned.
        MobileEntryService.Create(store, Request(id, category: "Padaria"));

        Assert.Equal("Mercado", store.BuildClassifier().Classify("Padaria Alfa").Classification!.Category);
        Assert.Equal("Mercado", Assert.Single(store.GetAll()).Category);
    }

    [Theory]
    [InlineData(0, "Carteira", "x", "expense")]
    [InlineData(-5, "Carteira", "x", "expense")]
    [InlineData(1.234, "Carteira", "x", "expense")]
    [InlineData(5, "Conta que não existe", "x", "expense")]
    [InlineData(5, "Carteira", "   ", "expense")]
    [InlineData(5, "Carteira", "x", "outro")]
    public void InvalidRequestsAreRefused(double amount, string account, string description, string kind)
    {
        using var store = NewStore();

        Assert.Throws<MobileRequestException>(() =>
            MobileEntryService.Create(store, Request(amount: (decimal)amount, account: account, description: description, kind: kind)));
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void AnEmptyClientIdIsRefused()
    {
        using var store = NewStore();

        Assert.Throws<MobileRequestException>(() =>
            MobileEntryService.Create(store, Request() with { ClientId = Guid.Empty }));
    }

    [Fact]
    public void TheAccountNameIsMatchedIgnoringCase()
    {
        using var store = NewStore();

        MobileEntryService.Create(store, Request(account: "banco exemplo"));

        Assert.Equal("Banco Exemplo", Assert.Single(store.GetAll()).Account);
    }

    [Fact]
    public void OnlyEntriesTypedByHandCanBeDeletedFromThePhone()
    {
        using var store = NewStore();
        var imported = new Transaction
        {
            Account = "Banco Exemplo", Date = Day, Amount = -30m, RawDescription = "Compra importada", Type = TransactionType.Expense,
        };
        store.TryInsert(imported, "IMPORT-1", "statement");

        Assert.False(MobileEntryService.DeleteManual(store, imported.Id));
        Assert.Single(store.GetAll());
        Assert.False(MobileEntryService.DeleteManual(store, Guid.NewGuid()));
    }

    [Fact]
    public void RecentListsOnlyEntriesTypedByHandNewestFirst()
    {
        using var store = NewStore();
        store.TryInsert(new Transaction
        {
            Account = "Banco Exemplo", Date = Day, Amount = -30m, RawDescription = "Compra importada", Type = TransactionType.Expense,
        }, "IMPORT-1", "statement");
        MobileEntryService.Create(store, Request(description: "Primeiro"));
        MobileEntryService.Create(store, Request(description: "Segundo", kind: EntryKinds.TransferIn));

        var recent = MobileEntryService.Recent(store);

        Assert.Equal(new[] { "Segundo", "Primeiro" }, recent.Select(e => e.Description).ToArray());
        Assert.Equal(EntryKinds.TransferIn, recent[0].Kind);
        Assert.Equal(12.5m, recent[0].Amount);
    }

    [Fact]
    public void LookupsListAccountsWithKindAndKnownCategories()
    {
        using var store = NewStore();
        MobileEntryService.Create(store, Request(category: "Mercado"));

        var lookups = MobileEntryService.Lookups(store);

        Assert.Equal(3, lookups.Accounts.Count);
        Assert.Contains(lookups.Accounts, a => a is { Name: "Banco Exemplo", Kind: "bank" });
        Assert.Contains(lookups.Accounts, a => a is { Name: "Carteira", Kind: "cash" });
        Assert.Equal("card", lookups.Accounts.Single(a => a.Name == "Cartão Exemplo").Kind);
        Assert.Contains(lookups.Categories, c => c.Category == "Mercado");
    }

    [Theory]
    [InlineData("chave-secreta", "chave-secreta", true)]
    [InlineData("chave-secreta", "outra", false)]
    [InlineData("chave-secreta", "", false)]
    [InlineData("chave-secreta", null, false)]
    [InlineData("", "", false)]
    [InlineData(null, "qualquer", false)]
    public void ApiKeyCheckAcceptsOnlyTheConfiguredKey(string? configured, string? given, bool expected) =>
        Assert.Equal(expected, ApiKeyCheck.IsValid(configured, given));
}

public class MoneyInputTests
{
    [Theory]
    [InlineData("12,50", 12.50)]
    [InlineData("12.50", 12.50)]
    [InlineData("1.250,00", 1250.00)]
    [InlineData("1,250.00", 1250.00)]
    [InlineData("R$ 8", 8)]
    [InlineData("8", 8)]
    [InlineData("0,5", 0.5)]
    [InlineData(",75", 0.75)]
    [InlineData("1.250", 1250)]
    [InlineData("1.250.300,10", 1250300.10)]
    public void ReadsCommonKeyboardInput(string text, double expected)
    {
        Assert.True(MoneyInput.TryParse(text, out var value));
        Assert.Equal((decimal)expected, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("12,345")]
    [InlineData("1,2,3")]
    [InlineData("-5")]
    [InlineData(null)]
    public void RefusesWhatIsNotAnAmount(string? text) => Assert.False(MoneyInput.TryParse(text, out _));
}
