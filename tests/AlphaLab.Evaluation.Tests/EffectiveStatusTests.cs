using AlphaLab.Data.Entities;

namespace AlphaLab.Evaluation.Tests;

/// <summary>
/// Phase-4 review: replay's effective status must be a pure function of the store's QUARANTINED
/// replay records — never of the forward-evolved `strategies.status` column, which the forward
/// monitor mutates by design. A forward retire between two replay launches must not change the
/// replay roster (determinism = f(inputs, watermark, seeds), D95).
/// </summary>
public class EffectiveStatusTests
{
    private static void Seed(AlphaLab.Data.AlphaLabDbContext db)
    {
        db.Strategies.AddRange(
            new StrategyRow
            {
                StrategyId = "buyhold:cw", Family = "passive", ConfigJson = "{}", ExitPolicyJson = "{}",
                CreatedOn = "2026-01-02", Status = "baseline",
            },
            new StrategyRow
            {
                StrategyId = "threshold:sma50", Family = "passive", ConfigJson = "{}", ExitPolicyJson = "{}",
                CreatedOn = "2026-01-02", Status = "retired",   // forward-evolved: the monitor retired it
            },
            new StrategyRow
            {
                StrategyId = "momentum:live1", Family = "momentum", ConfigJson = "{}", ExitPolicyJson = "{}",
                CreatedOn = "2026-01-02", Status = "live",      // forward-evolved: promoted
            });
        db.SaveChanges();
    }

    [Fact]
    public void Replay_BaseIsTheSeededRole_ForwardEvolutionInvisible()
    {
        using var arena = new EvalArena();
        using var db = arena.Open();
        Seed(db);

        var replay = EffectiveStatus.Resolve(db, "replay");

        // Forward 'retired' and 'live' both resolve to the seeded 'candidate' — the sealed room
        // starts every non-benchmark strategy from scratch, whatever forward later did to it.
        Assert.Equal("candidate", replay["threshold:sma50"]);
        Assert.Equal("candidate", replay["momentum:live1"]);
        Assert.Equal("baseline", replay["buyhold:cw"]);

        // The forward view is untouched: live resolves the raw column verbatim.
        var live = EffectiveStatus.Resolve(db, "live");
        Assert.Equal("retired", live["threshold:sma50"]);
        Assert.Equal("live", live["momentum:live1"]);
    }

    [Fact]
    public void Replay_EvolvesOnlyThroughItsOwnQuarantinedRecords()
    {
        using var arena = new EvalArena();
        using var db = arena.Open();
        Seed(db);

        // The replay's OWN promote + retire records drive the overlay.
        db.GoLiveLog.Add(new GoLiveLogRow
        {
            AsOf = "2015-06-01", Promoted = "momentum:live1", Verdict = "GoLive", EvidenceJson = "{}", RunKind = "replay",
        });
        db.OverfittingStatus.Add(new OverfittingStatusRow
        {
            AsOf = "2015-09-01", StrategyId = "threshold:sma50", Status = "retired", TriggerJson = "{}", RunKind = "replay",
        });
        db.SaveChanges();

        var replay = EffectiveStatus.Resolve(db, "replay");
        Assert.Equal("live", replay["momentum:live1"]);      // replay-promoted from its 'candidate' base
        Assert.Equal("retired", replay["threshold:sma50"]);  // replay-retired — its own record, not forward's
    }
}
