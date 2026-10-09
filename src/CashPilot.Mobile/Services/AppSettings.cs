namespace CashPilot.Mobile.Services;

/// <summary>Where the PC is (address) and the key that lets this phone in. The key is kept in the Android keystore.</summary>
public sealed class AppSettings
{
    private const string UrlKey = "server-url";
    private const string ApiKeyKey = "api-key";
    private const string LastAccountKey = "last-account";

    private string? _apiKey;
    private bool _apiKeyLoaded;

    /// <summary>For example http://192.168.0.10:5080 (no trailing slash).</summary>
    public string ServerUrl
    {
        get => Preferences.Default.Get(UrlKey, "");
        set => Preferences.Default.Set(UrlKey, Normalize(value));
    }

    public string LastAccount
    {
        get => Preferences.Default.Get(LastAccountKey, "");
        set => Preferences.Default.Set(LastAccountKey, value ?? "");
    }

    public async Task<string> GetApiKeyAsync()
    {
        if (_apiKeyLoaded) return _apiKey ?? "";
        try { _apiKey = await SecureStorage.Default.GetAsync(ApiKeyKey); }
        catch (Exception) { _apiKey = null; }   // the keystore can be unavailable (restored phone, no lock screen)
        _apiKeyLoaded = true;
        return _apiKey ?? "";
    }

    public async Task SetApiKeyAsync(string key)
    {
        key = (key ?? "").Trim();
        _apiKey = key;
        _apiKeyLoaded = true;
        try
        {
            if (key.Length == 0) SecureStorage.Default.Remove(ApiKeyKey);
            else await SecureStorage.Default.SetAsync(ApiKeyKey, key);
        }
        catch (Exception)
        {
            // Kept in memory for this run only; the settings page tells the user to type it again if it is lost.
        }
    }

    public bool IsConfigured => ServerUrl.Length > 0;

    public static string Normalize(string? url)
    {
        url = (url ?? "").Trim().TrimEnd('/');
        if (url.Length > 0 && !url.Contains("://", StringComparison.Ordinal)) url = "http://" + url;
        return url;
    }
}
