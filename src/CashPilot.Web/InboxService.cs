using CashPilot.Infrastructure.Importing;
using CashPilot.Infrastructure.Persistence;

namespace CashPilot.Web;

/// <summary>
/// Watches the folder set in <c>CashPilot:InboxFolder</c> (one subfolder per account or card) and queues what it reads for the
/// user to check on the Importar page. One scan at a time, whether it comes from the timer or from the "Verificar agora" button.
/// </summary>
public sealed class InboxService(
    IConfiguration configuration, DatabaseLocation database, ImageTextReader reader, ILogger<InboxService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string? Folder => string.IsNullOrWhiteSpace(configuration["CashPilot:InboxFolder"]) ? null : configuration["CashPilot:InboxFolder"]!.Trim();
    public bool Enabled => Folder is not null;

    /// <summary>What the last scan did, for the Importar page.</summary>
    public string? LastMessage { get; private set; }
    public DateTime? LastScan { get; private set; }

    public event Action? Changed;

    public async Task<InboxScanResult?> ScanNowAsync()
    {
        if (Folder is not { } folder) return null;
        await _lock.WaitAsync();
        try
        {
            var result = await Task.Run(() =>
            {
                using var store = new CashPilotStore(database.Path);
                return InboxScanner.Scan(folder, store, image => reader.Read(image), DateTime.Now);
            });
            LastScan = DateTime.Now;
            LastMessage = result.Queued == 0 && result.Failed == 0 ? "Nada novo na pasta." : $"{result.Queued} arquivo(s) para conferir, {result.Failed} com problema.";
            foreach (var message in result.Messages) logger.LogWarning("Inbox: {Message}", message);
            if (result.Queued + result.Failed > 0) Changed?.Invoke();
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastMessage = "Erro ao ler a pasta: " + ex.Message;
            logger.LogError(ex, "Inbox scan failed");
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;
        var seconds = int.TryParse(configuration["CashPilot:InboxIntervalSeconds"], out var s) && s >= 15 ? s : 60;
        logger.LogInformation("Inbox: watching {Folder} every {Seconds}s", Folder, seconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        do { await ScanNowAsync(); }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
