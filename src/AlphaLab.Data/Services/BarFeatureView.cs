using System.Globalization;
using AlphaLab.Core.Config;
using AlphaLab.Core.Domain;
using AlphaLab.Data.Entities;

namespace AlphaLab.Data.Services;

/// <summary>
/// The <see cref="IFeatureView"/> adapter over the versioned bar store (hard rule 4). Built per
/// (asOf, watermark) — one per strategy-day — and handed to <see cref="IModel"/>s, Stage 1, and the
/// cost path.
///
/// LEAK-PROOF BY CONSTRUCTION, and that is the whole design. This class contains NO point-in-time
/// logic of its own: every read goes through <see cref="IBarReadService"/>, whose rule (latest
/// version WHERE observed_at ≤ watermark) already exists and is already tested by
/// BarVersioningTests / BarCrossSectionTests. There is deliberately no second place for the
/// watermark rule to drift — the failure mode this avoids is a view that "helpfully" reads around
/// the reader and shows a model a correction it could not have seen, which no downstream statistic
/// could detect and which would flatter exactly the days that mattered.
///
/// The asOf bound is enforced here (the reader does not know about it): a request for a date after
/// <see cref="AsOf"/> THROWS rather than returning null. Null would be indistinguishable from "no
/// data", so a leak bug would present as thin history and be silently absorbed by callers that are
/// specifically written to tolerate thin history (catalog §2). A model asking about tomorrow is a
/// defect, not an absence.
///
/// NOT THREAD-SAFE and not long-lived: it memoizes reads for one (asOf, watermark) pair. Build one
/// per run-day; never cache one across days.
/// </summary>
public sealed class BarFeatureView : IFeatureView
{
    private readonly IBarReadService _bars;
    private readonly ICalendarService _calendar;
    private readonly int _advWindowSessions;

    // Memoized per (asOf, watermark) — a funnel day asks for the same windows repeatedly (ADV shares,
    // ADV notional, and realized vol all read the same 21 sessions), and each miss is a SQL round trip.
    private readonly Dictionary<(long Id, int Sessions), IReadOnlyList<BarRow>> _seriesCache = [];
    private readonly Dictionary<(long Id, string Date), BarRow?> _barCache = [];
    private readonly Dictionary<int, DateOnly?> _windowStartCache = [];
    private IReadOnlyList<SecurityId>? _pricedOnAsOf;

    /// <param name="advWindowSessions">The ADV window in SESSIONS, from <c>Costs.AdvWindowDays</c>
    /// (D43 default 21). The "21" in <see cref="Adv21Shares"/> / the <c>capacity_rejections.adv21</c>
    /// column is the system's vocabulary for "the ADV window", fixed by the schema; this parameter is
    /// what the window actually is. They agree at the default.</param>
    public BarFeatureView(
        IBarReadService bars,
        ICalendarService calendar,
        DateOnly asOf,
        string watermark,
        int advWindowSessions)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentException.ThrowIfNullOrWhiteSpace(watermark);
        ArgumentOutOfRangeException.ThrowIfLessThan(advWindowSessions, 2);

        _bars = bars;
        _calendar = calendar;
        _advWindowSessions = advWindowSessions;
        AsOf = asOf;
        Watermark = watermark;
    }

    /// <summary>Convenience ctor taking the D43 window straight from config.</summary>
    public BarFeatureView(IBarReadService bars, ICalendarService calendar, DateOnly asOf, string watermark, CostsOptions costs)
        : this(bars, calendar, asOf, watermark, (costs ?? throw new ArgumentNullException(nameof(costs))).AdvWindowDays)
    {
    }

    public DateOnly AsOf { get; }

    public string Watermark { get; }

    public IReadOnlyList<SecurityId> PricedOn(DateOnly date)
    {
        GuardNotFuture(date, nameof(date));

        // The date-major read (D78): one cross-section serves Stage 1's whole "which members have a
        // bar today?" question, instead of ~101 single-bar round trips.
        if (date == AsOf && _pricedOnAsOf is not null) return _pricedOnAsOf;

        var priced = _bars.GetCrossSection(Iso(date), Watermark)
            .Where(HasBothPriceBases)
            .Select(b => new SecurityId(b.SecurityId))
            .ToList();

        if (date == AsOf) _pricedOnAsOf = priced;
        return priced;
    }

    public double? AdjClose(SecurityId id, DateOnly date)
    {
        GuardNotFuture(date, nameof(date));
        return Bar(id, date)?.AdjClose;
    }

    public double? RawClose(SecurityId id, DateOnly date)
    {
        GuardNotFuture(date, nameof(date));
        return Bar(id, date)?.Close;
    }

    public double? RawOpen(SecurityId id, DateOnly date)
    {
        GuardNotFuture(date, nameof(date));
        return Bar(id, date)?.Open;
    }

    public IReadOnlyList<double> AdjCloseSeries(SecurityId id, int sessions)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sessions, 1);
        return Window(id, sessions)
            .Select(b => b.AdjClose)
            .OfType<double>()      // a bar with no adjusted close carries no signal price
            .ToList();
    }

    /// <summary>
    /// Average daily volume in SHARES over the ADV window ending at <see cref="AsOf"/>. Null unless
    /// the window is COMPLETE — every session present, each with a volume.
    ///
    /// Strict on purpose: a partial window would average fewer days and could read high or low with no
    /// way to tell which. Null propagates to <c>VirtualBroker</c>, which refuses the order with a
    /// logged reason (rule 10) rather than assuming unlimited liquidity.
    ///
    /// KNOWN SEAM — SPLITS (stop-and-report). Volume here is RAW, as the feed delivers it, so a window
    /// spanning a split mixes pre- and post-split share counts and the average is not a share count in
    /// any single basis. EODHD supplies no adjusted volume, so there is nothing to read instead; the
    /// distortion lives for at most one window (21 sessions) per split, and it biases the participation
    /// cap rather than a price. Note that <see cref="Adv21Notional"/> is immune — price÷r × volume×r is
    /// invariant — which is why the spread bucket never sees this. Revisit if a split lands on a name
    /// being actively traded near the cap.
    /// </summary>
    public double? Adv21Shares(SecurityId id)
    {
        var window = Window(id, _advWindowSessions);
        if (window.Count < _advWindowSessions) return null;

        var volumes = window.Select(b => b.Volume).OfType<long>().ToList();
        if (volumes.Count < _advWindowSessions) return null;

        return volumes.Average(v => (double)v);
    }

    /// <summary>
    /// Average daily volume in USD NOTIONAL over the ADV window ending at <see cref="AsOf"/> — the D43
    /// spread bucket's input. Null unless the window is complete.
    ///
    /// RAW close × raw volume, never adjusted close × volume: notional is the money that actually
    /// changed hands that day, and the adjusted close is a back-projected price that did not exist. On
    /// a 20-year-old bar the two differ by every dividend and split since, which would understate
    /// historical notional and quietly bucket a mega-cap as illiquid.
    /// </summary>
    public double? Adv21Notional(SecurityId id)
    {
        var window = Window(id, _advWindowSessions);
        if (window.Count < _advWindowSessions) return null;

        var notionals = window
            .Where(b => b.Volume.HasValue && b.Close is { } c && double.IsFinite(c) && c > 0)
            .Select(b => b.Close!.Value * b.Volume!.Value)
            .ToList();
        if (notionals.Count < _advWindowSessions) return null;

        return notionals.Average();
    }

    /// <summary>
    /// Realized daily volatility over <paramref name="window"/> sessions of RETURNS ending at
    /// <see cref="AsOf"/> — so it reads <paramref name="window"/> + 1 closes (see
    /// <see cref="PriceStatistics.RealizedVolDaily"/> for that convention). Null when the window is
    /// incomplete; the math itself lives in Core so D50's regime vol cannot drift from D43's σ.
    /// </summary>
    public double? RealizedVolDaily(SecurityId id, int window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(window, 1);

        var prices = AdjCloseSeries(id, window + 1);
        if (prices.Count < window + 1) return null;

        return PriceStatistics.RealizedVolDaily(prices);
    }

    // ---- reads ----

    private BarRow? Bar(SecurityId id, DateOnly date)
    {
        var key = (id.Value, Iso(date));
        if (_barCache.TryGetValue(key, out var cached)) return cached;

        var bar = _bars.GetBar(id.Value, key.Item2, Watermark);
        _barCache[key] = bar;
        return bar;
    }

    /// <summary>The visible bars over the last <paramref name="sessions"/> CALENDAR sessions ending at
    /// AsOf, oldest first. Short (or empty) when the name has thin history or gaps — the caller decides
    /// whether that disqualifies it.</summary>
    private IReadOnlyList<BarRow> Window(SecurityId id, int sessions)
    {
        var key = (id.Value, sessions);
        if (_seriesCache.TryGetValue(key, out var cached)) return cached;

        var start = WindowStart(sessions);
        IReadOnlyList<BarRow> rows = start is null
            ? []                                                       // the calendar does not reach back that far
            : _bars.GetSeries(id.Value, Iso(start.Value), Iso(AsOf), Watermark);

        _seriesCache[key] = rows;
        return rows;
    }

    /// <summary>
    /// The first date of the last <paramref name="sessions"/> sessions ending at AsOf, or null if the
    /// seeded calendar does not reach that far back.
    ///
    /// The window is counted in CALENDAR sessions, not in available bars. That matters: counting bars
    /// would let a window silently reach across a long halt to collect its 21 values and call the
    /// result a 21-day average, when the name barely traded. Counting sessions means a halted name
    /// returns a SHORT window, which becomes a null ADV, which becomes a refused order (rule 10) —
    /// the honest chain.
    /// </summary>
    private DateOnly? WindowStart(int sessions)
    {
        if (_windowStartCache.TryGetValue(sessions, out var cached)) return cached;

        // A run day is a session; tolerate a non-session asOf by anchoring on the prior session rather
        // than fabricating one.
        var cursor = _calendar.IsTradingDay(AsOf) ? AsOf : _calendar.PreviousSession(AsOf);

        for (var i = 1; i < sessions && cursor is not null; i++)
        {
            cursor = _calendar.PreviousSession(cursor.Value);
        }

        _windowStartCache[sessions] = cursor;
        return cursor;
    }

    // ---- helpers ----

    /// <summary>A bar is "priced" only with BOTH bases (D30): a raw close (the ledger's fill basis) and
    /// an adjusted close (the signal's basis). One without the other cannot complete the funnel.</summary>
    private static bool HasBothPriceBases(BarRow b) =>
        b.Close is { } c && double.IsFinite(c) && c > 0 &&
        b.AdjClose is { } a && double.IsFinite(a) && a > 0;

    private void GuardNotFuture(DateOnly date, string paramName)
    {
        if (date > AsOf)
        {
            throw new ArgumentOutOfRangeException(
                paramName, date,
                $"Point-in-time violation (rule 4): this view is as-of {AsOf:yyyy-MM-dd} and cannot answer for " +
                $"{date:yyyy-MM-dd}. Throwing rather than returning null — null would be indistinguishable from " +
                "'no data' and a leak would be silently absorbed as thin history.");
        }
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
