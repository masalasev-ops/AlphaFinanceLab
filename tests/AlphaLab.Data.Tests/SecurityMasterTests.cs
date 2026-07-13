using AlphaLab.Data.Entities;
using AlphaLab.Data.Services;

namespace AlphaLab.Data.Tests;

/// <summary>
/// FX-TickerChange (TEST_PLAN §2): ACME→ACMX on day 40 of an 80-day series, position held through.
/// Exercises FR-3 / D39 — permanent security_id identity, time-ranged ticker_history aliases, zero
/// identity break, zero churn, continuous history joins across the rename.
/// </summary>
public class SecurityMasterTests
{
    // 80 consecutive calendar days from 2026-01-01; Day(1)…Day(80). The rename lands on Day(40).
    private static string Day(int n) => new DateOnly(2026, 1, 1).AddDays(n - 1).ToString("yyyy-MM-dd");

    private static (string path, long securityId) BuildFixture()
    {
        var path = TestDb.CreateMigrated();
        using var db = TestDb.Open(path);
        var master = new SecurityMaster(db);

        var securityId = master.Register("ACME", "US", Day(1), name: "Acme Corp");

        // A position held across the whole 80-day series: one bar per day under the SAME security_id.
        for (var n = 1; n <= 80; n++)
        {
            db.Bars.Add(new BarRow
            {
                SecurityId = securityId, Date = Day(n), Version = 1, ObservedAt = Day(n) + "T00:00:00Z",
                Close = 100 + n, Source = "eodhd"
            });
        }
        db.SaveChanges();

        master.RecordTickerChange(securityId, "ACMX", Day(40));
        return (path, securityId);
    }

    [Fact]
    public void FR3_TickerChange_ResolvesSameIdOnBothSides_OfTheRename()
    {
        var (path, securityId) = BuildFixture();
        try
        {
            using var db = TestDb.Open(path);
            var master = new SecurityMaster(db);

            // Same permanent id whether you ask by the old or the new symbol, at any in-range date.
            Assert.Equal(securityId, master.ResolveAsOf("ACME", "US", Day(20)));
            Assert.Equal(securityId, master.ResolveAsOf("ACMX", "US", Day(60)));

            // valid_from inclusive, valid_to exclusive: the new symbol owns the boundary day.
            Assert.Equal(securityId, master.ResolveAsOf("ACMX", "US", Day(40)));
            Assert.Null(master.ResolveAsOf("ACME", "US", Day(40)));

            // A symbol never resolves outside its interval.
            Assert.Null(master.ResolveAsOf("ACME", "US", Day(60)));
            Assert.Null(master.ResolveAsOf("ACMX", "US", Day(20)));
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void FR3_TickerChange_ZeroChurn_OneSecurity_TwoAliases_RenamedCurrentSymbol()
    {
        var (path, securityId) = BuildFixture();
        try
        {
            using var db = TestDb.Open(path);

            // Zero churn: exactly one security row for the whole span.
            Assert.Single(db.Securities.ToList());
            Assert.Equal("ACMX", db.Securities.Single().CurrentSymbol);

            // Two alias intervals under the one id: ACME [Day1, Day40), ACMX [Day40, open).
            var aliases = db.TickerHistory.Where(t => t.SecurityId == securityId)
                .OrderBy(t => t.ValidFrom).ToList();
            Assert.Equal(2, aliases.Count);
            Assert.Equal(("ACME", Day(1), Day(40)), (aliases[0].Symbol, aliases[0].ValidFrom, aliases[0].ValidTo));
            Assert.Equal(("ACMX", Day(40), (string?)null), (aliases[1].Symbol, aliases[1].ValidFrom, aliases[1].ValidTo));

            // A typed ticker_change corporate action was recorded (FR-3 corporate-action feed).
            var ca = Assert.Single(db.CorporateActions.Where(c => c.Type == "ticker_change").ToList());
            Assert.Equal(securityId, ca.SecurityId);
            Assert.Equal("ACMX", ca.NewSymbol);
            Assert.Equal(Day(40), ca.EffectiveDate);
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void FR3_TickerChange_ContinuousHistoryJoins_NoIdentityBreak()
    {
        var (path, securityId) = BuildFixture();
        try
        {
            using var db = TestDb.Open(path);
            var master = new SecurityMaster(db);

            var bars = db.Bars.Where(b => b.SecurityId == securityId).OrderBy(b => b.Date).ToList();

            // 80 continuous days, every one attached to the single permanent id (no break at the rename).
            Assert.Equal(80, bars.Count);
            Assert.Single(bars.Select(b => b.SecurityId).Distinct());
            Assert.Equal(Enumerable.Range(1, 80).Select(Day), bars.Select(b => b.Date));

            // The symbol-of-record joins correctly on each side of the rename.
            Assert.Equal(securityId, master.ResolveAsOf("ACME", "US", bars[38].Date)); // Day 39
            Assert.Equal(securityId, master.ResolveAsOf("ACMX", "US", bars[39].Date)); // Day 40
        }
        finally { TestDb.Delete(path); }
    }
}
