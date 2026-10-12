using CashPilot.Domain.Accounts;
using CashPilot.Infrastructure.Importing;
using CashPilot.Infrastructure.Persistence;

namespace CashPilot.Infrastructure.Tests;

public sealed class InboxScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashpilot-inbox-" + Guid.NewGuid().ToString("N"));
    private readonly CashPilotStore _store = new(":memory:");
    private static readonly DateTime Later = DateTime.Now.AddMinutes(5);

    public InboxScannerTests()
    {
        Directory.CreateDirectory(_root);
        _store.SaveAccount(new Account { Name = "Cartão Exemplo", Kind = AccountKind.CreditCard, ClosingDay = 20, DueDay = 28 });
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string Drop(string folder, string file, string content)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, file);
        File.WriteAllText(path, content);
        return path;
    }

    private InboxScanResult Scan() => InboxScanner.Scan(_root, _store, _ => "", Later, TimeSpan.Zero);

    [Theory]
    [InlineData("cartao exemplo")]
    [InlineData("  Cartão   EXEMPLO ")]
    public void FolderNamesMatchIgnoringCaseAccentsAndSpaces(string folder) =>
        Assert.Equal("Cartão Exemplo", InboxScanner.Match(folder, new[] { "Conta A", "Cartão Exemplo" }));

    [Fact]
    public void ABillInAnAccountFolderIsQueuedAndMovedAside()
    {
        var path = Drop("Cartao Exemplo", "fatura.txt", "08/10 LOJA ALFA R$ 35,05\n07/10 LOJA BETA R$ 10,00\n");

        var result = Scan();

        Assert.Equal(1, result.Queued);
        var item = Assert.Single(_store.GetOpenInboxItems());
        Assert.Equal("Cartão Exemplo", item.Account);
        Assert.Equal("pending", item.Status);
        Assert.Equal(2, item.Entries);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "Cartao Exemplo", InboxScanner.ReadFolder)));
        // Nothing is imported until the user checks the item.
        Assert.Empty(_store.GetAll());
    }

    [Fact]
    public void TheSameContentDroppedAgainIsNotQueuedTwice()
    {
        Drop("Cartão Exemplo", "a.txt", "08/10 LOJA ALFA R$ 35,05\n");
        Scan();
        Drop("Cartão Exemplo", "copia.txt", "08/10 LOJA ALFA R$ 35,05\n");

        var second = Scan();

        Assert.Equal(0, second.Queued);
        Assert.Single(_store.GetOpenInboxItems());
    }

    [Fact]
    public void ACheckedItemIsImportedWithoutDuplicates()
    {
        Drop("Cartão Exemplo", "a.txt", "08/10 LOJA ALFA R$ 35,05\n");
        Scan();
        var item = _store.GetOpenInboxItems().Single();

        var (result, error) = InboxImport.Import(_store, item, item.Text, true, new DateOnly(2026, 10, 11));

        Assert.Null(error);
        Assert.Equal(1, result!.Inserted);
        Assert.Empty(_store.GetOpenInboxItems());
        Assert.Single(_store.GetAll());
    }

    [Fact]
    public void AFolderWithoutAMatchingAccountIsIgnoredWithANote()
    {
        var path = Drop("Pasta Qualquer", "a.txt", "08/10 LOJA ALFA R$ 35,05\n");

        var result = Scan();

        Assert.Equal(0, result.Queued);
        Assert.Contains(result.Messages, m => m.Contains("Pasta Qualquer"));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void AnUnreadableFileIsQueuedAsFailedWithTheReasonAndCanBeDismissed()
    {
        Drop("Cartão Exemplo", "lixo.txt", "nada que pareça fatura\n");

        var result = Scan();

        Assert.Equal(1, result.Failed);
        var item = Assert.Single(_store.GetOpenInboxItems());
        Assert.Equal("failed", item.Status);
        Assert.NotNull(item.Note);

        _store.DeleteInboxItem(item.Id);
        Assert.Empty(_store.GetOpenInboxItems());
    }

    [Fact]
    public void AFileStillBeingCopiedIsLeftForTheNextRound()
    {
        var path = Drop("Cartão Exemplo", "a.txt", "08/10 LOJA ALFA R$ 35,05\n");

        var result = InboxScanner.Scan(_root, _store, _ => "", DateTime.Now, TimeSpan.FromMinutes(1));

        Assert.Equal(0, result.Queued);
        Assert.True(File.Exists(path));
    }
}
