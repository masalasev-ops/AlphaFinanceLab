using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;

namespace AlphaLab.Data.Tests;

/// <summary>
/// FR-4 / D70 — the S&amp;P 100 launch slice served by the shared iShares provider with the OEF preset
/// (portfolioId 239723). The OEF CSV is the same shape as IVV (one C-4 fixture covers both, §2b), so
/// the parse is already covered by ISharesHoldingsParseTests; here we confirm the OEF feed via the
/// generalized provider and reconciliation at the slice count band [99,103].
/// </summary>
public class OefSliceTests
{
    [Fact]
    public void Presets_SelectTheRightFundAndSourceLabel()
    {
        var ivv = ISharesHoldingsOptions.Ivv();
        Assert.Equal(("239726", "ivv_csv"), (ivv.PortfolioId, ivv.Source));

        var oef = ISharesHoldingsOptions.Oef();
        Assert.Equal(("239723", "oef_csv"), (oef.PortfolioId, oef.Source));
    }

    [Fact]
    public void ToSnapshot_RealOef_Yields102CanonicalMembers_WithKnownNames()
    {
        var snap = ISharesHoldingsMembershipProvider.ToSnapshot("oef_csv", Fixtures.Holdings("OEF_holdings.csv"));

        Assert.Equal("oef_csv", snap.Source);
        Assert.Equal(102, snap.Members.Count);
        Assert.InRange(snap.Members.Count, 99, 103);

        var canonical = snap.Members.Select(m => m.CanonicalSymbol).ToHashSet();
        Assert.Contains("AAPL", canonical);
        Assert.Contains("NVDA", canonical);
        Assert.Contains("BRK-B", canonical); // OEF 'BRKB' canonicalized to the EODHD dash form
    }

    [Fact]
    public void Reconcile_AtSliceBand_99to103_AppliesWhenSourcesAgree()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var recon = new MembershipReconciler(db, new SecurityMaster(db));

            var slice = Enumerable.Range(0, 101).Select(i => $"S{i:D3}").ToArray(); // 101 ∈ [99,103]
            var members = slice.Select(s => new MemberRow(s, s, null)).ToList();

            var result = recon.Reconcile(
                new MembershipSnapshot("oef_csv", members),
                new MembershipSnapshot("wikipedia_sp100", members),
                "2026-01-01", [99, 103]);

            Assert.True(result.Applied);
            Assert.Equal(101, db.IndexMembership.Count(m => m.RemovedOn == null));
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void Reconcile_SliceCountOutOfBand_HoldsClosed()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var recon = new MembershipReconciler(db, new SecurityMaster(db));

            var tooMany = Enumerable.Range(0, 110).Select(i => $"S{i:D3}").ToArray(); // 110 > 103
            var members = tooMany.Select(s => new MemberRow(s, s, null)).ToList();

            var result = recon.Reconcile(
                new MembershipSnapshot("oef_csv", members),
                new MembershipSnapshot("wikipedia_sp100", members),
                "2026-01-01", [99, 103]);

            Assert.True(result.Held);
            Assert.Empty(db.IndexMembership.ToList());
        }
        finally { TestDb.Delete(path); }
    }
}
