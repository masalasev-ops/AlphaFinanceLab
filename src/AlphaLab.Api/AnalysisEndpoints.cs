using System.Globalization;
using System.Text.Json;
using AlphaLab.Core.Config;
using AlphaLab.Core.Llm;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Services;
using AlphaLab.Evaluation.Candidates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Api;

/// <summary>Body for <c>POST /api/v1/analysis/hypotheses</c> (FR-23, D82).</summary>
/// <param name="ParentEntryId">A journal entry this proposal grows from.</param>
/// <param name="ParentFinding">…or a recorded finding id.</param>
/// <param name="ParentAttributionRef">…or an attribution row reference.</param>
/// <param name="Topic">Optional steer for the researcher.</param>
/// <param name="PriorProb">D110: the pre-registered P(confirmed), in (0,1). REQUIRED for a researcher
/// proposal — an operator-authored hypothesis written directly to the journal may carry NULL, because a
/// human is not being graded on calibration.</param>
public sealed record HypothesesRequest(
    long? ParentEntryId = null,
    string? ParentFinding = null,
    string? ParentAttributionRef = null,
    string? Topic = null,
    double? PriorProb = null)
{
    /// <summary>D110: the researcher's stated P(confirmed), strictly inside (0,1). Strictly, because a
    /// log scoring rule is unbounded at both ends — 0 and 1 are not confident priors, they are claims
    /// that no evidence could change, and the rule prices them at infinity.</summary>
    public bool HasValidPrior => PriorProb is > 0 and < 1;

    /// <summary>D82: every proposal cites an outcome id, a finding, or an attribution row. **This is what
    /// makes the loop grow from measured outcomes rather than vibes**, which is why its absence is a 422
    /// rather than a defaulted field.</summary>
    public bool HasParentEvidence =>
        ParentEntryId is not null
        || !string.IsNullOrWhiteSpace(ParentFinding)
        || !string.IsNullOrWhiteSpace(ParentAttributionRef);
}

/// <summary>Body for the brief / skeptic actions (FR-23).</summary>
public sealed record AnalysisActionRequest(string? StrategyId = null, string? Topic = null);

/// <summary>
/// The research-assistant endpoints (FR-23/FR-32, D82).
///
/// **Every one returns 202 + job_id, never a result.** The Api may not run long work on a request thread
/// (rule 19) and — by the `ci.ps1` reference graph — cannot reference `AlphaLab.Llm` at all, so it
/// validates synchronously against Data/Evaluation, enqueues a `jobs` row, and lets the Worker (the sole
/// DB writer, D59) execute. The reference graph and the architecture rule agree here, which is a good
/// sign rather than a coincidence.
/// </summary>
public static class AnalysisEndpoints
{
    public static RouteGroupBuilder MapAnalysisEndpoints(
        this RouteGroupBuilder v1, int staleThresholdSeconds)
    {
        ArgumentNullException.ThrowIfNull(v1);

        // ---- POST /analysis/hypotheses (D82) ----
        v1.MapPost("/analysis/hypotheses", async (
            HypothesesRequest req, AlphaLabDbContext db, IWorkerLiveness liveness,
            ResearchOptions research, ILlmBudgetLedger budgetLedger, LlmOptions llm,
            GateOptions gate, TimeProvider clock, CancellationToken ct) =>
        {
            var worker = await liveness.GetAsync(staleThresholdSeconds, ct);
            if (worker.IsLive)
                return ApiResults.Error(409, "conflict", "A daily run is in progress — retry after it completes.");

            // D82: parent evidence or 422. Checked FIRST because it is a property of the request, and a
            // request that could never be valid should not consume a gate evaluation.
            if (!req.HasParentEvidence)
            {
                return ApiResults.Error(422, "unprocessable_entity",
                    "A proposal must cite parent evidence — an outcome entry id, a finding, or an " +
                    "attribution row (D82). This is what makes the loop grow from measured outcomes " +
                    "rather than vibes; a proposal with no parent is refused rather than defaulted.");
            }

            // ---- D110: the pre-registered prior. ----
            // Refused here rather than defaulted, and required only of the RESEARCHER: the whole
            // calibration channel is "did the seat's stated confidence beat an uninformed base rate",
            // and a default prior is the lab answering for the seat.
            if (!req.HasValidPrior)
            {
                return ApiResults.Error(422, "unprocessable_entity",
                    "A researcher proposal must carry prior_prob — the pre-registered P(confirmed), " +
                    "strictly between 0 and 1 (D110). Strictly, because the log scoring rule is unbounded " +
                    "at both ends: 0 and 1 are not confident priors but claims no evidence could change. " +
                    "An operator-authored hypothesis written directly to the journal may carry NULL; a " +
                    "proposal from the seat may not.");
            }

            // ---- D110: both score parameters must be PINNED before the first proposal exists. ----
            // Fail closed and name the keys. A parameter chosen after the first scores are visible is a
            // parameter chosen by looking at the answer, so the endpoint refuses rather than proceeding
            // with proposals that could never be scored comparably.
            var unpinned = ProposalScoreKeys.Unpinned(db);
            if (unpinned.Count > 0)
            {
                return ApiResults.Error(422, "proposal_thresholds_unpinned",
                    $"The D110 proposal-score parameters are not pinned: {string.Join(", ", unpinned)}. " +
                    "They are versioned config rows (never appsettings) because they are score INPUTS — a " +
                    "mid-experiment change breaks proposal-to-proposal comparability, and an appsettings " +
                    "value is not as-of resolvable, so a later recomputation could not reproduce the score " +
                    "a proposal was originally given. Pin them with the Worker's " +
                    "`pin-proposal-thresholds` verb, then retry.",
                    new { missing_keys = unpinned });
            }

            var asOf = clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            // ---- D24: the budget ceiling. 503, and BEFORE the evidence diet. ----
            // Ordered this way deliberately. A diet refusal is COUNTED as operator debt; an exhausted
            // budget is not the operator's debt, it is the day's capacity. Evaluating the diet first
            // would book a refusal against a day the seat could not have run on anyway, inflating the
            // very count D110 reads as a signal about outcome closure.
            var exhausted = await BudgetExhaustedAsync(budgetLedger, llm, asOf, ct);
            if (exhausted is not null) return exhausted;

            // ---- D112: the evidence diet. ----
            var diet = new EvidenceDietGate(db, research).Assess(asOf);
            if (!diet.Admitted)
            {
                // The refusal is COUNTED, not merely returned: a blocked proposal is a GAP in the D110
                // proposal stream, and an unattributed gap reads as researcher inactivity when it is in
                // fact operator debt. The counter is what makes the gap attributable later.
                RecordRefusal(db, asOf, EvidenceDietVerdict.RefusedCode, diet.OverdueCount);
                await db.SaveChangesAsync(ct);

                return ApiResults.Error(422, EvidenceDietVerdict.RefusedCode, diet.Message, new
                {
                    overdue_outcomes = diet.OverdueCount,
                    bound = diet.Bound,
                    bound_key = "Research.MaxConcurrentCandidates",
                });
            }

            // ---- D113: a proposal whose floor cannot be computed is permanently unscorable. ----
            // Refused with a named reason and COUNTED, through D112's machinery — no third column and no
            // new mechanism. The alternative, writing it as a silently unscorable row, is exactly the
            // "missing first link" D110 warns about: the chain would have a gap nobody could see.
            if (new DetectabilityGate(db, gate).ResolveCurrentFloor() is null)
            {
                RecordRefusal(db, asOf, UnscorableCode, diet.OverdueCount);
                await db.SaveChangesAsync(ct);

                return ApiResults.Error(422, UnscorableCode,
                    "The arena has no computable detectability floor (unassessed_no_sigma: no power_reports " +
                    "row with sigma > 0 for either run kind), so this proposal could never be scored on the " +
                    "margin channel. Refused and counted rather than written as a silently unscorable row. " +
                    "Run the replay calibration, which writes the sigma estimates the floor is built from.");
            }

            var jobId = await EnqueueAsync(db, "analysis_hypotheses", req, clock, ct);

            return Results.Accepted($"/api/v1/jobs/{jobId}", new
            {
                job_id = jobId,
                // D82: spend renders beside the deflated-Sharpe trials count, so the improver rations
                // itself — every trial spends everyone's significance (S2).
                fork_budget_per_year = research.ForkBudgetPerYear,
                max_concurrent_candidates = research.MaxConcurrentCandidates,
                overdue_outcomes = diet.OverdueCount,
            });
        });

        // ---- POST /analysis/brief and /analysis/skeptic (FR-23) ----
        foreach (var (route, kind) in new[] { ("/analysis/brief", "analysis_brief"), ("/analysis/skeptic", "analysis_skeptic") })
        {
            var jobKind = kind;
            v1.MapPost(route, async (
                AnalysisActionRequest req, AlphaLabDbContext db, IWorkerLiveness liveness,
                ILlmBudgetLedger budgetLedger, LlmOptions llm,
                TimeProvider clock, CancellationToken ct) =>
            {
                var worker = await liveness.GetAsync(staleThresholdSeconds, ct);
                if (worker.IsLive)
                    return ApiResults.Error(409, "conflict", "A daily run is in progress — retry after it completes.");

                // A SKEPTIC REVIEW NEEDS A SUBJECT; A BRIEF DOES NOT (D153, finding 425). The asymmetry is
                // in the design, not an oversight here: UX-10 lists "today's regime brief" as an
                // arena-level action, while MASTER §199 scopes "feed it a strategy's stats" to the
                // skeptic. Refused BEFORE the budget check so a request that can never produce a valid
                // review does not consume the day's headroom on its way to being useless.
                //
                // NON-EMPTY, not exists-in-`strategies`. A skeptic run against a just-drafted candidate
                // whose row CandidateFactory has not written yet is legitimate, and 404-ing it would make
                // the rail a race against registration. What the rail forbids is a review with no subject
                // at all; whether the arena has evidence for that subject is reported IN the prompt.
                if (jobKind == "analysis_skeptic" && string.IsNullOrWhiteSpace(req.StrategyId))
                {
                    return ApiResults.Error(422, "unprocessable_entity",
                        "A skeptic review requires strategy_id: a review that is not linked to the thing " +
                        "reviewed is an opinion with no subject (D52).");
                }

                var asOf = clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var exhausted = await BudgetExhaustedAsync(budgetLedger, llm, asOf, ct);
                if (exhausted is not null) return exhausted;

                var jobId = await EnqueueAsync(db, jobKind, req, clock, ct);
                return Results.Accepted($"/api/v1/jobs/{jobId}", new { job_id = jobId });
            });
        }

        return v1;
    }

    /// <summary>
    /// The D24 ceiling, read BEFORE the command is accepted (rule 13: the budget is enforced before any
    /// token is spent, never reconciled afterwards).
    ///
    /// **503, not 422.** Nothing is wrong with the request — the arena has spent its day. 503 is the code
    /// that says "correct request, try later", and it is the only one of the D60 set that does not imply
    /// the caller must change something.
    /// </summary>
    private static async Task<IResult?> BudgetExhaustedAsync(
        ILlmBudgetLedger ledger, LlmOptions llm, string asOf, CancellationToken ct)
    {
        var spent = await ledger.GetAsync(asOf, ct);
        var b = llm.DailyBudget;

        var reason =
            spent.CostUsd >= b.MaxCostUsd ? $"cost {spent.CostUsd:F4} of {b.MaxCostUsd:F4} USD"
            : spent.Calls >= b.MaxCalls ? $"calls {spent.Calls} of {b.MaxCalls}"
            : b.MaxTokens > 0 && spent.Tokens >= b.MaxTokens ? $"tokens {spent.Tokens} of {b.MaxTokens}"
            : null;

        return reason is null
            ? null
            : ApiResults.Error(503, "service_unavailable",
                $"The day's LLM budget is exhausted ({reason}) — the request is well-formed; retry after " +
                "the next session. Nothing was enqueued.");
    }

    /// <summary>
    /// Enqueue and return the job id.
    ///
    /// The SaveChanges is INSIDE rather than at the call site because <c>job_id</c> is database-generated:
    /// reading it before the insert yields 0, and a 202 carrying job_id 0 points the caller at a job that
    /// does not exist — a contract breach that still returns "success".
    /// </summary>
    private static async Task<long> EnqueueAsync(
        AlphaLabDbContext db, string kind, object request, TimeProvider clock, CancellationToken ct)
    {
        var job = new JobRow
        {
            Kind = kind,
            Status = "queued",
            SubmittedAt = clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            RequestJson = JsonSerializer.Serialize(request),
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(ct);
        return job.JobId;
    }

    /// <summary>
    /// Record a refusal so the gap in the proposal stream is attributable.
    ///
    /// Stored as a `failed` job with its reason rather than in a new table: the refusal IS an attempt at
    /// the job, and it belongs in the same stream the successful attempts are counted from. A separate
    /// table would need its own joins to answer "how many proposals were attempted this quarter?", which
    /// is the question the D110 score depends on.
    /// </summary>
    private static void RecordRefusal(AlphaLabDbContext db, string asOf, string reason, int overdueCount)
    {
        db.Jobs.Add(new JobRow
        {
            Kind = "analysis_hypotheses",
            Status = "failed",
            SubmittedAt = $"{asOf}T00:00:00Z",
            FinishedAt = $"{asOf}T00:00:00Z",
            RequestJson = "{}",
            ErrorJson = JsonSerializer.Serialize(new { code = reason, overdue_outcomes = overdueCount }),
        });
    }

    /// <summary>D113's unscorable-proposal refusal. A DIFFERENT code from the evidence diet because the
    /// two are different debts — one is an unclosed outcome, the other an uncalibrated arena — but the
    /// same recording machinery, exactly as D112 says ("one mechanism serves both").</summary>
    public const string UnscorableCode = "proposal_unscorable_no_floor";

    /// <summary>How many hypothesis proposals were refused because the arena had no computable floor.
    /// Published beside the evidence-diet count for the same reason: a gap in the D110 proposal stream is
    /// only interpretable if it is attributable.</summary>
    public static Task<int> CountUnscorableRefusalsAsync(AlphaLabDbContext db, CancellationToken ct = default)
        => db.Jobs.CountAsync(
            j => j.Kind == "analysis_hypotheses"
                 && j.Status == "failed"
                 && j.ErrorJson != null
                 && j.ErrorJson.Contains(UnscorableCode), ct);

    /// <summary>How many hypothesis proposals were refused by the evidence diet — published **beside the
    /// D110 proposal-quality score** so a gap in the stream reads as operator debt rather than as
    /// researcher inactivity.</summary>
    public static Task<int> CountEvidenceDietRefusalsAsync(AlphaLabDbContext db, CancellationToken ct = default)
        => db.Jobs.CountAsync(
            j => j.Kind == "analysis_hypotheses"
                 && j.Status == "failed"
                 && j.ErrorJson != null
                 && j.ErrorJson.Contains(EvidenceDietVerdict.RefusedCode), ct);
}
