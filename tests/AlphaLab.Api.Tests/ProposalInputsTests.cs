using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AlphaLab.Data.Entities;
using AlphaLab.Evaluation.Candidates;

namespace AlphaLab.Api.Tests;

/// <summary>
/// The D110 proposal-quality INPUTS at the endpoint (checkpoint 5.7): the pre-registered prior, the
/// pin-before-proposal discipline, and D113's uncomputable-floor refusal.
///
/// **The scorer is deliberately absent from these tests because it is deliberately absent from the
/// build.** What is asserted here is that the inputs the scorer will need are captured, refused when
/// missing, and never defaulted — which is the whole content of "the chained criterion has no missing
/// first link".
/// </summary>
public class ProposalInputsTests
{
    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>Pin both score parameters, as the Worker verb would. Without this every proposal is
    /// refused, which is the point of `FX-ProposalScorePinBeforeProposal`'s negative half.</summary>
    private static void Pin(ApiArenaFactory f)
    {
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
        db.SaveChanges();
    }

    /// <summary>A σ estimate, so the arena has a computable detectability floor. Without one the endpoint
    /// correctly refuses every proposal as unscorable.</summary>
    private static void SeedSigma(ApiArenaFactory f)
    {
        using var db = f.Open();
        db.PowerReports.Add(new PowerReportRow
        {
            AsOf = "2026-07-31", StrategyA = "cand:a", StrategyB = "buyhold:cw", TDays = 500,
            SigmaLr = 0.0008, NwLag = 21, MdeAnn = 0.03, ObservedGapAnn = 0.0,
            Verdict = "TooEarly", RunKind = "replay",
        });
        db.SaveChanges();
    }

    private static ApiArenaFactory Ready()
    {
        var f = new ApiArenaFactory();
        Pin(f);
        SeedSigma(f);
        return f;
    }

    [Fact]
    public async Task FX_ProposalPriorRequired_NoPrior_IsRefused_AndWritesNothing()
    {
        using var f = Ready();

        var response = await f.CreateClient().PostAsync("/api/v1/analysis/hypotheses",
            Body("""{"parent_finding":"f-311"}"""));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("prior_prob", json, StringComparison.Ordinal);

        using var db = f.Open();
        Assert.Empty(db.Jobs.ToList());
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public async Task FX_ProposalPriorRequired_AnEndpointPrior_IsRefusedNotClamped(double prior)
    {
        // Refused rather than clamped, and that distinction is the substantive one: a clamped INPUT would
        // be a number nobody stated being scored as though they had. The CLAMP that does exist
        // (Kpi.ProposalPriorClamp) bounds the PENALTY inside the scorer, which is a different job.
        using var f = Ready();

        var response = await f.CreateClient().PostAsync("/api/v1/analysis/hypotheses",
            Body($$"""{"parent_finding":"f-311","prior_prob":{{prior.ToString(CultureInfo.InvariantCulture)}}}"""));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var db = f.Open();
        Assert.Empty(db.Jobs.ToList());
    }

    [Fact]
    public async Task FX_ProposalPriorRequired_AValidPrior_Proceeds_AndTravelsToTheWorker()
    {
        using var f = Ready();

        var response = await f.CreateClient().PostAsync("/api/v1/analysis/hypotheses",
            Body("""{"parent_finding":"f-311","prior_prob":0.35}"""));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // The prior must reach the executor, which is what stamps it on the draft — an accepted request
        // whose prior was dropped in transit would produce an unscorable proposal that LOOKS scorable.
        using var db = f.Open();
        var job = Assert.Single(db.Jobs.ToList());
        Assert.Contains("0.35", job.RequestJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FX_ProposalScorePinBeforeProposal_Unpinned_Refuses_NamingTheKeys_WritingZeroRows()
    {
        using var f = new ApiArenaFactory();   // deliberately NOT pinned
        SeedSigma(f);

        var response = await f.CreateClient().PostAsync("/api/v1/analysis/hypotheses",
            Body("""{"parent_finding":"f-311","prior_prob":0.35}"""));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":\"proposal_thresholds_unpinned\"", json);

        // NAMED, not merely reported missing: the operator's next act is a config write that has to name
        // the key anyway, so "some threshold is missing" just sends them looking.
        Assert.Contains(ProposalScoreKeys.PriorClamp, json, StringComparison.Ordinal);
        Assert.Contains(ProposalScoreKeys.ScoreMinClosed, json, StringComparison.Ordinal);

        using var db = f.Open();
        Assert.Empty(db.JournalEntries.ToList());
        Assert.Empty(db.Jobs.ToList());
    }

    [Fact]
    public async Task FX_ProposalScorePinBeforeProposal_PartiallyPinned_StillRefuses_NamingOnlyTheMissingOne()
    {
        using var f = new ApiArenaFactory();
        SeedSigma(f);
        using (var db = f.Open())
        {
            db.Config.Add(new ConfigRow
            {
                Key = ProposalScoreKeys.PriorClamp, ValueJson = "0.02", Version = 1,
                ChangedOn = "2026-08-01T00:00:00Z", Reason = "test",
            });
            db.SaveChanges();
        }

        var response = await f.CreateClient().PostAsync("/api/v1/analysis/hypotheses",
            Body("""{"parent_finding":"f-311","prior_prob":0.35}"""));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains(ProposalScoreKeys.ScoreMinClosed, json, StringComparison.Ordinal);
        Assert.DoesNotContain($"\"{ProposalScoreKeys.PriorClamp}\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FX_ProposalScorePinBeforeProposal_Pinned_Proceeds()
    {
        // The positive half. A refusal test with no matching pass test proves only that something is
        // blocked, not that the block has a key.
        using var f = Ready();

        var response = await f.CreateClient().PostAsync("/api/v1/analysis/hypotheses",
            Body("""{"parent_finding":"f-311","prior_prob":0.35}"""));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task D113_NoComputableFloor_IsRefused_AndCounted_ThroughD112sMachinery()
    {
        using var f = new ApiArenaFactory();
        Pin(f);
        // No power_reports row ⇒ DetectabilityGate returns unassessed_no_sigma ⇒ no floor exists to stamp.
        // Phase 4's calibration wrote replay rows, so this is satisfied in the live arena — which is
        // exactly why it is ASSERTED here rather than assumed.

        var response = await f.CreateClient().PostAsync("/api/v1/analysis/hypotheses",
            Body("""{"parent_finding":"f-311","prior_prob":0.35}"""));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains($"\"code\":\"{AnalysisEndpoints.UnscorableCode}\"", json);

        using var db = f.Open();
        Assert.Empty(db.Jobs.Where(j => j.Status == "queued").ToList());

        // Counted through D112's machinery — no third column, no new mechanism — but under its OWN code,
        // because an unclosed outcome and an uncalibrated arena are different debts owed by different
        // people.
        Assert.Equal(1, await AnalysisEndpoints.CountUnscorableRefusalsAsync(db));
        Assert.Equal(0, await AnalysisEndpoints.CountEvidenceDietRefusalsAsync(db));
    }

    [Fact]
    public async Task ThePinCheckPrecedesTheBudget_SoAConfigErrorIsNotHiddenByAnExhaustedDay()
    {
        // Ordering assertion. A 503 on a day whose parameters were never pinned would send the operator
        // to wait for tomorrow, when the real fix is a config write that has nothing to do with the day.
        using var f = new ApiArenaFactory();
        SeedSigma(f);
        using (var db = f.Open())
        {
            db.LlmBudgetLog.Add(new LlmBudgetLogRow
            {
                AsOf = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Calls = 99, Tokens = 1, CostUsd = 99.0,
            });
            db.SaveChanges();
        }

        var response = await f.CreateClient().PostAsync("/api/v1/analysis/hypotheses",
            Body("""{"parent_finding":"f-311","prior_prob":0.35}"""));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("proposal_thresholds_unpinned",
            doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
