namespace AlphaLab.Data.Entities;

/// <summary>
/// analysis_cache — the FR-21 read cache, keyed (prompt_hash, model, as_of). Composite TEXT PK, so no
/// autoincrement question arises (rule 14).
///
/// **Forward-only by construction (D16):** a replay run never writes here, because the replay composition
/// root registers no <c>IAnalysisProvider</c> at all — the absence is structural, not a guard
/// (`FR21_Replay_HasNoAnalysisPath`).
///
/// <c>cost_usd</c> is <b>REAL</b>, not the D69 decimal→TEXT treatment, per SCHEMA: D69 governs LEDGER
/// money — cash, fills, P&amp;L, anything a position is valued at — and an operational spend metric is not
/// that. The computation is still done in <c>decimal</c> (see <c>TokenUsage</c>) so a day's calls do not
/// accumulate float error; only the persisted value is REAL.
/// </summary>
public sealed class AnalysisCacheRow
{
    /// <summary>SHA-256 over all three prompt layers — a change to any of them must miss the cache.</summary>
    public string PromptHash { get; set; } = default!;

    /// <summary>The model that SERVED the call, not the one configured. Part of the key: the same prompt
    /// on a different model is a different answer, so a re-pin (v1.9.60) correctly misses rather than
    /// serving an answer the current tier never produced.</summary>
    public string Model { get; set; } = default!;

    public string AsOf { get; set; } = default!;

    /// <summary>The <c>AnalysisTask</c> wire name. CHECK-constrained (finding 319).</summary>
    public string Task { get; set; } = default!;

    /// <summary>The RAW model output (D104 artefact (b)) — stored verbatim, never the parsed result, so a
    /// misparse remains detectable after the fact.</summary>
    public string OutputJson { get; set; } = default!;

    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public double? CostUsd { get; set; }
}

/// <summary>
/// llm_budget_log — one row per day (D24). <c>as_of</c> TEXT PK; no autoincrement question.
///
/// <c>degraded</c> is what makes an over-budget day a recorded FACT rather than something inferred from a
/// gap in <c>analysis_cache</c> — the difference between "we chose not to read" and "we could not tell".
/// </summary>
public sealed class LlmBudgetLogRow
{
    public string AsOf { get; set; } = default!;
    public int Calls { get; set; }

    /// <summary>Total tokens moved. The column pre-dated its ceiling; <c>Llm.DailyBudget.MaxTokens</c>
    /// gives it an enforcer (finding 320).</summary>
    public int Tokens { get; set; }

    public double CostUsd { get; set; }

    /// <summary>1 once any request on this day was refused or served by the degradation order.</summary>
    public int Degraded { get; set; }

    public string? Note { get; set; }
}

/// <summary>
/// news_items — POST-BUDGET only (D46). Nothing that the relevance filter, the title-hash dedupe, the
/// 25-article cap or the 2,000-char truncation removed is ever persisted, so the table is the record of
/// what the budget ADMITTED, not of what the feed returned.
///
/// UNIQUE (as_of, title_hash) is the dedupe key made structural: a duplicate cannot be stored even if the
/// in-memory dedupe were bypassed.
/// </summary>
public sealed class NewsItemRow
{
    /// <summary>Plain rowid alias — no AUTOINCREMENT (rule 14 hand-edit).</summary>
    public long NewsId { get; set; }

    public string AsOf { get; set; } = default!;
    public string TitleHash { get; set; } = default!;
    public string? Title { get; set; }
    public string? Source { get; set; }
    public string? SymbolsJson { get; set; }

    /// <summary>How many characters truncation removed — 0 when the article fitted. Kept so the budget's
    /// effect on what the model saw is measurable rather than assumed.</summary>
    public int? TruncatedChars { get; set; }
}

/// <summary>
/// ai_context_packs — exactly what an AI seat was shown, hashed and watermarked (D80; M8).
///
/// **Stored as BYTES, not as a recipe.** D104 artefact (a) is specific about this: never a summary,
/// never "the recipe plus the inputs". A recipe plus its inputs is a different claim — re-running it
/// proves what the recipe does TODAY, not what the model saw THEN, and the whole point of the pack is to
/// make re-execution unnecessary.
///
/// Append-only, and off the statistical hot path: no column here feeds any metric, verdict, threshold or
/// population (golden rule 32).
/// </summary>
public sealed class AiContextPackRow
{
    /// <summary>Plain rowid alias — no AUTOINCREMENT (rule 14 hand-edit).</summary>
    public long PackId { get; set; }
    public string Seat { get; set; } = default!;
    /// <summary>Contestant only; NULL for researcher/advisor.</summary>
    public string? StrategyId { get; set; }
    public string AsOf { get; set; } = default!;
    /// <summary>The D40 data watermark the pack was built at.</summary>
    public string Watermark { get; set; } = default!;
    /// <summary>`Ai.PackRecipeVersion` at build time — a frozen param (D81 rule 2), so a recipe change
    /// forks a candidate exactly like a prompt or model change.</summary>
    public string RecipeVersion { get; set; } = default!;
    /// <summary>Derived features only — NEVER raw series (D80).</summary>
    public string PackJson { get; set; } = default!;
    /// <summary>SHA-256 of pack_json; the audit key that ties a decision to the exact pack seen.</summary>
    public string PackHash { get; set; } = default!;
    public int TokenEstimate { get; set; }
    public string CreatedAt { get; set; } = default!;
}

/// <summary>
/// ai_decisions — the persisted output IS the decision (D81 rule 1; M8).
///
/// The funnel reads THIS ROW, never the API, and every re-run replays it. That is how a nondeterministic
/// sampler satisfies NFR-1: determinism for AI-seated strategies reads
/// **f(inputs, watermark, seeds, stored AI outputs)**.
///
/// `cost_usd` is decimal-as-TEXT here (D69) where `analysis_cache.cost_usd` is REAL — the schema's own
/// split, and not an inconsistency: this row is part of a decision record that a ledger claim can rest on.
/// </summary>
public sealed class AiDecisionRow
{
    /// <summary>Plain rowid alias — no AUTOINCREMENT (rule 14 hand-edit).</summary>
    public long DecisionId { get; set; }
    public string StrategyId { get; set; } = default!;
    public string AsOf { get; set; } = default!;
    /// <summary>Ties the decision to the exact ai_context_packs row seen (D104 artefact (a)).</summary>
    public string PackHash { get; set; } = default!;
    /// <summary>Frozen param (D81 rule 2); any change forks a candidate (rule 24).</summary>
    public string PromptVersion { get; set; } = default!;
    /// <summary>D104 artefact (d) — the model that SERVED the call.</summary>
    public string ModelVersion { get; set; } = default!;
    /// <summary>D104 artefact (b): the RAW model output, verbatim.</summary>
    public string OutputJson { get; set; } = default!;
    /// <summary>
    /// D104 artefact (c) — the parsed decision AND what the funnel actually did with it.
    ///
    /// **This is the artefact most easily dropped**, because (a) and (b) together prove what was asked
    /// and answered while neither shows what the arena DID. Without it a correct decision and a correct
    /// log can coexist with a wrong trade and nothing in the record would show it: a guardrail rejection,
    /// a sizing clamp or a cash constraint between the decision and the fill is exactly the gap it closes.
    /// NULL until a funnel consumes the decision (Phase 6) — the column exists now so the seam is built
    /// with it rather than around it.
    /// </summary>
    public string? AppliedJson { get; set; }
    /// <summary>D104 artefact (d), continued: the effort/thinking configuration. Named for what the
    /// pinned tier actually HAS — it has no temperature/top_p/top_k, so recording "sampling parameters"
    /// would be recording a field that does not exist.</summary>
    public string? SamplingJson { get; set; }
    public int TokensIn { get; set; }
    public int TokensOut { get; set; }
    /// <summary>decimal TEXT (D69), never REAL.</summary>
    public string CostUsd { get; set; } = default!;
    public string CreatedAt { get; set; } = default!;
}
