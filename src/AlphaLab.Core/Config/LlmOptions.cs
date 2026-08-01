namespace AlphaLab.Core.Config;

/// <summary>Per-task model tier (CONFIG_REFERENCE <c>Llm.Tasks.{task}</c>, D46). The key is the task's
/// wire name — <see cref="AlphaLab.Core.Llm.AnalysisTaskNames"/> — so config and storage cannot drift.</summary>
public sealed class LlmTaskOptions
{
    /// <summary>The model string for this task. Re-pinned deliberately at Phase-5 prep with its date
    /// (v1.9.60): the previous value for the reasoning tasks was superseded and had been carried forward
    /// unreviewed through four passes.</summary>
    public string Model { get; set; } = "";
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
    public decimal MaxCostUsd { get; set; } = 1.00m;
    public int MaxCalls { get; set; } = 10;

    /// <summary>Daily token ceiling across all seats and tasks; 0 = no token ceiling (cost and calls
    /// still apply).</summary>
    public int MaxTokens { get; set; }
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
