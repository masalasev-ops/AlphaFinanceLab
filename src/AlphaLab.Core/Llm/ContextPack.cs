namespace AlphaLab.Core.Llm;

/// <summary>Which AI seat a pack was built for (D79; <c>ai_context_packs.seat</c> CHECK).</summary>
public static class AiSeat
{
    public const string Researcher = "researcher";
    public const string Contestant = "contestant";
    public const string Advisor = "advisor";

    public static readonly IReadOnlyList<string> All = [Researcher, Contestant, Advisor];
}

/// <summary>
/// One field in a context pack.
///
/// <see cref="ObservedAt"/> is what makes <c>FX-PackNoLeak</c>'s first assertion possible **per field**.
/// A pack assembled at the right watermark can still hold ONE field resolved through a path that ignored
/// it — a pack-level check would pass on exactly that pack, which is why the timestamp travels with the
/// value rather than with the pack.
/// </summary>
/// <param name="Name">Whitelist key. A field whose name is not whitelisted fails construction.</param>
/// <param name="Value">The derived, compressed value. **Never a raw series** (D80).</param>
/// <param name="ObservedAt">When this fact became knowable. Null = timeless (a config constant, a
/// definition) — never "unknown", which would be a way to opt out of the leak check.</param>
public sealed record PackField(string Name, object? Value, string? ObservedAt = null);

/// <summary>
/// Thrown when a pack violates D80/D104. Construction fails rather than the pack being built and checked
/// afterwards: a leaked pack that exists is a leaked pack that can be sent.
/// </summary>
public sealed class PackViolationException(string message) : InvalidOperationException(message);

/// <summary>
/// The context-pack contract (D80, §23.2 / §23.8.2).
///
/// **The whitelist is a CLOSURE, not a filter.** A field added to the builder and not to the whitelist
/// **fails** rather than being silently dropped. Filtering would be the more forgiving design and the
/// wrong one: it makes the pack quietly incomplete instead of loudly wrong, and the invariant would decay
/// silently as the pack grew — which §23.8.2 names as the specific failure mode to prevent.
///
/// **Two assertions, and neither implies the other:**
/// <list type="number">
/// <item><b>Admissibility</b> — no field carries an <c>observed_at</c> later than the simulated as-of.</item>
/// <item><b>Closure</b> — only whitelisted fields may appear.</item>
/// </list>
/// Distinct from <c>FX-PackWatermark</c>, which asserts byte-identity: a pack that deterministically
/// includes a post-as-of fact is byte-identical on every build **and still leaks**.
/// </summary>
public static class PackWhitelist
{
    // ---- Common fields: every seat receives these. ----

    /// <summary>The simulated as-of date.</summary>
    public const string AsOf = "as_of";

    /// <summary>The PIT regime label (D50).</summary>
    public const string RegimeLabel = "regime_label";

    /// <summary>
    /// The arena's detectability floor, as-of (D89/FR-40; added at 5.4 as a **COMMON** field — D113's
    /// control arm receives it too; it is not the treatment).
    ///
    /// **This does NOT breach D110 R1**, and the distinction is recorded because a later reader will
    /// otherwise read it as one. R1 forbids the researcher reading **its own score**. The floor is a
    /// measured property of the ARENA — the bar every candidate faces — not a grade on the researcher.
    /// Different object, different rail. And withholding it protects nothing: D79 already grants closed
    /// journal outcomes, from which the floor can be reconstructed *slowly and wrongly*; publishing it
    /// replaces a bad inference with a good fact.
    /// </summary>
    public const string DetectabilityFloorAnn = "detectability_floor_ann";

    /// <summary>Count of live trials the floor was computed at — the floor is uninterpretable without it,
    /// since it RISES with the trials tax.</summary>
    public const string TrialsCount = "trials_count";

    // ---- Researcher fields. ----

    /// <summary>Closed journal outcomes with their lesson lines (D79/D82) — the measured facts the loop
    /// grows from.</summary>
    public const string ClosedOutcomes = "closed_outcomes";

    /// <summary>Remaining fork budget, so the seat rations itself (D82).</summary>
    public const string ForkBudgetRemaining = "fork_budget_remaining";

    // ---- The evidence-prior seam's occupant (D91/§24.6). TREATMENT ARM ONLY. ----

    /// <summary>The Phase-4.5 signal digest: one line per signal (1y rank-IC, 5y rank-IC, trend flag).
    /// **This field is the D113 arm difference** — the control arm receives it disabled or placebo'd.</summary>
    public const string SignalDigest = "signal_digest";

    /// <summary>
    /// Every admissible field name.
    ///
    /// **What is deliberately ABSENT is as load-bearing as what is present.** There is no
    /// `proposal_score`, no `detectability_margin`, no `calibration_skill` — D110 R1 says the researcher
    /// never reads its own score, so a score added to the builder **fails here** rather than passing
    /// silently. That is the same closure shape as the leak check, applied to a different rail, and it is
    /// what `FX-ProposalScoreIsMechanical` asserts at 5.7.
    ///
    /// There is also no `ai_decision` or `ai_context_pack` field: rule 32's corollary bars feeding a prior
    /// AI decision back into a pack, which would route AI output straight into the thing that prices AI
    /// output.
    /// </summary>
    /// <remarks>
    /// **These seven fields ARE recipe cp-1.0** (wired at v1.9.70, finding 330). §23.1's fuller researcher
    /// read set — verdicts + separation states, monitor statuses with triggering signals, regime episodes,
    /// factor attribution, trials-ledger detail — is DEFERRED to the contestant-phase recipe bump, on the
    /// record rather than silently: factor attribution structurally cannot land earlier (French factors
    /// are Phase 6), and a recipe change mid-series is attributable through `recipe_version` where a
    /// quietly grown field list would not be.
    /// </remarks>
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        AsOf, RegimeLabel, DetectabilityFloorAnn, TrialsCount,
        ClosedOutcomes, ForkBudgetRemaining, SignalDigest,
    };
}

/// <summary>
/// A built context pack: exactly what an AI seat was shown (D80).
///
/// The pack is the AI analog of NFR-1 — "what did it see, exactly?" — which is why it is persisted as
/// BYTES with a hash rather than as a recipe that could be re-run. A recipe plus its inputs is not the
/// same claim: re-running it proves what the recipe does *today*, not what the model saw *then*.
/// </summary>
public sealed record ContextPack(
    string Seat,
    string? StrategyId,
    string AsOf,
    string Watermark,
    string RecipeVersion,
    IReadOnlyList<PackField> Fields,
    string PackJson,
    string PackHash,
    int TokenEstimate);
