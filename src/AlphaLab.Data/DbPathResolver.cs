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
    /// the {LocalAppData} token form is the portable alternative. tools/migrate.ps1 still passes an
    /// explicit --connection resolved from the Worker appsettings (finding 119), so real migrations
    /// never depend on this constant — but keeping it equal keeps a bare `dotnet ef` on-target.
    /// </summary>
    public const string DefaultConnectionString =
        @"Data Source=E:\AlphaLabDatabase\{Arena.Id}\alphalab.db";

    /// <summary>PURE token replacement. No filesystem access. Returns the resolved connection string.</summary>
    public static string ResolvePath(string connectionString, string arenaId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(arenaId);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return connectionString
            .Replace("{Arena.Id}", arenaId, StringComparison.Ordinal)
            .Replace("{LocalAppData}", localAppData, StringComparison.Ordinal);
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
