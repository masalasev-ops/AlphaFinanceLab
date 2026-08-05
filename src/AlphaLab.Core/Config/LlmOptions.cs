namespace AlphaLab.Core.Config;

/// <summary>Per-task model tier (CONFIG_REFERENCE <c>Llm.Tasks.{task}</c>, D46). The key is the task's
/// wire name — <see cref="AlphaLab.Core.Llm.AnalysisTaskNames"/> — so config and storage cannot drift.</summary>
public sealed class LlmTaskOptions
{
    /// <summary>The model string for this task. Re-pinned deliberately at Phase-5 prep with its date
    /// (v1.9.60): the previous value for the reasoning tasks was superseded and had been carried forward
    /// unreviewed through four passes.</summary>
    public string Model { get; set; } = "";

    /// <summary>
    /// The EXPECTED output tokens for this task, feeding the PRE-FLIGHT estimate only (D130; finding 380).
    ///
    /// Until v1.9.92 the estimate assumed the full <c>MaxOutputTokens</c> ceiling (8192) as the output
    /// term — and output is the dominant cost term, so against the derived daily caps the guard refused
    /// calls the budget could comfortably afford: a LOCKOUT, not an over-estimate. The ceiling stays as
    /// the API's hard cap; THIS value is what the estimator reads.
    ///
    /// It is a PRE-REGISTERED ESTIMATE, neither authored nor derived (D130): the committed seeds are
    /// MODELLED, NOT MEASURED (provenance: the v1.9.91 design conversation — ~700 for the compact reads,
    /// ~1,500 for the researcher's long-form tasks), and the pre-registered recalibration trigger replaces
    /// each seed with a HIGH PERCENTILE (p90, never a mean — a mean lets half of calls breach the cap in
    /// aggregate) of the task's observed output in <c>analysis_cache</c> once N completed calls exist,
    /// where N = MaxCalls × the 21-session evaluation window (both existing numbers; finding-309 rule).
    ///
    /// 0 = no seed configured: the estimator falls back to the ceiling — the fail-conservative pre-v1.9.92
    /// behaviour, never a silent zero-cost estimate.
    /// </summary>
    public int ExpectedOutputTokens { get; set; }
}

/// <summary>The D46 news ingestion budget — the real token lever, enforced BEFORE any token is spent
/// (rule 13). Built at checkpoint 5.2; the keys are bound here so 5.1's options object is complete.</summary>
public sealed class NewsBudgetOptions
{
    public int MaxArticlesPerRead { get; set; } = 25;
    public int MaxCharsPerArticle { get; set; } = 2000;
    public string DedupeBy { get; set; } = "title_hash";
}

/// <summary>
/// The D24 hard daily ceiling. Three dimensions, all enforced pre-flight.
///
/// <see cref="MaxTokens"/> closes finding 320: D24 and MASTER §7 both describe the cap as
/// "tokens/calls/cost" and <c>llm_budget_log</c> has carried a <c>tokens</c> column since the schema was
/// written, but there was no key — so the documented third ceiling had no knob and the log column had no
/// enforcer. Cost *nearly* subsumes tokens, and the exception is exactly what v1.9.60 did: a per-task tier
/// change moves the tokens-per-dollar ratio, so a cost ceiling alone silently changes how much the lab
/// reads whenever a model is re-pinned. 0 disables the ceiling.
/// </summary>
public sealed class LlmDailyBudgetOptions
{
    /// <summary>DERIVED (D130): round(AnnualBudgetUsd × (1 − 0.15) / 252 × 1.15, 2) — the committed
    /// share spread over trading days with a 15% intraday overshoot allowance. 0.39 at the authored 100.
    /// Never hand-edit: FX-BudgetDerivation recomputes the formula from the committed appsettings and
    /// fails on a divergence. The caps assume a calibrated pre-flight estimator (D130; finding 380) —
    /// until it is calibrated, the binding constraint is the estimator, not the budget.</summary>
    public decimal MaxCostUsd { get; set; } = 0.39m;

    public int MaxCalls { get; set; } = 10;

    /// <summary>Daily token ceiling across all seats and tasks; 0 = no token ceiling (cost and calls
    /// still apply). DERIVED (D130, closing finding 320's open knob): floor(MaxCostUsd / (the mean
    /// uncached input rate across the configured pricing table / 1e6)) = 130,000 at the authored 100.
    /// Overshoot note (finding 382): this guard is checked as state ≥ cap (backward-looking), unlike the
    /// cost guard's state + estimate &gt; cap, so it admits one call past the limit — aligning it with the
    /// pre-flight shape is a named Phase 6 item, not changed in the v1.9.92 config pass.</summary>
    public int MaxTokens { get; set; } = 130_000;
}

/// <summary>
/// Per-model token rates in USD per MILLION tokens (CONFIG_REFERENCE <c>Llm.Pricing.{model}</c>).
///
/// **These are CONFIG, not constants in code, deliberately.** They are vendor facts with a date, and
/// INTEGRATIONS' standing rule is that provider facts are implemented against a recorded source rather
/// than from memory, because that memory goes stale. Putting them in config means a rate change is an
/// operator edit against a published price list — visible, dated, and reversible — instead of a code
/// change nobody reviews as a pricing decision. It also means the D24 ceiling keeps meaning the same
/// thing after a re-pin: cost is computed from the rate actually in force, not one compiled in months ago.
/// </summary>
public sealed class ModelPriceOptions
{
    /// <summary>Uncached input, USD per 1M tokens.</summary>
    public decimal InputPerMTok { get; set; }

    /// <summary>Output, USD per 1M tokens.</summary>
    public decimal OutputPerMTok { get; set; }

    /// <summary>Cache READ multiplier against the input rate (~0.1×) — the reason layering pays.</summary>
    public decimal CacheReadMultiplier { get; set; } = 0.1m;

    /// <summary>Cache WRITE multiplier against the input rate (~1.25× at the 5-minute TTL). Charged on
    /// the first call of a prefix, which is why a silent cache invalidator costs MORE than not caching.</summary>
    public decimal CacheWriteMultiplier { get; set; } = 1.25m;
}

/// <summary>
/// LLM configuration (CONFIG_REFERENCE "Llm"; D24/D46).
///
/// Follows the …Options convention: <c>SectionName</c> plus mutable get/set defaults mirroring
/// CONFIG_REFERENCE.
/// </summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>
    /// THE ONE AUTHORED SPEND NUMBER (D130, amends D24): the operator's annual LLM budget in USD. Every
    /// other spend cap is DERIVED from it and recomputed by FX-BudgetDerivation — the derivation (reserve
    /// 0.15; committed × 0.60 to the contestant, × 0.40 to the researcher; /252 daily, /12 monthly;
    /// × 1.15 global-daily overshoot allowance) is the decision's structure, changeable only by a row
    /// amending D130, never a config edit (rule 25). Full arithmetic: CONFIG_REFERENCE, the D130 block.
    /// </summary>
    public decimal AnnualBudgetUsd { get; set; } = 100m;

    /// <summary>
    /// The documented degradation order (D24): held names first, then whatever is cached, then a neutral
    /// fallback for the overflow. **Never a blackout** — an over-budget day still answers for the
    /// positions that matter, which is the whole point of an ordered degradation rather than a cut-off.
    ///
    /// A constant rather than a property initialiser because of **finding 301**: the configuration binder
    /// ADDS to a pre-populated collection instead of replacing it, so a property initialised to the three
    /// defaults and configured to <c>["cached"]</c> yields four entries with the two the operator tried to
    /// REMOVE still present — and, worse here than for the Signal Library's horizons, still *ahead* in
    /// priority order. Leaving the property empty and resolving the default explicitly removes the trap.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultDegradationOrder =
        ["held_positions", "cached", "neutral_fallback"];

    /// <summary>Per-task model tiers, keyed by the task's wire name.</summary>
    public Dictionary<string, LlmTaskOptions> Tasks { get; set; } = [];

    /// <summary>Scheduled reads go through the Message Batches API (half price, D46). False forces the
    /// synchronous path — for local debugging only; it doubles the cost of every scheduled read.</summary>
    public bool UseBatchesApiForScheduled { get; set; } = true;

    /// <summary>Mark the L0 static block with <c>cache_control</c> so only the day's fresh block is
    /// charged at full rate.</summary>
    public bool PromptCacheStaticBlock { get; set; } = true;

    public NewsBudgetOptions NewsBudget { get; set; } = new();
    public LlmDailyBudgetOptions DailyBudget { get; set; } = new();

    /// <summary>Token rates per model string. Keyed by the same value <c>Llm.Tasks.*.Model</c> holds.</summary>
    public Dictionary<string, ModelPriceOptions> Pricing { get; set; } = [];

    /// <summary>The multiplier applied to every rate when a call goes through the Message Batches API —
    /// 0.5, the documented half price (D46), which is why every scheduled read is batched.</summary>
    public decimal BatchDiscountMultiplier { get; set; } = 0.5m;

    /// <summary>Configured degradation order. EMPTY means "use
    /// <see cref="DefaultDegradationOrder"/>" — read <see cref="ResolvedDegradationOrder"/>, never this
    /// directly (finding 301).</summary>
    public IReadOnlyList<string> DegradationOrder { get; set; } = [];

    /// <summary>1 = one market-level read/day (the start); 2 = a shortlist of ≤20 names, unlocked only
    /// after the contestant-vs-twin A/B earns it; 3 = whole-universe, structurally unreachable by the
    /// shortlist cap (D24).</summary>
    public int ScopeLevel { get; set; } = 1;

    /// <summary>The degradation order actually in force.</summary>
    public IReadOnlyList<string> ResolvedDegradationOrder =>
        DegradationOrder.Count > 0 ? DegradationOrder : DefaultDegradationOrder;

    /// <summary>The expected output tokens feeding a task's PRE-FLIGHT estimate (D130; finding 380).
    /// Falls back to <paramref name="ceiling"/> when the task carries no seed (0) or is unconfigured —
    /// the fail-conservative pre-v1.9.92 behaviour. The ceiling itself remains the API's hard cap; the
    /// estimator reads THIS.</summary>
    public int ExpectedOutputTokensFor(string taskWireName, int ceiling) =>
        Tasks.TryGetValue(taskWireName, out var t) && t.ExpectedOutputTokens > 0
            ? t.ExpectedOutputTokens
            : ceiling;

    /// <summary>The model pinned for a task. **Fails closed** (rule 10): an unconfigured task throws
    /// rather than falling back to some other task's model, because a silent substitution would be
    /// invisible in every downstream number and would make D104 artefact (d) — the model string that
    /// explains a behaviour change — a record of the wrong thing.</summary>
    public string ModelFor(string taskWireName) =>
        Tasks.TryGetValue(taskWireName, out var t) && t.Model is { Length: > 0 }
            ? t.Model
            : throw new InvalidOperationException(
                $"Llm.Tasks:{taskWireName}:Model is not configured. Every task's model tier is a deliberate, " +
                "dated build-time choice (CONFIG_REFERENCE Llm.Tasks) — it is never defaulted or inherited.");

    /// <summary>
    /// The rates for a model, resolving an ALIAS to the DATED SNAPSHOT the API reports (finding 328).
    ///
    /// **Exact match first, then the longest configured key that the served model starts with.** The
    /// Anthropic API resolves an alias (`claude-haiku-4-5`) to a dated snapshot
    /// (`claude-haiku-4-5-20251001`) and reports the SNAPSHOT in the response — so a pinned alias is
    /// never the string that comes back. Costing "the model that actually served the call" (D104 artefact
    /// (d)) and looking that string up by exact key are each correct on their own and together made every
    /// live call throw. Prefix resolution is what reconciles them: the snapshot is the alias plus a date
    /// suffix, so the alias is a prefix of it by construction.
    ///
    /// **Longest, not first**, because the families nest: `claude-opus-4-6` is a prefix of nothing here
    /// today, but `claude-opus-4` would be a prefix of `claude-opus-4-8-...`, and a shortest-match rule
    /// would price an Opus 4.8 call at Opus 4 rates silently. Longest-match cannot cross a family
    /// boundary that a configured key does not already draw.
    ///
    /// **STILL FAILS CLOSED** (rule 10), which is the property this must not lose: a model matching no
    /// configured prefix throws rather than costing zero. A zero cost is indistinguishable from a free
    /// cache hit in <c>llm_budget_log</c> and would make the D24 ceiling unenforceable exactly when a
    /// newly-pinned model started spending. The relaxation is from "exact key" to "known family", not
    /// from "known" to "anything".
    /// </summary>
    public ModelPriceOptions PricingFor(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        if (Pricing.TryGetValue(model, out var exact)) return exact;

        var alias = Pricing.Keys
            .Where(k => k.Length > 0 && model.StartsWith(k, StringComparison.Ordinal))
            .OrderByDescending(k => k.Length)
            .FirstOrDefault();

        return alias is not null
            ? Pricing[alias]
            : throw new InvalidOperationException(
                $"Llm.Pricing:{model} is not configured, and no configured rate is a prefix of it. A model " +
                "with no recorded rate cannot be costed, and an uncosted call would silently bypass the " +
                "D24 ceiling (CONFIG_REFERENCE Llm.Pricing). Note the API reports the DATED SNAPSHOT an " +
                "alias resolves to, so the key to configure is the alias (e.g. 'claude-haiku-4-5'), which " +
                "prices every snapshot of it.");
    }
}
