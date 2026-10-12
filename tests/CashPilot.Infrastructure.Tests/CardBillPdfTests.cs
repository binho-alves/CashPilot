using CashPilot.Infrastructure.Importing;

namespace CashPilot.Infrastructure.Tests;

public class CardBillPdfTests
{
    // Columns, as in the app's PDF: Data 50, Histórico 120, Moeda de origem 475, US$ 540, Cotação 640, R$ 760.
    private static List<PdfWord> Header() =>
    [
        new(1, "Data", 50, 80, 100), new(1, "Histórico", 120, 170, 100), new(1, "Moeda", 475, 510, 100),
        new(1, "de", 512, 520, 100), new(1, "origem", 522, 560, 100), new(1, "US$", 600, 620, 100),
        new(1, "Cotação", 660, 710, 100), new(1, "US$", 712, 730, 100), new(1, "R$", 800, 815, 100),
    ];

    private static List<PdfWord> Row(double top, string date, string description, string amount, bool negative = false)
    {
        var words = new List<PdfWord> { new(1, date, 50, 80, top) };
        var left = 120.0;
        foreach (var part in description.Split(' '))
        {
            words.Add(new PdfWord(1, part, left, left + part.Length * 6, top));
            left += part.Length * 6 + 4;
        }
        words.Add(new PdfWord(1, "USD", 580, 600, top));
        words.Add(new PdfWord(1, "0,00", 602, 625, top));
        words.Add(new PdfWord(1, "R$", 700, 712, top));
        words.Add(new PdfWord(1, "0,00", 714, 740, top));
        words.Add(new PdfWord(1, negative ? "-R$" : "R$", 790, 806, top));
        words.Add(new PdfWord(1, amount, 808, 840, top));
        return words;
    }

    [Fact]
    public void ReadsDateDescriptionAndTheLastAmountIgnoringForeignCurrencyColumns()
    {
        var words = Header();
        words.AddRange(Row(150, "08/10", "LOJA EXEMPLO ALFA", "35,05"));
        words.AddRange(Row(180, "05/10", "ASSINATURA TESTE", "1.110,00"));

        var result = CardBillPdf.TryRead(words);

        Assert.NotNull(result);
        Assert.Equal(2, result.Entries);
        var lines = result.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("08/10 LOJA EXEMPLO ALFA R$ 35,05", lines[0]);
        Assert.Equal("05/10 ASSINATURA TESTE R$ 1.110,00", lines[1]);
    }

    [Fact]
    public void ThePaymentOfThePreviousBillIsLeftOut()
    {
        var words = Header();
        words.AddRange(Row(150, "02/10", "PAGAMENTO EFETUADO", "900,00", negative: true));
        words.AddRange(Row(180, "03/10", "LOJA EXEMPLO BETA", "20,00"));

        var result = CardBillPdf.TryRead(words);

        Assert.NotNull(result);
        Assert.Equal(1, result.Entries);
        Assert.Equal(1, result.Skipped);
        Assert.DoesNotContain("PAGAMENTO", result.Text);
    }

    [Fact]
    public void ACreditLineKeepsItsMinusSign()
    {
        var words = Header();
        words.AddRange(Row(150, "04/10", "ESTORNO LOJA GAMA", "12,50", negative: true));

        var result = CardBillPdf.TryRead(words);

        Assert.NotNull(result);
        Assert.Contains("04/10 ESTORNO LOJA GAMA -R$ 12,50", result.Text);
    }

    [Fact]
    public void TheTextFeedsTheExistingParserWithoutRejections()
    {
        var words = Header();
        words.AddRange(Row(150, "08/10", "LOJA EXEMPLO ALFA", "35,05"));
        var result = CardBillPdf.TryRead(words)!;

        var converted = StatementText.ToGastosCsv(result.Text, "Cartão Exemplo", new DateOnly(2026, 10, 11));

        Assert.Equal(1, converted.Entries);
        Assert.Empty(converted.Rejected);
    }

    [Fact]
    public void AnythingWithoutTheBillColumnsIsNotABill()
    {
        var words = new List<PdfWord> { new(1, "Extrato", 50, 90, 100), new(1, "08/10", 50, 80, 150) };

        Assert.Null(CardBillPdf.TryRead(words));
    }

    private const string OcrSample = """
        Situagao do Extrato: Em Aberto
        | Data | Historico origom de | US$ | Cotagao US$ | R$
        08/10 CIZI MERCADO EXPRESS USD 0,00 R$ 0,00 R$ 35,05
        01/10 ZP*LMF USD 0,00 R$ 0,00 R$ 1.000,00
        25/09 SALDO ANTERIOR USD 0,00 R$ 0,00 R$ 8.797,80
        25/09 PAGTO. POR DEB EM C/C USD 0,00 R$ 0,00 R$ -8.797,80
        Total para: FABIO R ALVES R$ 1.035,05
        03/10 ESTORNO LOJA GAMA USD 0,00 R$ 0,00 R$ -12,50
        """;

    [Fact]
    public void OcrTextKeepsDateDescriptionAndLastAmountAndDropsPaymentsAndBalance()
    {
        var result = CardBillPdf.FromOcrText(OcrSample);

        Assert.NotNull(result);
        Assert.Equal(3, result.Entries);
        Assert.Equal(2, result.Skipped);
        var lines = result.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("08/10 CIZI MERCADO EXPRESS R$ 35,05", lines[0]);
        Assert.Equal("01/10 ZP*LMF R$ 1.000,00", lines[1]);
        Assert.Equal("03/10 ESTORNO LOJA GAMA -R$ 12,50", lines[2]);
    }

    [Fact]
    public void OcrTextFeedsTheExistingParserWithoutRejections()
    {
        var result = CardBillPdf.FromOcrText(OcrSample)!;

        var converted = StatementText.ToGastosCsv(result.Text, "Cartão Exemplo", new DateOnly(2026, 10, 11));

        Assert.Equal(3, converted.Entries);
        Assert.Empty(converted.Rejected);
    }

    [Fact]
    public void OcrTextWithoutBillLinesGivesNothing() =>
        Assert.Null(CardBillPdf.FromOcrText("Aplicativo Bradesco\nTotal para: FABIO R ALVES"));

    [Fact]
    public void OcrLinesWithADamagedDateAreStillRead()
    {
        var result = CardBillPdf.FromOcrText("0810 LOJA ALFA USD 0,00 R$ 0,00 R$ 35,05\nO7/1O LOJA BETA USD 0,00 R$ 0,00 R$ 10,00");

        Assert.NotNull(result);
        Assert.Equal(2, result!.Entries);
        Assert.Equal("08/10 LOJA ALFA R$ 35,05\n07/10 LOJA BETA R$ 10,00\n", result.Text);
    }

    [Fact]
    public void ADateWhoseMonthIsLostIsRebuiltFromTheNeighborLines()
    {
        // What the Portuguese "fast" model gave for a real screenshot: "08M0" instead of "08/10", bars between the columns.
        var result = CardBillPdf.FromOcrText(string.Join("\n",
            "08M0 | LOJA ALFA USD 0,00 R$0,00 R$ 35,05",
            "0710 | LOJA BETA USD 0,00 R$0,00 R$ 103,89",
            "070 | LOJA GAMA USD 0,00 R$0,00 R$ 43,29",
            "0340 | LOJA DELTA USD 0,00 R$ 0,00 R$ 166,11"));

        Assert.NotNull(result);
        Assert.Equal(4, result!.Entries);
        Assert.Equal(3, result.Repaired);
        Assert.Equal(string.Join("\n",
            "08/10 LOJA ALFA R$ 35,05",
            "07/10 LOJA BETA R$ 103,89",
            "07/10 LOJA GAMA R$ 43,29",
            "03/10 LOJA DELTA R$ 166,11") + "\n", result.Text);
    }

    [Fact]
    public void ADamagedDateFarFromEveryNeighborIsMarkedForTheUserInsteadOfGuessed()
    {
        var result = CardBillPdf.FromOcrText(string.Join("\n",
            "0710 LOJA BETA USD 0,00 R$ 0,00 R$ 103,89",
            "28M0 LOJA GAMA USD 0,00 R$ 0,00 R$ 43,29"));

        Assert.Equal("07/10 LOJA BETA R$ 103,89\n28/?? LOJA GAMA R$ 43,29\n", result!.Text);
        Assert.Equal(0, result.Repaired);
    }

    [Fact]
    public void AListGroupedByDayWithTheMonthUnderTheDayIsRead()
    {
        // What OCR gives for a bank app list: day on the first purchase, month name under it, timeline dots, arrows.
        var result = CardBillPdf.FromOcrText(string.Join("\n",
            "Data Descrição Valor",
            "10 o LOJA ALFA R$ 15,99 >",
            "Out",
            "O LOJA BETA RS 88,99 >",
            "Oo LOJA GAMA R$ 1.500,00 >",
            "09 © LOJA DELTA R$ 20,00 >",
            "O LOJA EPSILON R$ 30,00 >"));

        Assert.NotNull(result);
        Assert.Equal(5, result!.Entries);
        Assert.Equal(string.Join("\n",
            "10/10 LOJA ALFA R$ 15,99",
            "10/10 LOJA BETA R$ 88,99",
            "10/10 LOJA GAMA R$ 1.500,00",
            "09/10 LOJA DELTA R$ 20,00",
            "09/10 LOJA EPSILON R$ 30,00") + "\n", result.Text);
        Assert.Equal(2, result.Repaired);   // the second day has no month name of its own: taken from the day above
    }
}
