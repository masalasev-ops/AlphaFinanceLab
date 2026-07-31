using AlphaLab.Core.Domain;
using AlphaLab.Core.Signals;
using AlphaLab.Data;
using AlphaLab.Data.Services;
using AlphaLab.Evaluation.Numerics;
using AlphaLab.Evaluation.Signals;

namespace AlphaLab.Evaluation.Tests;

/// <summary>
/// FR-44 (D91): the rank-IC engine. Covers <c>FX-SignalIcDeterminism</c> (a day recomputed twice at one
/// watermark is byte-identical), <c>FX-SignalIcPit</c> (a name outside membership as-of contributes
/// nothing and `n` excludes it), and the finding-294 pool rule (the graded set is the SCORABLE set).
/// </summary>
public class SignalIcEngineTests
{
    private const string Wm = "2026-06-30T22:00:00Z";

    /// <summary>An in-memory feature view over per-day adjusted closes, plus a priced-set per day so
    /// Stage-1 eligibility is exercised for real rather than stubbed past.</summary>
    private sealed class EngineView(DateOnly asOf, IReadOnlyDictionary<(long, string), double> px) : IFeatureView
    {
        public DateOnly AsOf => asOf;
        public string Watermark => Wm;
        private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd");

        public IReadOnlyList<SecurityId> PricedOn(DateOnly date) =>
            px.Keys.Where(k => k.Item2 == Iso(date)).Select(k => new SecurityId(k.Item1))
              .Distinct().OrderBy(s => s.Value).ToList();

        public double? AdjClose(SecurityId id, DateOnly date) =>
            px.TryGetValue((id.Value, Iso(date)), out var v) ? v : null;

        public IReadOnlyList<double> AdjCloseSeries(SecurityId id, int sessions)
        {
            var closes = px.Where(kv => kv.Key.Item1 == id.Value && string.CompareOrdinal(kv.Key.Item2, Iso(asOf)) <= 0)
                .OrderBy(kv => kv.Key.Item2, StringComparer.Ordinal).Select(kv => kv.Value).ToList();
            return closes.Count <= sessions ? closes : closes.Skip(closes.Count - sessions).ToList();
        }

        public double? RawClose(SecurityId id, DateOnly date) => AdjClose(id, date);
        public double? RawOpen(SecurityId id, DateOnly date) => AdjClose(id, date);
        public double? Adv21Shares(SecurityId id) => 1_000_000;
        public double? Adv21Notional(SecurityId id) => 100_000_000;
        public double? RealizedVolDaily(SecurityId id, int window) =>
            PriceStatistics.RealizedVolDaily(AdjCloseSeries(id, window + 1));
    }

    private sealed class FixedMembership(IReadOnlyList<long> ids) : IIndexMembershipRead
    {
        public IReadOnlyList<long> MembersAsOf(string date) => ids;
    }

    /// <summary>
    /// 40 sessions for 6 names. Each name's 22-session trailing return is distinct AND its forward
    /// 5-day return is monotone in that same ordering, so `rev:L21` (which NEGATES the return) must
    /// produce a strongly NEGATIVE rank-IC. Using a signal whose sign is part of its rule makes the
    /// fixture catch a sign error, which a symmetric signal would not.
    /// </summary>
    private static (List<DateOnly> Sessions, Dictionary<(long, string), double> Px) Market()
    {
        var sessions = new List<DateOnly>();
        var start = new DateOnly(2026, 1, 5);
        for (var i = 0; i < 40; i++) sessions.Add(start.AddDays(i));

        var px = new Dictionary<(long, string), double>();
        // Name i grows at i*0.001/day: higher id => stronger past return AND stronger forward return.
        for (long id = 1; id <= 6; id++)
        {
            var g = id * 0.001;
            for (var t = 0; t < sessions.Count; t++)
            {
                px[(id, sessions[t].ToString("yyyy-MM-dd"))] = 100.0 * Math.Pow(1 + g, t);
            }
        }
        return (sessions, px);
    }

    private static SignalIcEngine Engine(AlphaLabDbContext db, IReadOnlyList<long> members,
        Dictionary<(long, string), double> px) =>
        new(db, new FixedMembership(members), asOf => new EngineView(asOf, px));

    [Fact]
    public void FX_SignalIcDeterminism_SameDayRecomputedTwice_IsByteIdentical()
    {
        using var arena = new EvalArena();
        using var db = arena.Open();
        var (sessions, px) = Market();
        var engine = Engine(db, [1, 2, 3, 4, 5, 6], px);
        var asOf = sessions[25];

        var a = engine.GradeDay(asOf, [new ShortReversalSignal()], [5], sessions, null);
        var b = engine.GradeDay(asOf, [new ShortReversalSignal()], [5], sessions, null);

        Assert.NotEmpty(a);
        Assert.Equal(a, b);   // record struct equality == byte-identical grades

        // And persisting is idempotent: the second call writes nothing, which is also what makes the
        // FR-45 backfill resumable (the rows themselves are the progress marker).
        Assert.Equal(a.Count, engine.Persist(a));
        Assert.Equal(0, engine.Persist(b));
        Assert.Equal(a.Count, db.SignalIc.Count());
    }

    [Fact]
    public void RankIc_IsNegativeForReversal_WhenPastWinnersKeepWinning()
    {
        // rev:L21 ranks recent LOSERS high. In a market where past winners also win forward, that
        // ranking is exactly wrong, so the rank-IC must be strongly negative. A sign slip in the scorer
        // or in the correlation would surface here as +1.
        using var arena = new EvalArena();
        using var db = arena.Open();
        var (sessions, px) = Market();

        var grades = Engine(db, [1, 2, 3, 4, 5, 6], px)
            .GradeDay(sessions[25], [new ShortReversalSignal()], [5], sessions, null);

        var g = Assert.Single(grades);
        Assert.Equal("rev:L21", g.SignalId);
        Assert.Equal(5, g.HorizonDays);
        Assert.Equal(6, g.N);
        Assert.Equal(-1.0, g.RankIc, 9);   // perfectly inverted ranking
    }

    [Fact]
    public void FX_SignalIcPit_ANameOutsideMembershipAsOf_ContributesNothing_AndNExcludesIt()
    {
        using var arena = new EvalArena();
        using var db = arena.Open();
        var (sessions, px) = Market();

        // Same market, but two names are not index members as-of the grading day.
        var all = Engine(db, [1, 2, 3, 4, 5, 6], px)
            .GradeDay(sessions[25], [new ShortReversalSignal()], [5], sessions, null);
        var narrowed = Engine(db, [1, 2, 3, 4], px)
            .GradeDay(sessions[25], [new ShortReversalSignal()], [5], sessions, null);

        Assert.Equal(6, Assert.Single(all).N);
        Assert.Equal(4, Assert.Single(narrowed).N);   // n follows membership, not the price table
    }

    [Fact]
    public void ThePoolIsTheScorableSet_AnUnpricedNameCannotEnterARanking()
    {
        // finding 294: the priced filter is IMPLIED by the ranking operation, not chosen beside it. A
        // member with no bar on the grading day yields no score, so it cannot be ranked and `n` drops.
        using var arena = new EvalArena();
        using var db = arena.Open();
        var (sessions, px) = Market();
        foreach (var s in sessions) px.Remove((3L, s.ToString("yyyy-MM-dd")));   // name 3 has no prices at all

        var grades = Engine(db, [1, 2, 3, 4, 5, 6], px)
            .GradeDay(sessions[25], [new ShortReversalSignal()], [5], sessions, null);

        Assert.Equal(5, Assert.Single(grades).N);
    }

    [Fact]
    public void AHorizonWhoseForwardWindowIsNotYetResolved_YieldsNoGrade()
    {
        // t+k beyond the calendar means the realized return does not exist yet. Grading it from a
        // shorter window would silently describe a different horizon than the row claims.
        using var arena = new EvalArena();
        using var db = arena.Open();
        var (sessions, px) = Market();

        var grades = Engine(db, [1, 2, 3, 4, 5, 6], px)
            .GradeDay(sessions[^2], [new ShortReversalSignal()], [21], sessions, null);

        Assert.Empty(grades);
    }

    [Fact]
    public void Spearman_UsesMidRanksForTies_AndIsUndefinedWithoutDispersion()
    {
        // Ties are ordinary here (brk:L252 saturates at 1.0 at the high), so the tie rule is part of
        // the measurement rather than an edge case.
        Assert.Equal([1.5, 1.5, 3.0], Statistics.MidRanks([5.0, 5.0, 9.0]));
        Assert.Equal(1.0, Statistics.SpearmanRankCorrelation([1.0, 2.0, 3.0], [10.0, 20.0, 30.0])!.Value, 9);
        Assert.Equal(-1.0, Statistics.SpearmanRankCorrelation([1.0, 2.0, 3.0], [30.0, 20.0, 10.0])!.Value, 9);

        // Every value tied on one side => no information. Null, not 0.0: reporting zero would enter the
        // rolling mean as evidence of no skill rather than as an absence of evidence.
        Assert.Null(Statistics.SpearmanRankCorrelation([1.0, 1.0, 1.0], [1.0, 2.0, 3.0]));
        Assert.Null(Statistics.SpearmanRankCorrelation([1.0], [2.0]));
    }
}
