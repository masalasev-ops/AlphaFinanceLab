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
    public void Save(string source, string date, string name, string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(date);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var dir = Path.Combine(root, source, date);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), payload);
    }
}

/// <summary>No-op cache (tests, dry-runs). Archiving is an audit convenience, never required.</summary>
public sealed class NullRawCache : IRawCache
{
    public static readonly NullRawCache Instance = new();
    public void Save(string source, string date, string name, string payload) { }
}
