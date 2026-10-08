using CashPilot.Domain.Transactions;
using CashPilot.Infrastructure.Importing;
using CashPilot.Infrastructure.Persistence;

namespace CashPilot.Infrastructure.Tests;

public class CashOutTests
{
    private const string Header = "Dia,Categoria,Ítem,Conta,Valor,Obs";

    private static ImportResult Import(CashPilotStore store, params string[] lines) =>
        new GastosImporter(store).Import(new StringReader(string.Join("\n", lines)));

    [Fact]
    public void MarkingCashOutTakesStoredEntriesOutOfSpendingAndPending()
    {
        using var store = new CashPilotStore(":memory:");
        Import(store, Header,
            "03/09/2026,,,Cartão X,\"R$ 625,00\",Terminal*abc 11111",
            "03/09/2026,,,Cartão X,\"R$ 63,00\",Terminal*abc 11111",
            "04/09/2026,,,Cartão X,\"R$ 9,90\",Loja Desconhecida Xyz");

        var marked = store.MarkCashOut("TERMINAL*ABC 99999");

        Assert.Equal(2, marked);
        Assert.Equal(2, store.GetAll().Count(t => t.Type == TransactionType.CardCashAdvance));
        var pending = Assert.Single(store.GetPending());
        Assert.Equal("Loja Desconhecida Xyz", pending.RawDescription);
    }

    [Fact]
    public void FutureImportsMarkTheSameTerminalAutomatically()
    {
        using var store = new CashPilotStore(":memory:");
        store.MarkCashOut("Terminal*abc 11111");

        var result = Import(store, Header, "10/09/2026,,,Cartão X,\"R$ 1.000,00\",TERMINAL*ABC 22222");

        Assert.Equal(1, result.CardCashOut);
        Assert.Equal(0, result.Pending);
        Assert.Equal(TransactionType.CardCashAdvance, Assert.Single(store.GetAll()).Type);
    }
}
