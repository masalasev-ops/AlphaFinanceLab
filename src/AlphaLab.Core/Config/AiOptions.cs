namespace AlphaLab.Core.Config;

/// <summary>The contestant seat's caps (D81). Bound now because <see cref="AiOptions"/> is one object;
/// the seat itself is Phase 6.</summary>
public sealed class ContestantSeatOptions
{
    public string Model { get; set; } = "llm-a";

    /// <summary>The deterministic local pre-filter (D127) hands the model at most this many names, which
    /// is what keeps Level-3 whole-universe scoring unreachable (D24). DERIVATION (D130, 3-C): the
    /// budget-affordable N at the derived daily budget is min-capped by the D24 scope rail —
    /// floor(DailyBudgetUsd / cost-per-name-per-day) ≈ 416 at the authored 100 with the MODELLED
    /// per-name tokens (72 in / 24 out, the §23.2 layer table at 25 names; opus rates, batched), so the
    /// STRUCTURAL scope cap (25) binds, not the budget — the budget would start binding only below
    /// roughly $6/yr. FROZEN per contestant generation: computed once at candidate creation and written
    /// into that generation's config_json (never re-read per session) — a frozen param, so it must NOT
    /// float daily with budget or rates (FX-ShortlistSizeFrozenPerGeneration, Phase 6).</summary>
    public int ShortlistSize { get; set; } = 25;

    /// <summary>On exhaustion the contestant ABSTAINS — an empty score map, the funnel's honest "nothing
    /// scored" — never a padded or stale decision. DERIVED (D130): round(AnnualBudgetUsd × 0.85 × 0.60
    /// / 252, 2) = 0.20 at the authored 100; FX-BudgetDerivation recomputes it.</summary>
    public decimal DailyBudgetUsd { get; set; } = 0.20m;
}

/// <summary>The researcher seat's cap (D82).</summary>
public sealed class ResearcherSeatOptions
{
    public string Model { get; set; } = "llm-b";

    /// <summary>
    /// On exhaustion the researcher job simply queues — nothing is padded and nothing is stale.
    ///
    /// **D113 makes this a PAIRING constraint, not only a ceiling.** Both arms of the paper control draw
    /// on this budget, so exhaustion BETWEEN them would emit a treatment proposal with no control — an
    /// unpaired observation silently entering the margin series. Headroom is therefore checked for the
    /// PAIR before either arm dispatches: both propose, or neither does.
    ///
    /// DERIVED (D130): round(AnnualBudgetUsd × 0.85 × 0.40 / 12, 2) = 2.83 at the authored 100;
    /// FX-BudgetDerivation recomputes it.
    /// </summary>
    public decimal MonthlyBudgetUsd { get; set; } = 2.83m;
}

/// <summary>
/// The AI seats (D79–D82; CONFIG_REFERENCE "Ai").
///
/// Per-strategy frozen params — prompt hash, model id, shortlist size, memory option, the no-LLM twin's
/// scoring rule (D85) — live in <c>strategies.config_json</c>, NOT here (key rule 1).
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>The context-pack recipe id, a frozen param (D80): a change forks candidates rather than
    /// quietly re-shaping what an existing one sees.</summary>
    public string PackRecipeVersion { get; set; } = "cp-1.1";

    public ContestantSeatOptions Contestant { get; set; } = new();

    public ResearcherSeatOptions Researcher { get; set; } = new();
}
