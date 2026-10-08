using CashPilot.Domain.Descriptions;
using CashPilot.Infrastructure.Importing;
using CashPilot.Infrastructure.Persistence;

namespace CashPilot.Infrastructure.Tests;

public class RulesAndAmbiguityTests
{
    private const string Header = "Dia,Categoria,Ítem,Conta,Valor,Obs";

    private static ImportResult ImportGastos(CashPilotStore store, params string[] lines) =>
        new GastosImporter(store).Import(new StringReader(string.Join("\n", lines)));

    private static RulesImportResult ImportRules(CashPilotStore store, params string[] lines) =>
        new RulesImporter(store).Import(new StringReader(string.Join("\n", lines)));

    [Fact]
    public void RulesClassifyMatchingPendingEntriesAndLeaveTheRest()
    {
        using var store = new CashPilotStore(":memory:");
        ImportGastos(store, Header,
            "01/09/2026,,,Cartão X,\"R$ 20,00\",DI*uberrides",
            "02/09/2026,,,Cartão X,\"R$ 30,00\",UBER UBER *TRIP HELP.U",
            "03/09/2026,,,Cartão X,\"R$ 9,90\",Loja Desconhecida Xyz");

        var result = ImportRules(store,
            "pattern,category,item",
            "UBER,Transporte,Transporte Público / App");

        Assert.Equal(1, result.RulesSaved);
        Assert.Equal(2, result.Reclassified);
        Assert.Equal(1, result.StillPending);
        Assert.All(store.GetAll().Where(t => t.RawDescription.Contains("uber", StringComparison.OrdinalIgnoreCase)),
            t => Assert.Equal("Transporte", t.Category));
    }

    [Fact]
    public void RulesAlsoApplyToFutureImports()
    {
        using var store = new CashPilotStore(":memory:");
        ImportRules(store, "pattern,category,item", "SPOTIFY,Educação,Assinaturas");

        var result = ImportGastos(store, Header, "05/09/2026,,,Cartão X,\"R$ 21,90\",DM*SPOTIFY");

        Assert.Equal(1, result.ClassifiedByRules);
    }

    [Fact]
    public void RuleRowsWithoutPatternOrCategoryAreRejected()
    {
        using var store = new CashPilotStore(":memory:");

        var result = ImportRules(store,
            "pattern,category,item",
            ",Transporte,App",
            "UBER,,App",
            "SPOTIFY,Educação,Assinaturas");

        Assert.Equal(1, result.RulesSaved);
        Assert.Equal(2, result.Rejected.Count);
    }

    [Fact]
    public void RulesFileWithoutHeaderFailsClearly()
    {
        using var store = new CashPilotStore(":memory:");

        Assert.Throws<FormatException>(() => ImportRules(store, "foo,bar", "1,2"));
    }

    [Fact]
    public void AmbiguousDescriptionsStayPendingUntilTheUserDecides()
    {
        using var store = new CashPilotStore(":memory:");

        var result = ImportGastos(store, Header,
            "01/09/2026,Moradia & Habitação,Seguro,Banco X,\"R$ 75,00\",PAGTO ELETRON COBRANCA",
            "02/09/2026,Investimento,Consórcio,Banco X,\"R$ 500,00\",PAGTO ELETRON COBRANCA",
            "03/09/2026,,,Banco X,\"R$ 40,00\",PAGTO ELETRON COBRANCA");

        Assert.Equal(0, result.ClassifiedByRules);
        Assert.Equal(1, result.Pending);

        // Remembered by the next import.
        var next = ImportGastos(store, Header, "10/09/2026,,,Banco X,\"R$ 41,00\",PAGTO ELETRON COBRANCA");
        Assert.Equal(0, next.ClassifiedByRules);

        // The user decides, and it sticks.
        var updated = store.Classify("PAGTO ELETRON COBRANCA", new Classification("Investimento", "Consórcio"));
        Assert.Equal(2, updated);
        var after = ImportGastos(store, Header, "20/09/2026,,,Banco X,\"R$ 42,00\",PAGTO ELETRON COBRANCA");
        Assert.Equal(1, after.ClassifiedByRules);
    }
}
