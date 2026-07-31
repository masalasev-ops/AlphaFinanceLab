using AlphaLab.Core.Domain;
using AlphaLab.Core.Signals;

namespace AlphaLab.Core.Tests;

/// <summary>
/// A minimal <see cref="IFeatureView"/> over hand-built adjusted-close series, for the FX-SignalScorers
/// fixtures. Every member a scorer is NOT entitled to read throws — so the fixtures also assert that the
/// scorers read only adjusted closes and realized vol, which is what makes them pure functions of the
/// watermark-bounded price history (rule 4 / F-DET).
/// </summary>
internal sealed class SignalFakeFeatureView(DateOnly asOf, string watermark) : IFeatureView
{
    private readonly Dictionary<long, List<double>> _series = [];

    public DateOnly AsOf => asOf;
    public string Watermark => watermark;

    /// <summary>Add an oldest-first adjusted-close series for a security.</summary>
    public SignalFakeFeatureView With(long id, IEnumerable<double> closes)
    {
        _series[id] = [.. closes];
        return this;
    }

    /// <summary>A geometric ramp: P_i = start·(1+g)^i, i = 0..count-1. Closed-form, so a fixture can
    /// assert the exact expected return rather than a recorded number.</summary>
    public SignalFakeFeatureView WithRamp(long id, int count, double g, double start = 100.0)
    {
        var s = new List<double>(count);
        for (var i = 0; i < count; i++) s.Add(start * Math.Pow(1.0 + g, i));
        return With(id, s);
    }

    public IReadOnlyList<double> AdjCloseSeries(SecurityId id, int sessions)
    {
        if (!_series.TryGetValue(id.Value, out var s)) return [];
        return s.Count <= sessions ? s : s.Skip(s.Count - sessions).ToList();
    }

    public double? RealizedVolDaily(SecurityId id, int window) =>
        PriceStatistics.RealizedVolDaily(AdjCloseSeries(id, window + 1));

    // A scorer that reaches for any of these is reading something it has not declared.
    public IReadOnlyList<SecurityId> PricedOn(DateOnly date) => throw new NotSupportedException();
    public double? AdjClose(SecurityId id, DateOnly date) => throw new NotSupportedException();
    public double? RawClose(SecurityId id, DateOnly date) => throw new NotSupportedException();
    public double? RawOpen(SecurityId id, DateOnly date) => throw new NotSupportedException();
    public double? Adv21Shares(SecurityId id) => throw new NotSupportedException();
    public double? Adv21Notional(SecurityId id) => throw new NotSupportedException();
}

/// <summary>
/// FX-SignalScorers (FR-43/D91): each of the seven v1 <see cref="ISignal"/> implementations reproduces a
/// HAND-COMPUTED result on a tiny synthetic bar set, with no database and no external dependency. The
/// fixtures use closed-form geometric ramps so the expected value is arithmetic a reader can check —
/// `P_last/P_first − 1 = (1+g)^n − 1` — rather than a number recorded from a previous run.
/// </summary>
public class SignalScorerTests
{
    private static readonly DateOnly AsOf = new(2026, 1, 30);
    private const string Wm = "2026-01-30T22:00:00Z";
    private static SignalFakeFeatureView View() => new(AsOf, Wm);
    private static SecurityId Id(long v) => new(v);
    private static readonly SignalContext NoProxy = new();

    private static IReadOnlyList<SecurityId> Ids(params long[] ids) => [.. ids.Select(Id)];

    [Fact]
    public void Registry_HoldsExactlyTheSevenPreRegisteredV1Signals()
    {
        // The v1 set is pre-registered (§24.3): fixed before any grade exists. TSMOM is deliberately
        // absent — a time-series rule, for which rank-IC is the wrong grade.
        Assert.Equal(
            new[] { "mom:L252s21", "mom:L126", "rev:L21", "lowvol:L252", "brk:L252", "resmom:L252", "bab:L252" },
            SignalRegistry.V1.Select(s => s.SignalId).ToArray());
        Assert.All(SignalRegistry.V1, s => Assert.False(string.IsNullOrWhiteSpace(s.Family)));
        Assert.All(SignalRegistry.V1, s => Assert.NotEmpty(s.Params));
        Assert.All(SignalRegistry.V1, s => Assert.Equal(SignalRegistry.CodeVersion, s.CodeVersion));
        Assert.Equal("mom:L126", SignalRegistry.ById("mom:L126")!.SignalId);
        Assert.Null(SignalRegistry.ById("tsmom:L252"));
    }

    [Fact]
    public void MomSkip_IsTheTrailing252ReturnSkippingTheLast21()
    {
        // 274 sessions of a ramp: the score reads P[t-21]/P[t-273] - 1 = (1+g)^252 - 1. The skip is the
        // rule's substance, so the fixture makes the LAST 21 sessions crash: a scorer that failed to skip
        // would report a large negative number instead.
        const double g = 0.002;
        var rising = new List<double>();
        for (var i = 0; i < 274; i++) rising.Add(100.0 * Math.Pow(1 + g, i));
        for (var i = 274 - 21; i < 274; i++) rising[i] = 1.0;   // recent crash, must be skipped

        var view = View().With(1, rising).WithRamp(2, 274, 0.001);
        var scores = new MomentumSkipSignal().Score(Ids(1, 2), view, NoProxy);

        Assert.Equal(Math.Pow(1 + g, 252) - 1.0, scores[Id(1)], 9);          // crash skipped
        Assert.Equal(Math.Pow(1.001, 252) - 1.0, scores[Id(2)], 9);
        Assert.True(scores[Id(1)] > scores[Id(2)]);                           // stronger momentum ranks higher
    }

    [Fact]
    public void MomSkip_OmitsANameWithTooLittleHistory()
    {
        // Absence is the honest answer (catalog §2): 273 sessions cannot define a 252+21 window.
        var view = View().WithRamp(1, 273, 0.001).WithRamp(2, 274, 0.001);
        var scores = new MomentumSkipSignal().Score(Ids(1, 2), view, NoProxy);

        Assert.DoesNotContain(Id(1), scores.Keys);
        Assert.Contains(Id(2), scores.Keys);
    }

    [Fact]
    public void MediumMom_IsTheTrailing126Return()
    {
        var view = View().WithRamp(1, 127, 0.003).WithRamp(2, 127, -0.001);
        var scores = new MediumMomentumSignal().Score(Ids(1, 2), view, NoProxy);

        Assert.Equal(Math.Pow(1.003, 126) - 1.0, scores[Id(1)], 9);
        Assert.Equal(Math.Pow(0.999, 126) - 1.0, scores[Id(2)], 9);
        Assert.True(scores[Id(2)] < 0);
    }

    [Fact]
    public void ShortReversal_NegatesTheReturn_SoRecentLosersRankHigh()
    {
        // The sign IS the rule (De Bondt-Thaler direction). A rank-IC computed on the un-negated return
        // would measure momentum and report the reversal hypothesis backwards.
        var view = View().WithRamp(1, 22, 0.01).WithRamp(2, 22, -0.01);
        var scores = new ShortReversalSignal().Score(Ids(1, 2), view, NoProxy);

        Assert.Equal(-(Math.Pow(1.01, 21) - 1.0), scores[Id(1)], 9);
        Assert.Equal(-(Math.Pow(0.99, 21) - 1.0), scores[Id(2)], 9);
        Assert.True(scores[Id(2)] > scores[Id(1)]);   // the loser ranks HIGH
    }

    [Fact]
    public void LowVol_InvertsRealizedVol_SoQuietNamesRankHigh()
    {
        // A constant-growth ramp has ZERO realized vol; an alternating series has positive vol.
        var choppy = new List<double>();
        for (var i = 0; i < 253; i++) choppy.Add(i % 2 == 0 ? 100.0 : 110.0);

        var view = View().WithRamp(1, 253, 0.001).With(2, choppy);
        var scores = new LowVolSignal().Score(Ids(1, 2), view, NoProxy);

        Assert.Equal(0.0, scores[Id(1)], 9);          // a pure ramp has no return dispersion
        Assert.True(scores[Id(2)] < 0);               // choppy scores negative (vol inverted)
        Assert.True(scores[Id(1)] > scores[Id(2)]);   // quiet ranks higher
    }

    [Fact]
    public void Breakout_IsProximityToTheTrailingHigh_OneAtTheHigh()
    {
        // A monotone riser sits AT its 252-session high => exactly 1.0. A name that peaked and fell back
        // scores its fraction of that peak — here 50/100.
        var peaked = new List<double>();
        for (var i = 0; i < 251; i++) peaked.Add(100.0);
        peaked.Add(50.0);

        var view = View().WithRamp(1, 252, 0.001).With(2, peaked);
        var scores = new BreakoutSignal().Score(Ids(1, 2), view, NoProxy);

        Assert.Equal(1.0, scores[Id(1)], 9);
        Assert.Equal(0.5, scores[Id(2)], 9);
    }

    [Fact]
    public void ResidualMomentum_RemovesTheMarketComponent_TrackerScoresZero()
    {
        // A name whose daily returns EQUAL the market's has beta 1, so its residual return is ~0 however
        // much the market rose. That is the whole claim of the rule, and it is what a raw-momentum
        // scorer would get wrong.
        const long proxy = 900;
        var view = View().WithRamp(proxy, 253, 0.002)
                         .WithRamp(1, 253, 0.002)      // tracks the market exactly => beta 1, residual 0
                         .WithRamp(2, 253, 0.004);     // outruns it
        var scores = new ResidualMomentumSignal().Score(Ids(1, 2), view, new SignalContext(Id(proxy)));

        Assert.Equal(0.0, scores[Id(1)], 6);
        Assert.True(scores[Id(2)] > 0);
    }

    [Fact]
    public void Bab_IsBetaInverted_SoLowBetaRanksHigh()
    {
        // Daily returns exactly 1x and ~2x the market give beta 1 and ~2; the score negates them.
        const long proxy = 900;
        var market = new List<double>();
        var levered = new List<double>();
        double m = 100, l = 100;
        for (var i = 0; i < 253; i++)
        {
            market.Add(m); levered.Add(l);
            var r = i % 2 == 0 ? 0.01 : -0.005;    // a varying market, so beta is identified
            m *= 1 + r; l *= 1 + 2 * r;
        }

        var view = View().With(proxy, market).With(1, market).With(2, levered);
        var scores = new BettingAgainstBetaSignal().Score(Ids(1, 2), view, new SignalContext(Id(proxy)));

        Assert.Equal(-1.0, scores[Id(1)], 6);
        Assert.Equal(-2.0, scores[Id(2)], 2);
        Assert.True(scores[Id(1)] > scores[Id(2)]);   // low beta ranks higher
    }

    [Fact]
    public void ProxyDependentScorers_WithNoProxy_ReturnNothing_RatherThanDegrading()
    {
        // Fail closed (rule 10): silently falling back to raw momentum would publish a DIFFERENT signal
        // under resmom's name, which is worse than publishing nothing.
        var view = View().WithRamp(1, 253, 0.002);

        Assert.Empty(new ResidualMomentumSignal().Score(Ids(1), view, NoProxy));
        Assert.Empty(new BettingAgainstBetaSignal().Score(Ids(1), view, NoProxy));
    }

    [Fact]
    public void EveryScorer_IsDeterministic_SameInputsSameOutput()
    {
        // F-DET: the scorers are pure over the watermark-bounded view, which is what makes
        // FX-SignalIcDeterminism achievable one layer up.
        const long proxy = 900;
        var view = View().WithRamp(proxy, 300, 0.001).WithRamp(1, 300, 0.002).WithRamp(2, 300, 0.0005);
        var ctx = new SignalContext(Id(proxy));

        foreach (var signal in SignalRegistry.V1)
        {
            var a = signal.Score(Ids(1, 2), view, ctx);
            var b = signal.Score(Ids(1, 2), view, ctx);
            Assert.Equal(a.OrderBy(kv => kv.Key.Value).ToList(), b.OrderBy(kv => kv.Key.Value).ToList());
        }
    }
}
