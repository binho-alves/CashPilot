using System.Net;
using System.Net.Http.Json;
using CashPilot.Contracts;

namespace CashPilot.Mobile.Services;

public enum SendOutcome
{
    /// <summary>The PC stored the entry (or already had it from an earlier try).</summary>
    Sent,
    /// <summary>The PC answered and refused the entry; <see cref="SendResult.Message"/> says why.</summary>
    Rejected,
    /// <summary>The key is wrong or missing.</summary>
    Unauthorized,
    /// <summary>The PC did not answer (away from home, PC off, wrong address). Try again later.</summary>
    Unreachable,
}

public sealed record SendResult(SendOutcome Outcome, string? Message = null, NewEntryResponse? Response = null);

public sealed record FetchResult<T>(bool Ok, T? Value, string? Message);

/// <summary>Talks to the CashPilot API on the PC. Never throws for network trouble: it reports it.</summary>
public sealed class CashPilotClient(AppSettings settings)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    public async Task<SendResult> PostEntryAsync(NewEntryRequest request, CancellationToken token = default)
    {
        try
        {
            using var message = await BuildAsync(HttpMethod.Post, "/api/v1/entries");
            if (message is null) return new SendResult(SendOutcome.Unreachable, "Informe o endereço do PC em Config.");
            message.Content = JsonContent.Create(request, options: JsonFile.Options);
            using var response = await Http.SendAsync(message, token);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<NewEntryResponse>(JsonFile.Options, token);
                return new SendResult(SendOutcome.Sent, Response: body);
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new SendResult(SendOutcome.Unauthorized, "Chave de API recusada. Confira em Config.");
            if (response.StatusCode == HttpStatusCode.BadRequest)
                return new SendResult(SendOutcome.Rejected, await ReadErrorAsync(response, token));
            // 503 (API off), 5xx, anything else: not the entry's fault, keep it and try later.
            return new SendResult(SendOutcome.Unreachable, await ReadErrorAsync(response, token));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return new SendResult(SendOutcome.Unreachable, "Sem conexão com o PC.");
        }
    }

    public Task<FetchResult<LookupsDto>> GetLookupsAsync(CancellationToken token = default) =>
        GetAsync<LookupsDto>("/api/v1/lookups", token);

    public Task<FetchResult<List<EntryDto>>> GetRecentAsync(int limit = 30, CancellationToken token = default) =>
        GetAsync<List<EntryDto>>($"/api/v1/entries?limit={limit}", token);

    public async Task<FetchResult<bool>> PingAsync(CancellationToken token = default)
    {
        var result = await GetAsync<object>("/api/v1/ping", token);
        return new FetchResult<bool>(result.Ok, result.Ok, result.Message);
    }

    public async Task<FetchResult<bool>> DeleteAsync(Guid id, CancellationToken token = default)
    {
        try
        {
            using var message = await BuildAsync(HttpMethod.Delete, $"/api/v1/entries/{id}");
            if (message is null) return new FetchResult<bool>(false, false, "Informe o endereço do PC em Config.");
            using var response = await Http.SendAsync(message, token);
            if (response.IsSuccessStatusCode) return new FetchResult<bool>(true, true, null);
            return new FetchResult<bool>(false, false, await ReadErrorAsync(response, token));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return new FetchResult<bool>(false, false, "Sem conexão com o PC.");
        }
    }

    private async Task<FetchResult<T>> GetAsync<T>(string path, CancellationToken token)
    {
        try
        {
            using var message = await BuildAsync(HttpMethod.Get, path);
            if (message is null) return new FetchResult<T>(false, default, "Informe o endereço do PC em Config.");
            using var response = await Http.SendAsync(message, token);
            if (!response.IsSuccessStatusCode)
                return new FetchResult<T>(false, default, response.StatusCode == HttpStatusCode.Unauthorized
                    ? "Chave de API recusada. Confira em Config."
                    : await ReadErrorAsync(response, token));
            var value = await response.Content.ReadFromJsonAsync<T>(JsonFile.Options, token);
            return new FetchResult<T>(true, value, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return new FetchResult<T>(false, default, "Sem conexão com o PC.");
        }
    }

    private async Task<HttpRequestMessage?> BuildAsync(HttpMethod method, string path)
    {
        var baseUrl = settings.ServerUrl;
        if (baseUrl.Length == 0) return null;
        var message = new HttpRequestMessage(method, baseUrl + path);
        message.Headers.Add("X-Api-Key", await settings.GetApiKeyAsync());
        return message;
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken token)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ApiError>(JsonFile.Options, token);
            if (!string.IsNullOrWhiteSpace(error?.Error)) return error.Error;
        }
        catch (Exception) { }
        return $"O PC respondeu {(int)response.StatusCode}.";
    }
}
