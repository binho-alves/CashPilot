using System.Text.Json;

namespace CashPilot.Mobile.Services;

internal static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static T Read<T>(string path, T fallback)
    {
        try
        {
            if (!File.Exists(path)) return fallback;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? fallback;
        }
        catch (Exception)
        {
            return fallback;   // a damaged cache is rebuilt from the server; a damaged queue is handled by its caller
        }
    }

    /// <summary>Writes to a temporary file first, so a crash in the middle never leaves a half-written file.</summary>
    public static void Write<T>(string path, T value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, overwrite: true);
    }
}
