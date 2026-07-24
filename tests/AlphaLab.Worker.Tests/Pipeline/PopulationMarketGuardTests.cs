using System.Globalization;
using AlphaLab.Core.Config;
using AlphaLab.Core.Ledger;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Services;
using AlphaLab.Worker.Pipeline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Worker.Tests.Pipeline;

/// <summary>
/// R1 — the read-side fail-closed return guard (rule 10; D21/D40). A bar already in the versioned store
/// can never be deleted, so a physically-impossible single-session move — a vendor bad print such as the
/// CFC 0.04 dropout, a ×N spike or its ÷N revert far beyond any real move — is neutralized where the
/// return is DERIVED (<see cref="PopulationMarket.DailyReturn"/>): it contributes no return, exactly as a
/// halted/no-bar day does. That is the piece that stops one bad bar from inflating a plant's realized
/// dispersion and, through it, the whole calibration's MDE floor (the I7 finding). The bound is the
/// shared <c>Data.MaxSingleDayPriceFactor</c> — the same "physically impossible" both R1 and the R2
/// ingestion Reject key on. These tests seed the bad bar as a STORED row (the read guard's whole reason
/// for existing) and assert it is frozen while genuine large moves pass through untouched.
/// </summary>
public class PopulationMarketGuardTests
{
    private const long Sec = 1;
    private const string Watermark = "2030-01-01T00:00:00Z";
    private const double MaxFactor = 10.0;

    /// <summary><see cref="PopulationMarket.DailyReturn"/> never consults membership, so a trivial stub.</summary>
    private sealed class NoMembership : IIndexMembershipRead
    {
        public IReadOnlyList<long> MembersAsOf(string date) => [];
    }

    // One security's adjusted-close path: a genuine spike UP (+80%), a genuine drop (−44%), then the bad
    // print (a ÷5000 dropout) and its ×5000 revert, then an ordinary move to prove the bad bar did not
    // contaminate the days around it. Dates are consecutive NYSE-shaped weekdays (the 6th/7th are a
    // weekend and simply absent from the seeded calendar).
    private static readonly (string Date, double AdjClose)[] Scenario =
    [
        ("2024-01-02", 100.0),   // anchor
        ("2024-01-03", 100.0),   // flat
        ("2024-01-04", 180.0),   // +80%  genuine large move — PASSES
        ("2024-01-05", 100.0),   // −44%  genuine large move — PASSES
        ("2024-01-08", 0.02),    // ÷5000 impossible dropout (bad print) — NEUTRALIZED
        ("2024-01-09", 100.0),   // ×5000 its revert           (bad print) — NEUTRALIZED
        ("2024-01-10", 110.0),   // +10%  genuine move AFTER the bad bar — PASSES (no contamination)
    ];

    [Fact]
    public void DailyReturn_PassesGenuineMoves_NeutralizesImpossibleSpikeAndItsRevert()
    {
        using var arena = Arena.Seed(Scenario, MaxFactor);
        var m = arena.Market;

        Assert.Equal(0.80, m.DailyReturn(Sec, "2024-01-04"), 9);                    // +80% genuine — passes
        Assert.Equal(100.0 / 180.0 - 1.0, m.DailyReturn(Sec, "2024-01-05"), 9);     // −44% genuine — passes
        Assert.Equal(0.0, m.DailyReturn(Sec, "2024-01-08"), 12);                    // impossible drop — frozen
        Assert.Equal(0.0, m.DailyReturn(Sec, "2024-01-09"), 12);                    // impossible revert — frozen
        Assert.Equal(0.10, m.DailyReturn(Sec, "2024-01-10"), 9);                    // genuine +10% after — untouched
    }

    // The real CFC (Countrywide, security_id 558) bad print as stored in the arena: a single garbage
    // session (raw AND adjusted ≈ $0.028) on 2007-11-29, wedged between $124.67 and $119.68. This is the
    // exact "already-stored, can-never-be-deleted" bar R1 exists for (D21/D40). Adjusted closes verbatim.
    private static readonly (string Date, double AdjClose)[] CfcRealBars =
    [
        ("2007-11-26", 129.6581),
        ("2007-11-27", 128.6607),
        ("2007-11-28", 124.6713),
        ("2007-11-29",   0.0279),   // the bad print — ÷4467 dropout, ×4290 rebound
        ("2007-11-30", 119.6844),
        ("2007-12-03", 126.6660),
        ("2007-12-04", 126.6660),
    ];

    [Fact]
    public void DailyReturn_CfcRealBadBar_SkipsThePrintNotTheRecovery_AndSpansTheRealMove()
    {
        using var arena = Arena.Seed(CfcRealBars, MaxFactor);
        var m = arena.Market;

        // The guard skips the 2007-11-29 print (the ÷4467 dropout contributes no return)…
        Assert.Equal(0.0, m.DailyReturn(Sec, "2007-11-29"), 12);

        // …but NOT the 2007-11-30 bar: its return spans OVER the bad print to the last good price
        // (11-28 $124.67 → 11-30 $119.68), computing the genuine −4% move rather than a ×4290 extreme.
        var recovery = m.DailyReturn(Sec, "2007-11-30");
        Assert.Equal(119.6844 / 124.6713 - 1.0, recovery, 9);   // ≈ −0.0400
        Assert.NotEqual(0.0, recovery);                          // the recovery bar is kept, not skipped
        Assert.True(Math.Abs(recovery) < 0.10,                   // a sane single-digit magnitude, not an extreme
            $"spanned CFC return should be single-digit %, was {recovery:P2}");

        // The session after recovery is an ordinary move off the good $119.68 close — uncontaminated.
        Assert.Equal(126.6660 / 119.6844 - 1.0, m.DailyReturn(Sec, "2007-12-03"), 9); // ≈ +5.8%
    }

    [Theory]
    [InlineData(9.0, 8.0)]        // ×9      below the bound            → passes
    [InlineData(0.2, -0.8)]       // ÷5      above the 1/bound reciprocal → passes
    [InlineData(11.0, 0.0)]       // ×11     impossible spike           → neutralized
    [InlineData(0.05, 0.0)]       // ÷20     impossible drop            → neutralized
    [InlineData(2850.0, 0.0)]     // ×2850   CFC-magnitude spike        → neutralized
    [InlineData(0.00035, 0.0)]    // ÷2850   CFC-magnitude drop         → neutralized
    public void DailyReturn_FactorBoundary(double factor, double expected)
    {
        var series = new[] { ("2024-01-02", 100.0), ("2024-01-03", 100.0 * factor) };
        using var arena = Arena.Seed(series, MaxFactor);
        Assert.Equal(expected, arena.Market.DailyReturn(Sec, "2024-01-03"), 9);
    }

    /// <summary>A migrated on-disk arena seeded with one security's calendar + bar path, exposing a
    /// <see cref="PopulationMarket"/> built over the real bar-read / calendar services (so the guard is
    /// exercised against genuinely STORED bars, not a mock).</summary>
    private sealed class Arena : IDisposable
    {
        private readonly string _dir;
        private readonly AlphaLabDbContext _db;
        public PopulationMarket Market { get; }

        private Arena(string dir, AlphaLabDbContext db, PopulationMarket market)
        {
            _dir = dir;
            _db = db;
            Market = market;
        }

        public static Arena Seed(IReadOnlyList<(string Date, double AdjClose)> series, double maxFactor)
        {
            var dir = Path.Combine(Path.GetTempPath(), "alphalab-r1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "alphalab.db");
            var options = new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite($"Data Source={path}").Options;

            using (var seed = new AlphaLabDbContext(options))
            {
                seed.Database.Migrate();
                seed.Securities.Add(new SecurityRow { SecurityId = Sec, CurrentSymbol = "TEST", Exchange = "US", FirstSeen = "2024-01-01" });
                foreach (var (date, adj) in series)
                {
                    seed.TradingCalendar.Add(new TradingCalendarRow { Date = date, Session = "full", CloseTimeLocal = "16:00" });
                    seed.Bars.Add(new BarRow
                    {
                        SecurityId = Sec, Date = date, Version = 1, ObservedAt = "2024-01-01T00:00:00Z",
                        Open = adj, High = adj, Low = adj, Close = adj, AdjClose = adj, Volume = 1_000_000, Source = "test",
                    });
                }
                seed.SaveChanges();
            }

            var db = new AlphaLabDbContext(options);
            var calendar = new CalendarService(db);
            var costs = new CostsOptions();
            var asOf = DateOnly.ParseExact(series[^1].Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var features = new BarFeatureView(new BarReadService(db), calendar, asOf, Watermark, costs);
            var market = new PopulationMarket(features, new NoMembership(), calendar, new CostModel(costs), costs.AdvWindowDays, maxFactor);
            return new Arena(dir, db, market);
        }

        public void Dispose()
        {
            _db.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
