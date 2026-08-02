using System.Globalization;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;

namespace AlphaLab.Data.Tests;

/// <summary>
/// The D120 stored-corpus audit (findings 350/351): the CURRENT FR-6 gate re-run over a stored series,
/// plus the two member-window detectors the gate deliberately lacks (it is membership-blind). Exclusion
/// is recommended only on POSITIVE evidence the series is the wrong company — an impossible print, an
/// impossible single-event dividend yield, a non-member trading profile INSIDE a membership spell —
/// never on absence of data, and never outside a spell (a legit post-index microcap is not a defect).
/// </summary>
public class StoredSeriesAuditTests
{
    private const string Floor = "2006-01-03";

    private static StoredSeriesAudit NewAudit(DataQualityOptions? options = null)
    {
        var opts = options ?? new DataQualityOptions();
        return new StoredSeriesAudit(new DataQualityGate(opts), opts);
    }

    /// <summary>Sequential calendar dates from a start — the audit orders by ISO string, so weekends
    /// in the sequence are harmless.</summary>
    private static List<EodBar> Bars(string start, IReadOnlyList<double> closes, long? volume = 100_000)
    {
        var d = DateOnly.ParseExact(start, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var bars = new List<EodBar>(closes.Count);
        foreach (var close in closes)
        {
            var date = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            bars.Add(new EodBar(date, close, close, close, close, close, volume));
            d = d.AddDays(1);
        }
        return bars;
    }

    private static CorporateActionRow Dividend(string exDate, decimal cash) => new()
    {
        SecurityId = 1, Type = "dividend", ExDate = exDate, EffectiveDate = exDate,
        Version = 1, CashPerShare = cash, ObservedAt = "2026-07-24T00:00:00Z",
    };

    [Fact]
    public void FR6_StoreSweep_ImpossibleSpikeRevert_RecommendsExclusion()
    {
        // The ACS shape (finding 350): a flat penny regime with a one-session ×150 spike-and-revert.
        // Today's gate REJECTS that bar at ingest; the stored corpus predates the guard, so the audit
        // must surface it as positive evidence of a wrong series.
        var closes = Enumerable.Repeat(0.4, 10).Append(60.0).Concat(Enumerable.Repeat(0.4, 10)).ToList();
        var input = new SeriesAuditInput(1, "ACS", Bars("2010-01-04", closes), [],
            [new MembershipSpell("2004-04-02", "2010-02-08")], Floor);

        var finding = NewAudit().Audit(input);

        Assert.True(finding.GateRejects > 0);
        Assert.True(finding.RecommendExclusion);
        Assert.NotEmpty(finding.RejectSamples);
    }

    [Fact]
    public void FR6_StoreSweep_DividendYieldBreach_OnlyInsideMembershipSpell()
    {
        // The GR shape (finding 351): a $0.29 payout on a $0.40 print is ×0.72 of price in one event —
        // no real index member ever pays that; the PRICE is not the company that PAID. Sub-threshold
        // for R2 (the flaps are ×1.3), so only this detector can see it.
        var bars = Bars("2012-01-02", Enumerable.Repeat(0.4, 40).ToList(), volume: 10_000_000);

        var inSpell = new SeriesAuditInput(1, "GR", bars, [Dividend("2012-02-01", 0.29m)],
            [new MembershipSpell("1996-01-02", "2012-07-27")], Floor);
        var found = NewAudit().Audit(inSpell);
        Assert.Equal(1, found.DividendYieldBreaches);
        Assert.True(found.RecommendExclusion);

        // The SAME series and dividend with the spell already ENDED: a post-index microcap paying a
        // large yield is implausible-looking but not the lab's defect — never flagged.
        var outOfSpell = new SeriesAuditInput(1, "GR", bars, [Dividend("2012-02-01", 0.29m)],
            [new MembershipSpell("1996-01-02", "2011-12-30")], Floor);
        var notFound = NewAudit().Audit(outOfSpell);
        Assert.Equal(0, notFound.DividendYieldBreaches);
        Assert.False(notFound.RecommendExclusion);
    }

    [Fact]
    public void FR6_StoreSweep_DividendPriceTooStale_IsUnknowableNotImplausible()
    {
        // The last print sits 20 days before the ex-date — beyond the 14-day lookback the yield is
        // UNKNOWABLE (the series may simply have a gap), so no flag rather than a guessed breach.
        var bars = Bars("2012-01-02", Enumerable.Repeat(0.4, 10).ToList(), volume: 10_000_000);
        var input = new SeriesAuditInput(1, "GR", bars, [Dividend("2012-01-31", 0.29m)],
            [new MembershipSpell("1996-01-02", "2012-07-27")], Floor);

        Assert.Equal(0, NewAudit().Audit(input).DividendYieldBreaches);
    }

    [Fact]
    public void FR6_StoreSweep_MemberDollarVolumeFloor_CatchesSubThresholdGarbage()
    {
        // 0.40 × 1,000 shares = $400/day for 80 in-spell sessions: an "S&P member" trading four hundred
        // dollars a day is the recycled-ticker profile (finding 351). Every ×1.3 flap walks under R2;
        // the volume floor is what sees it.
        var garbage = new SeriesAuditInput(1, "TIN",
            Bars("2010-01-04", Enumerable.Repeat(0.4, 80).ToList(), volume: 1_000),
            [], [new MembershipSpell("1996-01-02", "2012-02-13")], Floor);
        var flagged = NewAudit().Audit(garbage);
        Assert.True(flagged.DollarVolumeBreachWindows > 0);
        Assert.True(flagged.RecommendExclusion);

        // A real member's profile — $50 × 1M shares = $50M/day — never breaches.
        var real = new SeriesAuditInput(2, "OK",
            Bars("2010-01-04", Enumerable.Repeat(50.0, 80).ToList(), volume: 1_000_000),
            [], [new MembershipSpell("1996-01-02", null)], Floor);
        var clean = NewAudit().Audit(real);
        Assert.Equal(0, clean.DollarVolumeBreachWindows);
        Assert.False(clean.RecommendExclusion);
    }

    [Fact]
    public void FR6_StoreSweep_NullVolume_IsMissingData_NeverAZeroDollarBreach()
    {
        // A null volume is data the vendor did not supply, not evidence of no trading — it must not
        // manufacture a $0 median (rule 10's "nothing silently defaulted", applied to the detector).
        var input = new SeriesAuditInput(1, "NV",
            Bars("2010-01-04", Enumerable.Repeat(50.0, 80).ToList(), volume: null),
            [], [new MembershipSpell("1996-01-02", null)], Floor);

        var finding = NewAudit().Audit(input);
        Assert.Equal(0, finding.DollarVolumeBreachWindows);
        Assert.False(finding.RecommendExclusion);
    }

    [Fact]
    public void FR6_StoreSweep_SpellWithNoBars_ReportedButNeverExcluded()
    {
        // The NCC shape (finding 350): a 1996–2009 membership spell with ZERO stored bars — the vendor
        // file is entirely the post-2012 recycled listing. Coverage, not exclusion: nothing was
        // ingested for the spell, so there is nothing to quarantine; the report line is the rollup of
        // the per-day missing_bar warns.
        var input = new SeriesAuditInput(1, "NCC",
            Bars("2012-05-03", Enumerable.Repeat(3.83, 10).ToList()),
            [], [new MembershipSpell("1996-01-02", "2009-01-02")], Floor);

        var finding = NewAudit().Audit(input);
        Assert.Equal(1, finding.SpellsWithNoBars);
        Assert.False(finding.RecommendExclusion);
    }

    [Fact]
    public void FR6_StoreSweep_CleanMemberSeries_IsNotFlagged()
    {
        // Gentle drift, real volume, a plausible dividend: nothing trips, nothing is recommended.
        var closes = Enumerable.Range(0, 80).Select(i => 50.0 + 0.05 * i).ToList();
        var input = new SeriesAuditInput(1, "OK", Bars("2010-01-04", closes, volume: 2_000_000),
            [Dividend("2010-02-01", 0.30m)], [new MembershipSpell("1996-01-02", null)], Floor);

        var finding = NewAudit().Audit(input);
        Assert.Equal(0, finding.GateRejects);
        Assert.Equal(0, finding.DividendYieldBreaches);
        Assert.Equal(0, finding.DollarVolumeBreachWindows);
        Assert.Equal(0, finding.SpellsWithNoBars);
        Assert.False(finding.RecommendExclusion);
    }
}
