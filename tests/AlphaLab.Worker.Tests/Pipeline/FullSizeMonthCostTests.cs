using AlphaLab.Core.Config;
using AlphaLab.Core.Llm;
using AlphaLab.Llm;
using AlphaLab.Worker.Pipeline;

namespace AlphaLab.Worker.Tests.Pipeline;

/// <summary>
/// The Phase-5 DoD's "a full month of daily reads under budget", sized from the DOCUMENTED CAPS rather
/// than from a fixture's placeholder token counts (checkpoint 5.8).
///
/// **Why this exists beside `MockedMonth_OfDailyReads_StaysUnderBudget`.** That test proves the budget
/// machinery works across a month; its scripted usage is 100 in / 50 out per day, which is a stub, not a
/// day. Recording ITS total in the gate box would put a number in the corpus that reads as a forecast and
/// is off by three orders of magnitude. So the figure the DoD records is computed here, from the caps the
/// system actually enforces: the frozen L0 block, `Llm.NewsBudget`'s 25 articles × 2,000 chars, and the
/// pinned Opus tier at the Batches half price.
///
/// It is a MODELLED figure, not a measurement — the character→token divisor is the conservative pre-flight
/// approximation, and the live confirmation is what the smoke test is for. Labelled as such wherever it is
/// quoted.
/// </summary>
public class FullSizeMonthCostTests
{
    private const int TradingDaysPerMonth = 21;

    /// <summary>3–6 sentences of prose, generously. Opus 5 counts thinking against the same ceiling, so
    /// this is deliberately well above the visible answer's length.</summary>
    private const int AssumedOutputTokens = 800;

    private static (int InputTokens, decimal DayCost) FullSizeDay()
    {
        var budget = new NewsBudgetOptions();          // the committed 25 / 2,000 caps
        var price = new ModelPriceOptions { InputPerMTok = 5m, OutputPerMTok = 25m };   // the pinned Opus tier

        // L2 at its cap: every admitted article at full truncation length, plus a title line each.
        var fresh = string.Concat(Enumerable.Repeat(
            new string('x', budget.MaxCharsPerArticle) + "\n- a headline of ordinary length\n",
            budget.MaxArticlesPerRead));

        var prompt = new PromptLayers(RegimeBriefStage.StaticInstructions, "", fresh);
        var inputTokens = CostModel.EstimateTokens(prompt.CacheablePrefix) + CostModel.EstimateTokens(prompt.Fresh);

        var cost = CostModel.Cost(
            inputTokens, AssumedOutputTokens, 0, 0, price,
            batchDiscountMultiplier: 0.5m, batched: true);

        return (inputTokens, cost);
    }

    [Fact]
    public void AFullSizeMonth_AtTheDocumentedCaps_StaysUnderTheDailyCeilingEveryDay()
    {
        var (inputTokens, dayCost) = FullSizeDay();
        var monthCost = dayCost * TradingDaysPerMonth;
        var ceiling = new LlmDailyBudgetOptions().MaxCostUsd;   // the committed $1.00/day

        // The DoD's figure, recorded in PROGRESS and the changelog. Asserted as a RANGE rather than an
        // equality so a re-pin or a cap change moves it visibly instead of failing on a rounding digit.
        Assert.InRange(dayCost, 0.03m, 0.08m);
        Assert.InRange(monthCost, 0.70m, 1.60m);

        // The invariant that matters operationally: the ceiling is DAILY, so a month is only "under
        // budget" if every single day is — a month total below 21× the ceiling would also be satisfied by
        // twenty cheap days and one that blew through it.
        Assert.True(dayCost < ceiling,
            $"a full-size day costs {dayCost:C4}, at or above the {ceiling:C2} daily ceiling");

        // And the cap is what bounds it: ~14k tokens of news is the whole of L2, so a day cannot grow
        // without the news budget being widened first (D46 — the budget narrows the text before a token
        // exists).
        Assert.InRange(inputTokens, 12_000, 20_000);
    }

    /// <summary>
    /// **finding 325 (recorded, deliberately NOT fixed): every L0 block is below the 512-token prompt-cache
    /// minimum, so the `cache_control` breakpoint currently caches nothing.**
    ///
    /// The 5.0 prep recorded *"L0 is ~1,500 tokens, so the static block caches"*. Measured, the regime
    /// brief's L0 is 161 tokens and the three researcher blocks are 276 / 211 / 136 — the estimate was
    /// wrong by an order of magnitude, in the direction that makes a stated economy inert.
    ///
    /// **The consequence is smaller than it sounds, and stating why is the point.** The breakpoint is
    /// harmless, not wrong: it costs nothing and starts working the moment a block grows past the
    /// threshold. `FR21_CacheHit_CostsZero` is unaffected because it exercises `analysis_cache` — the
    /// lab's own store-level cache, a different mechanism that already makes a repeated day free. What is
    /// actually lost is the ~10% input discount on the frozen prefix of a FIRST read, which on the figure
    /// above is under half a cent a day.
    ///
    /// **Not fixed here, deliberately.** The fix would be padding a frozen prompt to clear a vendor
    /// threshold — a change to instruction text that governs what the seats are told, made for a pricing
    /// reason. That is a decision, not a checkpoint-8 tidy-up, and D81 rule 2 makes an L0 edit a
    /// prompt-version event. Recorded with its measurement so the choice is available rather than lost.
    ///
    /// This test FAILS UPWARD: if a block later grows past 512, it fails and says so, because at that
    /// point the recorded month figure is an over-estimate and the finding is closed by events.
    /// </summary>
    [Fact]
    public void Finding325_EveryL0Block_IsBelowThePromptCacheMinimum()
    {
        var blocks = new (string Name, string Text)[]
        {
            (nameof(RegimeBriefStage.StaticInstructions), RegimeBriefStage.StaticInstructions),
            ("HypothesesInstructions", Worker.Ops.ResearchJobExecutor.HypothesesInstructions),
            ("SkepticInstructions", Worker.Ops.ResearchJobExecutor.SkepticInstructions),
            ("BriefInstructions", Worker.Ops.ResearchJobExecutor.BriefInstructions),
        };

        foreach (var (name, text) in blocks)
        {
            var tokens = CostModel.EstimateTokens(text);
            Assert.True(tokens < 512,
                $"{name} is now {tokens} tokens — at or ABOVE the 512-token prompt-cache minimum. The " +
                "cache breakpoint now bites, finding 325 is closed by events, and the recorded full-size " +
                "month figure is an over-estimate that should be revised downward.");
        }
    }
}
