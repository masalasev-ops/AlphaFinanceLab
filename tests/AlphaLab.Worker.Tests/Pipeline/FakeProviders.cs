using AlphaLab.Data.Providers;

namespace AlphaLab.Worker.Tests.Pipeline;

/// <summary>
/// An in-memory <see cref="IMarketDataProvider"/> for the D53 pipeline tests. Keyed by the exact symbol
/// the pipeline passes (the security's current_symbol — no ".US" suffix logic here). Returns the [from, to]
/// slice of each symbol's preloaded series; dividends/splits are per-symbol opt-ins. <see cref="ThrowOnFetch"/>
/// simulates a hard provider failure — the lever FX-StagedPipeline uses to prove Stage 1 wrote nothing.
/// </summary>
public sealed class FakeMarketData : IMarketDataProvider
{
    private readonly Dictionary<string, SortedDictionary<string, EodBar>> _bars = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DividendEvent>> _dividends = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<SplitEvent>> _splits = new(StringComparer.Ordinal);

    /// <summary>When true every fetch throws — a provider that hard-fails mid-run.</summary>
    public bool ThrowOnFetch { get; set; }

    public void SetBar(string symbol, EodBar bar)
    {
        if (!_bars.TryGetValue(symbol, out var series))
        {
            series = new SortedDictionary<string, EodBar>(StringComparer.Ordinal);
            _bars[symbol] = series;
        }
        series[bar.Date] = bar;
    }

    public void AddDividend(string symbol, DividendEvent dividend)
    {
        if (!_dividends.TryGetValue(symbol, out var list)) { list = []; _dividends[symbol] = list; }
        list.Add(dividend);
    }

    public Task<IReadOnlyList<EodBar>> GetEodAsync(string symbol, string from, string to, string asOf, CancellationToken ct = default)
    {
        if (ThrowOnFetch) throw new InvalidOperationException($"fake provider hard-failed fetching {symbol}.");
        IReadOnlyList<EodBar> slice = _bars.TryGetValue(symbol, out var series)
            ? series.Where(kv => string.CompareOrdinal(kv.Key, from) >= 0 && string.CompareOrdinal(kv.Key, to) <= 0)
                .Select(kv => kv.Value).ToList()
            : [];
        return Task.FromResult(slice);
    }

    public Task<IReadOnlyList<DividendEvent>> GetDividendsAsync(string symbol, string from, string asOf, CancellationToken ct = default)
    {
        if (ThrowOnFetch) throw new InvalidOperationException($"fake provider hard-failed fetching {symbol} dividends.");
        IReadOnlyList<DividendEvent> divs = _dividends.TryGetValue(symbol, out var list)
            ? list.Where(d => string.CompareOrdinal(d.Date, from) >= 0).ToList()
            : [];
        return Task.FromResult(divs);
    }

    public Task<IReadOnlyList<SplitEvent>> GetSplitsAsync(string symbol, string from, string asOf, CancellationToken ct = default)
    {
        if (ThrowOnFetch) throw new InvalidOperationException($"fake provider hard-failed fetching {symbol} splits.");
        IReadOnlyList<SplitEvent> splits = _splits.TryGetValue(symbol, out var list)
            ? list.Where(s => string.CompareOrdinal(s.Date, from) >= 0).ToList()
            : [];
        return Task.FromResult(splits);
    }
}

/// <summary>An in-memory <see cref="IRegimeProxyProvider"/> — the GSPC daily series, returned as a slice.</summary>
public sealed class FakeRegimeProxy : IRegimeProxyProvider
{
    private readonly SortedDictionary<string, EodBar> _bars = new(StringComparer.Ordinal);

    public bool ThrowOnFetch { get; set; }

    public void SetBar(EodBar bar) => _bars[bar.Date] = bar;

    public Task<IReadOnlyList<EodBar>> GetProxyBarsAsync(string from, string to, string asOf, CancellationToken ct = default)
    {
        if (ThrowOnFetch) throw new InvalidOperationException("fake regime proxy hard-failed.");
        IReadOnlyList<EodBar> slice = _bars
            .Where(kv => string.CompareOrdinal(kv.Key, from) >= 0 && string.CompareOrdinal(kv.Key, to) <= 0)
            .Select(kv => kv.Value).ToList();
        return Task.FromResult(slice);
    }
}

/// <summary>A fixed clock so run timestamps are deterministic under test (the pipeline never calls a bare
/// UtcNow; it stamps started_at/finished_at/recovered_at from the injected <see cref="TimeProvider"/>).</summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
