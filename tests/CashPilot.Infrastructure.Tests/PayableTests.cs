using CashPilot.Domain.Accounts;
using CashPilot.Domain.Payments;
using CashPilot.Infrastructure.Persistence;

namespace CashPilot.Infrastructure.Tests;

public class PayableTests
{
    [Fact]
    public void PayablesRoundTripPayAndDelete()
    {
        using var store = new CashPilotStore(":memory:");
        var bill = new Payable { Description = "  Condomínio ", DueDate = new DateOnly(2026, 10, 15), Amount = 640.90m };
        store.AddPayable(bill);

        var saved = Assert.Single(store.GetPayables());
        Assert.Equal("Condomínio", saved.Description);
        Assert.Equal(640.90m, saved.Amount);
        Assert.False(saved.Paid);

        Assert.True(store.SetPayablePaid(bill.Id, true, new DateOnly(2026, 10, 14)));
        var paid = Assert.Single(store.GetPayables());
        Assert.True(paid.Paid);
        Assert.Equal(new DateOnly(2026, 10, 14), paid.PaidDate);

        Assert.True(store.SetPayablePaid(bill.Id, false));
        Assert.Null(Assert.Single(store.GetPayables()).PaidDate);

        Assert.True(store.DeletePayable(bill.Id));
        Assert.Empty(store.GetPayables());
    }

    [Fact]
    public void InvalidPayablesAreRejected()
    {
        using var store = new CashPilotStore(":memory:");
        var due = new DateOnly(2026, 10, 15);
        Assert.Throws<ArgumentException>(() => store.AddPayable(new Payable { Description = " ", DueDate = due, Amount = 10m }));
        Assert.Throws<ArgumentException>(() => store.AddPayable(new Payable { Description = "x", DueDate = due, Amount = 0m }));
    }

    [Fact]
    public void BalanceAnchorSurvivesEditingTheAccount()
    {
        using var store = new CashPilotStore(":memory:");
        store.SaveAccount(new Account { Name = "Banco Y", Kind = AccountKind.BankAccount, OverdraftFreeDays = 5 });

        Assert.True(store.SetAccountBalance("Banco Y", -123.45m, new DateOnly(2026, 10, 8)));
        store.SaveAccount(new Account { Name = "Banco Y", Kind = AccountKind.BankAccount, OverdraftFreeDays = 10 });

        var account = Assert.Single(store.GetAccounts());
        Assert.Equal(10, account.OverdraftFreeDays);
        Assert.Equal(-123.45m, account.BalanceAnchor);
        Assert.Equal(new DateOnly(2026, 10, 8), account.BalanceAnchorDate);
        Assert.False(store.SetAccountBalance("Inexistente", 1m, new DateOnly(2026, 10, 8)));
    }
}
