using CashPilot.Contracts;

namespace CashPilot.Mobile.Services;

/// <summary>The last accounts and categories seen, so entries can be typed away from the PC (the phone is offline outside home).</summary>
public sealed class LookupsCache(string path)
{
    private LookupsDto? _value;

    public LookupsDto Current => _value ??= JsonFile.Read(path, new LookupsDto([], []));

    public void Save(LookupsDto lookups)
    {
        _value = lookups;
        JsonFile.Write(path, lookups);
    }
}
