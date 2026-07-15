namespace AlphaLab.Data.Http;

/// <summary>
/// Archives raw provider payloads to <c>tools/raw-cache/{source}/{date}/</c> (INTEGRATIONS §9;
/// gitignored, 30-day retention). Provenance for reproducing/debugging an ingest without re-hitting
/// the API. Not on the point-in-time read path — purely an audit artifact.
/// </summary>
public interface IRawCache
{
    /// <summary>Persist one raw payload under {source}/{date}/{name}. No-op for the null cache.</summary>
    void Save(string source, string date, string name, string payload);
}

/// <summary>Writes payloads under a configured root (e.g. the repo's <c>tools/raw-cache</c>).</summary>
public sealed class FileRawCache(string root) : IRawCache
{
    /// <summary>The documented raw-cache retention window (INTEGRATIONS §9: "30-day retention"). A fixed
    /// constant, NOT a config key — the doc already specifies it, so introducing one would violate rule 14.</summary>
    public const int RetentionDays = 30;

    public void Save(string source, string date, string name, string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(date);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var dir = Path.Combine(root, source, date);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), payload);
    }

    /// <summary>Enforce the documented <see cref="RetentionDays"/>-day retention: delete cache files older
    /// than the window by last-write time, then remove any directory left empty (deepest-first, NEVER the
    /// root). Age is keyed on file mtime (not the date-named partition), so it is robust to the un-dated
    /// <c>latest</c> partitions and to any historical mis-partitioning. Deletion is confined to
    /// <paramref name="root"/> by construction — only its descendants are ever enumerated. Returns the
    /// number of files deleted. Fails closed on a null/empty root (never enumerate an unbounded path).</summary>
    public int Prune(DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Directory.Exists(root)) return 0;

        var cutoff = utcNow - TimeSpan.FromDays(RetentionDays);
        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList())
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff)
            {
                File.Delete(file);
                deleted++;
            }
        }

        // Sweep now-empty directories deepest-first (so a parent is checked after its children are gone),
        // but never the root itself.
        var rootFull = Path.GetFullPath(root);
        static int Depth(string p) => p.Count(c => c is '\\' or '/');
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(Depth).ToList())
        {
            if (Path.GetFullPath(dir) == rootFull) continue;
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        return deleted;
    }
}

/// <summary>No-op cache (tests, dry-runs). Archiving is an audit convenience, never required.</summary>
public sealed class NullRawCache : IRawCache
{
    public static readonly NullRawCache Instance = new();
    public void Save(string source, string date, string name, string payload) { }
}
