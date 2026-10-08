using CashPilot.Domain.Descriptions;
using CashPilot.Infrastructure.Importing;
using CashPilot.Infrastructure.Persistence;

namespace CashPilot.Infrastructure.Tests;

public class RuleManagementTests
{
    private const string Header = "Dia,Categoria,Ítem,Conta,Valor,Obs";

    private static CashPilotStore StoreWith(params string[] lines)
    {
        var store = new CashPilotStore(":memory:");
        new GastosImporter(store).Import(new StringReader(string.Join("\n", new[] { Header }.Concat(lines))));
        return store;
    }

    [Fact]
    public void RulesCanBeListedAndDeleted()
    {
        using var store = StoreWith("03/09/2026,,,Conta A,\"R$ 10,00\",Loja Alfa");
        store.SaveRule(RuleKind.Contains, "Loja", new Classification("Compras", "Geral"));

        var rule = Assert.Single(store.GetRules());
        Assert.Equal(RuleKind.Contains, rule.Kind);
        Assert.Equal("LOJA", rule.Pattern);

        Assert.True(store.DeleteRule(rule.Id));
        Assert.Empty(store.GetRules());
        Assert.False(store.DeleteRule(rule.Id));
    }

    [Fact]
    public void RenameCategoryUpdatesEntriesAndRules()
    {
        using var store = StoreWith(
            "03/09/2026,Lazer,Cinema,Conta A,\"R$ 40,00\",Loja Alfa",
            "04/09/2026,Lazer,Teatro,Conta A,\"R$ 50,00\",Loja Beta",
            "05/09/2026,Casa,Reparos,Conta A,\"R$ 60,00\",Loja Gama");
        store.SaveRule(RuleKind.Contains, "CINE", new Classification("Lazer", "Cinema"));

        var changed = store.RenameCategory("Lazer", "Entretenimento");

        Assert.Equal(2, changed);
        Assert.Equal(2, store.GetAll().Count(t => t.Category == "Entretenimento"));
        Assert.Equal(1, store.GetAll().Count(t => t.Category == "Casa"));
        Assert.Equal("Entretenimento", store.GetRules().Single(r => r.Kind == RuleKind.Contains).Classification.Category);
    }

    [Fact]
    public void RenamingToAnExistingCategoryMergesThem()
    {
        using var store = StoreWith(
            "03/09/2026,Lazer,Cinema,Conta A,\"R$ 40,00\",Loja Alfa",
            "05/09/2026,Casa,Reparos,Conta A,\"R$ 60,00\",Loja Gama");

        store.RenameCategory("Lazer", "Casa");

        Assert.All(store.GetAll(), t => Assert.Equal("Casa", t.Category));
    }
}
