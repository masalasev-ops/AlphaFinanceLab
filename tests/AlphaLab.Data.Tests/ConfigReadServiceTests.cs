using AlphaLab.Data.Entities;
using AlphaLab.Data.Services;

namespace AlphaLab.Data.Tests;

/// <summary>
/// D96 (P14a resolved — landed BEFORE the first calibration config write): run-scoped config reads
/// resolve as-of the run's watermark, so a config row appended after a session committed is invisible
/// to a re-run of that session. The first test is the user-mandated visibility proof; the reproduce-day
/// cousin (byte-identity after a post-asOf insert) lives in Worker.Tests beside FX-ReproduceDay.
/// </summary>
public class ConfigReadServiceTests
{
    [Fact]
    public void D96_ConfigRow_ChangedOnAfterWatermark_InvisibleToResolveAsOf()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            db.Config.Add(new ConfigRow { Key = "K", ValueJson = "1", Version = 1, ChangedOn = "2026-07-10" });
            db.Config.Add(new ConfigRow { Key = "K", ValueJson = "2", Version = 2, ChangedOn = "2026-07-20" });
            db.SaveChanges();

            var config = new ConfigReadService(db);

            // Current = MAX(version), unchanged semantics (finding 108).
            Assert.Equal("2", config.ResolveCurrent("K"));

            // As-of a watermark BEFORE v2's changed_on: v2 is invisible; v1 resolves.
            Assert.Equal("1", config.ResolveAsOf("K", "2026-07-15T22:00:00Z"));
            // As-of after: v2 resolves.
            Assert.Equal("2", config.ResolveAsOf("K", "2026-07-20T22:00:00Z"));
            // A key with NO row at the watermark resolves to nothing — never a later value.
            Assert.Null(config.ResolveAsOf("K", "2026-07-01T22:00:00Z"));

            // The same-day rule: a bare-date changed_on sorts BEFORE that day's T22:00:00Z watermark,
            // so a row written during its own session is visible to that session (ordinal ISO compare).
            Assert.Equal("1", config.ResolveAsOf("K", "2026-07-10T22:00:00Z"));
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void D96_ResolveLongAsOf_ParsesOrNull()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            db.Config.Add(new ConfigRow { Key = "Id", ValueJson = "103", Version = 1, ChangedOn = "2026-07-10" });
            db.Config.Add(new ConfigRow { Key = "Bad", ValueJson = "\"x\"", Version = 1, ChangedOn = "2026-07-10" });
            db.SaveChanges();

            var config = new ConfigReadService(db);
            Assert.Equal(103L, config.ResolveLongAsOf("Id", "2026-07-15T22:00:00Z"));
            Assert.Null(config.ResolveLongAsOf("Bad", "2026-07-15T22:00:00Z"));
            Assert.Null(config.ResolveLongAsOf("Absent", "2026-07-15T22:00:00Z"));
        }
        finally { TestDb.Delete(path); }
    }
}
