using CashPilot.Infrastructure.Importing;
using CashPilot.Infrastructure.Persistence;

namespace CashPilot.Infrastructure.Tests;

public class DeleteTransactionTests
{
    private const string Sheet = """
        Dia,Categoria,Ítem,Conta,Valor,Obs
        03/09/2026,,,Conta A,"R$ 40,00",Loja Alfa
        04/09/2026,Lazer,Cinema,Conta B,"R$ 41,00",Loja Beta
        """;

    [Fact]
    public void DeletedEntriesDisappearFromListsPendingAndCounts()
    {
        using var store = new CashPilotStore(":memory:");
        new GastosImporter(store).Import(new StringReader(Sheet));
        var pending = Assert.Single(store.GetPending());

        Assert.True(store.DeleteTransaction(pending.Id));

        Assert.Equal(1, store.Count());
        Assert.Empty(store.GetPending());
        Assert.DoesNotContain(store.GetAll(), t => t.Id == pending.Id);
        Assert.Equal(new[] { "Conta B" }, store.GetAccountNamesInUse());
        Assert.False(store.DeleteTransaction(pending.Id)); // already deleted
    }

    [Fact]
    public void ImportingTheSameFileAgainDoesNotBringDeletedEntriesBack()
    {
        using var store = new CashPilotStore(":memory:");
        new GastosImporter(store).Import(new StringReader(Sheet));
        store.DeleteTransaction(store.GetAll().First().Id);

        var again = new GastosImporter(store).Import(new StringReader(Sheet));

        Assert.Equal(0, again.Inserted);
        Assert.Equal(1, store.Count());
    }

    [Fact]
    public void UnknownIdDeletesNothing()
    {
        using var store = new CashPilotStore(":memory:");

        Assert.False(store.DeleteTransaction(Guid.NewGuid()));
    }
}
