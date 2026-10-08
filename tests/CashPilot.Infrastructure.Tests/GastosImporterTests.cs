using CashPilot.Domain.Descriptions;
using CashPilot.Domain.Transactions;
using CashPilot.Infrastructure.Importing;
using CashPilot.Infrastructure.Persistence;

namespace CashPilot.Infrastructure.Tests;

// All data below is invented. Never put real statements in tests (the repository is public).
public class GastosImporterTests
{
    private const string Header = "Dia,Categoria,Ítem,Conta,Valor,Obs";

    private static ImportResult Import(CashPilotStore store, params string[] lines) =>
        new GastosImporter(store).Import(new StringReader(string.Join("\n", lines)));

    [Fact]
    public void ImportsDatesAmountsAndLabels()
    {
        using var store = new CashPilotStore(":memory:");

        var result = Import(store, Header,
            "05/09/2026,Alimentação,Mercado,Itaú,\"R$ 1.234,56\",Mercado Exemplo Centro");

        Assert.Equal(1, result.Inserted);
        var t = Assert.Single(store.GetAll());
        Assert.Equal(new DateOnly(2026, 9, 5), t.Date);
        Assert.Equal(-1234.56m, t.Amount);
        Assert.Equal("Itaú", t.Account);
        Assert.Equal("Alimentação", t.Category);
        Assert.Equal("Mercado", t.Item);
        Assert.Equal(TransactionType.Expense, t.Type);
    }

    [Fact]
    public void ImportingTheSameFileTwiceDoesNotDuplicate()
    {
        using var store = new CashPilotStore(":memory:");
        string[] lines =
        [
            Header,
            "05/09/2026,Alimentação,Mercado,Itaú,\"R$ 10,00\",Mercado Exemplo",
            "06/09/2026,Lazer,Cinema,Itaú,\"R$ 40,00\",Cinema Exemplo",
        ];

        Import(store, lines);
        var second = Import(store, lines);

        Assert.Equal(0, second.Inserted);
        Assert.Equal(2, second.Duplicates);
        Assert.Equal(2, store.Count());
    }

    [Fact]
    public void IdenticalRowsInsideOneFileAreBothKeptAndStillIdempotent()
    {
        using var store = new CashPilotStore(":memory:");
        string[] lines =
        [
            Header,
            "05/09/2026,Alimentação,Padaria,Itaú,\"R$ 7,50\",Padaria Exemplo",
            "05/09/2026,Alimentação,Padaria,Itaú,\"R$ 7,50\",Padaria Exemplo",
        ];

        var first = Import(store, lines);
        var second = Import(store, lines);

        Assert.Equal(2, first.Inserted);
        Assert.Equal(0, second.Inserted);
        Assert.Equal(2, store.Count());
    }

    [Fact]
    public void LabelledRowsTeachTheClassifierAndUnlabelledOnesAreClassified()
    {
        using var store = new CashPilotStore(":memory:");

        var result = Import(store, Header,
            "01/09/2026,Transporte,App,Itaú,\"R$ 20,00\",Corrida App Exemplo",
            "02/09/2026,,,Itaú,\"R$ 25,00\",Corrida App Exemplo",
            "03/09/2026,,,Itaú,\"R$ 9,90\",Loja Desconhecida Xyz");

        Assert.Equal(1, result.TaughtFromSheet);
        Assert.Equal(1, result.ClassifiedByRules);
        Assert.Equal(1, result.Pending);

        var classified = store.GetAll().Single(t => t.Date == new DateOnly(2026, 9, 2));
        Assert.Equal("Transporte", classified.Category);
        Assert.Equal("App", classified.Item);

        var pending = Assert.Single(store.GetPending());
        Assert.Equal("Loja Desconhecida Xyz", pending.RawDescription);
    }

    [Fact]
    public void WhatWasLearnedSurvivesToTheNextImport()
    {
        using var store = new CashPilotStore(":memory:");
        Import(store, Header, "01/09/2026,Transporte,App,Itaú,\"R$ 20,00\",Corrida App Exemplo");

        var next = Import(store, Header, "15/09/2026,,,Nubank,\"R$ 18,00\",Corrida App Exemplo");

        Assert.Equal(1, next.ClassifiedByRules);
        Assert.Empty(store.GetPending());
    }

    [Fact]
    public void ClassifyingAPendingEntryUpdatesItAndTeachesTheRule()
    {
        using var store = new CashPilotStore(":memory:");
        Import(store, Header, "03/09/2026,,,Itaú,\"R$ 9,90\",Loja Desconhecida Xyz");

        var updated = store.Classify("Loja Desconhecida Xyz", new Classification("Compras", "Geral"));

        Assert.Equal(1, updated);
        Assert.Empty(store.GetPending());
        var next = Import(store, Header, "20/09/2026,,,Itaú,\"R$ 12,00\",Loja Desconhecida Xyz");
        Assert.Equal(1, next.ClassifiedByRules);
    }

    [Fact]
    public void ReadsSemicolonSeparatedFilesFromExcel()
    {
        using var store = new CashPilotStore(":memory:");

        Import(store, "Dia;Categoria;Ítem;Conta;Valor;Obs",
            "05/09/2026;Lazer;Cinema;Nubank;R$ 45,00;Cinema Exemplo");

        Assert.Equal(-45.00m, Assert.Single(store.GetAll()).Amount);
    }

    [Fact]
    public void KeepsCommasInsideQuotedDescriptions()
    {
        using var store = new CashPilotStore(":memory:");

        Import(store, Header, "05/09/2026,Alimentação,Padaria,Itaú,\"R$ 7,50\",\"Padaria, Exemplo\"");

        Assert.Equal("Padaria, Exemplo", Assert.Single(store.GetAll()).RawDescription);
    }

    [Fact]
    public void RejectsBadRowsButImportsTheRest()
    {
        using var store = new CashPilotStore(":memory:");

        var result = Import(store, Header,
            "31/02/2026,Lazer,Cinema,Itaú,\"R$ 40,00\",Cinema Exemplo",
            "06/09/2026,Lazer,Cinema,Itaú,\"R$ 40,00\",Cinema Exemplo");

        Assert.Equal(1, result.Inserted);
        var rejected = Assert.Single(result.Rejected);
        Assert.Equal(2, rejected.Line);
    }

    [Fact]
    public void CardBillPaymentIsNotCountedAsSpending()
    {
        using var store = new CashPilotStore(":memory:");

        Import(store, Header, "10/09/2026,Cartão de Crédito,Fatura,Itaú,\"R$ 800,00\",Pagamento Fatura Cartao");

        Assert.Equal(TransactionType.CardBillPayment, Assert.Single(store.GetAll()).Type);
        Assert.Empty(store.GetPending());
    }

    [Fact]
    public void ReadsExplicitInstallmentMarkers()
    {
        using var store = new CashPilotStore(":memory:");

        Import(store, Header, "05/09/2026,Compras,Geral,Itaú,\"R$ 50,00\",Loja Exemplo PARC 03/06");

        var t = Assert.Single(store.GetAll());
        Assert.Equal(3, t.InstallmentNumber);
        Assert.Equal(6, t.InstallmentCount);
    }

    [Fact]
    public void ReadsInstallmentMarkerGluedToTheMerchantName()
    {
        using var store = new CashPilotStore(":memory:");

        Import(store, Header, "05/09/2026,,,Cartão X,\"R$ 50,00\",Mais Revestimeparc03/06");

        var t = Assert.Single(store.GetAll());
        Assert.Equal(3, t.InstallmentNumber);
        Assert.Equal(6, t.InstallmentCount);
    }

    [Fact]
    public void NegativeValueBecomesAnInflow()
    {
        using var store = new CashPilotStore(":memory:");

        Import(store, Header, "05/09/2026,Compras,Geral,Itaú,\"-R$ 30,00\",Estorno Loja Exemplo");

        var t = Assert.Single(store.GetAll());
        Assert.Equal(30.00m, t.Amount);
        Assert.Equal(TransactionType.Income, t.Type);
    }

    [Fact]
    public void UnnamedFirstColumnIsTheDate()
    {
        using var store = new CashPilotStore(":memory:");

        Import(store, ",Categoria,Ítem,Conta,Valor,Obs",
            "05/09/2026,Lazer,Cinema,Itaú,\"R$ 45,00\",Cinema Exemplo");

        Assert.Equal(new DateOnly(2026, 9, 5), Assert.Single(store.GetAll()).Date);
    }

    [Fact]
    public void HeaderWithoutRequiredColumnsFailsClearly()
    {
        using var store = new CashPilotStore(":memory:");

        Assert.Throws<FormatException>(() => Import(store, "Foo,Bar", "1,2"));
    }
}
