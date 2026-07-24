using System.Globalization;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;

namespace AlphaLab.Data.Tests;

/// <summary>
/// The D70 historical backfill (Phase 4, checkpoint 4.3): the fja05680 as-of membership drives bars for
/// every historical member inside the replay window; the D97 in-memory gate excludes Reject series fail
/// closed with a DETERMINISTIC exclusion set (the re-run-identical property sits beside the forward
/// backfill's idempotency suite); ticker-reuse suspects are flagged + excluded; and the rule-22 forward
/// slice survives the ingest via the pre-ingest snapshot + <see cref="SliceScopedMembershipRead"/>.
/// </summary>
public class HistoricalBackfillTests
{
    private const string From = "2010-01-04";
    private const string To = "2010-03-31";
    private const string Today = "2026-07-22";
    private const string ObservedAt = "2026-07-22T14:03:11Z";

    private static HistoricalBackfillOptions Options() => new()
    {
        Universe = "sp500", From = From, To = To, SliceAsOf = Today, ObservedAt = ObservedAt,
    };

    // fja05680 shape: date,tickers with the roster in ONE quoted field.
    private const string TwoMemberCsv = "date,tickers\n2010-01-04,\"AAA,BBB\"\n";

    private const string ReuseCsv =
        "date,tickers\n2000-01-03,\"AAA,RRR\"\n2001-06-01,\"AAA\"\n2010-01-04,\"AAA,RRR\"\n";

    private static HistoricalBackfillRunner Runner(AlphaLabDbContext db, FakeMarket market) =>
        new(db, market, new DataQualityGate(new DataQualityOptions()), null);

    private static void SeedCalendar(AlphaLabDbContext db, int fromYear = 2009, int toYear = 2011) =>
        new CalendarSeeder(db).Seed(fromYear, toYear);

    private static IReadOnlyList<EodBar> CleanBars(AlphaLabDbContext db, string from, string to)
    {
        var sessions = new CalendarService(db).SessionsBetween(
            DateOnly.ParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateOnly.ParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture));
        return sessions
            .Select(d => new EodBar(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), 100, 101, 99, 100, 100, 1_000_000))
            .ToList();
    }

    // ---- D97: a Reject series is excluded from ingestion entirely, and reported ----

    [Fact]
    public async Task D97_GateSweep_RejectSeriesExcludedAndReported()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            SeedCalendar(db);
            var market = new FakeMarket();
            var clean = CleanBars(db, From, To);
            market.BarsFor["AAA"] = clean;
            market.BarsFor["BBB"] = [.. clean, new EodBar("2010-02-01", 100, 101, 99, double.NaN, 100, 1_000_000)];

            var report = await Runner(db, market).RunAsync(Options(), TwoMemberCsv);

            // BBB: excluded fail closed — ZERO rows ingested, the exclusion named in the artifact.
            var bbb = db.Securities.Single(s => s.CurrentSymbol == "BBB").SecurityId;
            Assert.Empty(db.Bars.Where(b => b.SecurityId == bbb).ToList());
            var exclusion = Assert.Single(report.GateExclusions);
            Assert.Equal("BBB", exclusion.Symbol);
            Assert.Contains(exclusion.Rejects, r => r.Contains("NanField", StringComparison.Ordinal));

            // AAA: ingested at the TRUE observation instant (D92), full member-session coverage.
            var aaa = db.Securities.Single(s => s.CurrentSymbol == "AAA").SecurityId;
            Assert.Equal(clean.Count, db.Bars.Count(b => b.SecurityId == aaa));
            Assert.All(db.Bars.Where(b => b.SecurityId == aaa).ToList(), b => Assert.Equal(ObservedAt, b.ObservedAt));
            var aaaCoverage = Assert.Single(report.Coverage, c => c.Symbol == "AAA");
            Assert.Equal(100.0, aaaCoverage.CoveragePct);

            // The D97 marker config row exists, versioned, naming the exclusion.
            var marker = Assert.Single(db.Config.Where(c => c.Key == HistoricalBackfillRunner.GateSweepConfigKey).ToList());
            Assert.Equal(1, marker.Version);
            Assert.Contains("BBB", marker.ValueJson, StringComparison.Ordinal);
        }
        finally { TestDb.Delete(path); }
    }

    /// <summary>The user-mandated determinism property, beside the forward backfill's idempotency
    /// suite: an idempotent re-run reproduces the IDENTICAL artifact (a fortiori the identical
    /// exclusion list), so replay never depends on which attempt ingested the data.</summary>
    [Fact]
    public async Task D97_GateSweep_RerunReproducesIdenticalExclusionList()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            SeedCalendar(db);
            var market = new FakeMarket();
            var clean = CleanBars(db, From, To);
            market.BarsFor["AAA"] = clean;
            market.BarsFor["BBB"] = [.. clean, new EodBar("2010-02-01", 100, 101, 99, double.NaN, 100, 1_000_000)];
            var runner = Runner(db, market);

            var first = await runner.RunAsync(Options(), TwoMemberCsv);
            var barsAfterFirst = db.Bars.Count();

            var second = await runner.RunAsync(Options(), TwoMemberCsv);

            Assert.Equal(first.ToCanonicalJson(), second.ToCanonicalJson()); // byte-identical artifact
            Assert.Equal(barsAfterFirst, db.Bars.Count());                   // nothing new ingested
            // The marker row is append-only versioned: the second run appends v2, never edits v1.
            Assert.Equal(2, db.Config.Count(c => c.Key == HistoricalBackfillRunner.GateSweepConfigKey));
        }
        finally { TestDb.Delete(path); }
    }

    // ---- D70: ticker-reuse suspects are flagged and excluded fail closed ----

    [Fact]
    public async Task D70_TickerReuse_FlaggedAndExcluded()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            SeedCalendar(db, 1999, 2011);
            var market = new FakeMarket();
            market.BarsFor["AAA"] = CleanBars(db, "2000-01-03", "2010-12-31");

            var options = Options() with { From = "2000-01-03", To = "2010-12-31" };
            var report = await Runner(db, market).RunAsync(options, ReuseCsv);

            // RRR left in 2001 and "returned" in 2010 — 8.6 years apart, likely a DIFFERENT company on
            // the same ticker. EODHD resolves the symbol to ONE history, so ingesting would attribute
            // the wrong company's bars: flagged, EXCLUDED, and never even fetched (fail closed).
            var suspect = Assert.Single(report.TickerReuseSuspects);
            Assert.Equal("RRR", suspect.Symbol);
            Assert.True(suspect.GapYears > 8);
            Assert.DoesNotContain("RRR", market.EodCalls);
            var rrr = db.Securities.Single(s => s.CurrentSymbol == "RRR").SecurityId;
            Assert.Empty(db.Bars.Where(b => b.SecurityId == rrr).ToList());

            // AAA (continuous member) is unaffected.
            Assert.Contains("AAA", market.EodCalls);
        }
        finally { TestDb.Delete(path); }
    }

    // ---- Finding 266: the operator exclusion list — single-spell symbol reuse the disjoint-spell
    // heuristic above structurally cannot catch (e.g. SUN). Skipped on ingest, recorded, idempotent. ----

    [Fact]
    public async Task Finding266_OperatorExclusion_SkippedRecordedAndIdempotent()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            SeedCalendar(db);
            var market = new FakeMarket();
            var clean = CleanBars(db, From, To);
            market.BarsFor["AAA"] = clean;
            market.BarsFor["BBB"] = clean;

            // BBB is on Universe:Exclusions — lowercase, to prove the match is case-insensitive.
            var options = Options() with { Exclusions = ["bbb"] };
            var runner = Runner(db, market);
            var first = await runner.RunAsync(options, TwoMemberCsv);

            // BBB: never fetched, zero bars, recorded in OperatorExclusions, absent from Coverage.
            Assert.DoesNotContain("BBB", market.EodCalls);
            var bbb = db.Securities.Single(s => s.CurrentSymbol == "BBB").SecurityId;
            Assert.Empty(db.Bars.Where(b => b.SecurityId == bbb).ToList());
            var excl = Assert.Single(first.OperatorExclusions);
            Assert.Equal("BBB", excl.Symbol);
            Assert.DoesNotContain(first.Coverage, c => c.Symbol == "BBB");

            // AAA (not excluded) ingests normally.
            Assert.Contains("AAA", market.EodCalls);
            var aaa = db.Securities.Single(s => s.CurrentSymbol == "AAA").SecurityId;
            Assert.Equal(clean.Count, db.Bars.Count(b => b.SecurityId == aaa));

            // Idempotent: a re-run reproduces the identical artifact and ingests nothing new.
            var barsAfterFirst = db.Bars.Count();
            var second = await runner.RunAsync(options, TwoMemberCsv);
            Assert.Equal(first.ToCanonicalJson(), second.ToCanonicalJson());
            Assert.Equal(barsAfterFirst, db.Bars.Count());
        }
        finally { TestDb.Delete(path); }
    }

    // ---- Finding 266: the replay-roster deny-list (ExclusionScopedMembershipRead) — the rule-3-compliant
    // substitute for deleting an already-ingested wrong-company bar set: the security leaves the roster. ----

    [Fact]
    public void Finding266_ExclusionScopedMembershipRead_DeniesExcludedSymbolFromRoster()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            db.Securities.Add(new SecurityRow { SecurityId = 1, CurrentSymbol = "KEEP", Exchange = "US", FirstSeen = "2006-01-02" });
            db.Securities.Add(new SecurityRow { SecurityId = 2, CurrentSymbol = "EXCL", Exchange = "US", FirstSeen = "2006-01-02" });
            db.IndexMembership.Add(new IndexMembershipRow { SecurityId = 1, AddedOn = "2006-01-02", RemovedOn = null });
            db.IndexMembership.Add(new IndexMembershipRow { SecurityId = 2, AddedOn = "2006-01-02", RemovedOn = null });
            db.SaveChanges();

            var inner = new IndexMembershipReadService(db);
            const string asOf = "2010-01-04";
            Assert.Equal(new long[] { 1, 2 }, inner.MembersAsOf(asOf).OrderBy(x => x).ToArray());   // raw read sees both

            // The deny-list (case-insensitive) removes EXCL from the roster; KEEP survives.
            var denied = new ExclusionScopedMembershipRead(inner, db, new UniverseOptions { Exclusions = ["excl"] });
            Assert.Equal(new long[] { 1 }, denied.MembersAsOf(asOf).OrderBy(x => x).ToArray());

            // Empty exclusions ⇒ pass-through (a store that never listed one is unaffected).
            var passthrough = new ExclusionScopedMembershipRead(inner, db, new UniverseOptions { Exclusions = [] });
            Assert.Equal(new long[] { 1, 2 }, passthrough.MembersAsOf(asOf).OrderBy(x => x).ToArray());
        }
        finally { TestDb.Delete(path); }
    }

    // ---- rule 22: the forward slice survives the historical ingest ----

    [Fact]
    public async Task FR4_ForwardSliceSurvivesHistoricalIngest()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            SeedCalendar(db);
            // The pre-existing FORWARD slice: two members added by the (simulated) OEF bootstrap.
            var master = new SecurityMaster(db);
            var slc1 = master.Register("SLC1", "US", "2026-07-15");
            var slc2 = master.Register("SLC2", "US", "2026-07-15");
            db.IndexMembership.Add(new IndexMembershipRow { SecurityId = slc1, AddedOn = "2026-07-15" });
            db.IndexMembership.Add(new IndexMembershipRow { SecurityId = slc2, AddedOn = "2026-07-15" });
            db.SaveChanges();

            var market = new FakeMarket();
            market.BarsFor["AAA"] = CleanBars(db, From, To);
            market.BarsFor["BBB"] = CleanBars(db, From, To);
            var runner = Runner(db, market);

            await runner.RunAsync(Options(), TwoMemberCsv);

            // The CSV's last snapshot leaves AAA/BBB OPEN, so the raw as-of read at today now includes
            // them — that is exactly what the slice snapshot + decorator exist to scope away.
            var raw = new IndexMembershipReadService(db);
            Assert.Equal(4, raw.MembersAsOf(Today).Count);

            var scoped = new SliceScopedMembershipRead(raw, db, new UniverseOptions()); // Bootstrap = sp100
            Assert.Equal(new[] { slc1, slc2 }.OrderBy(x => x), scoped.MembersAsOf(Today).OrderBy(x => x));

            // The snapshot is write-once: a re-run never re-snapshots the widened roster as "the slice".
            await runner.RunAsync(Options(), TwoMemberCsv);
            var sliceRow = Assert.Single(db.Config.Where(c => c.Key == HistoricalBackfillRunner.SliceConfigKey).ToList());
            Assert.Equal(1, sliceRow.Version);
            Assert.DoesNotContain(db.Securities.Single(s => s.CurrentSymbol == "AAA").SecurityId.ToString(CultureInfo.InvariantCulture),
                sliceRow.ValueJson);
        }
        finally { TestDb.Delete(path); }
    }

    // Phase-4 review: the slice intersection is DATE-AWARE — the version whose changed_on <= the
    // queried date wins, so a post-snapshot index add flows through at its reconcile date, and a
    // reproduce-day of an earlier committed session resolves the slice THAT day traded (a date-blind
    // latest-version read would apply a scope the original run never had — a false NFR-1 FAIL).
    [Fact]
    public void FR4_SliceScope_IsDateAware_VersionsResolveAsOfTheQueriedDay()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var master = new SecurityMaster(db);
            var a = master.Register("AAA", "US", "2010-01-01");
            var b = master.Register("BBB", "US", "2010-01-01");
            var c = master.Register("CCC", "US", "2010-01-01");
            foreach (var id in new[] { a, b, c })
                db.IndexMembership.Add(new IndexMembershipRow { SecurityId = id, AddedOn = "2010-01-01" });

            // v1 (the backfill snapshot) = {AAA, BBB}; v2 (a reconcile that ADDED CCC) ten days later.
            db.Config.Add(new Data.Entities.ConfigRow
            {
                Key = HistoricalBackfillRunner.SliceConfigKey,
                ValueJson = $"[{a},{b}]", Version = 1, ChangedOn = "2026-07-01", Reason = "test v1",
            });
            db.Config.Add(new Data.Entities.ConfigRow
            {
                Key = HistoricalBackfillRunner.SliceConfigKey,
                ValueJson = $"[{a},{b},{c}]", Version = 2, ChangedOn = "2026-07-10", Reason = "test v2 (add)",
            });
            db.SaveChanges();

            var scoped = new SliceScopedMembershipRead(new IndexMembershipReadService(db), db, new UniverseOptions());

            // A session between the versions sees v1's scope; after the add, v2's; before v1, the
            // earliest snapshot bounds it (never the widened raw roster).
            Assert.Equal(new[] { a, b }.OrderBy(x => x), scoped.MembersAsOf("2026-07-05").OrderBy(x => x));
            Assert.Equal(new[] { a, b, c }.OrderBy(x => x), scoped.MembersAsOf("2026-07-15").OrderBy(x => x));
            Assert.Equal(new[] { a, b }.OrderBy(x => x), scoped.MembersAsOf("2026-06-01").OrderBy(x => x));
        }
        finally { TestDb.Delete(path); }
    }

    // Phase-4 review: removed_on is EXCLUSIVE, so a member removed exactly at the window end must
    // reach 100% coverage with bars through removed_on - 1. The old "< to" comparison counted the
    // removal day as expected, pinning coverage below 100% forever and re-fetching the name (3 EODHD
    // calls) on every re-run.
    [Fact]
    public async Task D70_Coverage_RemovalAtWindowEnd_ReachesFullCoverage()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            SeedCalendar(db);
            var market = new FakeMarket();
            market.BarsFor["AAA"] = CleanBars(db, From, To);
            // BBB is removed ON the window-end date (the last snapshot omits it) and trades through
            // the session before — the delisted-at-window-end shape.
            market.BarsFor["BBB"] = CleanBars(db, From, "2010-03-30");
            const string csv = "date,tickers\n2010-01-04,\"AAA,BBB\"\n2010-03-31,\"AAA\"\n";

            var report = await Runner(db, market).RunAsync(Options(), csv);

            var bbb = Assert.Single(report.Coverage, cov => cov.Symbol == "BBB");
            Assert.Equal(100.0, bbb.CoveragePct);
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public async Task FR19_ReplayUniverse_NeverTheSlice()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            SeedCalendar(db);
            var market = new FakeMarket();
            market.BarsFor["AAA"] = CleanBars(db, From, To);
            market.BarsFor["BBB"] = CleanBars(db, From, To);
            await Runner(db, market).RunAsync(Options(), TwoMemberCsv);

            // The REPLAY path reads the RAW service: a historical day resolves the full as-of roster.
            var raw = new IndexMembershipReadService(db);
            Assert.Equal(2, raw.MembersAsOf("2010-06-01").Count);

            // And the post-sign-off widen dissolves the filter by config flip alone (rule 22/D70).
            var widened = new UniverseOptions { Bootstrap = { Universe = "sp500" } };
            var scoped = new SliceScopedMembershipRead(raw, db, widened);
            Assert.Equal(raw.MembersAsOf("2010-06-01"), scoped.MembersAsOf("2010-06-01"));
        }
        finally { TestDb.Delete(path); }
    }

    // ---- CLI parsing (fail closed, the BackfillArgs discipline) ----

    [Fact]
    public void HistoricalArgs_ParsesTheFullShape()
    {
        var o = HistoricalBackfillArgs.Parse(
            ["--historical", "sp500", "--from", "2010-01-04", "--to", "2025-06-30", "--csv", "x.csv"], Today);
        Assert.Equal("sp500", o.Universe);
        Assert.Equal("2010-01-04", o.From);
        Assert.Equal("2025-06-30", o.To);
        Assert.Equal("x.csv", o.CsvPath);
        Assert.Equal(Today, o.SliceAsOf);
        Assert.False(o.DryRun);
    }

    [Theory]
    [InlineData(new[] { "--historical", "sp500" }, "--from")]                                            // missing window
    [InlineData(new[] { "--historical", "sp400", "--from", "2010-01-04", "--to", "2025-06-30" }, "sp500")] // D70: sp500 only
    [InlineData(new[] { "--historical", "sp500", "--from", "2025-06-30", "--to", "2010-01-04" }, "precede")] // inverted
    [InlineData(new[] { "--historical", "sp500", "--from", "2010-01-04", "--to", "2025-06-30", "--bogus" }, "Unknown")]
    [InlineData(new[] { "--historical", "sp500", "--from", "--to", "2025-06-30" }, "Missing value")]
    public void HistoricalArgs_FailsClosed(string[] args, string expectedFragment)
    {
        var ex = Assert.Throws<ArgumentException>(() => HistoricalBackfillArgs.Parse(args, Today));
        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
    }

    // ---- fake market data: per-symbol canned series, call log for the never-fetched assertions ----

    private sealed class FakeMarket : IMarketDataProvider
    {
        public Dictionary<string, IReadOnlyList<EodBar>> BarsFor { get; } = new(StringComparer.Ordinal);
        public List<string> EodCalls { get; } = [];

        public Task<IReadOnlyList<EodBar>> GetEodAsync(string symbol, string from, string to, string asOf, CancellationToken ct = default)
        {
            EodCalls.Add(symbol);
            return Task.FromResult(BarsFor.GetValueOrDefault(symbol, []));
        }

        public Task<IReadOnlyList<DividendEvent>> GetDividendsAsync(string symbol, string from, string asOf, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DividendEvent>>([]);

        public Task<IReadOnlyList<SplitEvent>> GetSplitsAsync(string symbol, string from, string asOf, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SplitEvent>>([]);
    }
}
