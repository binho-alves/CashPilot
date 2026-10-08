using CashPilot.Domain.Accounts;
using CashPilot.Domain.Descriptions;
using CashPilot.Domain.Transactions;
using CashPilot.Infrastructure.Importing;
using CashPilot.Infrastructure.Persistence;

namespace CashPilot.Infrastructure.Tests;

public class BankStatementImporterTests
{
    private const string Account = "Banco Alfa";

    private static BankStatement Statement() => BankStatementParsers.TryParseBradescoCsv(BankStatementParserTests.BradescoCsv)!;

    private static BankStatement Manual(params BankEntry[] entries) =>
        new("Teste", "Teste", entries, [], []);

    private static BankEntry Entry(int day, string history, decimal amount) =>
        new(new DateOnly(2026, 9, day), history, "", amount);

    private static BankImportResult ImportAll(BankStatementImporter importer, BankPreview preview) =>
        importer.Import(
            preview,
            preview.Candidates.Where(c => c.Status == CandidateStatus.New).Select(c => c.Index),
            sourceName: "teste");

    [Fact]
    public void PreviewDecidesWhatEachLineIs()
    {
        using var store = new CashPilotStore(":memory:");
        var preview = new BankStatementImporter(store).Preview(Statement(), Account);

        Assert.All(preview.Candidates, c => Assert.Equal(CandidateStatus.New, c.Status));

        Transaction Find(string history, decimal amount) =>
            preview.Candidates.Single(c => c.Entry.History == history && c.Entry.Amount == amount).Transaction;

        Assert.Equal(TransactionType.Deposit, Find("PIX RECEBIDO", 250m).Type);

        var interest = Find("ENCARGOS LIMITE DE CRED", -30m);
        Assert.Equal(TransactionType.Expense, interest.Type);
        Assert.Equal("Juros", interest.Category);
        Assert.Equal("Cheque Especial", interest.Item);

        Assert.Equal(TransactionType.CardBillPayment, Find("PAGTO FATURA CARTAO", -200m).Type);

        var plain = Find("LOJA ALFA", -400m);
        Assert.Equal(TransactionType.Expense, plain.Type);
        Assert.Null(plain.Category);
    }

    [Fact]
    public void BankFeesAndIofGoUnderInterestWithTheirOwnItem()
    {
        using var store = new CashPilotStore(":memory:");
        var statement = Manual(
            Entry(1, "TARIFA BANCARIA", -87m),
            Entry(2, "IOF", -19.91m),
            Entry(3, "JUROS EXCESSO LIM CONTA", -0.88m));

        var preview = new BankStatementImporter(store).Preview(statement, Account);

        Assert.Equal("Tarifa Bancária", preview.Candidates[0].Transaction.Item);
        Assert.Equal("Cheque Especial", preview.Candidates[1].Transaction.Item);
        Assert.Equal("Cheque Especial", preview.Candidates[2].Transaction.Item);
        Assert.All(preview.Candidates, c => Assert.Equal("Juros", c.Transaction.Category));
    }

    [Fact]
    public void ImportingTheSameStatementTwiceDoesNotDuplicate()
    {
        using var store = new CashPilotStore(":memory:");
        var importer = new BankStatementImporter(store);

        var first = ImportAll(importer, importer.Preview(Statement(), Account));
        var again = importer.Preview(Statement(), Account);

        Assert.Equal(6, first.Inserted);
        Assert.All(again.Candidates, c => Assert.Equal(CandidateStatus.Duplicate, c.Status));
        Assert.Equal(0, ImportAll(importer, again).Inserted);
        Assert.Equal(6, store.Count());
    }

    [Fact]
    public void IdenticalMovementsInOneFileAreBothKeptButNotRepeatedOnReimport()
    {
        using var store = new CashPilotStore(":memory:");
        var importer = new BankStatementImporter(store);
        var statement = Manual(Entry(4, "PIX TRANSF AMIGO", -50m), Entry(4, "PIX TRANSF AMIGO", -50m));

        Assert.Equal(2, ImportAll(importer, importer.Preview(statement, Account)).Inserted);
        Assert.Equal(0, ImportAll(importer, importer.Preview(statement, Account)).Inserted);
        Assert.Equal(2, store.Count());
    }

    [Fact]
    public void ADeletedEntryIsNotBroughtBackByANewImport()
    {
        using var store = new CashPilotStore(":memory:");
        var importer = new BankStatementImporter(store);
        ImportAll(importer, importer.Preview(Statement(), Account));
        store.DeleteTransaction(store.GetAll().First(t => t.RawDescription == "LOJA ALFA").Id);

        var preview = importer.Preview(Statement(), Account);

        Assert.All(preview.Candidates, c => Assert.Equal(CandidateStatus.Duplicate, c.Status));
        Assert.Equal(0, ImportAll(importer, preview).Inserted);
        Assert.Equal(5, store.Count());
    }

    [Fact]
    public void AnEntryTypedInTheSpreadsheetIsFlaggedAsSimilarAndNotImportedByDefault()
    {
        using var store = new CashPilotStore(":memory:");
        new GastosImporter(store).Import(new StringReader(
            "Dia,Categoria,Ítem,Conta,Valor,Obs\n05/09/2026,Mercado,Feira,Banco Alfa,\"R$ 400,00\",Pago na feira\n"));
        var importer = new BankStatementImporter(store);

        var preview = importer.Preview(Statement(), Account);

        var similar = Assert.Single(preview.Candidates, c => c.Status == CandidateStatus.Similar);
        Assert.Equal(-400m, similar.Entry.Amount);
        Assert.Contains("Pago na feira", similar.Note);

        ImportAll(importer, preview);
        Assert.DoesNotContain(store.GetAll(), t => t.RawDescription == "LOJA ALFA");
        Assert.Equal(1 + 5, store.Count());
    }

    [Fact]
    public void TheSameAmountOnAnotherAccountIsNotSimilar()
    {
        using var store = new CashPilotStore(":memory:");
        new GastosImporter(store).Import(new StringReader(
            "Dia,Categoria,Ítem,Conta,Valor,Obs\n05/09/2026,Mercado,Feira,Outra conta,\"R$ 400,00\",Feira\n"));

        var preview = new BankStatementImporter(store).Preview(Statement(), Account);

        Assert.DoesNotContain(preview.Candidates, c => c.Status == CandidateStatus.Similar);
    }

    [Fact]
    public void PairsBetweenAccountsBecomeInternalTransfers()
    {
        using var store = new CashPilotStore(":memory:");
        var importer = new BankStatementImporter(store);

        ImportAll(importer, importer.Preview(Manual(Entry(10, "PIX ENVIADO", -300m), Entry(11, "LOJA GAMA", -40m)), "Banco Alfa"));
        var second = ImportAll(importer, importer.Preview(Manual(Entry(10, "PIX RECEBIDO", 300m)), "Banco Beta"));

        Assert.Equal(1, second.TransferPairs);
        var types = store.GetAll().ToDictionary(t => t.RawDescription, t => t.Type);
        Assert.Equal(TransactionType.InternalTransfer, types["PIX ENVIADO"]);
        Assert.Equal(TransactionType.InternalTransfer, types["PIX RECEBIDO"]);
        Assert.Equal(TransactionType.Expense, types["LOJA GAMA"]);
        Assert.Single(store.GetPending()); // only the shop is left to classify
    }

    [Fact]
    public void APixWithoutPayeeIsLeftForTheUserEvenIfARuleMatches()
    {
        using var store = new CashPilotStore(":memory:");
        store.SaveRule(RuleKind.Exact, "PIX ENVIADO", new Classification("Lazer", "Outros"));

        var preview = new BankStatementImporter(store).Preview(Manual(Entry(3, "PIX ENVIADO", -20m)), Account);

        Assert.Null(preview.Candidates[0].Transaction.Category);
    }

    [Fact]
    public void ALearnedRuleClassifiesAPixWithAPayee()
    {
        using var store = new CashPilotStore(":memory:");
        store.SaveRule(RuleKind.Exact, "PIX ENVIADO - DES: Loja Alfa", new Classification("Lazer", "Doces"));
        var statement = Manual(new BankEntry(new DateOnly(2026, 9, 3), "PIX ENVIADO", "DES: Loja Alfa 03/09", -20m));

        var preview = new BankStatementImporter(store).Preview(statement, Account);

        Assert.Equal("Lazer", preview.Candidates[0].Transaction.Category);
        Assert.Equal("Doces", preview.Candidates[0].Transaction.Item);
    }

    [Fact]
    public void CreditsAndPixWithoutPayeeStayPendingUntilTheUserDecides()
    {
        using var store = new CashPilotStore(":memory:");
        var importer = new BankStatementImporter(store);

        ImportAll(importer, importer.Preview(Manual(Entry(3, "PIX RECEBIDO", 300m), Entry(4, "PIX ENVIADO", -20m)), Account));

        Assert.Equal(2, store.GetPending().Count);
    }

    [Fact]
    public void MarkingAnEntryAsBetweenOwnAccountsRemovesItFromPendingAndFromSpending()
    {
        using var store = new CashPilotStore(":memory:");
        var importer = new BankStatementImporter(store);
        ImportAll(importer, importer.Preview(Manual(Entry(4, "PIX ENVIADO", -20m), Entry(5, "PIX ENVIADO", -35m)), Account));
        var first = store.GetPending().First();

        Assert.True(store.MarkEntryInternalTransfer(first.Id));

        Assert.Single(store.GetPending());
        Assert.Equal(TransactionType.InternalTransfer, store.GetAll().Single(t => t.Id == first.Id).Type);
    }

    [Fact]
    public void ADescriptionWithAPayeeMarkedAsOwnTransferIsRememberedByLaterImports()
    {
        using var store = new CashPilotStore(":memory:");
        var importer = new BankStatementImporter(store);
        BankStatement WithPayee(int day) =>
            Manual(new BankEntry(new DateOnly(2026, 9, day), "PIX ENVIADO", "DES: Maria Teste 0" + day + "/09", -100m));

        ImportAll(importer, importer.Preview(WithPayee(3), Account));
        Assert.Equal(1, store.MarkInternalTransfer("PIX ENVIADO - DES: Maria Teste 03/09"));

        var next = importer.Preview(WithPayee(8), Account);
        Assert.Equal(TransactionType.InternalTransfer, next.Candidates[0].Transaction.Type);
    }

    [Fact]
    public void AGenericPixMarkedAsOwnTransferIsNotRemembered()
    {
        using var store = new CashPilotStore(":memory:");
        var importer = new BankStatementImporter(store);
        ImportAll(importer, importer.Preview(Manual(Entry(3, "PIX ENVIADO", -100m)), Account));

        store.MarkInternalTransfer("PIX ENVIADO");

        var next = importer.Preview(Manual(Entry(9, "PIX ENVIADO", -50m)), Account);
        Assert.Equal(TransactionType.Expense, next.Candidates[0].Transaction.Type);
    }

    [Fact]
    public void AccountSuggestionFollowsTheBankName()
    {
        using var store = new CashPilotStore(":memory:");
        store.SaveAccount(new Account { Name = "Banco Bradesco PF", Kind = AccountKind.BankAccount });
        store.SaveAccount(new Account { Name = "Banco Itaú", Kind = AccountKind.BankAccount });

        Assert.Equal(new[] { "Banco Bradesco PF" }, new BankStatementImporter(store).SuggestAccounts(Statement()));
    }
}
