using AlphaLab.Core.Llm;
using AlphaLab.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker.Pipeline;

/// <summary>
/// Stage 3: the daily market-level regime brief (D46; MASTER §7).
///
/// Reads the day's budget-admitted news, asks for a structured brief, and lets the provider stack persist
/// it to <c>analysis_cache</c>. Everything cost-related already happened upstream — the D46 news budget
/// narrowed the text before a token existed, and the D24 ceiling decides whether the call happens at all —
/// so this class is deliberately thin: it composes a prompt and records an outcome.
///
/// **The sentiment score is NOT resurrected here.** D46's framing is superseded by D79–D82 (golden rule
/// 28): the brief is prose for a human — never a machine-readable number that anything scores or trades
/// on. **And NOT a pack field either (corrected v1.9.70, finding 335):** an earlier version of this
/// comment claimed the brief becomes "a pack field for the researcher from 5.4", but §23.1 says the brief
/// "neither depends on nor replaces the seats" and `PackWhitelist` deliberately has no such field — the
/// pack's regime input is the MECHANICAL PIT label, not model prose. Feeding one seat's output into
/// another seat's pack would also be the shape rule 32's corollary exists to keep suspicious.
/// </summary>
public sealed class RegimeBriefStage(
    IAnalysisProvider analysis,
    INewsProvider news,
    AlphaLabDbContext db,
    ILogger<RegimeBriefStage> logger) : IPostCommitStage
{
    /// <summary>L0 — the static instruction block. **Frozen text, and it must stay byte-stable**: it is
    /// the cached prefix, so an edit here is a cache miss for every request that follows it. Changing it
    /// is a prompt-version event, not a tidy-up.</summary>
    public const string StaticInstructions = """
        You are a market analyst producing a daily regime brief for a paper-trading research lab.
        Read the supplied news items and write a concise, factual summary of the market-level regime.

        Rules:
        - Report only what the supplied items support. Do not speculate beyond them.
        - No trading recommendations, no price targets, no buy/sell language.
        - Do not output a numeric sentiment score or any single summary number.
        - If the items are thin or uninformative, say so plainly rather than padding.

        Output: 3-6 sentences of plain prose. No preamble, no headings, no JSON.
        """;

    public async Task RunAsync(PipelineDayContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var articles = await news.GetAdmittedAsync(context.AsOf, ct).ConfigureAwait(false);
        if (articles.Count == 0)
        {
            // An empty admitted set is an honest no-read day: the budget found nothing relevant, or the
            // feed was quiet. Calling the model with nothing to read would spend tokens to be told so.
            logger.LogInformation("{AsOf}: no admitted news — regime brief skipped (no-read day).", context.AsOf);
            return;
        }

        var request = new AnalysisRequest(
            $"regime-brief:{context.AsOf}",
            AnalysisTask.RegimeBrief,
            new PromptLayers(StaticInstructions, LessonSet(), FreshBlock(context, articles)));

        // ONE batch per day (D46: scheduled + non-interactive ⇒ Batches, half price).
        var results = await analysis.RunBatchAsync([request], ct).ConfigureAwait(false);
        var result = results[0];

        switch (result.Outcome)
        {
            case AnalysisOutcome.Succeeded:
            case AnalysisOutcome.CacheHit:
                logger.LogInformation(
                    "{AsOf}: regime brief {Outcome} ({Articles} articles, {Cost:C4}).",
                    context.AsOf, result.Outcome, articles.Count, result.Usage.CostUsd);
                break;

            default:
                // Every other outcome is a NO-READ DAY, not a failure. Logged as information rather than
                // as an error because it is the D24 contract working, not something going wrong.
                logger.LogInformation(
                    "{AsOf}: regime brief unavailable ({Outcome}: {Detail}) — no-read day.",
                    context.AsOf, result.Outcome, result.Detail);
                break;
        }
    }

    /// <summary>L1 — the lesson set. Empty at 5.3: memory Option A makes it part of the frozen policy,
    /// updated only at fork points (D81 rule 5), and no seat is registered yet. The layer exists so the
    /// cache boundary is in its final position now — moving it later would invalidate every cached
    /// prefix, which is the one edit this layering exists to avoid.</summary>
    private static string LessonSet() => "";

    /// <summary>L2 — the day's fresh block. The only part charged at full rate, so it carries derived,
    /// compressed facts rather than anything raw (D80: raw series never enter a prompt).</summary>
    private string FreshBlock(PipelineDayContext context, IReadOnlyList<NewsArticle> articles)
    {
        var regime = db.RegimeLabels
            .AsNoTracking()
            .Where(r => r.AsOf == context.AsOf && r.RunKind == context.RunKindToken)
            .Select(r => r.Label)
            .FirstOrDefault();

        var lines = new List<string>
        {
            $"Date: {context.AsOf}",
            $"PIT regime label: {regime ?? "unavailable"}",
            $"Admitted news items: {articles.Count}",
            "",
        };

        foreach (var a in articles)
        {
            lines.Add($"- {a.Title}");
            if (a.Content is { Length: > 0 }) lines.Add($"  {a.Content}");
        }

        return string.Join("\n", lines);
    }
}
