namespace AlphaLab.Core.Config;

/// <summary>
/// Calibration configuration (CONFIG_REFERENCE "Calibration") — the D64 planted-strategy fixtures the
/// Phase-4 replay validates the machinery against. Follows the …Options convention (SectionName +
/// mutable get/set defaults matching CONFIG). Bound by the replay composition (the consuming phase
/// owns the bind, CONFIG key rule 7).
/// </summary>
public sealed class CalibrationOptions
{
    public const string SectionName = "Calibration";

    public PlantOptions Plant { get; set; } = new();
}

/// <summary>The D64 plant parameters (CONFIG "Calibration.Plant"; MASTER §20.9).</summary>
public sealed class PlantOptions
{
    /// <summary>The edge plant's annualized overlay target, percent (default 2.0). The DAILY edge plant and
    /// the floor-cohort minimum; daily is a SURVIVAL case only (finding-113 cohort), never a promotion
    /// target — under its ~21.9%/yr random-redraw cost drag it cannot beat the benchmark at any plausible
    /// overlay (Change 4 finding). Promotion is demonstrated on the MONTHLY ladder below.</summary>
    public double AlphaAnnualPct { get; set; } = 2.0;

    /// <summary>Change 4 (B3, per-cadence ladder): the MONTHLY edge-plant strength rungs, percent
    /// (default GEOMETRIC 2/4/8/16 — geometric because the guarded MDE is unknown ahead of the smoke run
    /// and the unguarded value was ~15.9%). The 16% rung is an explicit DETECTION-SANITY rung: it exists to
    /// establish the machinery can detect an edge AT ALL, not to represent a plausible strategy. The
    /// per-rung promotion outcome IS the C-1 detection-power curve — the checkpoint's primary finding, read
    /// instead of the gate colour.</summary>
    public double[] MonthlyEdgeLadderPct { get; set; } = [2.0, 4.0, 8.0, 16.0];

    /// <summary>Change 4: the offline-estimated cost_drag + MDE floor per cadence, percent — the bar
    /// PrimaryEdgeIds' rule uses to pick the smallest rung that clears it. DAILY ~37% (cost drag 21.9% +
    /// clean MDE) is unreachable by any plausible daily overlay, so daily never becomes the primary; MONTHLY
    /// ~15.9% (the offline MDE; the guarded value is confirmed on the Stage-2 smoke run) is cleared by the
    /// 16% rung. Pre-registered BEFORE the run (never tuned to it) — the rule, not a hand-picked plant, is
    /// what keeps this from being tuning-by-another-name.</summary>
    public double DailyMdeFloorPct { get; set; } = 37.0;

    /// <summary>See <see cref="DailyMdeFloorPct"/> — the monthly cadence's offline cost_drag+MDE floor.</summary>
    public double MonthlyMdeFloorPct { get; set; } = 15.9;

    /// <summary>The pre-registered offline floor for a cadence; an unlisted family is unreachable (+∞), so it
    /// can never win the primary selection.</summary>
    public double MdeFloorFor(string family) => family switch
    {
        "daily" => DailyMdeFloorPct,
        "monthly" => MonthlyMdeFloorPct,
        _ => double.PositiveInfinity,
    };

    /// <summary>The anti-predictive plant's mirrored negative target, percent (default −2.0).</summary>
    public double AntiAlphaAnnualPct { get; set; } = -2.0;

    /// <summary>Stationary fraction of ACTIVE sessions in the two-state process (default 0.25).</summary>
    public double ActiveDayFrac { get; set; } = 0.25;

    /// <summary>Per-session persistence of the active state (default 0.9); the mean active run length
    /// is max(1/(1−φ), family holding horizon) — φ sets the floor, the horizon scales it up.</summary>
    public double PersistencePhi { get; set; } = 0.9;

    /// <summary>Regime multipliers on the PIT trend label (CONFIG key shape:
    /// <c>Calibration:Plant:RegimeMultipliers:{bull,bear}</c>; defaults bull 1.25 / bear 0.5),
    /// renormalized by the RUNNING realized regime mix so the unconditional annual target still nets
    /// to <see cref="AlphaAnnualPct"/> (PIT-clean: the normalizer uses labels ≤ t only).</summary>
    public Dictionary<string, double> RegimeMultipliers { get; set; } = new(StringComparer.Ordinal)
    {
        ["bull"] = 1.25,
        ["bear"] = 0.5,
    };

    /// <summary>The bull/bear multipliers with fail-closed-to-neutral fallbacks (an unlisted token = 1.0).</summary>
    public double MultiplierFor(string trendToken) => RegimeMultipliers.GetValueOrDefault(trendToken, 1.0);

    /// <summary>Seeds per plant kind (default 50) — each curve is the per-t median over these.</summary>
    public int SeedsPerPlant { get; set; } = 50;

    /// <summary>Naive-vs-realistic P_edge(t) divergence (percentile points, any t ≥ 126d) beyond which
    /// the realistic curves are adopted and the divergence archived (default 10).</summary>
    public double SensitivityMaxGapPts { get; set; } = 10.0;
}
