using AlphaLab.Data;
using AlphaLab.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Evaluation.Populations;

/// <summary>
/// Bulk-writes control_equity for a day — one compact equity scalar per member (600–800 rows/day total,
/// §5.2). ONE <c>AddRange</c> + <c>SaveChanges</c> per day, never a per-member round-trip (the §5.2
/// compute requirement). Idempotent per (population_id, member_index, as_of, run_kind): a re-run of a
/// recovered day overwrites rather than duplicating (FR-7 / the equity_curve precedent). Writes via the
/// caller's transaction (D59 sole writer — the Worker's Stage-2 transaction commits it).
/// </summary>
public sealed class ControlEquityWriter(AlphaLabDbContext db)
{
    /// <summary>One member's equity point for a day.</summary>
    public readonly record struct Point(long PopulationId, int MemberIndex, decimal Equity);

    private const string RunKindLive = "live";

    public void Write(string asOf, IReadOnlyList<Point> points, string runKind = RunKindLive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);
        if (points.Count == 0) return;

        // A same-day re-run overwrites: pull any existing rows for this (as_of, run_kind) once and update
        // in place, insert the rest — still a single SaveChanges.
        var populationIds = points.Select(p => p.PopulationId).Distinct().ToHashSet();
        var existing = db.ControlEquity
            .Where(e => e.AsOf == asOf && e.RunKind == runKind && populationIds.Contains(e.PopulationId))
            .ToDictionary(e => (e.PopulationId, e.MemberIndex));

        var toAdd = new List<ControlEquityRow>(points.Count);
        foreach (var p in points)
        {
            if (existing.TryGetValue((p.PopulationId, p.MemberIndex), out var row))
            {
                row.Equity = p.Equity;
            }
            else
            {
                toAdd.Add(new ControlEquityRow
                {
                    PopulationId = p.PopulationId,
                    MemberIndex = p.MemberIndex,
                    AsOf = asOf,
                    Equity = p.Equity,
                    RunKind = runKind,
                });
            }
        }

        if (toAdd.Count > 0) db.ControlEquity.AddRange(toAdd);
        db.SaveChanges();
    }

    /// <summary>
    /// Every member's equity AS AT one specific prior session — the fast path for the prior-equity seed
    /// (finding 359).
    ///
    /// <see cref="LatestEquity"/> asks for every row older than the target day and groups it down to the
    /// newest per member, so its cost grows with the entire run behind it: by session 1,178 that was
    /// ~242k rows scanned per population per session, and the table gains ~650 rows every session — the
    /// dominant term in a replay day (42.5% of sampled stacks). A day's rows are written by ONE AddRange
    /// inside that day's write transaction, so they are all-or-nothing: if the prior session has any row
    /// it has all of them, and one seek answers the question that a full scan was being used for.
    ///
    /// An empty result must NOT be read as inception — that is the caller's job to handle by falling back
    /// to the full scan, so a genuine gap in history still carries equity forward instead of silently
    /// resetting members to starting cash (rule 10, fail closed).
    ///
    /// AsNoTracking: these rows are read to seed a computation and are never mutated — only TODAY's rows
    /// are updated, by <see cref="Write"/>, and those are a different key range (finding 357's reasoning).
    /// </summary>
    public IReadOnlyDictionary<int, decimal> EquityAt(long populationId, string priorSession, string runKind = RunKindLive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(priorSession);
        return db.ControlEquity
            .AsNoTracking()
            .Where(e => e.PopulationId == populationId && e.RunKind == runKind && e.AsOf == priorSession)
            .ToDictionary(e => e.MemberIndex, e => e.Equity);
    }

    /// <summary>The latest persisted equity per member for a population at or before <paramref name="asOf"/>
    /// (the prior-equity seed for the next day). Empty ⇒ inception (members start at <c>startEquity</c>).</summary>
    public IReadOnlyDictionary<int, decimal> LatestEquity(long populationId, string asOf, string runKind = RunKindLive)
    {
        return db.ControlEquity
            .Where(e => e.PopulationId == populationId && e.RunKind == runKind && string.Compare(e.AsOf, asOf) < 0)
            .GroupBy(e => e.MemberIndex)
            .Select(g => g.OrderByDescending(e => e.AsOf).First())
            .ToDictionary(e => e.MemberIndex, e => e.Equity);
    }
}
