using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;

namespace AlphaLab.Data.Tests;

/// <summary>
/// FR-4 / D49/D70 — historical membership ingestion from the fja05680/sp500 community CSV (§8), the
/// input to as-of reconstruction (this feeds FX-AsOfMembership, which the Phase-4 replay engine
/// validates end-to-end). Parse is tested against the byte-real fixture; interval reconstruction is
/// tested with a controlled synthetic sequence (add/drop/re-add) and then confirmed end-to-end on the
/// real 30-year file via as-of reads.
/// </summary>
public class HistoricalMembershipTests
{
    private const string HistoricalCsv = "S_P_500_Historical_Components___Changes__Updated.csv";

    private static long Id(AlphaLabDbContext db, string canonical) =>
        db.Securities.Single(s => s.CurrentSymbol == canonical).SecurityId;

    // ---- parse (real fixture, no DB) ----
    [Fact]
    public void Parse_RealHistoricalCsv_2718Snapshots_DateRange_And_SymbologyQuirks()
    {
        var snaps = HistoricalMembershipCsvParser.Parse(Fixtures.Historical(HistoricalCsv));

        Assert.Equal(2718, snaps.Count);
        Assert.Equal("1996-01-02", snaps[0].Date);
        Assert.Equal("2026-06-30", snaps[^1].Date);

        var first = snaps[0].RawTickers.ToHashSet();
        var last = snaps[^1].RawTickers.ToHashSet();
        Assert.Contains("AAMRQ", first);      // bankruptcy *Q preserved (not stripped)
        Assert.Contains("AAPL", first);
        Assert.DoesNotContain("TSLA", first); // added later
        Assert.Contains("BRK.B", last);       // dot form preserved in the raw roster
        Assert.Contains("TSLA", last);
        Assert.DoesNotContain("AAMRQ", last); // delisted — the anti-survivorship point
    }

    // ---- interval reconstruction (synthetic sequence, real service) ----
    [Fact]
    public void Ingest_AddDropReadd_BuildsHalfOpenIntervals_NeverDeletes_CanonicalizesDots()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            var snaps = new List<HistoricalMembershipSnapshot>
            {
                new("2020-01-01", ["A", "B", "BRK.B"]),
                new("2020-01-02", ["A", "B", "C", "BRK.B"]),
                new("2020-01-03", ["A", "C", "BRK.B"]),          // B dropped
                new("2020-01-04", ["A", "C", "B", "BRK.B"]),     // B re-added
            };

            int written;
            using (var db = TestDb.Open(path))
            {
                written = new HistoricalMembershipIngestion(db).Ingest(snaps);
            }
            // A[open], C[open], BRK-B[open], B[bounded]+B[open] = 5 intervals
            Assert.Equal(5, written);

            using (var db = TestDb.Open(path))
            {
                // Dot canonicalized to the EODHD form.
                Assert.NotNull(db.Securities.SingleOrDefault(s => s.CurrentSymbol == "BRK-B"));

                // B has two intervals: [D1,D3) closed and [D4,open); the closed one persists (never deleted).
                var bId = Id(db, "B");
                var bRows = db.IndexMembership.Where(m => m.SecurityId == bId).OrderBy(m => m.AddedOn).ToList();
                Assert.Equal(2, bRows.Count);
                Assert.Equal(("2020-01-01", "2020-01-03"), (bRows[0].AddedOn, bRows[0].RemovedOn));
                Assert.Equal(("2020-01-04", (string?)null), (bRows[1].AddedOn, bRows[1].RemovedOn));

                var read = new IndexMembershipReadService(db);
                HashSet<long> AsOf(string d) => read.MembersAsOf(d).ToHashSet();
                var (a, b, c, brk) = (Id(db, "A"), bId, Id(db, "C"), Id(db, "BRK-B"));

                Assert.Equal(new HashSet<long> { a, b, brk }, AsOf("2020-01-01"));
                Assert.Equal(new HashSet<long> { a, b, c, brk }, AsOf("2020-01-02"));
                Assert.Equal(new HashSet<long> { a, c, brk }, AsOf("2020-01-03")); // B out on its removal date (half-open)
                Assert.Equal(new HashSet<long> { a, b, c, brk }, AsOf("2020-01-04"));
            }
        }
        finally { TestDb.Delete(path); }
    }

    // ---- end-to-end on the real 30-year file ----
    [Fact]
    public void Ingest_RealHistoricalCsv_AsOfResolvesSurvivorsAndDelistings()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            var snaps = HistoricalMembershipCsvParser.Parse(Fixtures.Historical(HistoricalCsv));

            using (var db = TestDb.Open(path))
            {
                new HistoricalMembershipIngestion(db).Ingest(snaps);
            }

            using (var db = TestDb.Open(path))
            {
                var read = new IndexMembershipReadService(db);
                var in2026 = read.MembersAsOf("2026-06-30").ToHashSet();
                var in1996 = read.MembersAsOf("1996-01-02").ToHashSet();

                var brk = Id(db, "BRK-B"); // from dot BRK.B
                var aapl = Id(db, "AAPL");
                var tsla = Id(db, "TSLA");
                var aamr = Id(db, "AAMRQ"); // bankruptcy *Q kept verbatim (no dot, no alias)

                // Survivors present as of 2026; delisted AAMR absent.
                Assert.Contains(brk, in2026);
                Assert.Contains(aapl, in2026);
                Assert.Contains(tsla, in2026);
                Assert.DoesNotContain(aamr, in2026);

                // 1996 roster: AAMR present, TSLA not yet.
                Assert.Contains(aamr, in1996);
                Assert.Contains(aapl, in1996);
                Assert.DoesNotContain(tsla, in1996);

                // A real S&P 500 roster is in-band on any given day (survivorship-free reconstruction).
                Assert.InRange(in2026.Count, 480, 520);
            }
        }
        finally { TestDb.Delete(path); }
    }
}
