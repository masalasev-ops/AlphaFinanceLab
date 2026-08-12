using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AlphaLab.Data.Entities;
using AlphaLab.Evaluation.Candidates;

namespace AlphaLab.Api.Tests;

/// <summary>
/// The researcher seat (FR-23/FR-32, D82) and the D112 evidence diet.
///
/// These are API-level tests rather than read-model unit tests because what is being asserted IS the
/// contract shape: which status code, which envelope, and — for the refusal — that the write side stayed
/// empty. A unit test on the gate could not see any of that.
/// </summary>
public class AnalysisEndpointsTests
{
    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>
    /// An arena where a proposal CAN be accepted: the two D110 score parameters pinned (5.7) and a sigma
    /// estimate so the detectability floor is computable.
    ///
    /// These are preconditions of the seat, not of these tests — an arena missing either refuses every
    /// proposal for its own stated reason, which is what `ProposalInputsTests` asserts. Seeding them here
    /// is what keeps THESE tests measuring the rail each of them is about.
    /// </summary>
    private static ApiArenaFactory Ready()
    {
        var f = new ApiArenaFactory();
        using var db = f.Open();
        db.Config.Add(new ConfigRow
        {
            Key = ProposalScoreKeys.PriorClamp, ValueJson = "0.02", Version = 1,
            ChangedOn = "2026-08-01T00:00:00Z", Reason = "test",
        });
        db.Config.Add(new ConfigRow
        {
            Key = ProposalScoreKeys.ScoreMinClosed, ValueJson = "10", Version = 1,
            ChangedOn = "2026-08-01T00:00:00Z", Reason = "test",
        });
        db.PowerReports.Add(new PowerReportRow
        {
            AsOf = "2026-07-31", StrategyA = "cand:a", StrategyB = "buyhold:cw", TDays = 500,
            SigmaLr = 0.0008, NwLag = 21, MdeAnn = 0.03, ObservedGapAnn = 0.0,
            Verdict = "TooEarly", RunKind = "replay",
        });
        db.SaveChanges();
        return f;
    }

    /// <summary>A well-formed proposal body: parent evidence plus the D110 prior.</summary>
    private const string ValidProposal = """{"parent_finding":"f-311","prior_prob":0.35}""";

    /// <summary>An unclosed, locked hypothesis whose evidence window elapsed long ago — overdue under any
    /// clock, so the fixture does not depend on the day it runs.</summary>
    private static JournalEntryRow OverdueHypothesis(int n) => new()
    {
        CreatedOn = "2020-01-01",
        Kind = "hypothesis",
        Title = $"overdue-{n}",
        BodyMd = "b",
        Metric = "alpha",
        EvidenceWindowDays = 30,
        Locked = true,
        Outcome = null,
    };

    [Fact]
    public async Task D153_Skeptic_WithoutASubject_Is422_AndNothingIsEnqueued()
    {
        using var f = new ApiArenaFactory();

        var response = await f.CreateClient().PostAsync("/api/v1/analysis/skeptic",
            Body("""{"topic":"is momentum real?"}"""));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":\"unprocessable_entity\"", json);
        Assert.Contains("strategy_id", json, StringComparison.OrdinalIgnoreCase);

        // Refused before enqueue AND before the budget check: a review that can never have a subject
        // must not reach the Worker, and must not spend the day's headroom on the way there.
        using var db = f.Open();
        Assert.Empty(db.Jobs.ToList());
    }

    [Fact]
    public async Task D153_TheSubjectRailIsSkepticOnly_AnArenaLevelBriefStillDispatches()
    {
        // THE CONTROL, and the reason the rail is not symmetric. UX-10 lists "today's regime brief" as an
        // arena-level action and MASTER §199 scopes "feed it a strategy's stats" to the SKEPTIC alone, so
        // a brief with no strategy_id is a legitimate request. A symmetric guard would break it, which is
        // exactly the over-correction this test exists to catch.
        using var f = new ApiArenaFactory();

        var response = await f.CreateClient().PostAsync("/api/v1/analysis/brief",
            Body("""{"topic":"how did the arena behave this month?"}"""));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var db = f.Open();
        Assert.Single(db.Jobs.ToList());
    }

    [Fact]
    public async Task FR23_Hypotheses_RequireParentEvidence()
    {
        using var f = new ApiArenaFactory();

        var response = await f.CreateClient().PostAsync("/api/v1/analysis/hypotheses",
            Body("""{"topic":"anything"}"""));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":\"unprocessable_entity\"", json);
        Assert.Contains("parent evidence", json, StringComparison.OrdinalIgnoreCase);

        // Refused BEFORE anything was enqueued: a proposal with no parent could never be valid, so it
        // must not reach the Worker at all.
        using var db = f.Open();
        Assert.Empty(db.Jobs.ToList());
    }

    [Fact]
    public async Task FR32_LongRunningCommand_Returns202Job()
    {
        using var f = Ready();

        var response = await f.CreateClient().PostAsync("/api/v1/analysis/hypotheses",
            Body("""{"parent_entry_id":41,"topic":"why did the momentum candidate fade","prior_prob":0.4}"""));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var jobId = doc.RootElement.GetProperty("job_id").GetInt64();
        Assert.True(jobId > 0);

        // The Api enqueues and returns; it never runs the model itself (rule 19) — and under the ci.ps1
        // reference graph it could not, since AlphaLab.Api cannot reference AlphaLab.Llm.
        using var db = f.Open();
        var job = Assert.Single(db.Jobs.ToList());
        Assert.Equal("analysis_hypotheses", job.Kind);
        Assert.Equal("queued", job.Status);
        Assert.Contains("41", job.RequestJson!, StringComparison.Ordinal);

        // D82: the seat's remaining trials budget renders WITH the acceptance, so the improver rations
        // itself against the deflated-Sharpe count rather than discovering the bound at admission.
        Assert.Equal(6, doc.RootElement.GetProperty("fork_budget_per_year").GetInt32());
    }

    [Fact]
    public async Task FR32_BriefAndSkeptic_AlsoReturn202Job()
    {
        using var f = new ApiArenaFactory();
        var client = f.CreateClient();

        foreach (var (route, kind) in new[] { ("brief", "analysis_brief"), ("skeptic", "analysis_skeptic") })
        {
            var response = await client.PostAsync($"/api/v1/analysis/{route}", Body("""{"strategy_id":"cand:a"}"""));
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

            using var db = f.Open();
            Assert.Contains(db.Jobs.ToList(), j => j.Kind == kind && j.Status == "queued");
        }
    }

    [Fact]
    public async Task FX_EvidenceDietRefusal_AtTheBound_Refuses_WritesNoProposal_AndCountsTheRefusal()
    {
        using var f = Ready();
        using (var db = f.Open())
        {
            // Three overdue outcomes == Research.MaxConcurrentCandidates. The bound is a COUNT with a
            // derived bound, not a grace period in days (D112).
            db.JournalEntries.AddRange(OverdueHypothesis(1), OverdueHypothesis(2), OverdueHypothesis(3));
            db.SaveChanges();
        }

        var response = await f.CreateClient().PostAsync("/api/v1/analysis/hypotheses", Body(ValidProposal));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":\"evidence_diet_refused\"", json);
        Assert.Contains("\"overdue_outcomes\":3", json);

        // The message frames the pause as OPERATOR debt, not a fault of the seat — D112 requires that
        // framing because the mechanism is misreadable without it.
        Assert.Contains("not a fault of the seat", json, StringComparison.OrdinalIgnoreCase);

        using var check = f.Open();
        Assert.Empty(check.Jobs.Where(j => j.Status == "queued").ToList());   // zero proposals written

        // …but the refusal itself IS recorded, so the gap in the D110 proposal stream is attributable to
        // the operator rather than reading as researcher inactivity.
        var refusal = Assert.Single(check.Jobs.Where(j => j.Status == "failed").ToList());
        Assert.Contains("evidence_diet_refused", refusal.ErrorJson!, StringComparison.Ordinal);
        Assert.Equal(1, await AnalysisEndpoints.CountEvidenceDietRefusalsAsync(check));
    }

    [Fact]
    public async Task FX_EvidenceDietRefusal_BelowTheBound_Proceeds()
    {
        using var f = Ready();
        using (var db = f.Open())
        {
            // Two late outcomes. Tolerating one or two while preventing the pile-up is the shape P8 asked
            // for — this half is what proves the gate is a diet and not a hard stop.
            db.JournalEntries.AddRange(OverdueHypothesis(1), OverdueHypothesis(2));

            // Neither of these is overdue, and each is excluded for its own reason: one is closed, one is
            // unlocked (a draft the operator never pre-registered), one is still inside its window.
            db.JournalEntries.Add(new JournalEntryRow
            {
                CreatedOn = "2020-01-01", Kind = "hypothesis", Title = "closed", BodyMd = "b",
                EvidenceWindowDays = 30, Locked = true, Outcome = "refuted",
            });
            db.JournalEntries.Add(new JournalEntryRow
            {
                CreatedOn = "2020-01-01", Kind = "hypothesis", Title = "draft", BodyMd = "b",
                EvidenceWindowDays = 30, Locked = false,
            });
            db.JournalEntries.Add(new JournalEntryRow
            {
                CreatedOn = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Kind = "hypothesis", Title = "fresh", BodyMd = "b", EvidenceWindowDays = 252, Locked = true,
            });
            db.SaveChanges();
        }

        var response = await f.CreateClient().PostAsync("/api/v1/analysis/hypotheses",
            Body("""{"parent_attribution_ref":"attr:2026-07-31","prior_prob":0.35}"""));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // The count rides along with the acceptance, so the operator sees the debt accumulating BEFORE it
        // becomes a refusal.
        Assert.Equal(2, doc.RootElement.GetProperty("overdue_outcomes").GetInt32());

        using var check = f.Open();
        Assert.Single(check.Jobs.Where(j => j.Kind == "analysis_hypotheses" && j.Status == "queued").ToList());
        Assert.Equal(0, await AnalysisEndpoints.CountEvidenceDietRefusalsAsync(check));
    }

    [Fact]
    public async Task FR24_BudgetExhausted_Returns503_AndEnqueuesNothing()
    {
        using var f = Ready();
        using (var db = f.Open())
        {
            // The committed Api ceiling is MaxCostUsd 1.00 (ConfigConsistencyTests holds it equal to the
            // Worker's). A day already at the ceiling has no capacity left to promise.
            db.LlmBudgetLog.Add(new LlmBudgetLogRow
            {
                AsOf = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Calls = 4, Tokens = 12_000, CostUsd = 1.25,
            });
            db.SaveChanges();
        }

        var response = await f.CreateClient().PostAsync("/api/v1/analysis/hypotheses", Body(ValidProposal));

        // 503, not 422: the request is well-formed, the day is spent. The distinction matters because a
        // 422 tells the caller to change something, and there is nothing here to change.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":\"service_unavailable\"", json);

        using var check = f.Open();
        Assert.Empty(check.Jobs.ToList());   // rule 13: refused before a token could be spent

        // And NOT recorded as an evidence-diet refusal — an exhausted budget is the day's capacity, not
        // the operator's outcome-closure debt, so it must not inflate the count D110 reads.
        Assert.Equal(0, await AnalysisEndpoints.CountEvidenceDietRefusalsAsync(check));
    }

    [Fact]
    public async Task FR34_Hypotheses_WhileARunIsLive_Returns409()
    {
        using var f = new ApiArenaFactory();
        using (var db = f.Open())
        {
            var state = db.WorkerState.Find(1)!;
            state.RunInProgress = 1;
            state.HeartbeatAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            db.SaveChanges();
        }

        var response = await f.CreateClient().PostAsync("/api/v1/analysis/hypotheses", Body(ValidProposal));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // Checked BEFORE the gate: a live run means the read the gate would do is racing the daily write
        // transaction, so the answer would be about a moving arena.
        using var check = f.Open();
        Assert.Empty(check.Jobs.ToList());
    }
}
