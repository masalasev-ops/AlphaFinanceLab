using System.Globalization;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Providers;

namespace AlphaLab.Data.Services;

/// <summary>One index-membership spell, as-of (SCHEMA `index_membership`).</summary>
public sealed record MembershipSpell(string AddedOn, string? RemovedOn);

/// <summary>Everything the audit needs for ONE security, pre-loaded by the caller so the audit itself
/// stays pure (the <see cref="DataQualityGate"/> precedent): the latest-version bar series in date
/// order, the security's dividend/split feed, its membership spells, and the store-wide coverage
/// floor (the earliest bar date in the whole store — spells before it cannot be judged).</summary>
public sealed record SeriesAuditInput(
    long SecurityId,
    string Symbol,
    IReadOnlyList<EodBar> Bars,
    IReadOnlyList<CorporateActionRow> Actions,
    IReadOnlyList<MembershipSpell> Spells,
    string StoreWindowFloor);

/// <summary>The audit verdict for one security (D120). Counts, a sample of evidence for the report,
/// and the roster-exclusion recommendation. REPORT-ONLY data — nothing here writes anything.</summary>
public sealed record SeriesAuditFinding(
    long SecurityId,
    string Symbol,
    int GateRejects,
    int GateWarns,
    IReadOnlyList<string> RejectSamples,
    int DividendYieldBreaches,
    string? WorstDividendDetail,
    int DollarVolumeBreachWindows,
    string? WorstVolumeDetail,
    int SpellsWithNoBars,
    bool RecommendExclusion);

/// <summary>
/// The stored-corpus quality audit (D120, finding 350). The live store's bars were ingested 2026-07-15..23,
/// BEFORE the v1.9.41 spike-and-revert Reject existed (R2 landed 2026-07-24, e786a0f), so the corpus was
/// never screened by the guard that now protects fresh ingests — 55 securities carry &gt;×10 close-to-close
/// jumps that today's gate would have excluded at ingest (finding 350). This audit re-runs the CURRENT
/// <see cref="IDataQualityGate"/> over each stored series and adds two detectors the gate deliberately
/// lacks because they need MEMBERSHIP context (the gate is symbol-keyed and membership-blind by design):
///
///  1. Single-event dividend-yield plausibility: a real index member never pays a single dividend worth
///     ≥ <see cref="DataQualityOptions.SweepMaxSingleDividendYield"/> of its price on the ex-date (the
///     largest real specials are ~×0.3). A breach means the PRICE is not the company that PAID the
///     dividend — GR's $0.29 quarterly on a $0.40 print implies a ×0.72 single payout (finding 351).
///  2. Member dollar-volume floor: an S&amp;P member whose 63-session median close×volume falls below
///     <see cref="DataQualityOptions.SweepMinMemberDollarVolume"/> is not trading like an index member —
///     the flapping recycled-ticker prints trade a few hundred dollars a day. This catches the
///     sub-threshold garbage R2 cannot see (GR flaps ×1.3, under the ×10 bound, finding 351).
///
/// Both detectors judge only sessions INSIDE a membership spell and at/after the store's coverage floor,
/// so a name that legitimately became a microcap AFTER leaving the index is never flagged for its
/// post-index life. A spell overlapping the store window with NO bars at all is reported (coverage, not
/// exclusion — there is nothing to exclude when nothing was ingested; NCC's 1996–2009 spell has zero
/// stored bars because EODHD's NCC file is entirely the post-2012 recycled listing).
///
/// The remediation is `Universe:Exclusions` (finding 266's roster deny-list) — the rule-3-compliant
/// substitute for deleting bars. The audit only RECOMMENDS; the operator reviews the report and edits
/// the config. PURE: no DB reads, no writes — the orchestrator loads, this judges.
/// </summary>
public sealed class StoredSeriesAudit(IDataQualityGate gate, DataQualityOptions options)
{
    /// <summary>The member-volume window, in sessions. Matches the monitor's 63-day rolling window: long
    /// enough that a real member's earnings-lull week cannot breach, short enough to localise a garbage
    /// regime inside an otherwise-plausible series.</summary>
    public const int DollarVolumeWindowSessions = 63;

    /// <summary>How far before an ex-date the price print may sit and still price the dividend (calendar
    /// days). Beyond this the yield is unknowable rather than implausible, so no flag.</summary>
    private const int DividendPriceLookbackDays = 14;

    /// <summary>How many reject dates to carry into the report per security — evidence, not inventory.</summary>
    private const int RejectSampleSize = 3;

    public SeriesAuditFinding Audit(SeriesAuditInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var report = gate.Evaluate(input.Symbol, input.Bars, input.Actions, expectedDates: null);
        var rejects = report.Flags.Where(f => f.Severity == QualitySeverity.Reject).ToList();
        var warns = report.Flags.Count - rejects.Count;
        var rejectSamples = rejects.Take(RejectSampleSize)
            .Select(f => $"{f.Date}: {f.Detail}")
            .ToList();

        var (divBreaches, worstDiv) = AuditDividendYields(input);
        var (volBreaches, worstVol) = AuditMemberDollarVolume(input);
        var barelessSpells = CountSpellsWithNoBars(input);

        // Exclusion is recommended on POSITIVE evidence of a wrong series (an impossible print, an
        // impossible yield, a non-member trading profile inside a membership spell) — never on absence
        // of data alone, and never on the gate's Warn-severity flags (those are review material).
        var recommend = rejects.Count > 0 || divBreaches > 0 || volBreaches > 0;

        return new SeriesAuditFinding(
            input.SecurityId, input.Symbol,
            rejects.Count, warns, rejectSamples,
            divBreaches, worstDiv,
            volBreaches, worstVol,
            barelessSpells, recommend);
    }

    // ---- Detector 1: single-event dividend yield inside a membership spell ----
    private (int Breaches, string? Worst) AuditDividendYields(SeriesAuditInput input)
    {
        var breaches = 0;
        string? worst = null;
        var worstYield = 0.0;

        foreach (var action in input.Actions)
        {
            if (action.Type != "dividend" || action.CashPerShare is not { } cash || cash <= 0) continue;
            var exDate = action.ExDate ?? action.EffectiveDate;
            if (string.IsNullOrWhiteSpace(exDate) || !InAnySpell(input.Spells, exDate)) continue;

            var price = LastCloseOnOrBefore(input.Bars, exDate, DividendPriceLookbackDays);
            if (price is not { } p || p <= 0) continue;

            var yield = (double)cash / p;
            if (yield < options.SweepMaxSingleDividendYield) continue;

            breaches++;
            if (yield > worstYield)
            {
                worstYield = yield;
                worst = string.Create(CultureInfo.InvariantCulture,
                    $"{exDate}: {cash} per share on a {p} close = ×{yield:0.00} of price in ONE payout");
            }
        }
        return (breaches, worst);
    }

    // ---- Detector 2: rolling median dollar volume inside a membership spell ----
    private (int BreachWindows, string? Worst) AuditMemberDollarVolume(SeriesAuditInput input)
    {
        // In-spell sessions at/after the store floor, with a priceable close AND a reported volume.
        // A null volume is missing data, not zero trading — it never contributes to a breach.
        var dollars = new List<(string Date, double Dollars)>();
        foreach (var b in input.Bars)
        {
            if (string.CompareOrdinal(b.Date, input.StoreWindowFloor) < 0) continue;
            if (!InAnySpell(input.Spells, b.Date)) continue;
            if (b.Close is not { } close || close <= 0 || b.Volume is not { } vol) continue;
            dollars.Add((b.Date, close * vol));
        }
        if (dollars.Count < DollarVolumeWindowSessions) return (0, null);

        var breaches = 0;
        string? worst = null;
        var worstMedian = double.MaxValue;
        var window = new double[DollarVolumeWindowSessions];
        for (var i = DollarVolumeWindowSessions - 1; i < dollars.Count; i++)
        {
            for (var j = 0; j < DollarVolumeWindowSessions; j++)
            {
                window[j] = dollars[i - DollarVolumeWindowSessions + 1 + j].Dollars;
            }
            var median = Median(window);
            if (median >= options.SweepMinMemberDollarVolume) continue;

            breaches++;
            if (median < worstMedian)
            {
                worstMedian = median;
                worst = string.Create(CultureInfo.InvariantCulture,
                    $"63-session median ${median:0} / day ending {dollars[i].Date} — an index member trades orders of magnitude more");
            }
        }
        return (breaches, worst);
    }

    // ---- Coverage: a membership spell overlapping the store window with no bars at all ----
    private static int CountSpellsWithNoBars(SeriesAuditInput input)
    {
        var count = 0;
        foreach (var spell in input.Spells)
        {
            // The judgeable part of the spell starts at the later of (spell start, store floor).
            var from = string.CompareOrdinal(spell.AddedOn, input.StoreWindowFloor) > 0
                ? spell.AddedOn : input.StoreWindowFloor;
            var to = spell.RemovedOn;
            if (to is not null && string.CompareOrdinal(to, from) < 0) continue; // ends before the floor

            var any = input.Bars.Any(b =>
                string.CompareOrdinal(b.Date, from) >= 0 &&
                (to is null || string.CompareOrdinal(b.Date, to) <= 0));
            if (!any) count++;
        }
        return count;
    }

    private static bool InAnySpell(IReadOnlyList<MembershipSpell> spells, string date) =>
        spells.Any(s => string.CompareOrdinal(date, s.AddedOn) >= 0 &&
                        (s.RemovedOn is null || string.CompareOrdinal(date, s.RemovedOn) <= 0));

    /// <summary>The last close at or before <paramref name="exDate"/>, no older than the lookback —
    /// a price too far from the ex-date makes the yield unknowable, not implausible.</summary>
    private static double? LastCloseOnOrBefore(IReadOnlyList<EodBar> bars, string exDate, int lookbackDays)
    {
        if (!DateOnly.TryParseExact(exDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var ex))
        {
            return null;
        }
        var floor = ex.AddDays(-lookbackDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        for (var i = bars.Count - 1; i >= 0; i--)
        {
            var b = bars[i];
            if (string.CompareOrdinal(b.Date, exDate) > 0) continue;
            if (string.CompareOrdinal(b.Date, floor) < 0) return null;
            return b.Close is { } c && c > 0 ? c : null;
        }
        return null;
    }

    private static double Median(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        var n = sorted.Length;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }
}
