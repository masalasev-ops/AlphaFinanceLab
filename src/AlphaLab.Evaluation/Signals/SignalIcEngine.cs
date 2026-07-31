using AlphaLab.Core.Domain;
using AlphaLab.Core.Funnel;
using AlphaLab.Core.Signals;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Services;
using AlphaLab.Evaluation.Numerics;

namespace AlphaLab.Evaluation.Signals;

/// <summary>One graded (signal, day, horizon) triple, before persistence.</summary>
/// <param name="RankIc">Spearman rank correlation of scores at t against t→t+k returns.</param>
/// <param name="N">Names that contributed — the SCORABLE set (finding 294).</param>
public readonly record struct SignalGrade(string SignalId, string AsOf, int HorizonDays, double RankIc, int N);

/// <summary>
/// The FR-44 rank-IC engine (D91, MASTER §24.2). For signal S on day t it scores the eligible pool,
/// waits k trading days, and correlates the score ranking against the realized-return ranking.
///
/// THE POOL IS THE SCORABLE SET, and that is a consequence rather than a choice (finding 294): an
/// unpriced name yields no score and therefore cannot enter a ranking, so the priced filter is implied
/// by the ranking operation. The candidate set is Stage-1 eligibility as-of t — as-of membership ∩
/// priced-at-t, via the SAME <see cref="Eligibility"/> the funnel uses — and each scorer then narrows
/// it further by what it can actually score (thin history ⇒ omitted). <c>n</c> records the result.
///
/// MEMBERSHIP RESOLVES THROUGH THE EXCLUSION-SCOPED READ THE REPLAY USED [D97], never the forward
/// slice-scoped one: the library grades market history, so scoping it to the sp100 launch slice would
/// grade a different universe than the one it claims to describe. The caller supplies the reader, which
/// is what keeps that decision visible at the composition root rather than buried here.
///
/// NOT A REPLAY GENERATION [D95]: this writes no `runs` row, creates no generation, and `signal_ic`
/// carries no `run_kind` — there is exactly one market history to grade.
///
/// DETERMINISTIC: scores and returns are read through the watermark-bounded feature view, so grading
/// one day twice at one watermark is byte-identical (<c>FX-SignalIcDeterminism</c>). No cross-section
/// is persisted; a grade is a re-derivable fact, not state.
/// </summary>
public sealed class SignalIcEngine(
    AlphaLabDbContext db,
    IIndexMembershipRead membership,
    Func<DateOnly, IFeatureView> featureViewAt)
{
    /// <summary>
    /// Grade every signal in <paramref name="signals"/> on day <paramref name="asOf"/> at each horizon,
    /// using <paramref name="sessions"/> (ascending, the trading calendar) to find t+k.
    ///
    /// A horizon whose t+k lies beyond the available calendar yields NO grade: the realized return does
    /// not exist yet, and inventing one from a shorter window would silently grade a different horizon
    /// than the row claims.
    /// </summary>
    public IReadOnlyList<SignalGrade> GradeDay(
        DateOnly asOf,
        IReadOnlyList<ISignal> signals,
        IReadOnlyList<int> horizons,
        IReadOnlyList<DateOnly> sessions,
        SecurityId? marketProxy)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(horizons);
        ArgumentNullException.ThrowIfNull(sessions);

        var index = -1;
        for (var i = 0; i < sessions.Count; i++)
        {
            if (sessions[i] == asOf) { index = i; break; }
        }
        if (index < 0) return [];

        var features = featureViewAt(asOf);
        var roster = membership.MembersAsOf(Iso(asOf)).Select(id => new SecurityId(id)).ToList();

        // Stage-1 eligibility: the same pure resolver the funnel uses, so the library grades the pool
        // the arena would actually have been able to act on (§24.5 "compare like for like").
        var pool = Eligibility.Resolve(roster, asOf, features).Eligible;
        if (pool.Count == 0) return [];

        var context = new SignalContext(marketProxy);
        var grades = new List<SignalGrade>();

        foreach (var signal in signals)
        {
            var scores = signal.Score(pool, features, context);
            if (scores.Count < 2) continue;   // a ranking needs at least two names

            foreach (var k in horizons)
            {
                var targetIndex = index + k;
                if (targetIndex >= sessions.Count) continue;   // t+k not resolved yet — no grade
                var target = sessions[targetIndex];

                // Returns are read at the TARGET day's view, so a name delisted between t and t+k simply
                // has no forward price and drops out — absence again, never a fabricated zero.
                var forward = featureViewAt(target);
                var scored = new List<double>();
                var realized = new List<double>();
                foreach (var (id, score) in scores.OrderBy(kv => kv.Key.Value))
                {
                    var p0 = features.AdjClose(id, asOf);
                    var p1 = forward.AdjClose(id, target);
                    if (p0 is not { } a || p1 is not { } b || a <= 0) continue;
                    scored.Add(score);
                    realized.Add(b / a - 1.0);
                }

                if (Statistics.SpearmanRankCorrelation(scored, realized) is not { } ic) continue;
                grades.Add(new SignalGrade(signal.SignalId, Iso(asOf), k, ic, scored.Count));
            }
        }
        return grades;
    }

    /// <summary>
    /// The (signal, horizon) pairs ALREADY graded for <paramref name="asOf"/> — the cheap coverage read
    /// a caller uses to skip a day BEFORE paying to score it.
    ///
    /// This exists because "resumable" was only nominally true without it (finding 300): grading a day
    /// and then discarding the rows at persist time produces the right table and the wrong cost, so a
    /// crashed multi-hour run resumed at the same price as starting over. The `HistoricalBackfill`
    /// precedent this was modelled on checks coverage BEFORE fetching, not after; this restores that
    /// ordering.
    /// </summary>
    public HashSet<(string SignalId, int Horizon)> GradedOn(string asOf) =>
        db.SignalIc.Where(r => r.AsOf == asOf)
            .Select(r => new { r.SignalId, r.HorizonDays })
            .AsEnumerable()
            .Select(r => (r.SignalId, r.HorizonDays))
            .ToHashSet();

    /// <summary>
    /// Persist grades, skipping any (signal, day, horizon) already present. Skipping rather than
    /// upserting is the idempotency contract (<c>FX-SignalBackfillIdempotent</c>). It remains the
    /// last-line guard against a duplicate even when the caller has already skipped the day via
    /// <see cref="GradedOn"/> — the cheap pre-check is an optimisation, this is the correctness rule,
    /// and collapsing the two would leave the invariant resting on the caller remembering to ask.
    /// </summary>
    public int Persist(IReadOnlyList<SignalGrade> grades)
    {
        ArgumentNullException.ThrowIfNull(grades);
        if (grades.Count == 0) return 0;

        var written = 0;
        foreach (var g in grades)
        {
            var exists = db.SignalIc.Any(r =>
                r.SignalId == g.SignalId && r.AsOf == g.AsOf && r.HorizonDays == g.HorizonDays);
            if (exists) continue;
            db.SignalIc.Add(new SignalIcRow
            {
                SignalId = g.SignalId, AsOf = g.AsOf, HorizonDays = g.HorizonDays, RankIc = g.RankIc, N = g.N,
            });
            written++;
        }
        if (written > 0) db.SaveChanges();
        return written;
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
