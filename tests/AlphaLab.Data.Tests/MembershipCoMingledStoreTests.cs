using System.Text.Json;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;

namespace AlphaLab.Data.Tests;

/// <summary>
/// Checkpoint 6.4 — the reconcile diff is taken against the FORWARD ROSTER, not against every open
/// interval row.
///
/// These fixtures reproduce the LIVE store's shape: the Phase-4 historical seed co-mingled the full
/// S&amp;P 500 as-of history into `index_membership`, so `WHERE removed_on IS NULL` stopped being the
/// universe a forward reconcile is authoritative for. One blind read caused two defects with opposite
/// symptoms, and both are pinned here because neither was catchable by the count-sanity gate — that
/// gate bounds the two SOURCES, never the store.
///
/// Scaled down from the real numbers (503 open ids, a 101-id slice, 402 would-be evictions) to keep the
/// fixture readable; the mechanism is identical and does not depend on the magnitudes.
/// </summary>
public class MembershipCoMingledStoreTests
{
    private const string Wiki = "wikipedia_sp100";
    private const string Oef = "oef_csv";

    private static MembershipSnapshot Snap(string source, params string[] symbols) =>
        new(source, symbols.Select(s => new MemberRow(s, s, null)).ToList());

    /// <summary>The live arena's shape: a small forward slice plus a wider co-mingled history.</summary>
    private static (long[] Slice, long[] HistoryOnly) SeedCoMingledStore(AlphaLabDbContext db)
    {
        var master = new SecurityMaster(db);

        // The forward slice: registered and open from the bootstrap date.
        var slice = new[] { "AAA", "BBB", "CCC" }
            .Select(s => master.ResolveOrRegister(s, "US", "2026-07-15")).ToArray();
        foreach (var id in slice)
        {
            db.IndexMembership.Add(new IndexMembershipRow { SecurityId = id, AddedOn = "2026-07-15", RemovedOn = null });
        }

        // The historical S&P 500 seed: the same three names ALSO carry a long-open historical spell
        // (this is why the live store has 101 double-open ids), plus names that are in the S&P 500 and
        // were never in the slice. Those are the eviction targets.
        foreach (var id in slice)
        {
            db.IndexMembership.Add(new IndexMembershipRow { SecurityId = id, AddedOn = "1996-01-02", RemovedOn = null });
        }
        var historyOnly = new[] { "HHH", "III", "JJJ", "KKK" }
            .Select(s => master.ResolveOrRegister(s, "US", "1996-01-02")).ToArray();
        foreach (var id in historyOnly)
        {
            db.IndexMembership.Add(new IndexMembershipRow { SecurityId = id, AddedOn = "1996-01-02", RemovedOn = null });
        }
        db.SaveChanges();

        // The versioned slice snapshot — the config row SliceScopedMembershipRead intersects against.
        db.Config.Add(new ConfigRow
        {
            Key = HistoricalBackfillRunner.SliceConfigKey,
            ValueJson = JsonSerializer.Serialize(slice.OrderBy(x => x).ToList()),
            Version = 1,
            ChangedOn = "2026-07-23",
            Reason = "test: the forward slice snapshot",
        });
        db.SaveChanges();
        return (slice, historyOnly);
    }

    private static MembershipReconciler Reconciler(AlphaLabDbContext db) =>
        new(db, new SecurityMaster(db),
            new SliceScopedMembershipRead(new IndexMembershipReadService(db), db,
                new UniverseOptions { Bootstrap = { Universe = "sp100" } }));

    /// <summary>
    /// THE MASS-EVICTION TRAP. A forward refresh names the ~S&amp;P 100 roster; every open name outside it
    /// used to classify as a drop, so the whole co-mingled S&amp;P 500 roster would be stamped
    /// `removed_on = today`. In the live arena that is 402 names, and it destroys the rule-22 widening
    /// target while recording 402 false universe-exit events.
    /// </summary>
    [Fact]
    public void FX_ForwardReconcile_NeverEvictsNamesOutsideTheForwardRoster()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var (slice, historyOnly) = SeedCoMingledStore(db);

            // The sources agree on exactly the forward slice — an ordinary, quiet refresh.
            var result = Reconciler(db).Reconcile(
                Snap(Oef, "AAA", "BBB", "CCC"), Snap(Wiki, "AAA", "BBB", "CCC"), "2026-08-07", [1, 10]);

            Assert.True(result.Applied);
            Assert.Empty(result.Drops);          // <- the trap: this used to be every history-only name
            Assert.Empty(result.Adds);

            foreach (var id in historyOnly)
            {
                Assert.All(db.IndexMembership.Where(m => m.SecurityId == id).ToList(),
                    m => Assert.Null(m.RemovedOn));
            }
        }
        finally { TestDb.Delete(path); }
    }

    /// <summary>
    /// THE INVISIBLE-ADDS LEAK, and the worse of the two because it is silent and is NOT repaired by
    /// re-seeding. A genuine new S&amp;P 100 entrant is ALREADY open as an S&amp;P 500 member, so it never
    /// appeared in `Adds`, never reached the slice snapshot, and was filtered out of the forward
    /// universe permanently — the exact "removals applied, adds vanished" asymmetry
    /// `MaintainSliceSnapshot` exists to prevent, reintroduced beneath it.
    /// </summary>
    [Fact]
    public void FX_ForwardReconcile_ANewEntrantAlreadyOpenInHistory_IsStillAnAdd()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var (_, historyOnly) = SeedCoMingledStore(db);
            var entrant = historyOnly[0];   // HHH: in the S&P 500 history, not in the slice

            var result = Reconciler(db).Reconcile(
                Snap(Oef, "AAA", "BBB", "CCC", "HHH"), Snap(Wiki, "AAA", "BBB", "CCC", "HHH"),
                "2026-08-07", [1, 10]);

            Assert.True(result.Applied);
            Assert.Equal([entrant], result.Adds);
            Assert.Empty(result.Drops);

            // ...and it gains NO second open row: it re-enters the forward universe through the slice
            // snapshot the caller advances, so writing another interval would only deepen the
            // double-open-row state the co-mingling created.
            Assert.Single(db.IndexMembership.Where(m => m.SecurityId == entrant && m.RemovedOn == null).ToList());
        }
        finally { TestDb.Delete(path); }
    }

    /// <summary>
    /// A real forward exit still works — and closes the CURRENT spell, not an arbitrary one. The
    /// previous `.First()` over an unordered list picked the forward row only because forward rows
    /// carry lower rowids; ordering by `added_on` makes it the rule rather than the luck, so an index
    /// change can never silently rewrite as-of membership for dates decades back.
    /// </summary>
    [Fact]
    public void FX_ForwardReconcile_ADropClosesTheCurrentSpell_LeavingTheHistoricalSpellOpen()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var (slice, _) = SeedCoMingledStore(db);
            var leaver = slice[2];   // CCC leaves the S&P 100

            var result = Reconciler(db).Reconcile(
                Snap(Oef, "AAA", "BBB"), Snap(Wiki, "AAA", "BBB"), "2026-08-07", [1, 10]);

            Assert.True(result.Applied);
            Assert.Equal([leaver], result.Drops);

            var spells = db.IndexMembership.Where(m => m.SecurityId == leaver).ToList();
            Assert.Equal("2026-08-07", spells.Single(m => m.AddedOn == "2026-07-15").RemovedOn);
            Assert.Null(spells.Single(m => m.AddedOn == "1996-01-02").RemovedOn);
        }
        finally { TestDb.Delete(path); }
    }

    /// <summary>
    /// The pre-co-mingling arena (no slice snapshot) is unchanged: `index_membership` IS the forward
    /// roster there, the raw as-of read returns it, and the diff behaves exactly as it did before 6.4.
    /// This is what makes the change behaviour-preserving on a fresh arena rather than a new mode.
    /// </summary>
    [Fact]
    public void FX_ForwardReconcile_OnAFreshArenaWithNoSlice_BehavesExactlyAsBefore()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var master = new SecurityMaster(db);
            foreach (var s in new[] { "AAA", "BBB" })
            {
                db.IndexMembership.Add(new IndexMembershipRow
                {
                    SecurityId = master.ResolveOrRegister(s, "US", "2026-07-15"),
                    AddedOn = "2026-07-15",
                    RemovedOn = null,
                });
            }
            db.SaveChanges();

            var result = Reconciler(db).Reconcile(
                Snap(Oef, "AAA", "CCC"), Snap(Wiki, "AAA", "CCC"), "2026-08-07", [1, 10]);

            Assert.True(result.Applied);
            Assert.Single(result.Adds);    // CCC
            Assert.Single(result.Drops);   // BBB
            Assert.Equal("2026-08-07", db.IndexMembership.Single(m => m.AddedOn == "2026-07-15" && m.RemovedOn != null).RemovedOn);
        }
        finally { TestDb.Delete(path); }
    }
}
