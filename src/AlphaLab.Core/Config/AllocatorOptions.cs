namespace AlphaLab.Core.Config;

/// <summary>
/// The ensemble allocator — shrinkage → softmax → ordered clamps (CONFIG_REFERENCE "Allocator", D51;
/// MASTER §20.2). Every default here MIRRORS that file — it is the single source of truth; never
/// hard-code a value that belongs there.
///
/// Follows the …Options convention (SectionName + mutable get/set defaults matching CONFIG). The
/// CONSUMING phase owns the bind (finding F): registered in AlphaLab.Worker where the allocator
/// (checkpoint 3.7) first reads it; unbound until then.
/// </summary>
public sealed class AllocatorOptions
{
    public const string SectionName = "Allocator";

    /// <summary>Band half-width (percentage points of target weight): sub-band moves are blocked (clamp order 4).</summary>
    public double BandPts { get; set; } = 5.0;

    /// <summary>Allocation cadence (days) — mirrors the 21-day evaluation cadence.</summary>
    public int CadenceDays { get; set; } = 21;

    /// <summary>Cap (percentage points) on how far a TooEarly pair may tilt weight (clamp order 2).</summary>
    public double TooEarlyTiltCapPts { get; set; } = 10.0;

    /// <summary>Per-evaluation weight decay applied to a Suspect strategy: ×(1 − this/100) (clamp order 3).</summary>
    public double SuspectDecayPctPerEval { get; set; } = 25.0;

    /// <summary>Softmax temperature λ, in %/yr of shrunk alpha: t_i = softmax(α̃_i / λ).</summary>
    public double TemperaturePctAlpha { get; set; } = 2.0;

    /// <summary>Shrinkage dispersion floor τ (%/yr alpha): w_i = τ²/(τ² + se_i²), τ floored at this.</summary>
    public double TauMinPctAlpha { get; set; } = 0.5;

    /// <summary>Per-strategy weight floor (%). finding 116: floors apply PRE-renormalization, so the
    /// promotable roster caps at ⌊100/WeightFloorPct⌋ = 20 at the default (clamp order 1).</summary>
    public double WeightFloorPct { get; set; } = 5.0;

    /// <summary>Per-strategy weight ceiling (%) (clamp order 1).</summary>
    public double WeightCeilingPct { get; set; } = 60.0;
}
