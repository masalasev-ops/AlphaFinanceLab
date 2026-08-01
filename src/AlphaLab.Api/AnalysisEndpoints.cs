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
public sealed record HypothesesRequest(
    long? ParentEntryId = null,
    string? ParentFinding = null,
    string? ParentAttributionRef = null,
    string? Topic = null)
{
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
            TimeProvider clock, CancellationToken ct) =>
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
