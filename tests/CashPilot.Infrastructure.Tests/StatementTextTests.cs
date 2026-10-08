using CashPilot.Domain.Transactions;
using CashPilot.Infrastructure.Importing;
using CashPilot.Infrastructure.Persistence;

namespace CashPilot.Infrastructure.Tests;

public class StatementTextTests
{
    private static readonly DateOnly Today = new(2026, 10, 8);

    private static IReadOnlyList<Transaction> Import(CashPilotStore store, string text, string account = "Cartão Exemplo",
        bool positiveAreExpenses = true)
    {
        var converted = StatementText.ToGastosCsv(text, account, Today, positiveAreExpenses);
        new GastosImporter(store).Import(new StringReader(converted.Csv));
        return store.GetAll();
    }

    [Fact]
    public void DateHeadersApplyToTheEntriesBelowAndInstallmentLinesJoinTheEntryAbove()
    {
        const string text = """
            05 Out
            ANTHROPIC* CLAUDE SUB   R$ 110,00
            CIZI MERCADO EXPRESS    R$ 19,56
            02 Out
            RI HAPPY LOJA 191   R$ 80,01
            Parcela 1 de 3
            01 Out
            ZP*XYZ   R$ 1.000,00
            """;
        using var store = new CashPilotStore(":memory:");

        var all = Import(store, text);

        Assert.Equal(4, all.Count);
        var claude = all.Single(t => t.RawDescription.StartsWith("ANTHROPIC"));
        Assert.Equal(new DateOnly(2026, 10, 5), claude.Date);
        Assert.Equal(-110.00m, claude.Amount); // positive on a card bill = spending, stored negative
        Assert.Equal("Cartão Exemplo", claude.Account);
        var happy = all.Single(t => t.RawDescription.StartsWith("RI HAPPY"));
        Assert.Equal(new DateOnly(2026, 10, 2), happy.Date);
        Assert.Equal((1, 3), (happy.InstallmentNumber, happy.InstallmentCount));
        Assert.Equal(-1000m, all.Single(t => t.RawDescription.StartsWith("ZP*")).Amount);
    }

    [Fact]
    public void DateCanSitOnTheLineAndIgnoredLinesAreSkipped()
    {
        const string text = """
            # provisional, not posted yet
            05/10 Padaria Central 12,50
            06/10/2026 Posto Exemplo R$ 100,00
            """;
        using var store = new CashPilotStore(":memory:");

        var all = Import(store, text);

        Assert.Equal(2, all.Count);
        Assert.Equal(new DateOnly(2026, 10, 6), all.Single(t => t.RawDescription == "Posto Exemplo").Date);
    }

    [Fact]
    public void RefundsAreInflowsAndBankStatementsFlipTheSign()
    {
        using var store = new CashPilotStore(":memory:");

        var card = Import(store, "05/10 Estorno Loja -30,00\n05/10 Loja Beta 40,00", "Cartão A");
        Assert.Equal(30.00m, card.Single(t => t.RawDescription == "Estorno Loja").Amount);
        Assert.Equal(-40.00m, card.Single(t => t.RawDescription == "Loja Beta").Amount);

        var bank = Import(store, "05/10 Mercado Gama -25,00\n06/10 Salario 3.000,00", "Conta B", positiveAreExpenses: false);
        Assert.Equal(-25.00m, bank.Single(t => t.RawDescription == "Mercado Gama").Amount);
        Assert.Equal(3000.00m, bank.Single(t => t.RawDescription == "Salario").Amount);
    }

    [Fact]
    public void ADescriptionStartingWithANumberIsNotADate()
    {
        using var store = new CashPilotStore(":memory:");

        var all = Import(store, "05 Out\n99FOOD *99FOOD 43,29\n24 Horas Padaria 10,00\n05 Outback Mall 20,00");

        Assert.Equal(3, all.Count);
        Assert.All(all, t => Assert.Equal(new DateOnly(2026, 10, 5), t.Date));
    }

    [Fact]
    public void YearIsInferredAndBadLinesAreRejectedWithTheirLineNumbers()
    {
        // December while "today" is October: last year. A date within a month ahead stays in this year.
        var converted = StatementText.ToGastosCsv(
            "28/12 Loja Velha 10,00\n01/11 Loja Futura 20,00\nsem valor aqui\n10,00",
            "Cartão A", Today);

        Assert.Equal(2, converted.Entries);
        Assert.Contains("28/12/2025", converted.Csv);
        Assert.Contains("01/11/2026", converted.Csv);
        Assert.Equal(new[] { 3, 4 }, converted.Rejected.Select(r => r.Line));
    }

    [Fact]
    public void LineWithoutAnyDateIsRejected()
    {
        var converted = StatementText.ToGastosCsv("Loja Sem Data 10,00", "Cartão A", Today);

        Assert.Equal(0, converted.Entries);
        Assert.Single(converted.Rejected);
    }

    [Fact]
    public void PastingTheSameTextTwiceDoesNotDuplicate()
    {
        const string text = "05 Out\nLoja Alfa 10,00\nLoja Alfa 10,00\nLoja Beta 5,00";
        using var store = new CashPilotStore(":memory:");

        Import(store, text);
        var again = new GastosImporter(store).Import(new StringReader(StatementText.ToGastosCsv(text, "Cartão Exemplo", Today).Csv));

        Assert.Equal(0, again.Inserted);
        Assert.Equal(3, store.GetAll().Count); // two identical lines are kept as two entries
    }

    // A card-app list where the description wraps and the installment sits on the second line.
    [Theory]
    [InlineData("24 SET Mp *Cobranca R$ 113,90\n15 SET Amazon BR VI - NuPay - R$ 96,33\nParcela 2/3")]
    [InlineData("24 SET Mp *Cobranca R$ 113,90\n15 SET Amazon BR VI - NuPay -\nParcela 2/3 R$ 96,33")]
    [InlineData("24 SET Mp *Cobranca R$ 113,90\n15 SET Amazon BR VI - NuPay -\nParcela 2/3\nR$ 96,33")]
    public void EntriesSplitOverSeveralLinesAreJoined(string text)
    {
        using var store = new CashPilotStore(":memory:");

        var all = Import(store, text, "Cartão Nu");

        Assert.Equal(2, all.Count);
        var cobranca = all.Single(t => t.RawDescription == "Mp *Cobranca");
        Assert.Equal(new DateOnly(2026, 9, 24), cobranca.Date);
        Assert.Equal(-113.90m, cobranca.Amount);
        var amazon = all.Single(t => t.RawDescription.StartsWith("Amazon"));
        Assert.Equal("Amazon BR VI - NuPay PARC 02/03", amazon.RawDescription);
        Assert.Equal(new DateOnly(2026, 9, 15), amazon.Date);
        Assert.Equal(-96.33m, amazon.Amount);
        Assert.Equal((2, 3), (amazon.InstallmentNumber, amazon.InstallmentCount));
    }

    [Fact]
    public void AnEntryThatNeverGetsAnAmountIsRejected()
    {
        var converted = StatementText.ToGastosCsv("15 SET Loja Sem Valor\n16 SET Loja Alfa 10,00", "Cartão A", Today);

        Assert.Equal(1, converted.Entries);
        Assert.Single(converted.Rejected, r => r.Line == 1);
    }

    [Fact]
    public void OcrOrderWithTheDescriptionAboveTheDateAndAmountLineIsJoined()
    {
        // What Tesseract returned for a real card-app screenshot.
        const string text = "24 SET Mp *Cobranca R$ 113,90\n\nAmazon BR VI - NuPay -\n15 SET R$ 96,33\nParcela 2/3";
        using var store = new CashPilotStore(":memory:");

        var all = Import(store, text, "Cartão Nu");

        Assert.Equal(2, all.Count);
        var amazon = all.Single(t => t.RawDescription.StartsWith("Amazon"));
        Assert.Equal("Amazon BR VI - NuPay PARC 02/03", amazon.RawDescription);
        Assert.Equal(new DateOnly(2026, 9, 15), amazon.Date);
        Assert.Equal(-96.33m, amazon.Amount);
        Assert.Equal((2, 3), (amazon.InstallmentNumber, amazon.InstallmentCount));
    }

    [Fact]
    public void ADateAndAmountWithNoDescriptionAboveIsRejected()
    {
        var converted = StatementText.ToGastosCsv("15 SET R$ 96,33", "Cartão A", Today);

        Assert.Equal(0, converted.Entries);
        Assert.Single(converted.Rejected);
    }
}
