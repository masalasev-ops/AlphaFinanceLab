using Microsoft.Data.Sqlite;

namespace AlphaLab.Data;

/// <summary>
/// Resolves ConnectionStrings:AlphaLab by replacing the two tokens shared by every
/// process (FR-37 / D71):
///   {Arena.Id}     -> the active arena slug (e.g. "sp500"), so the store is namespaced
///                     per arena: &lt;DbBase&gt;\{arena}\alphalab.db.
///   {LocalAppData} -> Environment.GetFolderPath(SpecialFolder.LocalApplicationData) — the
///                     known-folders API. D67 bans env-var reads, so we NEVER use
///                     GetEnvironmentVariable / ExpandEnvironmentVariables here.
///
/// After substitution the DataSource is rebuilt with the RUNNING platform's directory separator
/// (v1.9.36), so ONE config string is valid on every OS: a Windows '\' never survives on Linux and
/// a '/' never survives on Windows. That is what makes a cloud (Linux) lift-and-shift a config-value
/// change with no code change — only the base in the four spots moves. Normalization is pure string
/// work through SqliteConnectionStringBuilder; it reads no environment and touches no filesystem.
///
/// Path resolution is split from directory creation (v1.9.6):
///   ResolvePath  — PURE (token replacement only, no filesystem). Readers and tests use
///                  this to resolve a connection string without touching disk.
///   Resolve      — ResolvePath then EnsureDirectoryExists (the dir-creating contract for
///                  writers: the Worker and the EF design-time factory).
/// </summary>
public static class DbPathResolver
{
    /// <summary>Default arena when none is supplied (e.g. a bare `dotnet ef` invocation). D71.</summary>
    public const string DefaultArenaId = "sp500";

    /// <summary>
    /// The compiled connection string, used by the EF design-time factory (bare `dotnet ef`). This is
    /// one of the "four edit spots" that must stay identical to the Worker, Api, and Backfill-CLI
    /// appsettings.json ConnectionStrings:AlphaLab, so every process opens the SAME file
    /// (DB_RELOCATION.md; guarded by ConfigConsistencyTests). This deployment uses the E: literal;
    /// the {LocalAppData} token form is the portable alternative. The FORWARD SLASHES are deliberate
    /// (v1.9.36) — ResolvePath normalizes them to the running platform's separator, so this one string
    /// is valid on Windows and Linux alike; do NOT "correct" them back to backslashes, and if you do
    /// change the form, change all four spots together or ConfigConsistencyTests reddens.
    /// tools/migrate.ps1 still passes an explicit --connection resolved from the Worker appsettings
    /// (finding 119), so real migrations never depend on this constant — but keeping it equal keeps a
    /// bare `dotnet ef` on-target.
    /// </summary>
    public const string DefaultConnectionString =
        @"Data Source=E:/AlphaLabDatabase/{Arena.Id}/alphalab.db";

    /// <summary>PURE token replacement + OS separator normalization. No filesystem access. Returns the
    /// resolved connection string.</summary>
    public static string ResolvePath(string connectionString, string arenaId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(arenaId);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var substituted = connectionString
            .Replace("{Arena.Id}", arenaId, StringComparison.Ordinal)
            .Replace("{LocalAppData}", localAppData, StringComparison.Ordinal);
        // One config string, every OS: a template may be written with '/' or '\', and the
        // expanded {LocalAppData} contributes the running platform's own separator. Rebuild
        // DataSource with the OS separator so a Windows '\' never survives on Linux (cloud
        // lift-and-shift) and vice-versa. Still D67-safe (known-folders API only, no env
        // vars) and PURE (no filesystem access).
        var builder = new SqliteConnectionStringBuilder(substituted);
        builder.DataSource = builder.DataSource
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        return builder.ToString();
    }

    /// <summary>Resolve the connection string, then ensure the store's parent directory exists (writers only).</summary>
    public static string Resolve(string connectionString, string arenaId)
    {
        var resolved = ResolvePath(connectionString, arenaId);
        EnsureDirectoryExists(resolved);
        return resolved;
    }

    /// <summary>Extract the on-disk file path (DataSource) from a resolved connection string.</summary>
    public static string GetDataSourcePath(string resolvedConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedConnectionString);
        var builder = new SqliteConnectionStringBuilder(resolvedConnectionString);
        return Path.GetFullPath(builder.DataSource);
    }

    /// <summary>
    /// The per-launch local-backup directory (RUNBOOK §3 / D72): a <c>backups</c> folder beside the store,
    /// i.e. <c>&lt;DbBase&gt;\{arena}\backups</c>. PURE — string composition only, no filesystem access (the
    /// <see cref="ResolvePath"/> purity precedent); the backup step creates it. Because the store is already
    /// arena-namespaced (<c>…\{arena}\alphalab.db</c>), so is this — no cross-arena bleed (rule 23).
    /// </summary>
    public static string BackupDirectory(string resolvedConnectionString)
    {
        var dbPath = GetDataSourcePath(resolvedConnectionString);
        var dir = Path.GetDirectoryName(dbPath)
            ?? throw new InvalidOperationException($"Resolved store path '{dbPath}' has no parent directory.");
        return Path.Combine(dir, "backups");
    }

    /// <summary>The dated backup file path for a given calendar day: <c>…\backups\alphalab-{yyyy-MM-dd}.db</c>.
    /// PURE. One file per day (a second launch the same day skips — the backup step checks existence).</summary>
    public static string BackupFilePath(string resolvedConnectionString, DateOnly day) =>
        Path.Combine(
            BackupDirectory(resolvedConnectionString),
            $"alphalab-{day:yyyy-MM-dd}.db");

    private static void EnsureDirectoryExists(string resolvedConnectionString)
    {
        var fullPath = GetDataSourcePath(resolvedConnectionString);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
