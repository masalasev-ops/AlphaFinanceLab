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
    /// <summary>The edge plant's annualized overlay target, percent (default 2.0).</summary>
    public double AlphaAnnualPct { get; set; } = 2.0;

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
