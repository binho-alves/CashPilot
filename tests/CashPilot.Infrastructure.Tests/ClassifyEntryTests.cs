using CashPilot.Domain.Descriptions;
using CashPilot.Infrastructure.Importing;
using CashPilot.Infrastructure.Persistence;

namespace CashPilot.Infrastructure.Tests;

public class ClassifyEntryTests
{
    private const string Header = "Dia,Categoria,Ítem,Conta,Valor,Obs";

    private static CashPilotStore StoreWith(params string[] lines)
    {
        var store = new CashPilotStore(":memory:");
        new GastosImporter(store).Import(new StringReader(string.Join("\n", new[] { Header }.Concat(lines))));
        return store;
    }

    [Fact]
    public void CorrectsOnlyThatEntryWhenNotLearning()
    {
        using var store = StoreWith(
            "03/09/2026,Lazer,Cinema,Conta A,\"R$ 40,00\",Loja Alfa",
            "04/09/2026,Lazer,Cinema,Conta A,\"R$ 41,00\",Loja Alfa");
        var first = store.GetAll()[0];

        store.ClassifyEntry(first.Id, new Classification("Mercado", "Feira"), learn: false);

        var all = store.GetAll();
        Assert.Equal("Mercado", all.Single(t => t.Id == first.Id).Category);
        Assert.Equal("Lazer", all.Single(t => t.Id != first.Id).Category);
    }

    [Fact]
    public void LearningMakesTheCorrectionTheRuleForPendingEntries()
    {
        using var store = StoreWith(
            "03/09/2026,Lazer,Cinema,Conta A,\"R$ 40,00\",Loja Alfa",
            "04/09/2026,,,Conta A,\"R$ 12,00\",Loja Beta",
            "05/09/2026,,,Conta A,\"R$ 13,00\",Loja Beta");
        var known = store.GetAll()[0];
        Assert.Equal(2, store.GetPending().Count);

        store.ClassifyEntry(known.Id, new Classification("Mercado", "Feira"), learn: false);
        var pendingId = store.GetPending()[0].Id;
        store.ClassifyEntry(pendingId, new Classification("Casa", "Reparos"), learn: true);

        Assert.Empty(store.GetPending());
    }
}
