namespace AlphaLab.Core.Config;

/// <summary>
/// Risk guardrails (CONFIG_REFERENCE "Guardrails"; DESIGN_IMPROVEMENTS §3.4). Fail closed.
///
/// PHASE-2 BOUNDARY — read this before assuming a guardrail is live. The full exposure system is the
/// GUARDRAIL family of DESIGN_IMPROVEMENTS section 3.4, which is PHASE 7 — NOT "FR-17", which is the
/// ensemble allocator (finding 376, v1.9.94). The DRAWDOWN CIRCUIT BREAKER is the one member pulled
/// FORWARD into Phase 6 by the v1.9.91 scope amendment. Phase 2 applies only the three the six-stage
/// funnel structurally needs to produce a portfolio at all:
///   • <see cref="MinScore"/>            — Stage 3's floor (beneath the per-strategy MinScore)
///   • PositionCapPct (on SizingOptions) — Stage 5's per-position cap
///   • <see cref="MaxConcurrentPositions"/> — Stage 3/4's breadth cap
///
/// The rest — <see cref="HeatMaxPredictedVolAnn"/> (needs D42's covariance, Phase 6),
/// <see cref="ReentryCooldownDays"/>, <see cref="DrawdownCircuitBreakerPct"/>, and the
/// regime-halt guardrails (which need Phase 2's labels but Phase 7's wiring) — are carried here
/// for CONFIG fidelity and are NOT read in Phase 2. They are unbuilt, not broken; PROGRESS
/// records the line so a half-built guardrail is never mistaken for a bug.
/// </summary>
public sealed class GuardrailsOptions
{
    public const string SectionName = "Guardrails";

    /// <summary>System-level score floor; a per-strategy override lives in config_json. The
    /// zero-score invariant (catalog §3) holds independently of this: score == 0 is never
    /// selectable even at the default 0.0.</summary>
    public double MinScore { get; set; } = 0.0;

    public int MaxConcurrentPositions { get; set; } = 60;

    /// <summary>Phase 7 (needs D42's covariance). Unread in Phase 2.</summary>
    public double HeatMaxPredictedVolAnn { get; set; } = 0.15;

    /// <summary>Phase 7. Unread in Phase 2.</summary>
    public int ReentryCooldownDays { get; set; } = 3;

    /// <summary>The account-level absolute-loss halt. PULLED FORWARD to PHASE 6 (v1.9.91 scope
    /// amendment): unread in Phases 2-5, wired at Phase 6 so it covers every arm — mechanical strategies
    /// and the contestant/twin pair alike — from its first forward day. Applied per account identically,
    /// so it cannot bias the pairing. A halt is a STATE, never a verdict (UX-20).</summary>
    public double DrawdownCircuitBreakerPct { get; set; } = 25.0;
}
