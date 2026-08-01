namespace AlphaLab.Core.Llm;

/// <summary>
/// One layered prompt (D80 §23.2). The layering is not decoration — it is what makes prompt caching pay:
/// L0 and L1 are byte-stable across calls so the provider marks the boundary with <c>cache_control</c> and
/// only <see cref="Fresh"/> is charged at full rate.
///
/// Caching is a PREFIX match, so ORDER IS LOAD-BEARING: anything volatile placed before the breakpoint
/// invalidates everything after it. A timestamp or a per-request id in <see cref="StaticInstructions"/>
/// silently costs the whole cache — which is why the volatile content has its own field rather than being
/// left to the caller's discipline.
/// </summary>
/// <param name="StaticInstructions">L0 — instructions + output schema. Frozen per prompt version.</param>
/// <param name="LessonSet">L1 — the memory Option-A lesson set; cache-stable between forks (D81 rule 5).</param>
/// <param name="Fresh">L2 — the day's fresh block (regime line, rows, summaries). Charged every call.</param>
public sealed record PromptLayers(string StaticInstructions, string LessonSet, string Fresh)
{
    /// <summary>The cacheable prefix — L0 + L1. Exposed because the cache breakpoint is placed after it
    /// and because a test asserting cache stability needs to hash exactly this.</summary>
    public string CacheablePrefix => LessonSet.Length == 0 ? StaticInstructions : StaticInstructions + "\n" + LessonSet;
}

/// <summary>What a model call cost. Persisted to <c>analysis_cache</c> and aggregated into
/// <c>llm_budget_log</c>.</summary>
/// <param name="InputTokens">Uncached input tokens — charged at full rate.</param>
/// <param name="OutputTokens">Generated tokens.</param>
/// <param name="CacheReadTokens">Served from cache (~0.1×). Zero across repeated identical-prefix calls
/// means a silent invalidator is at work, which is what <c>FR21_CacheHit_CostsZero</c> pins.</param>
/// <param name="CacheWriteTokens">Written to cache (~1.25×), i.e. the first call of a prefix.</param>
/// <param name="CostUsd">Computed cost. <c>decimal</c>, never double (D69 governs money).</param>
public sealed record TokenUsage(
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,
    int CacheWriteTokens,
    decimal CostUsd)
{
    public static readonly TokenUsage Zero = new(0, 0, 0, 0, 0m);

    /// <summary>Total tokens moved, for the <c>llm_budget_log.tokens</c> ceiling (finding 320).</summary>
    public int TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;
}

/// <summary>One unit of model work. <see cref="CustomId"/> is the batch correlation key — results come
/// back in ANY order, so it is required rather than optional (INTEGRATIONS §5).</summary>
public sealed record AnalysisRequest(string CustomId, AnalysisTask Task, PromptLayers Prompt);

/// <summary>Why a call produced no usable answer. Every one of these is an honest outcome, never an
/// exception path: a no-read day is the D24 degradation contract, not a failure.</summary>
public enum AnalysisOutcome
{
    /// <summary>The model answered and the answer parsed.</summary>
    Succeeded,

    /// <summary>Served from <c>analysis_cache</c> — zero tokens spent (<c>FR21_CacheHit_CostsZero</c>).</summary>
    CacheHit,

    /// <summary>The D24 budget was exhausted BEFORE any token was spent. The read degrades per
    /// <c>Llm.DegradationOrder</c>; the contestant seat abstains rather than padding (D80).</summary>
    BudgetExhausted,

    /// <summary>Safety classifiers declined. On the current tier this is an HTTP 200 with
    /// <c>stop_reason: "refusal"</c> and empty or partial content — NOT an error status, which is why it
    /// has to be a first-class outcome instead of being discovered while reading <c>content[0]</c>.</summary>
    Refused,

    /// <summary>The batch did not return in time, or the provider failed after retries. A late batch is a
    /// no-read day and never blocks the pipeline (D53 Stage 3; forward-only makes late arrival safe).</summary>
    Unavailable,
}

/// <summary>The result of one <see cref="AnalysisRequest"/>.</summary>
/// <param name="CustomId">Correlates to the request. Never positional (INTEGRATIONS §5).</param>
/// <param name="Outcome">What happened. Check this before reading <see cref="RawOutput"/>.</param>
/// <param name="RawOutput">The RAW model output — D104 artefact (b), persisted verbatim so a misparse is
/// detectable later. Empty unless <see cref="Outcome"/> is Succeeded or CacheHit.</param>
/// <param name="Usage">What it cost. <see cref="TokenUsage.Zero"/> for a cache hit or an unspent refusal.</param>
/// <param name="ModelVersion">D104 artefact (d) — the model string that actually served the call.</param>
/// <param name="Detail">Human-readable reason for a non-success outcome (the refusal category, the
/// exhausted budget). Never parsed by anything; a rationale string is not evidence (§23.8.4).</param>
public sealed record AnalysisResult(
    string CustomId,
    AnalysisOutcome Outcome,
    string RawOutput,
    TokenUsage Usage,
    string ModelVersion,
    string? Detail = null);

/// <summary>
/// The LLM seam (FR-21). **Forward-only by construction (D16, rule 13): the replay composition root
/// registers no implementation of this interface**, so a replay cannot reach a model even by mistake —
/// compile-time absence is preferred to a runtime guard (`FR21_Replay_HasNoAnalysisPath`).
///
/// Lives in <c>AlphaLab.Core</c>, not beside its Anthropic implementation, for the same reason
/// <c>ISignal</c> does (finding 295): the <c>ci.ps1</c> reference graph makes AlphaLab.Llm and
/// AlphaLab.Data siblings, and both ends of this seam need the contract.
/// </summary>
public interface IAnalysisProvider
{
    /// <summary>Run a day's requests as ONE batch (D46: scheduled + non-interactive ⇒ Batches, half
    /// price). Results are keyed by <see cref="AnalysisRequest.CustomId"/> and may arrive in any order.
    /// The budget is enforced BEFORE any token is spent (rule 13).</summary>
    Task<IReadOnlyList<AnalysisResult>> RunBatchAsync(
        IReadOnlyList<AnalysisRequest> requests, CancellationToken ct = default);

    /// <summary>Run ONE request interactively (the research-assistant path, `POST /v1/messages`).
    /// Same budget rule.</summary>
    Task<AnalysisResult> RunAsync(AnalysisRequest request, CancellationToken ct = default);
}

/// <summary>A cached answer (<c>analysis_cache</c>), keyed (prompt_hash, model, as_of) per FR-21.</summary>
public sealed record CachedAnalysis(string RawOutput, TokenUsage OriginalUsage);

/// <summary>
/// The <c>analysis_cache</c> port (FR-21). A hit spends **nothing** — the contract
/// <c>FR21_CacheHit_CostsZero</c> pins — which is why the cache is consulted BEFORE the budget rather
/// than after: a cached day must not consume headroom it does not need.
///
/// A port rather than a direct store reference because AlphaLab.Llm may not reference AlphaLab.Data
/// (the `ci.ps1` reference graph); AlphaLab.Data implements it.
/// </summary>
public interface IAnalysisCache
{
    Task<CachedAnalysis?> TryGetAsync(string promptHash, string model, string asOf, CancellationToken ct = default);

    Task PutAsync(
        string promptHash, string model, string asOf, AnalysisTask task,
        string rawOutput, TokenUsage usage, CancellationToken ct = default);
}

/// <summary>Today's spend so far, against the D24 ceilings.</summary>
public sealed record BudgetState(int Calls, int Tokens, decimal CostUsd)
{
    public static readonly BudgetState Empty = new(0, 0, 0m);
}

/// <summary>
/// The <c>llm_budget_log</c> port (D24). The budget is enforced **before any token is spent** (rule 13),
/// so this is read pre-flight rather than reconciled afterwards.
/// </summary>
public interface ILlmBudgetLedger
{
    /// <summary>What has been spent on <paramref name="asOf"/> so far.</summary>
    Task<BudgetState> GetAsync(string asOf, CancellationToken ct = default);

    /// <summary>Add an actual spend. <paramref name="degraded"/> stamps <c>llm_budget_log.degraded</c> so
    /// an over-budget day is visible afterwards as a recorded fact rather than inferred from a gap.</summary>
    Task RecordAsync(
        string asOf, int calls, TokenUsage usage, bool degraded, string? note, CancellationToken ct = default);
}

/// <summary>One article as it reaches the budget (D46). The budget narrows this set before any token is
/// spent; only what survives is persisted to <c>news_items</c>.</summary>
/// <param name="Title">Used for the title-hash dedupe key.</param>
/// <param name="Content">Extracted body, truncated to <c>Llm.NewsBudget.MaxCharsPerArticle</c>.</param>
/// <param name="Source">Publisher, for provenance.</param>
/// <param name="Symbols">Tickers the article mentions — the relevance filter's universe input.</param>
/// <param name="Tags">Macro tags — the relevance filter's other arm.</param>
public sealed record NewsArticle(
    string Title,
    string Content,
    string? Source,
    IReadOnlyList<string> Symbols,
    IReadOnlyList<string> Tags);

/// <summary>One article that survived the D46 budget, with the dedupe key it was admitted under and how
/// many characters truncation removed (0 when it fitted).</summary>
public sealed record AdmittedArticle(NewsArticle Article, string TitleHash, int TruncatedChars);

/// <summary>
/// Persistence port for the articles that survived the budget (<c>news_items</c>).
///
/// In Core, not beside the budget that produces the rows nor beside the store that writes them, because
/// the `ci.ps1` reference graph makes AlphaLab.Llm and AlphaLab.Data siblings — **neither may reference
/// the other**, so a contract they share has exactly one legal home. Same shape as
/// <see cref="IAnalysisCache"/>.
/// </summary>
public interface IAdmittedNewsStore
{
    /// <summary>Persist the day's admitted articles. Idempotent: re-running a day must not duplicate, and
    /// the <c>ux_news_items_as_of_title</c> unique index makes that structural rather than trusted.</summary>
    Task SaveAsync(string asOf, IReadOnlyList<AdmittedArticle> admitted, CancellationToken ct = default);
}

/// <summary>
/// The news seam (FR-22). **The D46 budget is enforced HERE, before any token is spent** (rule 13) —
/// relevance filter → title-hash dedupe → 25-article cap → 2,000-char truncation. Implemented at
/// checkpoint 5.2 as a decorator over the EODHD fetch, so the budget cannot be bypassed by calling the
/// raw provider directly.
/// </summary>
public interface INewsProvider
{
    /// <summary>The day's admitted articles — already filtered, deduped, capped and truncated.</summary>
    Task<IReadOnlyList<NewsArticle>> GetAdmittedAsync(string asOf, CancellationToken ct = default);
}
