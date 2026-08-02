using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlphaLab.Core.Config;
using AlphaLab.Core.Llm;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Services;
using AlphaLab.Evaluation.Ai;
using AlphaLab.Evaluation.Candidates;
using AlphaLab.Evaluation.ReadModels;
using Microsoft.EntityFrameworkCore;
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
/// **The hypotheses path runs through CONTEXT PACKS (D80/D104; wired at v1.9.70, finding 330).** Each D113
/// arm gets a persisted `ai_context_packs` row (artefact (a)) and a subject-keyed `ai_decisions` row
/// (artefacts (b)+(d), with (c) recorded once the draft lands) — see D114 for the subject grammar. The
/// brief/skeptic paths deliberately do NOT: their outputs are advisory prose for the operator, no
/// controlled comparison depends on their inputs being held constant, and their pack-ification lands with
/// the contestant-phase recipe work rather than silently here.
/// </summary>
public sealed class ResearchJobExecutor(
    string kind,
    IServiceScopeFactory scopeFactory,
    ILogger<ResearchJobExecutor> logger) : IJobExecutor
{
    /// <summary>The three `jobs.kind` values this executor is registered under.</summary>
    public static readonly IReadOnlyList<string> Kinds =
        ["analysis_hypotheses", "analysis_brief", "analysis_skeptic"];

    /// <summary>The researcher seat's frozen prompt-policy version (D81 rule 2's discipline applied to a
    /// seat with no fork lifecycle): any edit to the instruction blocks below bumps this.</summary>
    public const string PromptVersion = "rs-1.1";

    /// <summary>A conservative per-arm estimate for the pairing check. Conservative deliberately: an
    /// UNDER-estimate here produces exactly the unpaired observation the check exists to prevent.</summary>
    public const decimal EstimatedArmCostUsd = 0.25m;

    public string Kind => kind;

    public async Task ExecuteAsync(JobRow job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AlphaLabDbContext>();
        var analysis = sp.GetRequiredService<IAnalysisProvider>();

        if (kind != "analysis_hypotheses")
        {
            await RunAdvisoryAsync(db, analysis, job, ct).ConfigureAwait(false);
            return;
        }

        await RunHypothesesPairAsync(sp, db, analysis, job, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------------------------
    // The hypotheses pair (D113): treatment + paper control, one job run, through persisted packs.
    // ---------------------------------------------------------------------------------------------

    private async Task RunHypothesesPairAsync(
        IServiceProvider sp, AlphaLabDbContext db, IAnalysisProvider analysis, JobRow job, CancellationToken ct)
    {
        var req = Parse<HypothesesJobRequest>(job);
        var ai = sp.GetRequiredService<AiOptions>();
        var research = sp.GetRequiredService<ResearchOptions>();
        var gate = sp.GetRequiredService<GateOptions>();
        var signalOptions = sp.GetRequiredService<SignalLibraryOptions>();

        // Wall-clock date for OPERATIONAL records (the journal's created_on, the budget month). The PACK
        // anchors to arena evidence instead — see ResolveAnchor.
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // ---- D113 pairing: both arms fit the monthly budget, or neither dispatches. ----
        var budget = new ResearcherSeatBudget(db, ai).Assess(today, EstimatedArmCostUsd);
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

        // ---- The pack anchor: the arena's last committed evidence, never the wall clock. ----
        // A pack stamped with a wall-clock as-of but data from an older watermark would pass the leak
        // check trivially while claiming knowledge of days the arena never processed. Fail closed when no
        // run has ever committed: an arena with no evidence has no pack to build (rule 10).
        var anchor = ResolveAnchor(db);

        // ---- The floor, both ways, deliberately. ----
        // The JOURNAL rows stamp the CURRENT floor (D113: assessment-time, read ONCE before either arm so
        // the pair shares one number). The PACK carries the AS-OF floor (D96, AsOfDetectabilityFloor) -
        // the operational read resolves current state by design and would put a post-as-of fact in a pack.
        var floorNow = new DetectabilityGate(db, gate).ResolveCurrentFloor();
        var asOfFloor = new AsOfDetectabilityFloor(db, gate).Resolve(anchor.AsOf);

        var commonFields = BuildCommonFields(db, research, anchor.AsOf, asOfFloor);
        var readModel = new SignalLibraryBuilder(db, signalOptions).Build(anchor.AsOf);
        var seed = DeterministicSeed(anchor.AsOf, job.JobId);

        // ---- Build + persist BOTH packs before any token is spent (D81 rule 1's order, seat-adapted). ----
        var packStore = new ContextPackStore(db);
        var arms = new (string Arm, EvidencePriorMode Mode)[]
        {
            ("treatment", EvidencePriorMode.On),
            ("control", EvidencePriorMode.Placebo),
        };
        var packs = new Dictionary<string, ContextPack>(StringComparer.Ordinal);
        foreach (var (arm, mode) in arms)
        {
            var fields = new List<PackField>(commonFields);
            var digest = new EvidencePriorSeam(mode).BuildDigestField(readModel, seed);
            if (digest is not null) fields.Add(digest);

            var pack = new ContextPackBuilder(ai.PackRecipeVersion).Build(
                AiSeat.Researcher, Subject(job.JobId, arm), anchor.AsOf, anchor.Watermark, fields);
            await packStore.SaveAsync(pack, ct).ConfigureAwait(false);
            packs[arm] = pack;
        }

        // ---- ONE batch, two requests (D46: scheduled ⇒ batched). ----
        // The prompt NEVER declares the seam mode (D114: the placebo is BLIND - a control that is told its
        // evidence is fake is not a control). Arm identity lives in the RECORDS: the subject string, the
        // journal title, and SamplingJson. The two L2 blocks differ ONLY in the digest field's content.
        var requests = arms
            .Select(a => new AnalysisRequest(
                $"{kind}:{job.JobId}:{a.Arm}",
                AnalysisTask.Hypotheses,
                new PromptLayers(HypothesesInstructions, "", packs[a.Arm].PackJson + "\n\n" + RequestBlock(req))))
            .ToList();

        var results = await analysis.RunBatchAsync(requests, ct).ConfigureAwait(false);
        var byArm = arms.ToDictionary(
            a => a.Arm,
            a => results.Single(r => r.CustomId.EndsWith(":" + a.Arm, StringComparison.Ordinal)));

        // BOTH arms usable or the whole job fails (rule 10 + the pairing constraint). A one-armed success
        // would be the unpaired observation again, from the response side instead of the budget side. The
        // successful arm's output is already in analysis_cache, so a retry costs ~nothing.
        foreach (var (arm, result) in byArm)
        {
            if (result.Outcome is not (AnalysisOutcome.Succeeded or AnalysisOutcome.CacheHit))
            {
                throw new InvalidOperationException(
                    $"{kind}: the {arm} arm was {result.Outcome} ({result.Detail ?? "no detail"}) - " +
                    "no journal entry written for EITHER arm (D113: both or neither).");
            }
        }

        // ---- Persist-before-use: the decision rows (artefacts (b)+(d)) land BEFORE the journal drafts
        // that consume them, then (c) is recorded against each once its draft has an id. ----
        var decisionStore = new AiDecisionStore(db);
        foreach (var (arm, _) in arms)
        {
            var result = byArm[arm];
            var samplingJson = JsonSerializer.Serialize(new
            {
                seam = arm == "treatment" ? "on" : "placebo",
                seed,
            });
            await decisionStore.PersistAsync(new AiDecisionRecord(
                Subject(job.JobId, arm), anchor.AsOf, packs[arm].PackHash, PromptVersion,
                result.ModelVersion, result.RawOutput, result.Usage, null, samplingJson), ct)
                .ConfigureAwait(false);
        }

        foreach (var (arm, _) in arms)
        {
            var result = byArm[arm];
            var entry = new JournalEntryRow
            {
                CreatedOn = today,
                Kind = "hypothesis",
                // The arm rides in the TITLE because it must survive into the journal: a margin series
                // computed from entries that do not say which arm produced them is unattributable.
                Title = $"Proposed hypothesis [{arm}] ({today}, job {job.JobId})",
                BodyMd = result.RawOutput,
                LinkedEntryId = req.ParentEntryId,
                // D110: the stated prior, and the CURRENT floor at assessment (D113's amendment) - one
                // read, both arms, so the pair is comparable by construction.
                PriorProb = req.PriorProb,
                DetectabilityFloorAnn = floorNow,
                // UNLOCKED, always. Locking is the operator's pre-registration act (D52/rule 30); a seat
                // that could lock its own hypothesis would be pre-registering itself, and the frozen claim
                // would no longer be a commitment made before the evidence.
                Locked = false,
            };
            db.JournalEntries.Add(entry);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            // Artefact (c): what the arena did with the decision - it became draft entry N, unlocked,
            // awaiting the operator. Recorded once; a second application is itself a defect.
            await decisionStore.RecordAppliedAsync(
                Subject(job.JobId, arm), anchor.AsOf, PromptVersion,
                JsonSerializer.Serialize(new { journal_entry_id = entry.EntryId, arm, locked = false }), ct)
                .ConfigureAwait(false);

            logger.LogInformation(
                "{Kind}: job {JobId} [{Arm}] wrote unlocked draft {EntryId} (pack {PackHash}, floor {Floor}, {Cost:C4}).",
                kind, job.JobId, arm, entry.EntryId, packs[arm].PackHash[..12],
                floorNow?.ToString("P2", CultureInfo.InvariantCulture) ?? "n/a", result.Usage.CostUsd);
        }
    }

    /// <summary>The D114 subject grammar for researcher records: `job:{id}#{arm}` in BOTH
    /// `ai_context_packs.strategy_id` and `ai_decisions.strategy_id`. A strategy id for the contestant;
    /// a job-arm subject for the researcher — the column names the decision's SUBJECT, not always a
    /// strategy.</summary>
    public static string Subject(long jobId, string arm) =>
        $"job:{jobId.ToString(CultureInfo.InvariantCulture)}#{arm}";

    /// <summary>
    /// The pack anchor: the latest committed forward run's (as_of, watermark).
    ///
    /// Fail closed when none exists — an arena that has never committed a session has no evidence to pack,
    /// and stamping a pack with a wall-clock date over no data would be a record claiming knowledge that
    /// was never there (rule 10).
    /// </summary>
    private static (string AsOf, string Watermark) ResolveAnchor(AlphaLabDbContext db)
    {
        var run = db.Runs.AsNoTracking()
            .Where(r => r.Status == "ok" && (r.RunKind == "live" || r.RunKind == "catchup"))
            .OrderByDescending(r => r.AsOf)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "Researcher seat: no committed forward run exists, so there is no arena evidence to build " +
                "a context pack from. Run the daily pipeline first (fail closed, rule 10).");
        return (run.AsOf, run.Watermark);
    }

    /// <summary>The COMMON cp-1.0 fields — both D113 arms receive every one of these; the digest is the
    /// only difference. All reads are as-of-bounded so `FX-PackNoLeak` holds per field.</summary>
    private static List<PackField> BuildCommonFields(
        AlphaLabDbContext db, ResearchOptions research, string asOf, AsOfFloor floor)
    {
        // Closed outcomes (D79): bounded by the OUTCOME ENTRY's created_on — the recorded closure act —
        // not the hypothesis row's mutable column alone, so a closure recorded after the anchor cannot
        // leak into a pack anchored before it.
        var closedHypIds = db.JournalEntries.AsNoTracking()
            .Where(e => e.Kind == "outcome" && e.LinkedEntryId != null
                        && string.Compare(e.CreatedOn, asOf) <= 0)
            .Select(e => e.LinkedEntryId!.Value)
            .Distinct()
            .ToList();
        var closedOutcomes = db.JournalEntries.AsNoTracking()
            .Where(h => closedHypIds.Contains(h.EntryId) && h.Kind == "hypothesis" && h.Outcome != null)
            .OrderBy(h => h.EntryId)
            .Select(h => new ClosedOutcome(h.EntryId, h.Title, h.Metric, h.EvidenceWindowDays, h.Outcome!))
            .ToList();

        // Fork budget remaining: ForkBudgetPerYear minus live trials registered in the trailing 365
        // calendar days of the anchor ("per year" is definitional, not tunable), floored at 0.
        var yearAgo = DateOnly.ParseExact(asOf, "yyyy-MM-dd", CultureInfo.InvariantCulture)
            .AddDays(-365).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var trialsThisYear = db.TrialsRegistry.AsNoTracking().Count(t =>
            t.RunKind == "live"
            && string.Compare(t.RegisteredOn, yearAgo) >= 0
            && string.Compare(t.RegisteredOn, asOf) <= 0);
        var forkBudgetRemaining = Math.Max(0, research.ForkBudgetPerYear - trialsThisYear);

        var regimeLabel = db.RegimeLabels.AsNoTracking()
            .Where(r => r.AsOf == asOf && (r.RunKind == "live" || r.RunKind == "catchup"))
            .Select(r => r.Label)
            .FirstOrDefault();

        return
        [
            new PackField(PackWhitelist.AsOf, asOf, asOf),
            new PackField(PackWhitelist.RegimeLabel, regimeLabel, asOf),
            // The AS-OF floor (D96), never DetectabilityGate's operational read — and its trials count
            // beside it, because the floor RISES with the trials tax and is uninterpretable without the
            // count that set it. A null floor is the honest unassessed answer, never a zero.
            new PackField(PackWhitelist.DetectabilityFloorAnn, floor.FloorAnn, asOf),
            // D116 (cp-1.1): the OTHER end of the band, from the same as-of read. Both ends or neither —
            // a pack carrying only the floor gives the seat one scale cue and it points up (finding 337).
            // COMMON to both D113 arms, exactly like the floor; only `signal_digest` is differenced.
            new PackField(PackWhitelist.DetectabilityCeilingAnn, floor.CeilingAnn, asOf),
            new PackField(PackWhitelist.TrialsCount, floor.TrialsCount, asOf),
            new PackField(PackWhitelist.ClosedOutcomes, closedOutcomes, asOf),
            new PackField(PackWhitelist.ForkBudgetRemaining, forkBudgetRemaining, asOf),
        ];
    }

    /// <summary>One closed outcome, compact (D80: derived, never raw).</summary>
    private sealed record ClosedOutcome(long EntryId, string Title, string? Metric, int? WindowDays, string Outcome);

    /// <summary>The operator's ask — parent evidence + topic. Deliberately OUTSIDE the pack: the pack is
    /// arena-derived evidence under a whitelist closure; the ask is already persisted verbatim in
    /// `jobs.request_json`, so together the full input stays reconstructable without the whitelist having
    /// to admit free text.</summary>
    private static string RequestBlock(HypothesesJobRequest req) => string.Join("\n",
    [
        $"Parent outcome entry: {req.ParentEntryId?.ToString(CultureInfo.InvariantCulture) ?? "-"}",
        $"Parent finding: {req.ParentFinding ?? "-"}",
        $"Parent attribution: {req.ParentAttributionRef ?? "-"}",
        $"Topic: {req.Topic ?? "(none — the operator left it open)"}",
    ]);

    /// <summary>Deterministic placebo seed from (asOf, jobId). SHA-256 rather than GetHashCode because
    /// string hashing is randomized per process — an irreproducible control is not a control.</summary>
    public static int DeterministicSeed(string asOf, long jobId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{asOf}|{jobId.ToString(CultureInfo.InvariantCulture)}"));
        return BitConverter.ToInt32(bytes, 0);
    }

    // ---------------------------------------------------------------------------------------------
    // The advisory kinds (brief / skeptic) — deliberately NOT pack-routed; see the class comment.
    // ---------------------------------------------------------------------------------------------

    private async Task RunAdvisoryAsync(
        AlphaLabDbContext db, IAnalysisProvider analysis, JobRow job, CancellationToken ct)
    {
        var req = Parse<AnalysisActionJobRequest>(job);
        var asOf = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var (task, journalKind, instructions) = kind == "analysis_brief"
            ? (AnalysisTask.ResearchBrief, "decision_note", BriefInstructions)
            : (AnalysisTask.Skeptic, "skeptic_review", SkepticInstructions);

        var fresh = string.Join("\n",
        [
            $"Date: {asOf}",
            $"Strategy: {req.StrategyId ?? "(arena-level — no single strategy)"}",
            $"Topic: {req.Topic ?? "(none)"}",
        ]);

        var results = await analysis
            .RunBatchAsync([new AnalysisRequest($"{kind}:{job.JobId}", task, new PromptLayers(instructions, "", fresh))], ct)
            .ConfigureAwait(false);
        var result = results[0];

        if (result.Outcome is not (AnalysisOutcome.Succeeded or AnalysisOutcome.CacheHit))
        {
            // Fail closed with the reason (rule 10): the drainer marks the job 'failed' and the operator
            // sees WHY. Writing an empty journal entry instead would put an unattributed blank into the
            // record, which reads as the seat having had nothing to say.
            throw new InvalidOperationException(
                $"{kind}: the model was {result.Outcome} ({result.Detail ?? "no detail"}) - no journal entry written.");
        }

        db.JournalEntries.Add(new JournalEntryRow
        {
            CreatedOn = asOf,
            Kind = journalKind,
            Title = kind == "analysis_brief"
                ? $"Research brief ({asOf}, job {job.JobId})"
                : $"Skeptic review ({asOf}, job {job.JobId})",
            BodyMd = result.RawOutput,
            StrategyId = req.StrategyId,
            Locked = false,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation(
            "{Kind}: job {JobId} wrote an unlocked '{JournalKind}' entry ({Cost:C4}).",
            kind, job.JobId, journalKind, result.Usage.CostUsd);
    }

    private static T Parse<T>(JobRow job) =>
        JsonSerializer.Deserialize<T>(job.RequestJson ?? "")
        ?? throw new InvalidOperationException(
            $"jobs.request_json for job {job.JobId} does not deserialize to {typeof(T).Name} (fail closed).");

    // ---- L0 blocks. Frozen text: each is the cached prefix for its task, so an edit is a prompt-version
    // event (bump PromptVersion) and a cache miss for everything after it, not a tidy-up (D81 rule 2). ----

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
        - The expected effect must fall between `detectability_floor_ann` and `detectability_ceiling_ann`
          in the supplied context. Below the floor the arena cannot adjudicate the claim within its
          patience horizon; above the ceiling the claim is larger than any edge this arena has ever
          calibrated against, so there is no evidence about what the machinery does there. A proposal
          outside that band is refused. A bigger number is not a better proposal.
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
