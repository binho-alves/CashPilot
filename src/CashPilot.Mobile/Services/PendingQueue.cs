using CashPilot.Contracts;

namespace CashPilot.Mobile.Services;

/// <summary>An entry typed on the phone that the PC has not accepted yet. <see cref="Error"/> is set when the PC refused it.</summary>
public sealed record PendingEntry(NewEntryRequest Request, DateTime CreatedAt, string? Error = null);

/// <summary>
/// Entries typed while the PC could not be reached. They live in a file, so closing the app or restarting the phone
/// loses nothing. Each one carries its client id, so sending it again is safe.
/// </summary>
public sealed class PendingQueue(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<PendingEntry>> GetAllAsync()
    {
        await _gate.WaitAsync();
        try { return Load(); }
        finally { _gate.Release(); }
    }

    public async Task AddAsync(NewEntryRequest request)
    {
        await _gate.WaitAsync();
        try
        {
            var list = Load();
            list.Add(new PendingEntry(request, DateTime.Now));
            JsonFile.Write(path, list);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(Guid clientId)
    {
        await _gate.WaitAsync();
        try
        {
            var list = Load();
            if (list.RemoveAll(p => p.Request.ClientId == clientId) > 0) JsonFile.Write(path, list);
        }
        finally { _gate.Release(); }
    }

    public async Task SetErrorAsync(Guid clientId, string? error)
    {
        await _gate.WaitAsync();
        try
        {
            var list = Load();
            var at = list.FindIndex(p => p.Request.ClientId == clientId);
            if (at < 0) return;
            list[at] = list[at] with { Error = error };
            JsonFile.Write(path, list);
        }
        finally { _gate.Release(); }
    }

    private List<PendingEntry> Load() => JsonFile.Read(path, new List<PendingEntry>());
}
