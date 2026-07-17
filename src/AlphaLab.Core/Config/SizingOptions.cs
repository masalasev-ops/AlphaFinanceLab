using AlphaLab.Core.Domain;

namespace AlphaLab.Core.Config;

/// <summary>
/// Position-sizing knobs (CONFIG_REFERENCE "Sizing"; D32/D42).
///
/// PHASE-2 NOTE (CHANGELOG finding 169 / FR-11 partial). CONFIG_REFERENCE documents
/// Sizing.Mode's default as "inverse_vol" — that is the DESIGNED END STATE and is left alone.
/// The Worker's appsettings ships Mode=equal until FR-11 full (inverse-vol + Ledoit–Wolf
/// covariance, D42) lands in Phase 6, and the sizer REFUSES any other mode rather than falling
/// back (see <see cref="SizingMode"/>). The default below is <see cref="SizingMode.Equal"/> so
/// that a config with no Sizing section is honest about what this build can actually do, rather
/// than claiming a mode it would then throw on.
///
/// <see cref="Covariance"/> and <see cref="Kelly"/> are carried because CONFIG documents them and
/// this class is that section's binding target; nothing in Phase 2 reads them.
/// </summary>
public sealed class SizingOptions
{
    public const string SectionName = "Sizing";

    public SizingMode Mode { get; set; } = SizingMode.Equal;

    /// <summary>Annualized portfolio vol target. FR-11 full (Phase 6).</summary>
    public double PortfolioVolTargetAnn { get; set; } = 0.12;

    /// <summary>Per-position cap as a fraction of equity. Applied in Phase 2 (one of the three
    /// guardrails the funnel structurally needs).</summary>
    public double PositionCapPct { get; set; } = 0.05;

    public CovarianceOptions Covariance { get; set; } = new();
    public KellyOptions Kelly { get; set; } = new();

    /// <summary>D42 — Phase 6. Carried for CONFIG fidelity; unread in Phase 2.</summary>
    public sealed class CovarianceOptions
    {
        public string Estimator { get; set; } = "ledoit_wolf";
        public int WindowDays { get; set; } = 252;
        public string Fallback { get; set; } = "ewma_single_index";
        public double EwmaLambda { get; set; } = 0.97;
    }

    /// <summary>Phase 6+. Carried for CONFIG fidelity; unread in Phase 2.</summary>
    public sealed class KellyOptions
    {
        public double FractionCap { get; set; } = 0.25;
        public int MinTradesForB { get; set; } = 30;
        public double ShrinkBToward { get; set; } = 1.0;
    }
}
