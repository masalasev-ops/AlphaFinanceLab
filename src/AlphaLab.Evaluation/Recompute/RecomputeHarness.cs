using AlphaLab.Core.Config;
using AlphaLab.Data;
using AlphaLab.Evaluation.Gate;

namespace AlphaLab.Evaluation.Recompute;

/// <summary>One artefact's agreement with what generation 1 actually recorded. <c>Differing = 0</c> on all
/// three is <c>FX-RecomputeParity</c>'s pass condition — an EXACT equality, never a tolerance (§25.3).</summary>
public sealed record ArtefactDiff(string Artefact, int Stored, int Recomputed, int Differing, IReadOnlyList<string> Examples);

/// <summary>
/// How the promotion set CHANGED, not merely how much. A bare count cannot distinguish the three cases,
/// and they mean opposite things: <c>Moved</c> is the same edge found at a different time, <c>Gained</c> is
/// an edge the old rule never found at all, and <c>Lost</c> is an edge the new rule STOPS finding — the one
/// direction that would argue against a change. Added v1.9.73 after the finding-285 run reported "65
/// differing" with a ten-line example cap, from which the lost count could not be read.
/// </summary>
public sealed record PromotionBreakdown(int Moved, int Gained, int Lost, IReadOnlyList<string> LostSubjects);

/// <summary>The harness's whole answer. Report-only by construction (D117 clause 1): nothing here is
/// written to the store.</summary>
public sealed record RecomputeReportModel(
    string RunKind,
    string SpecDescription,
    RecomputeTier Tier,
    IReadOnlyList<string> ExcludedTruncationLimited,
    int SubjectsRecomputed,
    ArtefactDiff Statuses,
    ArtefactDiff Promotions,
    ArtefactDiff WouldReverts,
    PromotionBreakdown PromotionShape,
    DetectionPowerComparison? DetectionPower,
    CohortSeparationResult? Separation)
{
    /// <summary>§25.3: parity holds only when all three artefacts match exactly.</summary>
    public bool ParityHolds => Statuses.Differing == 0 && Promotions.Differing == 0 && WouldReverts.Differing == 0;
}

/// <summary>
/// The D106 recompute harness (MASTER §25; settlements in D117). Scores a monitor-rule or gate-rule change
/// by re-deriving verdicts from stored rows instead of re-simulating — minutes instead of the multi-day
/// replay a rule change costs today.
///
/// **Report-only (D117 clause 1).** It opens the store read-only in spirit and in fact: no <c>Add</c>, no
/// <c>SaveChanges</c>, anywhere on this path. Recomputed rows never touch generation 1's, and no third
/// <c>run_kind</c> is introduced for every quarantine filter to be re-audited against (rule 1).
///
/// **The retire-exempt guard (D117 clause 3, finding 338).** A subject that RETIRED in the generation left
/// the promotable set and stopped emitting rows, so the sessions after its retirement were never recorded
/// and cannot be recomputed in the direction where it would NOT have retired. Plants are retire-exempt
/// under replay (D100), which is precisely what makes the cohorts the curves are built from recomputable
/// in both directions. Any subject carrying a retire event is EXCLUDED and NAMED — never silently dropped,
/// which would be the same defect in a quieter place.
///
/// **If parity fails, the harness is not used and generation 2 stands (§25.3).** The equality is never
/// relaxed to a tolerance; a failure routes to investigating which input is impure. It is a finding about
/// the store, not a fixture to soften.
/// </summary>
public sealed class RecomputeHarness(AlphaLabDbContext db, GateOptions gate, string runKind = "replay")
{
    private const int MaxExamples = 40;

    public RecomputeReportModel Run(RecomputeSpec spec, string benchmarkStrategyId = EvaluationStep.DefaultBenchmarkStrategyId)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var tier = spec.Tier;   // throws RecomputeRefusedException on an unclassifiable parameter

        var (subjects, excluded) = ResolveSubjects();

        var statuses = new MonitorRecompute(db, spec, runKind).Run(subjects);
        var verdicts = new GateRecompute(db, spec, gate, runKind).Run(subjects, benchmarkStrategyId);

        // A strategy is promoted at most ONCE: the gate writes the row only while its effective status is
        // still 'candidate', and a promotion flips it to 'live' (EvaluationStep.cs:111). So the recomputed
        // promotion is the FIRST session whose verdict is Promoted. Built once and used twice — the diff
        // and the detection-power curve must not disagree about what was promoted.
        var recomputedPromotions = verdicts
            .Where(v => v.Verdict == PromotionVerdict.Promoted)
            .GroupBy(v => v.StrategyId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(v => v.AsOf, StringComparer.Ordinal).First().AsOf, StringComparer.Ordinal);

        var (promotions, shape) = DiffPromotions(recomputedPromotions, subjects);

        return new RecomputeReportModel(
            runKind, spec.Describe(), tier, excluded, subjects.Count,
            DiffStatuses(statuses, subjects),
            promotions,
            DiffWouldReverts(statuses, subjects),
            shape,
            new DetectionPowerRecompute(db, gate, runKind).Build(recomputedPromotions),
            new CohortSeparation(db, runKind).Build(statuses));
    }

    /// <summary>Everything the generation monitored or evaluated, minus the truncation-limited subjects.</summary>
    private (IReadOnlyCollection<string> Subjects, IReadOnlyList<string> Excluded) ResolveSubjects()
    {
        var monitored = db.OverfittingChecks.Where(c => c.RunKind == runKind)
            .Select(c => c.StrategyId).Distinct().ToList();
        var evaluated = db.PowerReports.Where(p => p.RunKind == runKind)
            .Select(p => p.StrategyA).Distinct().ToList();

        // A retire event anywhere in the generation ⇒ the subject's later sessions were never recorded.
        var retired = db.OverfittingStatus
            .Where(o => o.RunKind == runKind && o.Status == "retired")
            .Select(o => o.StrategyId).Distinct()
            .ToHashSet(StringComparer.Ordinal);

        var all = monitored.Concat(evaluated).ToHashSet(StringComparer.Ordinal);
        var excluded = all.Where(retired.Contains).OrderBy(s => s, StringComparer.Ordinal).ToList();
        all.ExceptWith(retired);
        return (all, excluded);
    }

    // ---- the three §25.3 artefacts -----------------------------------------------------------------------

    private ArtefactDiff DiffStatuses(IReadOnlyList<RecomputedStatus> recomputed, IReadOnlyCollection<string> subjects)
    {
        var stored = db.OverfittingStatus
            .Where(o => o.RunKind == runKind)
            .Select(o => new { o.StrategyId, o.AsOf, o.Status })
            .AsEnumerable()
            .Where(o => subjects.Contains(o.StrategyId))
            .ToDictionary(o => (o.StrategyId, o.AsOf), o => o.Status);

        // The COUNT is authoritative and is never capped; only the EXAMPLES are, so a large diff still
        // reports its true size rather than "10".
        var count = 0;
        var differing = new List<string>();
        foreach (var r in recomputed)
        {
            if (!stored.TryGetValue((r.StrategyId, r.AsOf), out var was))
            {
                count++;
                Note(differing, $"{r.StrategyId}@{r.AsOf}: recomputed '{r.Status}', no stored row");
            }
            else if (!string.Equals(was, r.Status, StringComparison.Ordinal))
            {
                count++;
                Note(differing, $"{r.StrategyId}@{r.AsOf}: stored '{was}' → recomputed '{r.Status}'");
            }
        }
        var missing = stored.Count - recomputed.Count(r => stored.ContainsKey((r.StrategyId, r.AsOf)));
        if (missing > 0)
        {
            count += missing;
            Note(differing, $"{missing} stored status row(s) had no recomputed counterpart");
        }

        return new ArtefactDiff("overfitting_status", stored.Count, recomputed.Count, count, differing);
    }

    /// <summary>Diffs the promotion set AND classifies how it changed (v1.9.73): moved / gained / LOST.
    /// The three mean opposite things and a bare count hides the one that matters — an edge the new rule
    /// stops finding. Every LOST subject is listed in full, never sampled: it is the direction that would
    /// argue against a rule change, so it is the last thing an example cap may elide.</summary>
    private (ArtefactDiff Diff, PromotionBreakdown Shape) DiffPromotions(
        IReadOnlyDictionary<string, string> recomputed, IReadOnlyCollection<string> subjects)
    {
        var stored = db.GoLiveLog
            .Where(g => g.RunKind == runKind && g.Verdict == "Promoted" && g.Promoted != null)
            .Select(g => new { Strategy = g.Promoted!, g.AsOf })
            .AsEnumerable()
            .Where(g => subjects.Contains(g.Strategy))
            .ToDictionary(g => g.Strategy, g => g.AsOf, StringComparer.Ordinal);

        int moved = 0, gained = 0;
        var lost = new List<string>();
        var differing = new List<string>();
        foreach (var (strategy, asOf) in recomputed.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!stored.TryGetValue(strategy, out var was))
            { gained++; Note(differing, $"{strategy}: GAINED — recomputed promotion @{asOf}, none stored"); }
            else if (was != asOf)
            {
                moved++;
                var direction = string.CompareOrdinal(asOf, was) < 0 ? "EARLIER" : "LATER";
                Note(differing, $"{strategy}: MOVED {direction} — stored @{was} → recomputed @{asOf}");
            }
        }
        foreach (var (strategy, was) in stored.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!recomputed.ContainsKey(strategy))
            {
                lost.Add($"{strategy} (stored @{was})");
                differing.Add($"{strategy}: LOST — stored promotion @{was}, none recomputed");
            }
        }

        var diff = new ArtefactDiff(
            "go_live_log(Promoted)", stored.Count, recomputed.Count, moved + gained + lost.Count, differing);
        return (diff, new PromotionBreakdown(moved, gained, lost.Count, lost));
    }

    private ArtefactDiff DiffWouldReverts(IReadOnlyList<RecomputedStatus> recomputed, IReadOnlyCollection<string> subjects)
    {
        var stored = db.GoLiveLog
            .Where(g => g.RunKind == runKind && g.Verdict == "WouldRevert" && g.Demoted != null)
            .Select(g => new { Strategy = g.Demoted!, g.AsOf })
            .AsEnumerable()
            .Where(g => subjects.Contains(g.Strategy))
            .Select(g => (g.Strategy, g.AsOf))
            .ToHashSet();

        var events = recomputed.Where(r => r.WouldRevert).Select(r => (r.StrategyId, r.AsOf)).ToHashSet();

        var differing = new List<string>();
        foreach (var e in events.Except(stored).OrderBy(e => e.Item1, StringComparer.Ordinal).Take(MaxExamples))
            Note(differing, $"{e.Item1}@{e.Item2}: recomputed would-revert, none stored");
        foreach (var e in stored.Except(events).OrderBy(e => e.Item1, StringComparer.Ordinal).Take(MaxExamples))
            Note(differing, $"{e.Item1}@{e.Item2}: stored would-revert, none recomputed");

        var count = events.Except(stored).Count() + stored.Except(events).Count();
        return new ArtefactDiff("go_live_log(WouldRevert)", stored.Count, events.Count, count, differing);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static void Note(List<string> examples, string message)
    {
        if (examples.Count < MaxExamples) examples.Add(message);
        else if (examples.Count == MaxExamples) examples.Add("… (further differences elided; the COUNT is authoritative)");
    }

}
