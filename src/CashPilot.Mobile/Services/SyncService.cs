using CashPilot.Contracts;

namespace CashPilot.Mobile.Services;

public sealed record SyncReport(int Sent, int Rejected, int Waiting, string? Problem);

/// <summary>Sends the queued entries to the PC, oldest first, and refreshes the pick lists.</summary>
public sealed class SyncService(PendingQueue queue, CashPilotClient client, LookupsCache lookups)
{
    private readonly SemaphoreSlim _running = new(1, 1);

    /// <summary>Raised after a sync that changed the queue, so open pages can refresh.</summary>
    public event Action? Changed;

    public async Task<SyncReport> SyncAsync()
    {
        if (!await _running.WaitAsync(0)) return new SyncReport(0, 0, (await queue.GetAllAsync()).Count, null);
        try
        {
            int sent = 0, rejected = 0;
            string? problem = null;

            // No Wi-Fi at all (away from home): do not wait for timeouts, the queue keeps everything.
            if (Connectivity.Current.NetworkAccess == NetworkAccess.None)
                return new SyncReport(0, 0, (await queue.GetAllAsync()).Count(p => p.Error is null), "Sem rede.");

            foreach (var item in (await queue.GetAllAsync()).Where(p => p.Error is null))
            {
                var result = await client.PostEntryAsync(item.Request);
                if (result.Outcome == SendOutcome.Sent)
                {
                    await queue.RemoveAsync(item.Request.ClientId);
                    sent++;
                }
                else if (result.Outcome == SendOutcome.Rejected)
                {
                    await queue.SetErrorAsync(item.Request.ClientId, result.Message);
                    rejected++;
                }
                else
                {
                    problem = result.Message;   // Unreachable or Unauthorized: stop, the rest would fail the same way
                    break;
                }
            }

            if (problem is null)
            {
                var fresh = await client.GetLookupsAsync();
                if (fresh is { Ok: true, Value: { } value }) lookups.Save(value);
                else problem = fresh.Message;
            }

            var waiting = (await queue.GetAllAsync()).Count(p => p.Error is null);
            if (sent > 0 || rejected > 0) Changed?.Invoke();
            return new SyncReport(sent, rejected, waiting, problem);
        }
        finally { _running.Release(); }
    }
}
