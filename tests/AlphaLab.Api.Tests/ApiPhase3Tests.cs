using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AlphaLab.Data.Entities;

namespace AlphaLab.Api.Tests;

/// <summary>The Phase-3 read + bounded-command endpoints (FR-32; completes FR-34). Each test uses its own
/// migrated temp arena for isolation (command tests mutate worker_state).</summary>
public class ApiPhase3Tests
{
    private static void SeedRun(ApiArenaFactory f, string asOf = "2026-03-01")
    {
        using var db = f.Open();
        db.Runs.Add(new RunRow { AsOf = asOf, RunKind = "live", Watermark = asOf + "T22:00:00Z", StartedAt = "t", Status = "ok" });
        db.SaveChanges();
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Strategies_AfterACommittedRun_IsStamped_WithTheStrategyRows()
    {
        using var f = new ApiArenaFactory();
        SeedRun(f);
        using (var db = f.Open())
        {
            db.Strategies.Add(new StrategyRow { StrategyId = "cand:a", Family = "momentum", ConfigJson = "{}", ExitPolicyJson = "{}", CreatedOn = "2026-02-01", Status = "candidate" });
            db.SaveChanges();
        }

        var json = await f.CreateClient().GetStringAsync("/api/v1/strategies");
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("stamped", doc.RootElement.GetProperty("stamp").GetProperty("status").GetString());
        Assert.Contains("cand:a", json);
        Assert.Contains("\"seat\":\"math\"", json);   // §23.6 seat badge, rendered verbatim
    }

    [Fact]
    public async Task FR33_Strategies_IgnoresAReplayPowerReport()
    {
        using var f = new ApiArenaFactory();
        SeedRun(f);
        using (var db = f.Open())
        {
            db.Strategies.Add(new StrategyRow { StrategyId = "cand:a", Family = "momentum", ConfigJson = "{}", ExitPolicyJson = "{}", CreatedOn = "2026-02-01", Status = "candidate" });
            db.PowerReports.Add(new PowerReportRow
            {
                AsOf = "2026-03-01", StrategyA = "cand:a", StrategyB = "buyhold:cw", TDays = 80, SigmaLr = 0.001,
                NwLag = 21, MdeAnn = 0.01, ObservedGapAnn = 0.5, Verdict = "Promoted", RunKind = "replay",
            });
            db.SaveChanges();
        }

        var json = await f.CreateClient().GetStringAsync("/api/v1/strategies");

        // The forward read-model never reads the replay row — cand:a stays TooEarly, not Promoted (FR-33).
        Assert.DoesNotContain("Promoted", json);
        Assert.Contains("TooEarly", json);
    }

    [Fact]
    public async Task CreateCandidate_WithoutHypothesisOrFlag_Returns422()
    {
        using var f = new ApiArenaFactory();
        var response = await f.CreateClient().PostAsync("/api/v1/candidates",
            Body("{\"strategy_id\":\"cand:x\",\"unregistered\":false}"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":\"unprocessable_entity\"", json);
    }

    [Fact]
    public async Task CreateCandidate_InlineHypothesis_WithBlankMetric_Returns422_NotA500()
    {
        using var f = new ApiArenaFactory();

        // A blank hypothesis metric is a VALIDATION failure (RegisterHypothesis throws ArgumentException) ⇒
        // the D60 contract requires 422, never a 500 internal_error.
        var body = "{\"strategy_id\":\"cand:x\",\"hypothesis\":{\"title\":\"t\",\"body_md\":\"b\",\"metric\":\"\",\"evidence_window_days\":252}}";
        var response = await f.CreateClient().PostAsync("/api/v1/candidates", Body(body));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":\"unprocessable_entity\"", json);

        using var check = f.Open();
        Assert.Empty(check.JournalEntries.ToList());   // rolled back — no orphan hypothesis
        Assert.Empty(check.Strategies.ToList());
    }

    [Fact]
    public async Task CreateCandidate_Unregistered_Succeeds_AndPersistsTheCandidate()
    {
        using var f = new ApiArenaFactory();
        SeedRun(f);

        var response = await f.CreateClient().PostAsync("/api/v1/candidates",
            Body("{\"strategy_id\":\"cand:x\",\"family\":\"momentum\",\"unregistered\":true}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var db = f.Open();
        var created = Assert.Single(db.Strategies.Where(s => s.StrategyId == "cand:x").ToList());
        Assert.Contains("\"unregistered\":true", created.ConfigJson.Replace(" ", ""));
        Assert.Single(db.TrialsRegistry.ToList());   // a trial was registered
    }

    /// <summary>D116 (v1.9.71): the create path's refusal codes are SPLIT by reason, because the three
    /// refusals ask the operator for three different things. `implausible_effect` says the claim is too
    /// big; `detectability_refused` (D99's code, unchanged) says it is too small; `floor_unreachable` says
    /// no claim would have helped. One code for all three would have made the arena's closed gate read as
    /// a complaint about the operator's number (finding 336).</summary>
    [Fact]
    public async Task CreateCandidate_D116_AboveThePlausibilityCeiling_Returns422_ImplausibleEffect()
    {
        using var f = new ApiArenaFactory();
        SeedRun(f);
        using (var db = f.Open())
        {
            // A tiny analytic floor plus a three-rung ladder ⇒ floor 3.5%/yr, ceiling 8 × (8/4) = 16%/yr.
            db.PowerReports.Add(new PowerReportRow
            {
                AsOf = "2026-02-01", StrategyA = "x", StrategyB = "buyhold:cw",
                TDays = 100, SigmaLr = 0.0001, NwLag = 21, MdeAnn = 0.05, RunKind = "live",
            });
            db.Config.Add(new ConfigRow
            {
                Key = "Calibration.DetectionPower", Version = 1, ChangedOn = "2026-02-01",
                ValueJson = """
                    { "curves": {
                        "2": { "knots": [ { "t": 756, "p_promoted": 0.5 } ] },
                        "4": { "knots": [ { "t": 756, "p_promoted": 0.9 } ] },
                        "8": { "knots": [ { "t": 756, "p_promoted": 0.95 } ] } } }
                    """,
            });
            db.SaveChanges();
        }

        var body = "{\"strategy_id\":\"cand:moon\",\"hypothesis\":{\"title\":\"t\",\"body_md\":\"b\"," +
                   "\"metric\":\"beta_adjusted_alpha\",\"evidence_window_days\":252,\"expected_effect_ann\":4.0}}";
        var response = await f.CreateClient().PostAsync("/api/v1/candidates", Body(body));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":\"implausible_effect\"", json);
        Assert.Contains("\"reason\":\"above_ceiling\"", json);
        Assert.Contains("\"ceiling_ann\":0.16", json);

        using var check = f.Open();
        Assert.Empty(check.Strategies.ToList());        // nothing admitted
        Assert.Empty(check.TrialsRegistry.ToList());    // no trial spent on a claim the arena cannot host
        Assert.Empty(check.JournalEntries.ToList());    // and no orphaned hypothesis (atomic command)
    }

    [Fact]
    public async Task CreateCandidate_InlineHypothesis_ButDuplicateStrategyId_Returns422_AndLeavesNoOrphanHypothesis()
    {
        using var f = new ApiArenaFactory();
        using (var db = f.Open())   // the strategy already exists ⇒ CreateCandidate will throw after the hypothesis write
        {
            db.Strategies.Add(new StrategyRow { StrategyId = "dup", Family = "m", ConfigJson = "{}", ExitPolicyJson = "{}", CreatedOn = "2026-02-01", Status = "candidate" });
            db.SaveChanges();
        }

        var body = "{\"strategy_id\":\"dup\",\"hypothesis\":{\"title\":\"t\",\"body_md\":\"b\",\"metric\":\"alpha\",\"evidence_window_days\":252}}";
        var response = await f.CreateClient().PostAsync("/api/v1/candidates", Body(body));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var check = f.Open();
        Assert.Empty(check.JournalEntries.ToList());   // the hypothesis INSERT was rolled back — no orphan (atomic command)
    }

    [Fact]
    public async Task FR34_CreateCandidate_WhileARunIsLive_Returns409()
    {
        using var f = new ApiArenaFactory();
        using (var db = f.Open())
        {
            var state = db.WorkerState.Find(1)!;
            state.RunInProgress = 1;
            state.HeartbeatAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture); // fresh ⇒ IsLive
            db.SaveChanges();
        }

        var response = await f.CreateClient().PostAsync("/api/v1/candidates",
            Body("{\"strategy_id\":\"cand:x\",\"unregistered\":true}"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":\"conflict\"", json);

        using var check = f.Open();
        Assert.Empty(check.Strategies.ToList());   // the command never wrote — it did not race the run
    }
}
