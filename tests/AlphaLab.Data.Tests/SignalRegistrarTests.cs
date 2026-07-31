using System.Text.Json;
using AlphaLab.Core.Signals;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Data.Tests;

/// <summary>
/// FR-43/D91: the pre-registered v1 set lands in `signals` as FROZEN rows, and re-registering is a
/// no-op. The freeze matters because `signal_ic` grades cite the `code_version` beside the params — a
/// silently rewritten registry row would leave a grade record describing arithmetic that no longer
/// exists.
/// </summary>
public class SignalRegistrarTests
{
    private static AlphaLabDbContext Migrated(string path)
    {
        var db = new AlphaLabDbContext(
            new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite($"Data Source={path}").Options);
        db.Database.Migrate();
        return db;
    }

    [Fact]
    public void RegisterV1_WritesTheSevenFrozenRows_ThenIsIdempotent()
    {
        var path = TestDb.NewPath();
        try
        {
            using var db = Migrated(path);

            Assert.Equal(7, new SignalRegistrar(db).RegisterV1("2026-01-30"));
            Assert.Equal(7, db.Signals.Count());

            // Every registered row round-trips its frozen params, family and code_version.
            foreach (var signal in SignalRegistry.V1)
            {
                var row = db.Signals.Single(s => s.SignalId == signal.SignalId);
                Assert.Equal(signal.Family, row.Family);
                Assert.Equal(signal.CodeVersion, row.CodeVersion);
                Assert.Equal("2026-01-30", row.RegisteredOn);
                var parsed = JsonSerializer.Deserialize<Dictionary<string, double>>(row.ConfigJson)!;
                Assert.Equal(signal.Params.OrderBy(p => p.Key, StringComparer.Ordinal).ToList(),
                             parsed.OrderBy(p => p.Key, StringComparer.Ordinal).ToList());
            }

            // Re-running writes NOTHING — an existing instrument is left untouched, not rewritten.
            var before = db.Signals.AsNoTracking().OrderBy(s => s.SignalId).ToList();
            Assert.Equal(0, new SignalRegistrar(db).RegisterV1("2026-06-01"));
            var after = db.Signals.AsNoTracking().OrderBy(s => s.SignalId).ToList();

            Assert.Equal(before.Count, after.Count);
            for (var i = 0; i < before.Count; i++)
            {
                // Including registered_on: the SECOND call passed a different date, and the row must
                // still carry the ORIGINAL. A row that re-stamped itself would rewrite provenance.
                Assert.Equal(before[i].RegisteredOn, after[i].RegisteredOn);
                Assert.Equal(before[i].ConfigJson, after[i].ConfigJson);
                Assert.Equal(before[i].CodeVersion, after[i].CodeVersion);
            }
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void ConfigJson_IsByteStable_AcrossRegistrations()
    {
        // The frozen JSON backs a determinism claim, so its bytes must not depend on dictionary
        // ordering. Two independent stores must produce identical config_json for the same signal.
        var a = TestDb.NewPath();
        var b = TestDb.NewPath();
        try
        {
            using var dbA = Migrated(a);
            using var dbB = Migrated(b);
            new SignalRegistrar(dbA).RegisterV1("2026-01-30");
            new SignalRegistrar(dbB).RegisterV1("2026-01-30");

            var rowsA = dbA.Signals.AsNoTracking().OrderBy(s => s.SignalId).Select(s => s.ConfigJson).ToList();
            var rowsB = dbB.Signals.AsNoTracking().OrderBy(s => s.SignalId).Select(s => s.ConfigJson).ToList();
            Assert.Equal(rowsA, rowsB);
        }
        finally { TestDb.Delete(a); TestDb.Delete(b); }
    }

    [Fact]
    public void SignalIc_RoundTrips_AndItsCompositeKeyRejectsADuplicateGrade()
    {
        var path = TestDb.NewPath();
        try
        {
            using var db = Migrated(path);
            new SignalRegistrar(db).RegisterV1("2026-01-30");

            db.SignalIc.Add(new SignalIcRow
            { SignalId = "mom:L126", AsOf = "2026-01-30", HorizonDays = 21, RankIc = 0.0375, N = 98 });
            db.SaveChanges();

            var row = db.SignalIc.Single();
            Assert.Equal(0.0375, row.RankIc, 9);
            Assert.Equal(98, row.N);

            // Same (signal, day, horizon) twice is a PK violation — one grade per triple, by construction.
            using var db2 = new AlphaLabDbContext(
                new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite($"Data Source={path}").Options);
            db2.SignalIc.Add(new SignalIcRow
            { SignalId = "mom:L126", AsOf = "2026-01-30", HorizonDays = 21, RankIc = 0.9, N = 1 });
            Assert.Throws<DbUpdateException>(() => db2.SaveChanges());
        }
        finally { TestDb.Delete(path); }
    }
}
