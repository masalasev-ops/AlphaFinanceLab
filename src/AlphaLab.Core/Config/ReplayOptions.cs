namespace AlphaLab.Core.Config;

/// <summary>
/// Replay configuration (CONFIG_REFERENCE "Replay", D37). Bound by the replay composition (the
/// consuming phase owns the bind, CONFIG key rule 7 — Phase 4 is the first consumer).
/// </summary>
public sealed class ReplayOptions
{
    public const string SectionName = "Replay";

    /// <summary>Minimum replay-years for the machinery-validation suite (default 15).</summary>
    public int ValidationYears { get; set; } = 15;

    /// <summary>After Phase-4 sign-off, prune the per-member replay ledgers (control_equity + plant
    /// member equity rows) — the curves/report/runs stay (default true).</summary>
    public bool PrunePerMemberLedgersAfterSignoff { get; set; } = true;

    /// <summary>v1.9.7 finding 113: the Phase-4 DoD floor — fraction of D64 edge plants still
    /// promotable at 5y (default 0.90). A floor failure recalibrates S6's patience, never the plant.</summary>
    public double EdgePlantSurvivalFloor5y { get; set; } = 0.90;

    /// <summary>v1.9.7 finding 114: bound on the fraction of no-edge plants EVER reaching Suspect via
    /// ANY signal over the replay window (default 0.10).</summary>
    public double JointFalseAlarmMaxFrac { get; set; } = 0.10;
}
