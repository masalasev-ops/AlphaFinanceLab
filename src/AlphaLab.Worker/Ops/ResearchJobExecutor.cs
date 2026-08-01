using System.Globalization;
using System.Text.Json;
using AlphaLab.Core.Config;
using AlphaLab.Core.Llm;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Evaluation.Ai;
using AlphaLab.Evaluation.Candidates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker.Ops;

/// <summary>The `analysis_hypotheses` request as the API enqueued it (mirrors `HypothesesRequest`).</summary>
public sealed record HypothesesJobRequest(
    long? ParentEntryId = null,
    string? ParentFinding = null,
    string? ParentAttributionRef = null,
    string? Topic = null,
    double? PriorProb = null);

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
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AlphaLabDbContext>();
        var analysis = sp.GetRequiredService<IAnalysisProvider>();

        var asOf = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (kind != "analysis_hypotheses")
        {
            await RunOneAsync(db, analysis, job, asOf, EvidencePriorMode.On, ct).ConfigureAwait(false);
            return;
        }

        // ---- D113: the paper control. Two arms, ONE job run. ----
        //
        // Same seat, same pack recipe, same floor, differenced ONLY on the evidence-prior seam:
        // treatment runs the digest, control runs it PLACEBO'd (shuffled grades of identical shape and
        // token count, so a measured difference cannot be an artefact of prompt length). Two identical
        // researchers would produce a margin difference of zero by construction and measure nothing.
        //
        // Same run, before any admission, is the other half: the floor resolves from CURRENT state, so an
        // admission between the arms would move it by one Bonferroni step and the difference would stop
        // being clean.
        var ai = sp.GetRequiredService<AiOptions>();
        var budget = new ResearcherSeatBudget(db, ai).Assess(asOf, EstimatedArmCostUsd);
        if (!budget.PairFits)
        {
            // BOTH arms abstain or NEITHER does. Dispatching the treatment alone would emit an unpaired
            // observation into the margin series - worse than no observation, because it looks like one.
            throw new InvalidOperationException(
                $"Researcher seat: the monthly budget cannot fund a PAIR of arms " +
                $"(spent {budget.SpentUsd:C4} of {budget.CapUsd:C4}, pair needs ~{budget.EstimatedPairUsd:C4}). " +
                "Both arms abstain - a treatment proposal without its control is an unpaired observation " +
                "silently entering the D110 margin series (D113). The job queues; nothing was written.");
        }

        // The floor ONCE, before either arm writes, and stamped on both: it is a property of the arena
        // rather than of the proposal, and reading it twice would risk two different numbers for a
        // quantity the whole comparison assumes is shared.
        var floorAnn = new DetectabilityGate(db, sp.GetRequiredService<GateOptions>()).ResolveCurrentFloor();

        await RunOneAsync(db, analysis, job, asOf, EvidencePriorMode.On, ct, floorAnn, "treatment").ConfigureAwait(false);
        await RunOneAsync(db, analysis, job, asOf, EvidencePriorMode.Placebo, ct, floorAnn, "control").ConfigureAwait(false);
    }

    /// <summary>A conservative per-arm estimate for the pairing check. Conservative deliberately: an
    /// UNDER-estimate here produces exactly the unpaired observation the check exists to prevent.</summary>
    public const decimal EstimatedArmCostUsd = 0.25m;

    /// <summary>
    /// One arm (or, for brief/skeptic, the whole job).
    ///
    /// <paramref name="arm"/> is null for the non-paired kinds and treatment/control for the D113 pair.
    /// The control entry is written like any other, unlocked and never admitted - which is precisely what
    /// makes it free of the trials tax: the tax is paid at ADMISSION, and a proposal that never seeks
    /// admission never pays it.
    /// </summary>
    private async Task RunOneAsync(
        AlphaLabDbContext db, IAnalysisProvider analysis, JobRow job, string asOf,
        EvidencePriorMode seam, CancellationToken ct, double? floorAnn = null, string? arm = null)
    {
        var (task, journalKind, layers, linkedEntryId, strategyId, priorProb) = Compose(job, asOf, seam);

        var results = await analysis
            .RunBatchAsync([new AnalysisRequest($"{kind}:{job.JobId}:{arm ?? "single"}", task, layers)], ct)
            .ConfigureAwait(false);
        var result = results[0];

        if (result.Outcome is not (AnalysisOutcome.Succeeded or AnalysisOutcome.CacheHit))
        {
            // Fail closed with the reason (rule 10): the drainer marks the job 'failed' and the operator
            // sees WHY. Writing an empty journal entry instead would put an unattributed blank into the
            // D110 proposal stream - a gap that reads as the researcher having nothing to say.
            throw new InvalidOperationException(
                $"{kind}: the model was {result.Outcome} ({result.Detail ?? "no detail"}) - no journal entry written.");
        }

        db.JournalEntries.Add(new JournalEntryRow
        {
            CreatedOn = asOf,
            Kind = journalKind,
            Title = Title(job, asOf, arm),
            BodyMd = result.RawOutput,
            StrategyId = strategyId,
            LinkedEntryId = linkedEntryId,
            // D110: the stated prior, and the floor AS AT ASSESSMENT (D113's amendment). Both stamped
            // here rather than at admission - a control proposal never reaches admission, so an
            // admission-time reading would leave it permanently unscorable.
            PriorProb = priorProb,
            DetectabilityFloorAnn = floorAnn,
            // UNLOCKED, always. Locking is the operator's pre-registration act (D52/rule 30); a seat that
            // could lock its own hypothesis would be pre-registering itself, and the frozen claim would no
            // longer be a commitment made before the evidence.
            Locked = false,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation(
            "{Kind}: job {JobId}{Arm} wrote an unlocked '{JournalKind}' entry (floor {Floor}, {Cost:C4}).",
            kind, job.JobId, arm is null ? "" : $" [{arm}]", journalKind,
            floorAnn?.ToString("P2", CultureInfo.InvariantCulture) ?? "n/a", result.Usage.CostUsd);
    }

    /// <summary>Task, journal kind, prompt and links for this job's kind.</summary>
    private (AnalysisTask Task, string JournalKind, PromptLayers Layers, long? Linked, string? StrategyId,
             double? PriorProb)
        Compose(JobRow job, string asOf, EvidencePriorMode seam)
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
                    // The seam MODE is stated in the prompt, not only in the wiring. The arms must be
                    // distinguishable afterwards from the recorded prompt alone, and an undeclared placebo
                    // would leave two L2 blocks differing by content nobody could attribute.
                    $"Evidence-prior seam: {seam.ToString().ToLowerInvariant()}",
                });
                return (AnalysisTask.Hypotheses, "hypothesis",
                    new PromptLayers(HypothesesInstructions, "", fresh), req.ParentEntryId, null, req.PriorProb);
            }

            case "analysis_brief":
            {
                var req = Parse<AnalysisActionJobRequest>(job);
                return (AnalysisTask.ResearchBrief, "decision_note",
                    new PromptLayers(BriefInstructions, "", FreshFor(req, asOf)), null, req.StrategyId, null);
            }

            case "analysis_skeptic":
            {
                var req = Parse<AnalysisActionJobRequest>(job);
                return (AnalysisTask.Skeptic, "skeptic_review",
                    new PromptLayers(SkepticInstructions, "", FreshFor(req, asOf)), null, req.StrategyId, null);
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

    /// <summary>The arm rides in the TITLE because it must survive into the journal: a margin series
    /// computed from entries that do not say which arm produced them is a series of unattributable
    /// numbers.</summary>
    private string Title(JobRow job, string asOf, string? arm) => kind switch
    {
        "analysis_hypotheses" => $"Proposed hypothesis [{arm ?? "single"}] ({asOf}, job {job.JobId})",
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
