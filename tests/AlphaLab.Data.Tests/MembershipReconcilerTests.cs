using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;

namespace AlphaLab.Data.Tests;

/// <summary>
/// FX-MembershipAgree / FX-MembershipDiverge (TEST_PLAN §2) — FR-4 reconciliation. Sources are
/// synthetic per TEST_PLAN (deterministic builders, not the byte-real CSV). Asserts: agreement
/// applies the diff (added_on/removed_on stamped, nothing deleted, and NO delist corporate action —
/// decision #5); a count-sanity breach on either source or any divergence holds yesterday's state
/// and alerts (agreed=0 log row, zero mutation, no registration).
/// </summary>
public class MembershipReconcilerTests
{
    private static readonly int[] SmallBand = [1, 10];

    private static MembershipSnapshot Snap(string source, params string[] canonical) =>
        new(source, canonical.Select(s => new MemberRow(s, s, null)).ToList());

    private static MembershipReconciler NewReconciler(AlphaLabDbContext db) => new(db, new SecurityMaster(db));

    [Fact]
    public void FR4_Agreement_AppliesDiff_StampsDates_NothingDeleted_NoDelistCA()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            long cccId, dddId;
            using (var db = TestDb.Open(path))
            {
                var recon = NewReconciler(db);
                // Day 1: establish {AAA, BBB, CCC}.
                recon.Reconcile(Snap("ivv_csv", "AAA", "BBB", "CCC"),
                                Snap("wikipedia", "AAA", "BBB", "CCC"), "2026-01-01", SmallBand);
                // Day 2: drop CCC, add DDD (both sources agree).
                var result = recon.Reconcile(Snap("ivv_csv", "AAA", "BBB", "DDD"),
                                             Snap("wikipedia", "AAA", "BBB", "DDD"), "2026-01-02", SmallBand);

                cccId = db.Securities.Single(s => s.CurrentSymbol == "CCC").SecurityId;
                dddId = db.Securities.Single(s => s.CurrentSymbol == "DDD").SecurityId;

                Assert.True(result.Applied);
                Assert.Equal(new[] { dddId }, result.Adds);
                Assert.Equal(new[] { cccId }, result.Drops);
            }

            using (var db = TestDb.Open(path))
            {
                // DDD added on day 2, open.
                var ddd = db.IndexMembership.Single(m => m.SecurityId == dddId);
                Assert.Equal(("2026-01-02", (string?)null), (ddd.AddedOn, ddd.RemovedOn));

                // CCC dropped: removed_on stamped, row STILL EXISTS (never deleted).
                var ccc = db.IndexMembership.Single(m => m.SecurityId == cccId);
                Assert.Equal("2026-01-02", ccc.RemovedOn);

                // 4 rows total (AAA, BBB, CCC, DDD); 3 currently open (AAA, BBB, DDD).
                Assert.Equal(4, db.IndexMembership.Count());
                Assert.Equal(3, db.IndexMembership.Count(m => m.RemovedOn == null));

                // Two agreed refreshes logged; NO corporate action written (decision #5).
                Assert.Equal(2, db.IndexMembershipLog.Count(l => l.Agreed == 1));
                Assert.Empty(db.CorporateActions.ToList());
            }
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void FR4_CountSanityBreach_HoldsState_Alerts_NoMutation()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var recon = NewReconciler(db);

            var big = Enumerable.Range(0, 512).Select(i => $"T{i:D4}").ToArray(); // 512 > 510
            var cross = big.Take(500).ToArray();                                  // 500, in band
            var result = recon.Reconcile(Snap("ivv_csv", big), Snap("wikipedia", cross), "2026-01-01", [495, 510]);

            Assert.True(result.Held);
            Assert.Equal(512, result.PrimaryCount);
            Assert.Contains("count sanity", result.HeldReason);

            Assert.Empty(db.IndexMembership.ToList());   // nothing applied
            Assert.Empty(db.Securities.ToList());        // no registration on a held run

            var log = Assert.Single(db.IndexMembershipLog.ToList());
            Assert.Equal(0, log.Agreed);
            Assert.Contains("count sanity", log.Note!);
            Assert.Equal(512, log.SourceCount);
            Assert.Equal(500, log.CrosscheckCount);
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void FR4_Divergence_InBand_HoldsYesterdaysState()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var recon = NewReconciler(db);

            // Establish {AAA, BBB}.
            recon.Reconcile(Snap("ivv_csv", "AAA", "BBB"),
                            Snap("wikipedia", "AAA", "BBB"), "2026-01-01", SmallBand);

            // Day 2: primary says +NEWCO, cross-check does not (both counts in band) ⇒ divergence.
            var result = recon.Reconcile(Snap("ivv_csv", "AAA", "BBB", "NEWCO"),
                                         Snap("wikipedia", "AAA", "BBB"), "2026-01-02", SmallBand);

            Assert.True(result.Held);
            Assert.Contains("divergence", result.HeldReason);
            Assert.Contains("NEWCO", result.HeldReason);

            // Yesterday's state intact; NEWCO never registered or added.
            Assert.Equal(2, db.IndexMembership.Count(m => m.RemovedOn == null));
            Assert.False(db.Securities.Any(s => s.CurrentSymbol == "NEWCO"));
            Assert.Equal(1, db.IndexMembershipLog.Count(l => l.Agreed == 0));
        }
        finally { TestDb.Delete(path); }
    }
}
