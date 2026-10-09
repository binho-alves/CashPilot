using System.Security.Cryptography;
using System.Text;

namespace CashPilot.Infrastructure.Mobile;

/// <summary>Compares the key sent by the phone with the configured one without leaking where they differ.</summary>
public static class ApiKeyCheck
{
    public const string HeaderName = "X-Api-Key";

    /// <summary>False when no key is configured (the API is then off) or when the given key differs.</summary>
    public static bool IsValid(string? configuredKey, string? givenKey)
    {
        if (string.IsNullOrEmpty(configuredKey) || string.IsNullOrEmpty(givenKey)) return false;
        // Hashing first makes both sides the same length, which FixedTimeEquals needs.
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(configuredKey));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(givenKey));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
