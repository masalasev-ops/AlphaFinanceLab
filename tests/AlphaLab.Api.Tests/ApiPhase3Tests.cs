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
