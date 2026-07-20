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
}
