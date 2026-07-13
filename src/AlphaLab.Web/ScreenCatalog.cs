namespace AlphaLab.Web;

/// <summary>One §15 screen: its API path and a one-sentence hint of what will appear here (UX-8c).</summary>
public sealed record ScreenDescriptor(string Key, string Title, string ApiPath, string EmptyHint);

/// <summary>
/// The catalog the client renders. Each EmptyHint teaches — it says what will appear on the screen
/// and which run produces it — so an empty database reads as "waiting for the first run", not a bare
/// "no data" (UX-8c, finding 121).
///
/// These are the 13 non-parameterized §21 list screens (NFR-3: the empty DB renders every one). The
/// two parameterized §21 screens — strategy detail (<c>/api/v1/strategies/{id}</c>) and why-trade
/// (<c>/api/v1/why-trade/{strategyId}/{date}</c>) — are deliberately excluded from this flat catalog:
/// they have no standalone nav entry, being reached by drilling into a specific row (a real id/date
/// that does not exist until the first run). Their endpoints and read-models still exist server-side.
/// </summary>
public static class ScreenCatalog
{
    public static readonly IReadOnlyList<ScreenDescriptor> Screens =
    [
        new("strategies", "Strategies", "api/v1/strategies",
            "The strategy leaderboard — one row per candidate with its verdict chip, tier and population percentile — populated by the first daily run's metrics."),
        new("live", "Live", "api/v1/live",
            "The live paper account's positions and P&L, updated by each daily commit transaction."),
        new("allocation", "Allocation", "api/v1/allocation",
            "The ensemble allocator's target vs applied weights with the clamp bound on each arrow, produced on cadence days by the allocator."),
        new("trades", "Trades", "api/v1/trades",
            "The blotter of executed paper trades with their stamped cost-model version, appended by each daily commit transaction."),
        new("go-live-log", "Go-live log", "api/v1/go-live-log",
            "The audit trail of promotions and retirements — each with its gate verdict and the run that decided it — appended on cadence days."),
        new("regimes", "Regimes", "api/v1/regimes",
            "The dated market-regime episodes from the regime-proxy feed, written by the regime-label service once its warm-up history exists."),
        new("risk", "Risk", "api/v1/risk",
            "Active guardrails and any frozen positions with the reason each fired — filled from the risk checks in every run."),
        new("data-health", "Data health", "api/v1/data-health",
            "Bar coverage, corporate-action mapping and any stale-run alerts — filled from the fetch and commit stages of each run."),
        new("journal", "Journal", "api/v1/journal",
            "The hypothesis journal (claim, metric, evidence window) for every pre-registered candidate, written when candidates are created."),
        new("overfitting", "Overfitting", "api/v1/health/overfitting",
            "The eight overfitting signals and their thresholds, computed on cadence days as a promotion veto."),
        new("admin-interventions", "Admin interventions", "api/v1/admin/interventions",
            "The audited log of D55 admin actions (typed-confirmed, validated like provider rows), appended whenever an admin action is taken."),
        new("activity", "Activity", "api/v1/activity",
            "A chronological feed of runs, go-lives and admin actions, appended as the system operates."),
        new("replay", "Replay", "api/v1/replay",
            "Quarantined Arena Replay artifacts — always flagged, never mixed into forward views — produced by an explicit replay job."),
    ];
}
