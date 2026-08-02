using AlphaLab.Data;

namespace AlphaLab.Evaluation.Recompute;

/// <summary>One plant cohort's ever-Suspect rate WITHIN a horizon, stored beside recomputed. The D63 rule
/// is asymmetric: <c>anti</c> SHOULD be caught, <c>noedge</c> should NOT — "S3 never flags a merely
/// edgeless strategy" (OVERFITTING_MONITOR §3).</summary>
public sealed record CohortFlagRate(string Kind, int Cohort, int StoredEverSuspect, int RecomputedEverSuspect);

/// <summary>
/// A cohort's median sessions-to-FIRST-Suspect, stored beside recomputed. **The metric that does not
/// saturate** (finding 346): D63's asymmetry is not that anti plants are eventually caught and edgeless ones
/// never are — over a long enough window both are. It is that anti should be caught FAST and edgeless slowly.
/// A plant never flagged contributes no median and is counted in <c>NeverFlagged</c>, which is the other half
/// of the picture.
/// </summary>
public sealed record CohortSpeed(
    string Kind, int Cohort, int? StoredMedianSessions, int? RecomputedMedianSessions,
    int StoredNeverFlagged, int RecomputedNeverFlagged);

/// <summary>The finding-280 measurement AT ONE HORIZON. <paramref name="Sessions"/> is null for the full
/// window.</summary>
public sealed record SeparationAtHorizon(
    string Label, int? Sessions,
    IReadOnlyList<CohortFlagRate> Cohorts,
    double? StoredSeparation,
    double? RecomputedSeparation)
{
    /// <summary>
    /// A horizon is saturated when BOTH judged cohorts sit within ONE PLANT of the ceiling. The bound is
    /// `1/n` — the measurement's own resolution, not an authored threshold: with 50 seeds a cohort cannot
    /// move by less than 0.02, so "49/50 vs 50/50" is a tie reported at the limit of what the instrument
    /// can see, and treating its −0.02 as a signal is reading the noise floor as data (finding 344).
    /// </summary>
    public bool Saturated =>
        Judged.Count == 2 && Judged.All(c => c.Cohort > 0 && c.StoredEverSuspect >= c.Cohort - 1);

    /// <summary>The two cohorts D63's asymmetry is about; the others are context.</summary>
    public IReadOnlyList<CohortFlagRate> Judged =>
        [.. Cohorts.Where(c => c.Kind is "anti" or "noedge")];

    /// <summary>The instrument's resolution here: one plant, as a rate. A separation change at or below
    /// this is one plant moving and is not a result.</summary>
    public double? Resolution =>
        Judged.Count == 2 && Judged.All(c => c.Cohort > 0) ? 1.0 / Judged.Min(c => c.Cohort) : null;
}

/// <summary>
/// The finding-280 measurement across horizons. **Reported at several, never one**, because the
/// ever-Suspect predicate SATURATES: over a 20-year window every cohort reaches it, so the full-window
/// form returns 50/50 for everything and discriminates nothing. Finding 280's own 50/50 was measured
/// "live at session 639" — about 2.5 years — not over the whole generation.
/// </summary>
public sealed record CohortSeparationResult(
    IReadOnlyList<SeparationAtHorizon> Horizons,
    IReadOnlyList<CohortSpeed> Speeds)
{
    /// <summary>anti's median minus noedge's, in sessions — NEGATIVE is the D63 direction (anti caught
    /// sooner than edgeless). Null when either cohort has no median. Unlike the ever-rates this does not
    /// saturate, which is why finding 346 exists.</summary>
    public (int? Stored, int? Recomputed) SpeedGap
    {
        get
        {
            var anti = Speeds.FirstOrDefault(s => s.Kind == "anti");
            var noEdge = Speeds.FirstOrDefault(s => s.Kind == "noedge");
            int? Gap(int? a, int? n) => a is { } av && n is { } nv ? av - nv : null;
            return anti is null || noEdge is null
                ? (null, null)
                : (Gap(anti.StoredMedianSessions, noEdge.StoredMedianSessions),
                   Gap(anti.RecomputedMedianSessions, noEdge.RecomputedMedianSessions));
        }
    }

    /// <summary>
    /// The horizon a verdict may be read from: the shortest one that is NOT saturated. Null when every
    /// horizon saturates — which is itself the finding, and must be reported as an inability to measure
    /// rather than as a result.
    ///
    /// **Corrected at finding 344.** The first version picked the shortest horizon with a non-zero stored
    /// separation, and on the live store that selected a horizon reading `anti` 49/50 vs `noedge` 50/50 —
    /// a −0.02 produced by two cohorts BOTH at the ceiling, one plant apart. It then called a move to 0.00
    /// an improvement. A non-zero separation is not evidence of discrimination when both cohorts are
    /// saturated; the sign of a one-plant difference is noise wearing a number's clothes.
    /// </summary>
    public SeparationAtHorizon? Discriminating => Horizons.FirstOrDefault(h => !h.Saturated);
}

/// <summary>
/// **The finding-280 instrument** (v1.9.73). Finding 280 is not "the monitor flags too much" — it is that
/// the monitor flags the two cohorts at *identical* rates: *"50/50 no-edge plants ever Suspect and 50/50
/// anti-predictive plants ever Suspect"*. One of those is the design intent; the other is what
/// OVERFITTING_MONITOR §3 forbids. So a candidate fix can only be judged on the **differential**, and a
/// rule change that suppresses both equally has fixed nothing while looking like progress.
///
/// **Why the artefact counts could not answer this.** A run reporting "2,946 statuses differ" is silent on
/// WHICH cohort moved, and the report's examples are ordered by strategy id — so <c>plant:anti:*</c> sorts
/// first and fills the sample regardless of what happened to <c>plant:noedge:*</c>. The first
/// `monitor.s6.sustain_evals` run showed exactly that: forty anti-plant lines and no way to tell whether
/// the false-alarm rate had moved at all. The count was authoritative and the question was still open.
///
/// **Separation** = (anti ever-Suspect rate) − (noedge ever-Suspect rate). Zero is finding 280's defect;
/// a rule change is progress only if it RAISES this, and suppressing anti-detection lowers it.
/// </summary>
public sealed class CohortSeparation(AlphaLabDbContext db, string runKind = "replay")
{
    private const string Suspect = "suspect";

    /// <summary>The cohorts the D63 conformance question is about, in the order a reader should see them:
    /// the one that must be caught, then the one that must not.</summary>
    private static readonly string[] Kinds = ["anti", "noedge", "edge", "naive"];

    public CohortSeparationResult Build(IReadOnlyList<RecomputedStatus> recomputed)
    {
        ArgumentNullException.ThrowIfNull(recomputed);

        var sessions = db.Runs.Where(r => r.RunKind == runKind && r.Status == "ok")
            .OrderBy(r => r.AsOf).Select(r => r.AsOf).ToList();

        var storedSuspect = db.OverfittingStatus
            .Where(o => o.RunKind == runKind && o.Status == Suspect)
            .Select(o => new { o.StrategyId, o.AsOf })
            .AsEnumerable()
            .ToList();
        var recomputedSuspect = recomputed.Where(r => r.Status == Suspect).ToList();

        // The cohort denominators come from the SUBJECTS the harness actually recomputed, not from the
        // strategies table: a plant that was never simulated (finding 341's pre-Change-4 residue) cannot
        // be flagged and would silently deflate the rate it is counted into.
        var subjects = recomputed.Select(r => r.StrategyId).Distinct().ToList();

        // 1y and 3y bracket finding 280's own measurement point (session 639, ~2.5y); the full window is
        // carried too so the SATURATION is visible rather than being the only thing reported.
        var horizons = new List<(string Label, int? Sessions)>
        {
            ("1 year (252 sessions)", 252),
            ("3 years (756 sessions)", 756),
            ("full window", null),
        };

        var result = new List<SeparationAtHorizon>();
        foreach (var (label, limit) in horizons)
        {
            var cutoff = limit is { } n && n - 1 < sessions.Count ? sessions[n - 1] : null;
            bool Within(string asOf) => cutoff is null || string.CompareOrdinal(asOf, cutoff) <= 0;

            var storedIn = storedSuspect.Where(x => Within(x.AsOf)).Select(x => x.StrategyId).ToHashSet(StringComparer.Ordinal);
            var recomputedIn = recomputedSuspect.Where(x => Within(x.AsOf)).Select(x => x.StrategyId).ToHashSet(StringComparer.Ordinal);

            var rows = new List<CohortFlagRate>();
            foreach (var kind in Kinds)
            {
                var prefix = $"plant:{kind}:";
                var cohort = subjects.Where(s => s.StartsWith(prefix, StringComparison.Ordinal)).ToList();
                if (cohort.Count == 0) continue;
                rows.Add(new CohortFlagRate(
                    kind, cohort.Count, cohort.Count(storedIn.Contains), cohort.Count(recomputedIn.Contains)));
            }
            result.Add(new SeparationAtHorizon(
                label, limit, rows,
                Separation(rows, r => r.StoredEverSuspect), Separation(rows, r => r.RecomputedEverSuspect)));
        }
        return new CohortSeparationResult(result, Speeds(subjects, storedSuspect, recomputedSuspect, sessions));
    }

    /// <summary>Median sessions-to-first-Suspect per cohort (finding 346). The ever-rates saturate; this
    /// does not, because a cohort caught on day 30 and one caught on day 900 are distinguishable however
    /// long the window runs.</summary>
    private List<CohortSpeed> Speeds(
        List<string> subjects,
        IEnumerable<dynamic> storedSuspect,
        IReadOnlyList<RecomputedStatus> recomputedSuspect,
        List<string> sessions)
    {
        var index = new Dictionary<string, int>(sessions.Count, StringComparer.Ordinal);
        for (var i = 0; i < sessions.Count; i++) index[sessions[i]] = i + 1;

        var storedFirst = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var x in storedSuspect)
        {
            string id = x.StrategyId; string asOf = x.AsOf;
            if (!index.TryGetValue(asOf, out var i)) continue;
            if (!storedFirst.TryGetValue(id, out var cur) || i < cur) storedFirst[id] = i;
        }
        var recomputedFirst = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in recomputedSuspect)
        {
            if (!index.TryGetValue(r.AsOf, out var i)) continue;
            if (!recomputedFirst.TryGetValue(r.StrategyId, out var cur) || i < cur) recomputedFirst[r.StrategyId] = i;
        }

        static int? Median(List<int> xs)
        {
            if (xs.Count == 0) return null;
            var sorted = xs.Order().ToList();
            return sorted[sorted.Count / 2];
        }

        var rows = new List<CohortSpeed>();
        foreach (var kind in Kinds)
        {
            var prefix = $"plant:{kind}:";
            var cohort = subjects.Where(s => s.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            if (cohort.Count == 0) continue;
            var storedIdx = cohort.Where(storedFirst.ContainsKey).Select(c => storedFirst[c]).ToList();
            var recomputedIdx = cohort.Where(recomputedFirst.ContainsKey).Select(c => recomputedFirst[c]).ToList();
            rows.Add(new CohortSpeed(
                kind, cohort.Count, Median(storedIdx), Median(recomputedIdx),
                cohort.Count - storedIdx.Count, cohort.Count - recomputedIdx.Count));
        }
        return rows;
    }

    /// <summary>anti-rate − noedge-rate. Null when either cohort is absent — a separation computed against
    /// a missing cohort would be a number with no meaning, which is worse than no number.</summary>
    private static double? Separation(List<CohortFlagRate> rows, Func<CohortFlagRate, int> flagged)
    {
        var anti = rows.FirstOrDefault(r => r.Kind == "anti");
        var noEdge = rows.FirstOrDefault(r => r.Kind == "noedge");
        if (anti is null || noEdge is null || anti.Cohort == 0 || noEdge.Cohort == 0) return null;
        return flagged(anti) / (double)anti.Cohort - flagged(noEdge) / (double)noEdge.Cohort;
    }
}
