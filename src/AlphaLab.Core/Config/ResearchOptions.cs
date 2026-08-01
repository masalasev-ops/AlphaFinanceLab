namespace AlphaLab.Core.Config;

/// <summary>
/// The D82 trials budget that rations self-improvement's deflated-Sharpe spend (CONFIG_REFERENCE
/// "Research").
///
/// The seat surfaces its remaining budget with every proposal, rendered beside the deflated-Sharpe trials
/// count: **the improver rations itself because every trial spends everyone's significance** (S2).
/// </summary>
public sealed class ResearchOptions
{
    public const string SectionName = "Research";

    /// <summary>
    /// Fork cadence.
    ///
    /// **The value is NOT derived, and that is recorded rather than hidden (finding 309).** The rationale
    /// for *a* budget is sound; the number 6 is not derived anywhere. D110's trend rail forbids raising it
    /// to make the improvement trend readable, and the budget determines whether that trend is ever
    /// readable — so the resolution is not the budget but the **per-arena** tax: a second arena adds
    /// proposal capacity without raising either arena's bar. Changing this value would need a decision
    /// amending D82, never a config edit (rule 25).
    /// </summary>
    public int ForkBudgetPerYear { get; set; } = 6;

    /// <summary>
    /// Matches the "1 Live + 2–3 Candidates" roster shape (§8).
    ///
    /// **Also the D112 evidence-diet bound**, and it carries that second job precisely because it IS
    /// derived where `ForkBudgetPerYear` is not: §8 states the roster shape is *"bounded by statistical
    /// honesty, not compute"*, which makes this the count of claims the lab can honestly hold in flight —
    /// the right quantity to measure a saturated evidence base against.
    /// </summary>
    public int MaxConcurrentCandidates { get; set; } = 3;
}
