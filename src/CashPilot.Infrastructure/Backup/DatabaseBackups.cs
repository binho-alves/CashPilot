using Microsoft.Data.Sqlite;

namespace CashPilot.Infrastructure.Backup;

public sealed record BackupFile(string Path, string Name, DateTime Modified, long Bytes);

/// <summary>
/// Copies of the SQLite database. A copy is made with SQLite's own backup API, so it is consistent even while the
/// app is open, and it carries the same data as the original (personal data: the folder is git-ignored).
/// </summary>
public static class DatabaseBackups
{
    private const string FolderName = "backups";
    private const string Prefix = "cashpilot-";

    /// <summary>Copies the database that <paramref name="source"/> is connected to into a new file at <paramref name="destination"/>.</summary>
    public static void Copy(SqliteConnection source, string destination)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString();
        using var target = new SqliteConnection(connectionString);
        target.Open();
        source.BackupDatabase(target);
    }

    public static string FolderFor(string databasePath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath)) ?? ".", FolderName);

    /// <summary>
    /// Makes today's copy in <c>backups/cashpilot-yyyyMMdd.db</c> next to the database, unless it already exists, and
    /// keeps only the <paramref name="keep"/> newest ones. Meant to run at startup, before the app migrates the schema.
    /// Returns the path of the copy made, or null when nothing was needed (no database yet, or already copied today).
    /// </summary>
    public static string? EnsureDailyCopy(string databasePath, DateOnly today, int keep = 14)
    {
        if (!File.Exists(databasePath)) return null;

        var folder = FolderFor(databasePath);
        var target = Path.Combine(folder, $"{Prefix}{today:yyyyMMdd}.db");
        string? created = null;

        if (!File.Exists(target))
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString();
            using (var source = new SqliteConnection(connectionString))
            {
                source.Open();
                Copy(source, target);
            }
            created = target;
        }

        Prune(folder, keep);
        return created;
    }

    /// <summary>The automatic copies, newest first.</summary>
    public static IReadOnlyList<BackupFile> List(string databasePath)
    {
        var folder = FolderFor(databasePath);
        if (!Directory.Exists(folder)) return Array.Empty<BackupFile>();

        return Directory.EnumerateFiles(folder, Prefix + "*.db")
            .Select(path => new FileInfo(path))
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Select(f => new BackupFile(f.FullName, f.Name, f.LastWriteTime, f.Length))
            .ToList();
    }

    private static void Prune(string folder, int keep)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var old in Directory.EnumerateFiles(folder, Prefix + "*.db")
                     .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                     .Skip(Math.Max(1, keep)))
        {
            try { File.Delete(old); }
            catch (IOException) { /* in use: next run tries again */ }
        }
    }
}
