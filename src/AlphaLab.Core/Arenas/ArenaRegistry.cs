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
public sealed class ArenaRegistry(IReadOnlyList<ArenaEntry> arenas, ArenaEntry active)
{
    public IReadOnlyList<ArenaEntry> Arenas { get; } = arenas;
    public ArenaEntry Active { get; } = active;

    /// <summary>
    /// Build a registry from configured entries, choosing the first as active (FR-37). When the
    /// registry is empty, falls back to a single default arena at <paramref name="fallbackBaseUrl"/>.
    /// </summary>
    public static ArenaRegistry FromEntries(IEnumerable<ArenaEntry>? arenas, string? fallbackBaseUrl = null)
    {
        var list = arenas?.ToList() ?? [];
        var active = list.Count > 0
            ? list[0]
            : new ArenaEntry { BaseUrl = fallbackBaseUrl ?? new ArenaEntry().BaseUrl };
        return new ArenaRegistry(list, active);
    }
}
