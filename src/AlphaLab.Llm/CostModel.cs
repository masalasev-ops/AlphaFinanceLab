using AlphaLab.Core.Config;
using AlphaLab.Core.Llm;

namespace AlphaLab.Llm;

/// <summary>
/// Token→cost arithmetic (D24/D46). Pure and static: no HTTP, no clock, no store, so the budget's
/// behaviour is unit-testable without a provider — which is what lets `FR22_Budget_DegradesInOrder` and
/// the mocked-month test assert spend without spending anything.
///
/// Money is <c>decimal</c> throughout (D69). Rates are USD per MILLION tokens, so every computation
/// divides by 1_000_000 exactly once.
/// </summary>
public static class CostModel
{
    private const decimal PerMillion = 1_000_000m;

    /// <summary>Cost of an observed usage on a model, at the given rates.</summary>
    /// <param name="batched">Applies the Batches half price (D46). Scheduled reads are always batched;
    /// the interactive research-assistant path is not.</param>
    public static decimal Cost(
        int inputTokens,
        int outputTokens,
        int cacheReadTokens,
        int cacheWriteTokens,
        ModelPriceOptions price,
        decimal batchDiscountMultiplier,
        bool batched)
    {
        ArgumentNullException.ThrowIfNull(price);

        var discount = batched ? batchDiscountMultiplier : 1m;

        var input = inputTokens * price.InputPerMTok;
        var output = outputTokens * price.OutputPerMTok;
        var cacheRead = cacheReadTokens * price.InputPerMTok * price.CacheReadMultiplier;
        var cacheWrite = cacheWriteTokens * price.InputPerMTok * price.CacheWriteMultiplier;

        return (input + output + cacheRead + cacheWrite) * discount / PerMillion;
    }

    /// <summary>Build a <see cref="TokenUsage"/> with its cost already resolved.</summary>
    public static TokenUsage Usage(
        int inputTokens,
        int outputTokens,
        int cacheReadTokens,
        int cacheWriteTokens,
        ModelPriceOptions price,
        decimal batchDiscountMultiplier,
        bool batched)
        => new(
            inputTokens,
            outputTokens,
            cacheReadTokens,
            cacheWriteTokens,
            Cost(inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, price, batchDiscountMultiplier, batched));

    /// <summary>
    /// A PRE-FLIGHT estimate, used to refuse before a token is spent (rule 13, D24: the budget is
    /// enforced BEFORE any token is spent, not reconciled after).
    ///
    /// Deliberately CONSERVATIVE: it charges the whole prompt at the uncached input rate and assumes the
    /// full <paramref name="maxOutputTokens"/> is generated. It therefore over-estimates a cache hit and a
    /// short answer — which is the direction an estimate guarding a hard ceiling must err in. An estimate
    /// that under-charges lets the last call of the day cross the ceiling it was checked against.
    /// </summary>
    public static decimal Estimate(
        PromptLayers prompt,
        int maxOutputTokens,
        ModelPriceOptions price,
        decimal batchDiscountMultiplier,
        bool batched)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var inputTokens = EstimateTokens(prompt.CacheablePrefix) + EstimateTokens(prompt.Fresh);
        return Cost(inputTokens, maxOutputTokens, 0, 0, price, batchDiscountMultiplier, batched);
    }

    /// <summary>
    /// The TOKEN count the pre-flight estimate prices — the same quantity <see cref="Estimate"/> costs,
    /// exposed so the D24 token ceiling can be checked in the same pre-flight shape as the cost ceiling
    /// (finding 382). Input tokens plus the task's expected output; <paramref name="expectedOutputTokens"/>
    /// is D130's pre-registered seed, never the API ceiling.
    /// </summary>
    public static int EstimateTokenCount(PromptLayers prompt, int expectedOutputTokens)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return EstimateTokens(prompt.CacheablePrefix) + EstimateTokens(prompt.Fresh) + expectedOutputTokens;
    }

    /// <summary>
    /// Characters→tokens approximation for the pre-flight estimate ONLY.
    ///
    /// **This is never used to report or bill anything.** Reported usage always comes from the API
    /// response, which is authoritative; this exists so the ceiling can be checked before the call that
    /// would produce that response. ~3.5 chars/token is a deliberately low divisor (i.e. a high token
    /// count) for the same fail-safe reason <see cref="Estimate"/> is conservative.
    /// </summary>
    public static int EstimateTokens(string text) =>
        string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / 3.5);
}
