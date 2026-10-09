using CashPilot.Domain.Transactions;
using CashPilot.Infrastructure.Backup;
using CashPilot.Infrastructure.Importing;
using CashPilot.Infrastructure.Persistence;

namespace CashPilot.Infrastructure.Tests;

public sealed class BackupTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cashpilot-tests-" + Guid.NewGuid().ToString("N"));

    public BackupTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private string DatabaseWithOneEntry()
    {
        var path = Path.Combine(_folder, "cashpilot.db");
        using var store = new CashPilotStore(path);
        ManualEntries.Add(store, new DateOnly(2026, 10, 8), "Dinheiro", "Padaria", 12.50m, income: false);
        return path;
    }

    [Fact]
    public void ACopyHoldsTheSameEntriesAndOpensAsADatabase()
    {
        using var source = new CashPilotStore(":memory:");
        ManualEntries.Add(source, new DateOnly(2026, 10, 8), "Dinheiro", "Padaria", 12.50m, income: false);
        var target = Path.Combine(_folder, "copy.db");

        source.BackupTo(target);

        using var copy = new CashPilotStore(target);
        var entry = Assert.Single(copy.GetAll());
        Assert.Equal("Padaria", entry.RawDescription);
        Assert.Equal(-12.50m, entry.Amount);
    }

    [Fact]
    public void TheDailyCopyIsMadeOncePerDayNextToTheDatabase()
    {
        var database = DatabaseWithOneEntry();
        var today = new DateOnly(2026, 10, 8);

        var first = DatabaseBackups.EnsureDailyCopy(database, today);
        var second = DatabaseBackups.EnsureDailyCopy(database, today);

        Assert.NotNull(first);
        Assert.EndsWith("cashpilot-20261008.db", first);
        Assert.Null(second);
        using var copy = new CashPilotStore(first);
        Assert.Single(copy.GetAll());
        Assert.Single(DatabaseBackups.List(database));
    }

    [Fact]
    public void OnlyTheNewestCopiesAreKept()
    {
        var database = DatabaseWithOneEntry();

        for (var day = 1; day <= 5; day++)
            DatabaseBackups.EnsureDailyCopy(database, new DateOnly(2026, 10, day), keep: 3);

        var names = DatabaseBackups.List(database).Select(f => f.Name).ToArray();
        Assert.Equal(new[] { "cashpilot-20261005.db", "cashpilot-20261004.db", "cashpilot-20261003.db" }, names);
    }

    [Fact]
    public void NothingIsCopiedWhenThereIsNoDatabaseYet()
    {
        var missing = Path.Combine(_folder, "nao-existe.db");

        Assert.Null(DatabaseBackups.EnsureDailyCopy(missing, new DateOnly(2026, 10, 8)));
        Assert.Empty(DatabaseBackups.List(missing));
    }

    [Fact]
    public void TheCsvHasOneLinePerEntryWithBrazilianFormats()
    {
        var entries = new[]
        {
            new Transaction
            {
                Account = "Conta A", Date = new DateOnly(2026, 10, 8), Amount = -1234.5m, RawDescription = "Loja; Alfa",
                Type = TransactionType.Expense, Category = "Lazer", Item = "Cinema", InstallmentNumber = 2, InstallmentCount = 3,
            },
            new Transaction
            {
                Account = "Conta A", Date = new DateOnly(2026, 10, 1), Amount = 300m, RawDescription = "=SOMA(A1)",
                Type = TransactionType.Income,
            },
        };

        var lines = TransactionExport.ToCsv(entries).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(TransactionExport.Header, lines[0]);
        Assert.Equal("01/10/2026;Conta A;'=SOMA(A1);300,00;Receita;;;", lines[1]);   // formula defused, date order
        Assert.Equal("08/10/2026;Conta A;\"Loja; Alfa\";-1234,50;Gasto;Lazer;Cinema;2/3", lines[2]);
    }
}
