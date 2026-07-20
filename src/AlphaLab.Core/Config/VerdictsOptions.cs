namespace AlphaLab.Core.Config;

/// <summary>
/// The D63 separation state, resolved in the read-models (CONFIG_REFERENCE "Verdicts"). Every default
/// here MIRRORS that file — it is the single source of truth; never hard-code a value that belongs there.
///
/// Follows the …Options convention (SectionName + mutable get/set defaults matching CONFIG). The
/// CONSUMING phase owns the bind (finding F): registered in AlphaLab.Api where the read-model builders
/// (checkpoints 3.11/3.13) resolve separation_state; unbound until then.
/// </summary>
public sealed class VerdictsOptions
{
    public const string SectionName = "Verdicts";

    /// <summary>Once the forward track reaches this many trading days, a persistent 'none' state renders
    /// the IndistinguishableFromRandom chip with its day count.</summary>
    public int SeparationMinTrackDays { get; set; } = 252;

    /// <summary>The population's central band fraction: 'none' = the percentile path stays inside the
    /// 25th–75th pct region (central 0.50); outside it (but not yet distinguishable) = 'emerging'.</summary>
    public double SeparationBandCentralFrac { get; set; } = 0.50;
}
