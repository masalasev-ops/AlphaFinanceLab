using System.Globalization;
using System.Text.Json;
using AlphaLab.Core.Llm;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker.Ops;

/// <summary>The `analysis_hypotheses` request as the API enqueued it (mirrors `HypothesesRequest`).</summary>
public sealed record HypothesesJobRequest(
    long? ParentEntryId = null,
    string? ParentFinding = null,
    string? ParentAttributionRef = null,
    string? Topic = null);

/// <summary>The `analysis_brief` / `analysis_skeptic` request (mirrors `AnalysisActionRequest`).</summary>
public sealed record AnalysisActionJobRequest(string? StrategyId = null, string? Topic = null);

/// <summary>
/// The researcher seat's job executor (FR-23, D82) — one class for all three analysis kinds.
///
/// **The output is a journal entry, never an action.** A hypothesis lands `locked = 0`: the AI PROPOSES and
/// only the operator pre-registers (rule 30), so nothing here can create a candidate, register a trial, or
/// move an allocation. A brief and a skeptic review land as their own kinds, linked to what they are about
/// (D52) — a review that is not linked to the thing reviewed is an opinion with no subject.
///
/// One executor rather than three because the three differ only in their task, their prompt and their
/// journal kind. Three classes would have triplicated the enqueue/parse/persist spine, and the spine is
/// where the invariant lives.
/// </summary>
public sealed class ResearchJobExecutor(
    string kind,
    IServiceScopeFactory scopeFactory,
    ILogger<ResearchJobExecutor> logger) : IJobExecutor
{
    /// <summary>The three `jobs.kind` values this executor is registered under, paired with the
    /// <see cref="AnalysisTask"/> each dispatches and the `journal_entries.kind` each writes.</summary>
    public static readonly IReadOnlyList<string> Kinds =
        ["analysis_hypotheses", "analysis_brief", "analysis_skeptic"];

    public string Kind => kind;

    public async Task ExecuteAsync(JobRow job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AlphaLabDbContext>();
        var analysis = scope.ServiceProvider.GetRequiredService<IAnalysisProvider>();

        var asOf = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var (task, journalKind, layers, linkedEntryId, strategyId) = Compose(job, asOf);

        var results = await analysis
            .RunBatchAsync([new AnalysisRequest($"{kind}:{job.JobId}", task, layers)], ct)
            .ConfigureAwait(false);
        var result = results[0];

        if (result.Outcome is not (AnalysisOutcome.Succeeded or AnalysisOutcome.CacheHit))
        {
            // Fail closed with the reason (rule 10): the drainer marks the job 'failed' and the operator
            // sees WHY. Writing an empty journal entry instead would put an unattributed blank into the
            // D110 proposal stream — a gap that reads as the researcher having nothing to say.
            throw new InvalidOperationException(
                $"{kind}: the model was {result.Outcome} ({result.Detail ?? "no detail"}) — no journal entry written.");
        }

        db.JournalEntries.Add(new JournalEntryRow
        {
            CreatedOn = asOf,
            Kind = journalKind,
            Title = Title(job, asOf),
            BodyMd = result.RawOutput,
            StrategyId = strategyId,
            LinkedEntryId = linkedEntryId,
            // UNLOCKED, always. Locking is the operator's pre-registration act (D52/rule 30); a seat that
            // could lock its own hypothesis would be pre-registering itself, and the frozen claim would no
            // longer be a commitment made before the evidence.
            Locked = false,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation(
            "{Kind}: job {JobId} wrote an unlocked '{JournalKind}' entry ({Cost:C4}).",
            kind, job.JobId, journalKind, result.Usage.CostUsd);
    }

    /// <summary>Task, journal kind, prompt and links for this job's kind.</summary>
    private (AnalysisTask Task, string JournalKind, PromptLayers Layers, long? Linked, string? StrategyId)
        Compose(JobRow job, string asOf)
    {
        switch (kind)
        {
            case "analysis_hypotheses":
            {
                var req = Parse<HypothesesJobRequest>(job);
                var fresh = string.Join("\n", new[]
                {
                    $"Date: {asOf}",
                    $"Parent outcome entry: {req.ParentEntryId?.ToString(CultureInfo.InvariantCulture) ?? "-"}",
                    $"Parent finding: {req.ParentFinding ?? "-"}",
                    $"Parent attribution: {req.ParentAttributionRef ?? "-"}",
                    $"Topic: {req.Topic ?? "(none — the operator left it open)"}",
                });
                return (AnalysisTask.Hypotheses, "hypothesis",
                    new PromptLayers(HypothesesInstructions, "", fresh), req.ParentEntryId, null);
            }

            case "analysis_brief":
            {
                var req = Parse<AnalysisActionJobRequest>(job);
                return (AnalysisTask.ResearchBrief, "decision_note",
                    new PromptLayers(BriefInstructions, "", FreshFor(req, asOf)), null, req.StrategyId);
            }

            case "analysis_skeptic":
            {
                var req = Parse<AnalysisActionJobRequest>(job);
                return (AnalysisTask.Skeptic, "skeptic_review",
                    new PromptLayers(SkepticInstructions, "", FreshFor(req, asOf)), null, req.StrategyId);
            }

            default:
                throw new InvalidOperationException($"ResearchJobExecutor was registered for unknown kind '{kind}'.");
        }
    }

    private static string FreshFor(AnalysisActionJobRequest req, string asOf) => string.Join("\n",
    [
        $"Date: {asOf}",
        $"Strategy: {req.StrategyId ?? "(arena-level — no single strategy)"}",
        $"Topic: {req.Topic ?? "(none)"}",
    ]);

    private static T Parse<T>(JobRow job) =>
        JsonSerializer.Deserialize<T>(job.RequestJson ?? "")
        ?? throw new InvalidOperationException(
            $"jobs.request_json for job {job.JobId} does not deserialize to {typeof(T).Name} (fail closed).");

    private string Title(JobRow job, string asOf) => kind switch
    {
        "analysis_hypotheses" => $"Proposed hypothesis ({asOf}, job {job.JobId})",
        "analysis_brief" => $"Research brief ({asOf}, job {job.JobId})",
        _ => $"Skeptic review ({asOf}, job {job.JobId})",
    };

    // ---- L0 blocks. Frozen text: each is the cached prefix for its task, so an edit is a prompt-version
    // event and a cache miss for everything after it, not a tidy-up (D81 rule 2). ----

    /// <summary>D82's proposal contract, stated as instructions. It names the four pre-declared fields
    /// because a hypothesis missing any of them cannot be pre-registered — the operator would have to
    /// invent the missing part, and the claim would then no longer be the one the seat made.</summary>
    public const string HypothesesInstructions = """
        You are a research assistant proposing testable hypotheses for a paper-trading research lab.
        Every proposal must grow from the parent evidence supplied — an outcome, a finding, or an
        attribution row — and must say plainly which part of it the proposal rests on.

        A proposal states, explicitly:
        - the claim, in one sentence;
        - the confirm/refute metric it will be judged on;
        - the evidence window, in trading days;
        - the expected annualized effect size, as a number.

        Rules:
        - Propose at most three hypotheses. Fewer is better than padded.
        - Do not propose a variation of a live strategy: any change to a live strategy forks a new one and
          spends a trial, so a variation is a costly proposal, not a free one.
        - You are proposing, not deciding. A human pre-registers what is worth testing.
        - If the parent evidence does not support a proposal, say so and propose nothing.

        Output: markdown. One section per hypothesis, with the four fields above as labelled lines.
        """;

    public const string BriefInstructions = """
        You are a research assistant writing a brief for the operator of a paper-trading research lab.
        Summarize what the supplied context supports, factually and without recommendations.

        Rules:
        - Report only what the context supports. Do not speculate beyond it.
        - No trading recommendations, no price targets, no buy/sell language.
        - Do not output a numeric score of any kind.
        - If the context is thin, say so plainly rather than padding.

        Output: markdown prose, under 400 words.
        """;

    /// <summary>The skeptic's job is to ARGUE THE OTHER SIDE. Its instructions say so explicitly because a
    /// balanced review is the failure mode here: a review that finds the claim reasonable adds nothing the
    /// claim did not already assert.</summary>
    public const string SkepticInstructions = """
        You are a skeptical reviewer for a paper-trading research lab. Your job is to argue against the
        claim in front of you, as strongly as the evidence honestly allows.

        Address, specifically:
        - what would have to be true for this result to be luck rather than edge;
        - which of the lab's known biases (multiple testing, survivorship, look-ahead, cost optimism)
          this claim is most exposed to;
        - what measurement would most cheaply distinguish the claim from its null.

        Rules:
        - Do not hedge into balance. If the claim survives your best attack, say exactly which attack it
          survived and why — that is a stronger statement than "it seems reasonable".
        - No trading recommendations, no numeric scores.

        Output: markdown prose, under 400 words.
        """;
}
