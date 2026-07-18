using AlphaLab.Core.Domain;

namespace AlphaLab.Core.Funnel;

/// <summary>
/// One (security, reason) note from any funnel stage — a name dropped, held, closed, or skipped,
/// always with the reason. Every stage emits these and they all land in
/// <c>decisions.stage_json</c>, so it can answer "why wasn't X considered?" — or "why is X still
/// held?" — a year later. A silent drop is indistinguishable from a bug.
///
/// Deliberately NOT named `Exclusion`: Stage 4 uses it to record HOLDS as well as closes, and a
/// type called Exclusion carrying "held: rank 5 is still within the buffer" would be lying. The
/// exclusion semantics live in the property names (`Excluded`), not in the type.
/// </summary>
public sealed record FunnelNote(SecurityId Id, string Reason);

/// <summary>The Stage-1 outcome: the shared pool, plus every name that fell out and why.</summary>
public sealed record EligibilityResult(
    IReadOnlyList<SecurityId> Eligible,
    IReadOnlyList<FunnelNote> Excluded);

/// <summary>
/// Stage 1 of the daily funnel (MASTER §6) — the SHARED eligible pool. Shared is the point: every
/// strategy starts from the same names, so any difference in results is genuinely about the
/// strategy and not about who saw which universe.
///
/// PURE. Takes the as-of roster and the point-in-time view; returns the pool. No DB, no clock.
/// The caller supplies the roster from IIndexMembershipRead (Data) — Core cannot reference Data,
/// and pushing that read out keeps the whole funnel testable with a hand-built feature view.
///
/// §6 describes Stage 1 as "(in-index flag, liquid, priced)". Two of those three are implemented
/// here. The third is not, and that is deliberate — see below.
/// </summary>
public static class Eligibility
{
    /// <summary>
    /// Resolve the shared pool for <paramref name="asOf"/>.
    ///
    /// IN-INDEX comes from <paramref name="indexMembers"/> — the as-of roster (D20: membership is
    /// state, not a filter; a name is in the pool on the days it was in the index, and history is
    /// never rewritten when it leaves).
    ///
    /// PRICED is <see cref="IFeatureView.PricedOn"/>: a bar visible at the run's watermark carrying
    /// both a raw and an adjusted close. A member with no bar today (a halt, a late feed) is not
    /// dropped from the index — it is simply not actionable today, which is a different claim and
    /// is recorded as such.
    ///
    /// LIQUID CURRENTLY FILTERS NOTHING. There is no liquidity gate in this method, and that is not
    /// an oversight or a TODO — nothing in the design defines `liquid` as an ELIGIBILITY filter.
    /// §6's diagram lists the word; D20 says "Liquid default" (i.e. an S&P-500 universe is liquid by
    /// construction, so no filter is needed); D43's "liquidity bucket" is a COST concept that prices
    /// a fill, not a gate that removes a name. There is no `Eligibility.*` config block and no
    /// threshold to read, and inventing one ("never invent a key") would be a design decision
    /// smuggled in as an implementation detail.
    ///
    /// The only non-inventing substitute available would be a data-availability check — "has a
    /// computable 21-session ADV window with non-zero volume" — which every S&P 100 name passes and
    /// which duplicates what DataQualityGate already catches. So it would be a no-op wearing the
    /// costume of a filter, which is worse than nothing: a reader of Stage 1 would believe the pool
    /// had been screened for liquidity when it had not.
    ///
    /// READ THIS BEFORE THE SP500 WIDENING (PROGRESS proposal P1). Today the no-op is harmless —
    /// every S&P 100 name is liquid, so a liquidity gate would remove nobody anyway. After the D70
    /// widening to the S&P 500 the tail genuinely IS less liquid, and at that point this method will
    /// still filter nothing while §6 still says it does. That must be resolved — either by defining
    /// an Eligibility threshold (a new CONFIG key, needing a D-number) or by striking `liquid` from
    /// §6's Stage-1 description — BEFORE the widening, not after.
    /// </summary>
    public static EligibilityResult Resolve(
        IReadOnlyList<SecurityId> indexMembers,
        DateOnly asOf,
        IFeatureView features)
    {
        ArgumentNullException.ThrowIfNull(indexMembers);
        ArgumentNullException.ThrowIfNull(features);

        if (features.AsOf != asOf)
        {
            // A view built for another day cannot answer for this one; silently trusting it is how
            // a leak gets in (rule 4). Fail loudly — this is a wiring bug, not a data absence.
            throw new ArgumentException(
                $"Feature view is as-of {features.AsOf:yyyy-MM-dd} but Stage 1 was asked for {asOf:yyyy-MM-dd}.",
                nameof(features));
        }

        var priced = features.PricedOn(asOf).ToHashSet();

        var eligible = new List<SecurityId>();
        var excluded = new List<FunnelNote>();

        // Deterministic order (F-DET): the pool is ordered by security_id regardless of the order the
        // roster arrived in, so two runs of the same day produce byte-identical stage_json.
        foreach (var id in indexMembers.Distinct().OrderBy(x => x.Value))
        {
            if (priced.Contains(id)) eligible.Add(id);
            else excluded.Add(new FunnelNote(id, "not priced: no bar at asOf visible at the run's watermark."));
        }

        return new EligibilityResult(eligible, excluded);
    }
}
