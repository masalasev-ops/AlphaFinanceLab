using AlphaLab.Core.Domain;

namespace AlphaLab.Strategies.Tests;

/// <summary>One session's prices for one security in the fake market. Both bases present (D30):
/// raw for the ledger, adjusted for signals.</summary>
internal sealed record FakeBar(double RawOpen, double RawClose, double AdjClose, long Volume);

/// <summary>
/// A controllable in-memory market backing <see cref="FakeFeatureView"/> — the shared bar store the
/// per-day views read. This is a TEST DOUBLE for driving the pure funnel/ledger engines; the real
/// point-in-time reader (<c>BarFeatureView</c>, Data) has its own F-LEAK tests. Sessions are added in
/// order and define the trading calendar for the acceptance harness.
/// </summary>
internal sealed class FakeMarket
{
    public List<string> Sessions { get; } = [];
    private readonly Dictionary<(long Id, string Date), FakeBar> _bars = [];

    public void Add(long id, string date, double rawOpen, double rawClose, double adjClose, long volume)
    {
        _bars[(id, date)] = new FakeBar(rawOpen, rawClose, adjClose, volume);
        if (!Sessions.Contains(date)) Sessions.Add(date);
    }

    public FakeBar? Get(long id, string date) => _bars.TryGetValue((id, date), out var b) ? b : null;

    /// <summary>Every security id with a bar on <paramref name="date"/>.</summary>
    public IEnumerable<long> IdsOn(string date) =>
        _bars.Keys.Where(k => k.Date == date).Select(k => k.Id).Distinct();

    public FakeFeatureView At(DateOnly asOf, string watermark) => new(this, asOf, watermark);
}

/// <summary>
/// A pure in-memory <see cref="IFeatureView"/> over a <see cref="FakeMarket"/>, for exercising the
/// funnel + ledger + cost engines without a database. Lenient on window completeness (it averages the
/// available trailing sessions rather than requiring a full 21) so a short acceptance span still yields
/// a usable ADV/σ — the real reader's strictness is tested elsewhere.
/// </summary>
internal sealed class FakeFeatureView(FakeMarket market, DateOnly asOf, string watermark) : IFeatureView
{
    public DateOnly AsOf => asOf;
    public string Watermark => watermark;

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd");

    private IReadOnlyList<string> SessionsUpToAsOf()
    {
        var cutoff = Iso(asOf);
        return market.Sessions.Where(s => string.CompareOrdinal(s, cutoff) <= 0).OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<SecurityId> PricedOn(DateOnly date) =>
        market.IdsOn(Iso(date)).OrderBy(x => x).Select(x => new SecurityId(x)).ToList();

    public double? AdjClose(SecurityId id, DateOnly date) => market.Get(id.Value, Iso(date))?.AdjClose;
    public double? RawClose(SecurityId id, DateOnly date) => market.Get(id.Value, Iso(date))?.RawClose;
    public double? RawOpen(SecurityId id, DateOnly date) => market.Get(id.Value, Iso(date))?.RawOpen;

    public IReadOnlyList<double> AdjCloseSeries(SecurityId id, int sessions)
    {
        var closes = new List<double>();
        foreach (var s in SessionsUpToAsOf())
        {
            if (market.Get(id.Value, s) is { } b) closes.Add(b.AdjClose);
        }
        return closes.Count <= sessions ? closes : closes.Skip(closes.Count - sessions).ToList();
    }

    public double? Adv21Shares(SecurityId id)
    {
        var vols = SessionsUpToAsOf()
            .Select(s => market.Get(id.Value, s))
            .Where(b => b is not null)
            .Select(b => (double)b!.Volume)
            .TakeLast(21)
            .ToList();
        return vols.Count == 0 ? null : vols.Average();
    }

    public double? Adv21Notional(SecurityId id)
    {
        var shares = Adv21Shares(id);
        var close = RawClose(id, asOf);
        return shares is { } sh && close is { } px ? sh * px : null;
    }

    public double? RealizedVolDaily(SecurityId id, int window)
    {
        var series = AdjCloseSeries(id, window + 1);
        return PriceStatistics.RealizedVolDaily(series);
    }
}
