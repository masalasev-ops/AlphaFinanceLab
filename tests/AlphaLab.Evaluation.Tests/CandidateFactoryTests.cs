using AlphaLab.Evaluation.Candidates;

namespace AlphaLab.Evaluation.Tests;

public class CandidateFactoryTests
{
    private static CandidateSpec Spec(string id = "mom:L126:K21") => new(id, "momentum", "{\"lookback\":126}", "{}", 21);

    [Fact]
    public void FR28_Fork_WithoutHypothesisOrFlag_Fails()
    {
        using var arena = new EvalArena();
        using var db = arena.Open();
        var factory = new CandidateFactory(db);

        Assert.Throws<InvalidOperationException>(() =>
            factory.CreateCandidate(Spec(), hypothesisEntryId: null, unregistered: false, createdOn: "2026-03-10"));

        Assert.Empty(db.Strategies.ToList());
        Assert.Empty(db.TrialsRegistry.ToList());
    }

    [Fact]
    public void CreateCandidate_WithLockedHypothesis_Succeeds_RegistersTrial_AndLinksTheHypothesis()
    {
        using var arena = new EvalArena();
        using var db = arena.Open();
        var factory = new CandidateFactory(db);

        var hid = factory.RegisterHypothesis("2026-03-10", "Momentum beats cap-weight",
            "12-1 momentum should earn positive net beta-adjusted alpha.", metric: "beta_adjusted_alpha", evidenceWindowDays: 252);

        var strategy = factory.CreateCandidate(Spec(), hypothesisEntryId: hid, unregistered: false, createdOn: "2026-03-10", trialKind: "new");

        Assert.Equal("candidate", strategy.Status);
        Assert.Single(db.Strategies.Where(s => s.StrategyId == "mom:L126:K21"));
        var trial = Assert.Single(db.TrialsRegistry.ToList());
        Assert.Equal("new", trial.Kind);
        Assert.Equal("mom:L126:K21", trial.StrategyId);
        Assert.Equal("mom:L126:K21", db.JournalEntries.Single(j => j.EntryId == hid).StrategyId);   // linked
    }

    [Fact]
    public void CreateCandidate_Unregistered_Succeeds_AndMarksConfigJsonPermanently()
    {
        using var arena = new EvalArena();
        using var db = arena.Open();
        var factory = new CandidateFactory(db);

        var strategy = factory.CreateCandidate(Spec("adhoc:1"), hypothesisEntryId: null, unregistered: true, createdOn: "2026-03-10");

        Assert.Contains("\"unregistered\":true", strategy.ConfigJson.Replace(" ", ""));
        Assert.Single(db.TrialsRegistry.ToList());
    }

    [Fact]
    public void CreateCandidate_WithUnlockedHypothesis_Fails()
    {
        using var arena = new EvalArena();
        using var db = arena.Open();

        // A hypothesis row that was never locked cannot back a candidate (pre-registration must be immutable).
        db.JournalEntries.Add(new AlphaLab.Data.Entities.JournalEntryRow
        {
            CreatedOn = "2026-03-10", Kind = "hypothesis", Title = "draft", BodyMd = "…", Metric = "alpha", Locked = false,
        });
        db.SaveChanges();
        var hid = db.JournalEntries.Single().EntryId;

        Assert.Throws<InvalidOperationException>(() =>
            new CandidateFactory(db).CreateCandidate(Spec(), hid, unregistered: false, "2026-03-10"));
    }

    [Fact]
    public void CreateCandidate_Fork_SetsParentAndForkTrialKind()
    {
        using var arena = new EvalArena();
        using var db = arena.Open();
        var factory = new CandidateFactory(db);

        var spec = new CandidateSpec("mom:L252:K21", "momentum", "{\"lookback\":252}", "{}", 21, ParentStrategyId: "mom:L126:K21");
        factory.CreateCandidate(spec, hypothesisEntryId: null, unregistered: true, "2026-03-10", trialKind: "fork");

        var strategy = db.Strategies.Single(s => s.StrategyId == "mom:L252:K21");
        Assert.Equal("mom:L126:K21", strategy.ParentStrategyId);
        Assert.Equal("fork", db.TrialsRegistry.Single().Kind);
    }

    [Fact]
    public void CreateCandidate_DuplicateStrategyId_Fails()
    {
        using var arena = new EvalArena();
        using var db = arena.Open();
        var factory = new CandidateFactory(db);
        factory.CreateCandidate(Spec(), null, unregistered: true, "2026-03-10");

        Assert.Throws<InvalidOperationException>(() =>
            factory.CreateCandidate(Spec(), null, unregistered: true, "2026-03-11"));   // same id
    }
}
