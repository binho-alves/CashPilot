using System.Text;
using CashPilot.Infrastructure.Importing;
using CashPilot.Infrastructure.Persistence;

namespace CashPilot.Infrastructure.Tests;

public class OfxParserTests
{
    // Invented data. SGML flavour (OFX 1.x): leaf elements have no closing tag.
    private const string Sgml = """
        OFXHEADER:100
        DATA:OFXSGML
        VERSION:102
        CHARSET:1252

        <OFX>
        <SIGNONMSGSRSV1><SONRS><STATUS><CODE>0<SEVERITY>INFO</STATUS><DTSERVER>20260930120000<LANGUAGE>POR<FI><ORG>BANCO TESTE<FID>999</FI></SONRS></SIGNONMSGSRSV1>
        <BANKMSGSRSV1><STMTTRNRS><TRNUID>1<STMTRS><CURDEF>BRL
        <BANKACCTFROM><BANKID>0237<ACCTID>12345<ACCTTYPE>CHECKING</BANKACCTFROM>
        <BANKTRANLIST><DTSTART>20260901<DTEND>20260930
        <STMTTRN><TRNTYPE>CREDIT<DTPOSTED>20260902120000[-3:BRT]<TRNAMT>250.00<FITID>A1<MEMO>PIX RECEBIDO</STMTTRN>
        <STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20260904<TRNAMT>-400,50<FITID>A2<CHECKNUM>333<NAME>FULANO DE TAL<MEMO>LOJA ALFA &amp; CIA</STMTTRN>
        <STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20260905<TRNAMT>-30.00<FITID>A3<NAME>TARIFA TESTE</STMTTRN>
        </BANKTRANLIST>
        <LEDGERBAL><BALAMT>1234.56<DTASOF>20260930</LEDGERBAL>
        </STMTRS></STMTTRNRS></BANKMSGSRSV1>
        </OFX>
        """;

    // XML flavour (OFX 2.x), a credit card statement.
    private const string Xml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <?OFX OFXHEADER="200" VERSION="211" SECURITY="NONE" OLDFILEUID="NONE" NEWFILEUID="NONE"?>
        <OFX>
          <SIGNONMSGSRSV1><SONRS><FI><ORG>Cartao Teste</ORG></FI></SONRS></SIGNONMSGSRSV1>
          <CREDITCARDMSGSRSV1><CCSTMTTRNRS><CCSTMTRS>
            <CURDEF>BRL</CURDEF>
            <BANKTRANLIST>
              <STMTTRN><TRNTYPE>DEBIT</TRNTYPE><DTPOSTED>20260910000000</DTPOSTED><TRNAMT>-96.33</TRNAMT><FITID>X1</FITID><MEMO>Amazon BR VI - NuPay - Parcela 2/3</MEMO></STMTTRN>
              <STMTTRN><TRNTYPE>CREDIT</TRNTYPE><DTPOSTED>20260920000000</DTPOSTED><TRNAMT>500.00</TRNAMT><FITID>X2</FITID><MEMO>Pagamento recebido</MEMO></STMTTRN>
            </BANKTRANLIST>
          </CCSTMTRS></CCSTMTTRNRS></CREDITCARDMSGSRSV1>
        </OFX>
        """;

    [Fact]
    public void SgmlStatementIsReadWithSignedAmountsAndDatesWithoutTimeZoneShift()
    {
        var statement = OfxParser.TryParse(Sgml);

        Assert.NotNull(statement);
        Assert.Equal("Bradesco", statement.Bank);       // BANKID 0237
        Assert.Equal(OfxParser.Format, statement.Format);
        Assert.Equal(new[] { 250m, -400.50m, -30m }, statement.Entries.Select(e => e.Amount).ToArray());
        Assert.Equal(new DateOnly(2026, 9, 2), statement.Entries[0].Date);
        Assert.Empty(statement.Rejected);
        Assert.Empty(statement.Warnings);
    }

    [Fact]
    public void MemoIsTheHistoryAndNameBecomesTheDetail()
    {
        var statement = OfxParser.TryParse(Sgml)!;

        Assert.Equal("PIX RECEBIDO", statement.Entries[0].History);
        Assert.Equal("", statement.Entries[0].Detail);

        Assert.Equal("LOJA ALFA & CIA", statement.Entries[1].History);   // entity unescaped
        Assert.Equal("FULANO DE TAL", statement.Entries[1].Detail);
        Assert.Equal("333", statement.Entries[1].Document);              // CHECKNUM wins over FITID

        Assert.Equal("TARIFA TESTE", statement.Entries[2].History);      // no MEMO: NAME is the history
        Assert.Equal("A3", statement.Entries[2].Document);
    }

    [Fact]
    public void BalancesAreIgnored()
    {
        var statement = OfxParser.TryParse(Sgml)!;

        Assert.DoesNotContain(statement.Entries, e => e.Amount == 1234.56m);
    }

    [Fact]
    public void XmlStatementIsReadAndCreditCardIsWarnedAbout()
    {
        var statement = OfxParser.TryParse(Xml);

        Assert.NotNull(statement);
        Assert.Equal("Cartao Teste", statement.Bank);   // unknown BANKID: falls back to ORG
        Assert.Equal(new[] { -96.33m, 500m }, statement.Entries.Select(e => e.Amount).ToArray());
        Assert.Single(statement.Warnings);
        Assert.Contains("cartão", statement.Warnings[0]);
    }

    [Fact]
    public void EntriesWithoutDateAmountOrDescriptionAreRejectedWithTheirLine()
    {
        const string text = """
            <OFX><BANKMSGSRSV1><STMTRS><CURDEF>BRL<BANKTRANLIST>
            <STMTTRN><DTPOSTED>20260902<TRNAMT>0.00<MEMO>ZERO</STMTTRN>
            <STMTTRN><DTPOSTED>sem data<TRNAMT>-10.00<MEMO>X</STMTTRN>
            <STMTTRN><DTPOSTED>20260903<TRNAMT>-10.00</STMTTRN>
            <STMTTRN><DTPOSTED>20260904<TRNAMT>-10.00<MEMO>BOA</STMTTRN>
            </BANKTRANLIST></STMTRS></BANKMSGSRSV1></OFX>
            """;

        var statement = OfxParser.TryParse(text)!;

        Assert.Single(statement.Entries);
        Assert.Equal("BOA", statement.Entries[0].History);
        Assert.Equal(new[] { 2, 3, 4 }, statement.Rejected.Select(r => r.Line).ToArray());
    }

    [Fact]
    public void AnotherCurrencyIsWarnedAbout()
    {
        var statement = OfxParser.TryParse(Sgml.Replace("<CURDEF>BRL", "<CURDEF>USD"))!;

        Assert.Contains(statement.Warnings, w => w.Contains("USD"));
    }

    [Fact]
    public void AFileWithNoMovementsSaysSo()
    {
        var statement = OfxParser.TryParse("<OFX><BANKMSGSRSV1></BANKMSGSRSV1></OFX>")!;

        Assert.Empty(statement.Entries);
        Assert.Contains(statement.Warnings, w => w.Contains("Nenhum lançamento"));
    }

    [Theory]
    [InlineData("Data;Histórico;Docto.;Crédito (R$);Débito (R$);Saldo (R$)")]
    [InlineData("24 SET Mp *Cobranca R$ 113,90")]
    public void OtherTextIsNotOfx(string text) => Assert.Null(OfxParser.TryParse(text));

    [Fact]
    public void LoaderRecognizesAnOfxFileEvenWithLatin1Accents()
    {
        var bytes = Encoding.Latin1.GetBytes(Sgml.Replace("PIX RECEBIDO", "DEPÓSITO"));

        var loaded = ImportFileLoader.Load("extrato.ofx", bytes);

        Assert.Equal(ImportKind.BankStatement, loaded.Kind);
        Assert.Equal("DEPÓSITO", loaded.Statement!.Entries[0].History);
    }

    [Fact]
    public void ImportedOfxEntriesAreNotDuplicatedWhenTheFileIsSentAgain()
    {
        using var store = new CashPilotStore(":memory:");
        var statement = OfxParser.TryParse(Sgml)!;
        var importer = new BankStatementImporter(store);

        var first = importer.Import(importer.Preview(statement, "Banco Bradesco PF"), [0, 1, 2], "ofx");
        var second = importer.Preview(statement, "Banco Bradesco PF");

        Assert.Equal(3, first.Inserted);
        Assert.All(second.Candidates, c => Assert.Equal(CandidateStatus.Duplicate, c.Status));
    }
}
