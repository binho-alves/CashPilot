using CashPilot.Infrastructure.Importing;
using CashPilot.Infrastructure.Persistence;

namespace CashPilot.Infrastructure.Tests;

public class KnownClassificationsTests
{
    [Fact]
    public void ListsCategoryAndItemPairsFromEntriesAndRulesWithoutDuplicates()
    {
        using var store = new CashPilotStore(":memory:");
        new GastosImporter(store).Import(new StringReader(string.Join("\n",
            "Dia,Categoria,Ítem,Conta,Valor,Obs",
            "01/09/2026,Lazer,Passeios,Itaú,\"R$ 20,00\",Cinema Exemplo",
            "02/09/2026,Lazer,Passeios,Itaú,\"R$ 25,00\",Teatro Exemplo",
            "03/09/2026,,,Itaú,\"R$ 9,90\",Loja Desconhecida Xyz")));
        new RulesImporter(store).Import(new StringReader("pattern,category,item\nUBER,Transporte,Transporte Público / App"));

        var known = store.GetKnownClassifications();

        Assert.Equal(2, known.Count);
        Assert.Contains(known, k => k.Category == "Lazer" && k.Item == "Passeios");
        Assert.Contains(known, k => k.Category == "Transporte" && k.Item == "Transporte Público / App");
    }
}
