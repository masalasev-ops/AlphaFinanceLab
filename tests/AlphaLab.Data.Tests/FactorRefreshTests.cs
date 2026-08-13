using AlphaLab.Core.Config;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Data.Tests;

/// <summary>
/// The D41 refresh's validation and write (checkpoint 6.6).
///
/// **THE FIXTURE THAT MATTERS IS THE REFUSAL.** "Fail-closed on checksum" presupposes the checksum arm
/// can fire, and a hash of bytes just downloaded has nothing to fail against unless something is
/// compared. Here the comparison subject is stated in code and exercised here: the fingerprint's job is
/// the no-op fast path, and the REACHABLE refusal is the revision check — newly-fetched values
/// disagreeing with values this arena already stored, i.e. upstream revising published history.
/// Without that fixture the arm would be unfalsifiable, which is the defect this checkpoint opened by
/// finding in `MetricsConstants`.
/// </summary>
public class FactorRefreshTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"alphalab-fr-{Guid.NewGuid():N}.db");

    private static AlphaLabDbContext NewContext(string path) =>
        new(new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite($"Data Source={path}").Options);

    private static void TryDelete(string path)
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { /* best effort */ }
    }

    private static readonly string[] Sessions =
        ["2026-07-01", "2026-07-02", "2026-07-03", "2026-07-06", "2026-07-07"];

    private static AlphaLabDbContext Seeded(string path, bool withCalendar = true)
    {
        using (var db = NewContext(path)) db.Database.Migrate();
        var ctx = NewContext(path);
        if (withCalendar)
        {
            foreach (var d in Sessions)
            {
                ctx.TradingCalendar.Add(new TradingCalendarRow
                {
                    Date = d, Session = "full", CloseTimeLocal = "16:00",
                });
            }
            ctx.SaveChanges();
        }
        return ctx;
    }

    private static FactorFetch Fetch(string fingerprint, params (string Date, string Factor, double Value)[] obs) =>
        new([.. obs.Select(o => new FactorObservation(o.Date, o.Factor, o.Value))],
            fingerprint,
            ["https://example/five.zip", "https://example/mom.zip"]);

    private static FactorFetch FullWindow(string fingerprint, double mktOnDay1 = 0.001) =>
        Fetch(fingerprint,
            ("2026-07-01", "MKT_RF", mktOnDay1), ("2026-07-01", "RF", 0.00012),
            ("2026-07-02", "MKT_RF", 0.002), ("2026-07-02", "RF", 0.00012),
            ("2026-07-03", "MKT_RF", 0.003), ("2026-07-03", "RF", 0.00013),
            ("2026-07-06", "MKT_RF", 0.004), ("2026-07-06", "RF", 0.00013),
            ("2026-07-07", "MKT_RF", 0.005), ("2026-07-07", "RF", 0.00013));

    // ---------- the happy path ----------

    [Fact]
    public void FR5_D41_AFirstRefresh_WritesEveryObservationAndOneLogRow()
    {
        var p = TempDb();
        try
        {
            using var db = Seeded(p);
            var outcome = new FactorRefresh(db, new FactorDataOptions()).Apply(FullWindow("aaa"), "2026-07-08T02:00:00Z");

            Assert.True(outcome.Written, outcome.Reason);
            Assert.Equal(10, outcome.RowsAdded);
            Assert.Equal("2026-07-07", outcome.ThroughDate);
            Assert.Equal(10, db.FactorReturns.Count());

            var log = Assert.Single(db.FactorRefreshLog.ToList());
            Assert.Equal("aaa", log.Checksum);
            Assert.Equal(10, log.RowsAdded);
            Assert.Contains("five.zip", log.FilesJson!, StringComparison.Ordinal);
        }
        finally { TryDelete(p); }
    }

    /// <summary>An unchanged fingerprint means the upstream bytes did not move, so there is nothing to
    /// do — the fast path, and one of the fingerprint's two stated jobs.</summary>
    [Fact]
    public void FR5_D41_AnUnchangedFingerprint_IsANoOp_NotARewrite()
    {
        var p = TempDb();
        try
        {
            using var db = Seeded(p);
            var refresh = new FactorRefresh(db, new FactorDataOptions());
            refresh.Apply(FullWindow("same-bytes"), "2026-07-08T02:00:00Z");

            var second = refresh.Apply(FullWindow("same-bytes"), "2026-08-05T02:00:00Z");

            Assert.False(second.Written);
            Assert.Contains("unchanged", second.Reason, StringComparison.Ordinal);
            Assert.Single(db.FactorRefreshLog.ToList());   // no second log row
            Assert.Equal(10, db.FactorReturns.Count());
        }
        finally { TryDelete(p); }
    }

    // ---------- THE REFUSAL, proven reachable ----------

    [Fact]
    public void FR5_D41_UpstreamRevisingPublishedHistory_RefusesTheWholeRefresh()
    {
        var p = TempDb();
        try
        {
            using var db = Seeded(p);
            var refresh = new FactorRefresh(db, new FactorDataOptions());
            refresh.Apply(FullWindow("v1"), "2026-07-08T02:00:00Z");

            // Same window, new bytes, and one PUBLISHED day silently restated.
            var revised = refresh.Apply(FullWindow("v2", mktOnDay1: 0.009), "2026-08-05T02:00:00Z");

            Assert.False(revised.Written);
            Assert.Contains("revised published history", revised.Reason, StringComparison.Ordinal);
            Assert.Contains("MKT_RF on 2026-07-01", revised.Reason, StringComparison.Ordinal);

            // NOTHING was written: not the log row, not a partial series.
            Assert.Single(db.FactorRefreshLog.ToList());
            Assert.Equal(10, db.FactorReturns.Count());
            Assert.Equal(0.001, db.FactorReturns.Single(r => r.Date == "2026-07-01" && r.Factor == "MKT_RF").Value, 12);
        }
        finally { TryDelete(p); }
    }

    /// <summary>A NEW period alongside unchanged history is the normal monthly case and must NOT trip
    /// the revision arm — otherwise the check would fire every month and be turned off.</summary>
    [Fact]
    public void FR5_D41_AppendingANewPeriod_IsNotARevision()
    {
        var p = TempDb();
        try
        {
            using var db = Seeded(p);
            var refresh = new FactorRefresh(db, new FactorDataOptions());
            refresh.Apply(FullWindow("v1"), "2026-07-08T02:00:00Z");

            db.TradingCalendar.Add(new TradingCalendarRow { Date = "2026-07-08", Session = "full", CloseTimeLocal = "16:00" });
            db.SaveChanges();

            var extended = Fetch("v2",
                ("2026-07-01", "MKT_RF", 0.001), ("2026-07-01", "RF", 0.00012),
                ("2026-07-02", "MKT_RF", 0.002), ("2026-07-02", "RF", 0.00012),
                ("2026-07-03", "MKT_RF", 0.003), ("2026-07-03", "RF", 0.00013),
                ("2026-07-06", "MKT_RF", 0.004), ("2026-07-06", "RF", 0.00013),
                ("2026-07-07", "MKT_RF", 0.005), ("2026-07-07", "RF", 0.00013),
                ("2026-07-08", "MKT_RF", 0.006), ("2026-07-08", "RF", 0.00013));

            var outcome = refresh.Apply(extended, "2026-08-05T02:00:00Z");

            Assert.True(outcome.Written, outcome.Reason);
            Assert.Equal(2, outcome.RowsAdded);       // only the new day
            Assert.Equal(12, db.FactorReturns.Count());
        }
        finally { TryDelete(p); }
    }

    // ---------- continuity: the first-fetch check ----------

    [Fact]
    public void FR5_D41_ATruncatedFile_IsRefusedByContinuity()
    {
        var p = TempDb();
        try
        {
            using var db = Seeded(p);
            var options = new FactorDataOptions { MaxMissingSessions = 1 };

            // Covers the endpoints so the range spans all five sessions, but three are absent.
            var gappy = Fetch("v1",
                ("2026-07-01", "MKT_RF", 0.001),
                ("2026-07-07", "MKT_RF", 0.005));

            var outcome = new FactorRefresh(db, options).Apply(gappy, "2026-07-08T02:00:00Z");

            Assert.False(outcome.Written);
            Assert.Contains("continuity", outcome.Reason, StringComparison.Ordinal);
            Assert.Equal(3, outcome.MissingSessions);
            Assert.Empty(db.FactorReturns.ToList());
            Assert.Empty(db.FactorRefreshLog.ToList());
        }
        finally { TryDelete(p); }
    }

    /// <summary>A gap WITHIN tolerance passes: the library's calendar and the NYSE calendar disagree on
    /// a handful of historical days, and a bar-for-bar match was never the claim.</summary>
    [Fact]
    public void FR5_D41_AGapWithinTolerance_IsAccepted()
    {
        var p = TempDb();
        try
        {
            using var db = Seeded(p);
            var oneShort = Fetch("v1",
                ("2026-07-01", "MKT_RF", 0.001),
                ("2026-07-02", "MKT_RF", 0.002),
                ("2026-07-03", "MKT_RF", 0.003),
                ("2026-07-07", "MKT_RF", 0.005));   // 2026-07-06 missing: one session

            var outcome = new FactorRefresh(db, new FactorDataOptions { MaxMissingSessions = 1 })
                .Apply(oneShort, "2026-07-08T02:00:00Z");

            Assert.True(outcome.Written, outcome.Reason);
            Assert.Equal(1, outcome.MissingSessions);
            Assert.Equal(5, outcome.SessionsChecked);
        }
        finally { TryDelete(p); }
    }

    [Fact]
    public void FR5_D41_AnEmptyFetch_WritesNothingAndSaysSo()
    {
        var p = TempDb();
        try
        {
            using var db = Seeded(p);
            var outcome = new FactorRefresh(db, new FactorDataOptions()).Apply(Fetch("v1"), "2026-07-08T02:00:00Z");

            Assert.False(outcome.Written);
            Assert.Contains("no observations", outcome.Reason, StringComparison.Ordinal);
            Assert.Empty(db.FactorRefreshLog.ToList());
        }
        finally { TryDelete(p); }
    }
}
