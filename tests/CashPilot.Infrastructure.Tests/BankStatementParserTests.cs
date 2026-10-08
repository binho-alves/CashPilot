using System.Text;
using CashPilot.Infrastructure.Importing;

namespace CashPilot.Infrastructure.Tests;

public class BankStatementParserTests
{
    // Invented data; the layout (two blocks, opening-balance rows, unsigned balance, totals row) follows the real export.
    internal const string BradescoCsv = """
        Extrato de: Ag: 0001 | Conta: 12345-6;;;;;
        Data;Histórico;Docto.;Crédito (R$);Débito (R$);Saldo (R$)
        31/08/2026;COD. LANC. 0;0; ;0,00;100,00
        02/09/2026;PIX RECEBIDO;111;250,00; ;350,00
        03/09/2026;ENCARGOS LIMITE DE CRED;222; ;30,00;320,00
        04/09/2026;LOJA ALFA;333; ;400,00;80,00
        05/09/2026;PAGTO FATURA CARTAO;444; ;200,00;120,00
        06/09/2026;PIX RECEBIDO;555;500,00; ;380,00
        ;;;;;
        Filtro de resultados - Movimentação entre:  01/09/2026 e 30/09/2026;;;;;
        ;;;;;
        Últimos Lancamentos;;;;;
        Data;Histórico;Docto.;Crédito (R$);Débito (R$);
        07/09/2026;COD. LANC. 0;0; ; ;380,00
        08/09/2026;LOJA BETA;666; ;500,00;120,00

        ;;Total;0,00;500,00;120,00;
        """;

    [Fact]
    public void BradescoCsvReadsEntriesWithSignedAmountsAndSkipsOpeningRowsAndTotals()
    {
        var statement = BankStatementParsers.TryParseBradescoCsv(BradescoCsv);

        Assert.NotNull(statement);
        Assert.Equal("Bradesco", statement.Bank);
        Assert.Equal(
            new[] { 250m, -30m, -400m, -200m, 500m, -500m },
            statement.Entries.Select(e => e.Amount).ToArray());
        Assert.Equal("ENCARGOS LIMITE DE CRED", statement.Entries[1].History);
        Assert.Equal(new DateOnly(2026, 9, 8), statement.Entries[^1].Date);
        Assert.Empty(statement.Rejected);
    }

    [Fact]
    public void BradescoCsvIsRecognizedEvenWhenTheFileIsLatin1()
    {
        var loaded = ImportFileLoader.Load("extrato.csv", Encoding.Latin1.GetBytes(BradescoCsv));

        Assert.Equal(ImportKind.BankStatement, loaded.Kind);
        Assert.Equal(6, loaded.Statement!.Entries.Count);
    }

    [Fact]
    public void LoaderTellsTheSpreadsheetAndPlainTextApart()
    {
        var sheet = "Dia,Categoria,Ítem,Conta,Valor,Obs\n03/09/2026,,,Conta A,\"R$ 40,00\",Loja Alfa\n";
        Assert.Equal(ImportKind.GastosSheet, ImportFileLoader.Load("gastos.csv", Encoding.UTF8.GetBytes(sheet)).Kind);

        var text = "05 Out\nLOJA ALFA   R$ 19,56\n";
        Assert.Equal(ImportKind.PlainText, ImportFileLoader.Load("fatura.txt", Encoding.UTF8.GetBytes(text)).Kind);

        Assert.Equal(ImportKind.Unknown, ImportFileLoader.Load("vazio.csv", Array.Empty<byte>()).Kind);
    }

    // ------------------------------------------------------------ Bradesco PDF (words with positions)

    private static PdfWord Word(string text, double left, double right, double top) => new(1, text, left, right, top);

    /// <summary>A number right-aligned at <paramref name="edge"/>, 4 points per character.</summary>
    private static PdfWord Number(string text, double edge, double top) => Word(text, edge - text.Length * 4, edge, top);

    private static List<PdfWord> BradescoHeader() =>
    [
        Word("Data", 46, 61, 100),
        Word("Histórico", 110, 140, 100),
        Word("Docto.", 305, 326, 100),
        Word("Crédito", 385, 409, 100), Word("(R$)", 411, 425.6, 100),
        Word("Débito", 451, 473, 100), Word("(R$)", 475.5, 490.1, 100),
        Word("Saldo", 520, 538, 100), Word("(R$)", 540, 554.6, 100),
    ];

    private static List<PdfWord> BradescoPage()
    {
        var words = BradescoHeader();

        // Opening balance row: everything on one line.
        words.Add(Word("30/09/2026", 46, 82, 130));
        words.AddRange([Word("COD.", 110, 122, 130), Word("LANC.", 124, 150, 130), Word("0", 152, 156, 130)]);
        words.Add(Number("0,00", 426.7, 130));
        words.Add(Number("100,00", 550.5, 130));

        // Wording above the numbers line, payee below it.
        words.AddRange([Word("PIX", 110, 122, 155.5), Word("ENVIADO", 124, 160, 155.5)]);
        words.Add(Word("01/10/2026", 46, 82, 160));
        words.Add(Number("0938210", 331, 160));
        words.Add(Number("40,00", 490.5, 160));
        words.Add(Number("60,00", 550.5, 160));
        words.AddRange([Word("DES:", 110, 127, 164.5), Word("Loja", 129, 146, 164.5), Word("Alfa", 148, 165, 164.5), Word("01/10", 167, 185, 164.5)]);

        // Short entry: wording on the numbers line, no payee line.
        words.Add(Word("02/10/2026", 46, 82, 190));
        words.AddRange([Word("IOF", 110, 122, 190), Word("S/", 124, 131, 190), Word("UTILIZACAO", 133, 181, 190), Word("LIMITE", 183, 210, 190)]);
        words.Add(Number("8084399", 331, 190));
        words.Add(Number("5,00", 490.5, 190));
        words.Add(Number("55,00", 550.5, 190));

        // Credit; the date is not repeated on the same day.
        words.AddRange([Word("PIX", 110, 122, 215.5), Word("RECEBIDO", 124, 165, 215.5)]);
        words.Add(Number("1528418", 331, 220));
        words.Add(Number("300,00", 426.7, 220));
        words.Add(Number("355,00", 550.5, 220));
        words.AddRange([Word("REM:", 110, 127, 224.5), Word("Fulano", 129, 160, 224.5)]);

        // Debit that takes the account below zero: the balance prints without sign.
        words.AddRange([Word("PAGTO", 110, 135, 245.5), Word("ELETRON", 137, 170, 245.5), Word("COBRANCA", 172, 215, 245.5)]);
        words.Add(Number("0000096", 331, 250));
        words.Add(Number("400,00", 490.5, 250));
        words.Add(Number("45,00", 550.5, 250));
        words.AddRange([Word("BOLETO", 110, 140, 254.5), Word("ALFA", 142, 160, 254.5)]);

        // Totals row repeats the column positions and must not become an entry.
        words.Add(Word("Total", 46, 70, 290));
        words.Add(Number("300,00", 426.7, 290));
        words.Add(Number("445,00", 490.5, 290));
        words.Add(Number("45,00", 550.5, 290));
        return words;
    }

    [Fact]
    public void BradescoPdfRebuildsWordingPayeeColumnsAndDates()
    {
        var statement = BankStatementParsers.TryParsePdf(BradescoPage());

        Assert.NotNull(statement);
        Assert.Equal(BankStatementParsers.BradescoPdf, statement.Format);
        Assert.Equal(4, statement.Entries.Count);

        var first = statement.Entries[0];
        Assert.Equal(new DateOnly(2026, 10, 1), first.Date);
        Assert.Equal("PIX ENVIADO", first.History);
        Assert.Equal("DES: Loja Alfa 01/10", first.Detail);
        Assert.Equal(-40m, first.Amount);
        Assert.Equal("0938210", first.Document);

        var iof = statement.Entries[1];
        Assert.Equal("IOF S/ UTILIZACAO LIMITE", iof.History);
        Assert.Equal("", iof.Detail);
        Assert.Equal(-5m, iof.Amount);

        var credit = statement.Entries[2];
        Assert.Equal(300m, credit.Amount);
        Assert.Equal(new DateOnly(2026, 10, 2), credit.Date); // carried from the previous row
        Assert.Equal("REM: Fulano", credit.Detail);

        Assert.Equal(-400m, statement.Entries[3].Amount);
    }

    // ------------------------------------------------------------ Itaú PDF

    private static List<PdfWord> Line(double top, params string[] tokens) =>
        tokens.Select((t, i) => Word(t, 10 + i * 60, 60 + i * 60, top)).ToList();

    private static List<PdfWord> ItauPage()
    {
        var words = new List<PdfWord>();
        words.AddRange(Line(100, "saldo", "em", "conta", "Limite", "da", "Conta", "utilizado"));
        words.AddRange(Line(118, "R$", "-100,00", "R$", "100,00"));
        words.AddRange(Line(150, "extrato", "conta", "/", "lançamentos"));
        words.AddRange(Line(170, "período", "de", "visualização:", "01/09/2026", "até", "10/09/2026", "emitido", "em:", "10/09/2026", "10:11:52"));
        words.AddRange(Line(200, "data", "lançamentos", "valor", "(R$)", "saldo", "(R$)"));
        // Newest first; the balance line of a day can come before that day's entries.
        words.AddRange(Line(230, "08/09/2026", "SALDO", "DO", "DIA", "-100,00"));
        words.AddRange(Line(245, "08/09/2026", "PIX", "TRANSF", "LOJA", "BETA08/09", "-180,00"));
        words.AddRange(Line(260, "07/09/2026", "SALDO", "DO", "DIA", "80,00"));
        words.AddRange(Line(275, "07/09/2026", "PIX", "TRANSF", "AMIGO07/09", "30,00"));
        words.AddRange(Line(290, "06/09/2026", "SALDO", "DO", "DIA", "50,00"));
        words.AddRange(Line(305, "06/09/2026", "LOJA", "ALFA", "-150,00"));
        words.AddRange(Line(320, "05/09/2026", "SALDO", "DO", "DIA", "200,00"));
        return words;
    }

    [Fact]
    public void ItauPdfReadsOnlyTheMovementsOldestFirst()
    {
        var statement = BankStatementParsers.TryParsePdf(ItauPage());

        Assert.NotNull(statement);
        Assert.Equal("Itaú", statement.Bank);
        Assert.Equal(
            new[] { "LOJA ALFA", "PIX TRANSF AMIGO07/09", "PIX TRANSF LOJA BETA08/09" },
            statement.Entries.Select(e => e.History).ToArray());
        Assert.Equal(new[] { -150m, 30m, -180m }, statement.Entries.Select(e => e.Amount).ToArray());
        Assert.Empty(statement.Rejected); // the "SALDO DO DIA" lines are not movements
    }

    [Fact]
    public void UnknownPdfWordsAreNotAStatement()
    {
        Assert.Null(BankStatementParsers.TryParsePdf(Line(100, "Nota", "fiscal", "de", "serviço")));
    }
}
