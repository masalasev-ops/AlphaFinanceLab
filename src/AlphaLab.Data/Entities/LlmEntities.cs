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
