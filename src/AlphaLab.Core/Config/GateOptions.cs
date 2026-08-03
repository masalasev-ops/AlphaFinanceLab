namespace AlphaLab.Core.Config;

/// <summary>
/// The paired promotion gate + NW-MDE statistics (CONFIG_REFERENCE "Gate", D31/D48). Every default
/// here MIRRORS that file — it is the single source of truth; never hard-code a value that belongs there.
///
/// Follows the …Options convention (SectionName + mutable get/set defaults matching CONFIG). The
/// CONSUMING phase owns the bind (finding F): registered in AlphaLab.Worker where the evaluation step
/// (checkpoints 3.4–3.5) first reads it; unbound until then, its C# defaults equal the CONFIG values.
/// </summary>
public sealed class GateOptions
{
    public const string SectionName = "Gate";

    /// <summary>The 21-day evaluation cadence (D31): metrics/MDE/gate/monitor/allocator recompute this often.</summary>
    public int EvaluationCadenceDays { get; set; } = 21;

    /// <summary>Minimum forward track before the gate will render anything but TooEarly.</summary>
    public int MinTrackDays { get; set; } = 63;

    /// <summary>MDE confidence 1−α (two-sided). z_{1−α/2} at 0.95 ≈ 1.96.</summary>
    public double Confidence { get; set; } = 0.95;

    /// <summary>MDE power 1−β. z_power at 0.80 ≈ 0.84. (1.96 + 0.84 ≈ 2.8 — the DESIGN_IMPROVEMENTS constant.)</summary>
    public double Power { get; set; } = 0.80;

    /// <summary>Bartlett-kernel lag cap L for the Newey–West long-run variance (D48). L = min(2·maxHorizon, this).</summary>
    public int NwLagCapDays { get; set; } = 21;

    /// <summary>
    /// D89 (v1.9.35)/FR-40: the detectability-at-admission horizon — a candidate whose pre-registered
    /// expected effect could not clear the NW-MDE within this many years (net of the trials-budget cost
    /// it adds) is refused at creation (Phase 4).
    ///
    /// **10 since D121 (v1.9.79); 3 as originally issued.** The floor this implies is `z·TE/√H`, so the
    /// horizon decides what may be proposed at all. At 3 years and generation 2's clean noise the floor
    /// is ~13 %/yr against D116's 32 %/yr ceiling — a band that is entirely too-good-to-be-true for a
    /// real equity strategy, so the FLOOR would push the researcher's claims up exactly as the CEILING
    /// holds them down. Ten years puts it at ~7 %/yr, which admits realistic edges. Pre-registered by
    /// the operator BEFORE generation 2's curves existed: choosing it afterwards would be picking the
    /// value that opens the gate, which is tuning against the monitor.
    /// </summary>
    public int DetectabilityHorizonYears { get; set; } = 10;
}
