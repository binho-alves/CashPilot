namespace CashPilot.Web;

/// <summary>Where the SQLite database lives (the Backup page lists the copies kept next to it).</summary>
public sealed record DatabaseLocation(string Path);
