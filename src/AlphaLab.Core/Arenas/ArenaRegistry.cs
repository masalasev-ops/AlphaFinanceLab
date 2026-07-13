namespace AlphaLab.Core.Arenas;

/// <summary>
/// One entry in the non-secret Arenas registry (FR-37 / D71), loaded from the client's
/// wwwroot/appsettings.json. baseUrl must equal that arena's API "Urls" value; there is no bare
/// Api:BaseUrl key — the active arena's baseUrl plays that role.
/// </summary>
public sealed class ArenaEntry
{
    public string Id { get; set; } = "sp500";
    public string DisplayName { get; set; } = "S&P 500";
    public string BaseUrl { get; set; } = "http://127.0.0.1:5230";
}

/// <summary>
/// The full registry plus the active arena (default: the first entry). No UI merges arenas into one
/// ranking (UX-13) — the registry only selects which single arena the client talks to.
/// </summary>
public sealed class ArenaRegistry(IReadOnlyList<ArenaEntry> arenas, ArenaEntry active, bool isFallback)
{
    public IReadOnlyList<ArenaEntry> Arenas { get; } = arenas;
    public ArenaEntry Active { get; } = active;

    /// <summary>
    /// True when no Arenas registry was configured, so <see cref="Active"/> is a synthesized fallback
    /// (typically the client's own origin). Fail-closed (hard rule 10): a client in this state has no
    /// real API address — every read would call the wrong host — so the UI must surface a visible
    /// config-error state rather than silently emitting confusing transport errors.
    /// </summary>
    public bool IsFallback { get; } = isFallback;

    /// <summary>
    /// Build a registry from configured entries, choosing the first as active (FR-37). When the
    /// registry is empty (a missing/blank Arenas section — a plausible editing accident), the result
    /// is flagged <see cref="IsFallback"/> with the active baseUrl set to <paramref name="fallbackBaseUrl"/>;
    /// the caller must render a config-error state rather than treat it as a working arena.
    /// </summary>
    public static ArenaRegistry FromEntries(IEnumerable<ArenaEntry>? arenas, string? fallbackBaseUrl = null)
    {
        var list = arenas?.ToList() ?? [];
        if (list.Count > 0)
        {
            return new ArenaRegistry(list, list[0], isFallback: false);
        }

        var fallback = new ArenaEntry { BaseUrl = fallbackBaseUrl ?? new ArenaEntry().BaseUrl };
        return new ArenaRegistry(list, fallback, isFallback: true);
    }
}
