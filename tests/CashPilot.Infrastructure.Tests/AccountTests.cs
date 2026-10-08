using CashPilot.Domain.Accounts;
using CashPilot.Infrastructure.Importing;
using CashPilot.Infrastructure.Persistence;

namespace CashPilot.Infrastructure.Tests;

public class AccountTests
{
    [Fact]
    public void AccountsRoundTripAndUpdateByName()
    {
        using var store = new CashPilotStore(":memory:");
        store.SaveAccount(new Account
        {
            Name = "Banco Exemplo",
            Kind = AccountKind.BankAccount,
            OverdraftLimit = 1500.50m,
            OverdraftFreeDays = 5,
            OverdraftMonthlyRatePercent = 7.95m,
        });
        store.SaveAccount(new Account
        {
            Name = "Cartão Exemplo",
            Kind = AccountKind.CreditCard,
            CreditLimit = 3000m,
            ClosingDay = 20,
            DueDay = 28,
        });

        var accounts = store.GetAccounts();
        Assert.Equal(2, accounts.Count);
        var bank = accounts.Single(a => a.Kind == AccountKind.BankAccount);
        Assert.Equal(1500.50m, bank.OverdraftLimit);
        Assert.Equal(5, bank.OverdraftFreeDays);
        Assert.Equal(7.95m, bank.OverdraftMonthlyRatePercent);
        Assert.Null(bank.CreditLimit);
        var card = accounts.Single(a => a.Kind == AccountKind.CreditCard);
        Assert.Equal((20, 28), (card.ClosingDay, card.DueDay));

        store.SaveAccount(bank with { OverdraftFreeDays = 10 });
        Assert.Equal(10, store.GetAccounts().Single(a => a.Name == "Banco Exemplo").OverdraftFreeDays);
        Assert.Equal(2, store.GetAccounts().Count);
    }

    [Fact]
    public void DeleteRemovesTheAccountAndNamesInUseComeFromEntries()
    {
        using var store = new CashPilotStore(":memory:");
        new GastosImporter(store).Import(new StringReader(string.Join("\n",
            "Dia,Categoria,Ítem,Conta,Valor,Obs",
            "03/09/2026,Lazer,Cinema,Conta A,\"R$ 40,00\",Loja Alfa",
            "04/09/2026,Lazer,Cinema,Conta B,\"R$ 41,00\",Loja Alfa")));
        store.SaveAccount(new Account { Name = "Conta A" });

        Assert.Equal(new[] { "Conta A", "Conta B" }, store.GetAccountNamesInUse());
        Assert.True(store.DeleteAccount("Conta A"));
        Assert.Empty(store.GetAccounts());
    }

    [Fact]
    public void EmptyNameIsRejected()
    {
        using var store = new CashPilotStore(":memory:");
        Assert.Throws<ArgumentException>(() => store.SaveAccount(new Account { Name = "  " }));
    }
}
