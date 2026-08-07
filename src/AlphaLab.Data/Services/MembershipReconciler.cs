using System.Text.Json;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Providers;

namespace AlphaLab.Data.Services;

/// <summary>The outcome of one membership reconciliation. When <see cref="Applied"/> the diff was
/// written; when held, <see cref="HeldReason"/> explains why and nothing was mutated. Adds/Drops are
/// the applied security_ids (empty on a hold).</summary>
public sealed record MembershipReconcileResult(
    bool Applied,
    string? HeldReason,
    int PrimaryCount,
    int CrosscheckCount,
    IReadOnlyList<long> Adds,
    IReadOnlyList<long> Drops)
{
    public bool Held => !Applied;
}

/// <summary>
/// Reconciles a primary membership roster against a cross-check and, on agreement, applies the diff
/// to <c>index_membership</c> (FR-4 / D35/D49). Fail-closed by design: a count-sanity breach on
/// EITHER source (checked independently, before comparison) or ANY divergence holds yesterday's
/// state, writes a <c>index_membership_log</c> row with <c>agreed=0</c> + a note (the alert), and
/// mutates nothing. On agreement it stamps <c>added_on</c> / <c>removed_on</c> (never deletes; mirrors
/// <see cref="SecurityMaster.RecordTickerChange"/>) and logs the applied diff. A drop is a universe
/// exit only — it stamps <c>removed_on</c> and does NOT write a delist corporate action (decision #5;
/// index removal ≠ delisting — Stage-4 exits stay governed by ExitPolicy, hard rule 7).
///
/// **THE DIFF IS COMPUTED AGAINST THE FORWARD ROSTER, NOT AGAINST EVERY OPEN ROW (6.4).** Until then
/// it read <c>index_membership</c> blind — <c>WHERE removed_on IS NULL</c> over the whole table — and
/// since the Phase-4 historical seed co-mingled the full S&amp;P 500 as-of history into that same table,
/// that set is no longer the universe this reconcile is authoritative for. One blind read caused BOTH
/// live defects:
///
///  • **Mass eviction.** Every open name outside the S&amp;P 100 slice classified as a drop — 402 of them
///    in the live arena — so a forward refresh would stamp `removed_on = today` on the entire S&amp;P 500
///    roster. The count-sanity gate cannot catch it: it bounds the two SOURCES, never the store.
///  • **Invisible adds.** A genuine new S&amp;P 100 entrant is ALREADY open as an S&amp;P 500 member, so it
///    was not in `addIds`, never reached the slice snapshot, and was filtered out of the forward
///    universe permanently. That is the asymmetric "removals applied, adds vanished" leak
///    `MaintainSliceSnapshot` was written to close, reintroduced beneath it — and unlike the eviction
///    it is silent and is NOT repaired by re-seeding.
///
/// The forward roster is what <see cref="IIndexMembershipRead"/> resolves: raw as-of membership on a
/// fresh arena, and the slice-scoped intersection once a slice snapshot exists (rule 22). Taking it
/// from the same read the FUNNEL trades against is the point — a reconcile that judged a different set
/// from the one the pipeline uses is the bug in another costume.
/// </summary>
public interface IMembershipReconciler
{
    /// <param name="observedAt">The instant the roster was actually FETCHED (UTC ISO-8601), stamped on
    /// the audit row (6.4). NULL when the caller cannot vouch for one — honest, and what every row
    /// written before the column existed carries; never <paramref name="asOf"/>, which is the run date.</param>
    MembershipReconcileResult Reconcile(
        MembershipSnapshot primary, MembershipSnapshot crossCheck, string asOf, int[] countBand,
        string? observedAt = null);
}

/// <summary>EF-backed <see cref="IMembershipReconciler"/>. Uses <see cref="ISecurityMaster"/> to
/// resolve/register canonical symbols → security_ids, and <see cref="IIndexMembershipRead"/> to resolve
/// the FORWARD roster the diff is taken against; writes only <c>index_membership</c> +
/// <c>index_membership_log</c>.</summary>
public sealed class MembershipReconciler(
    AlphaLabDbContext db, ISecurityMaster securities, IIndexMembershipRead membershipRead) : IMembershipReconciler
{
    private const string Exchange = "US";

    public MembershipReconcileResult Reconcile(
        MembershipSnapshot primary, MembershipSnapshot crossCheck, string asOf, int[] countBand,
        string? observedAt = null)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(crossCheck);
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);
        if (countBand is not { Length: 2 })
        {
            throw new ArgumentException("countBand must be [min, max].", nameof(countBand));
        }
        var (min, max) = (countBand[0], countBand[1]);

        var primarySymbols = CanonicalSet(primary);
        var crossSymbols = CanonicalSet(crossCheck);
        var primaryCount = primarySymbols.Count;
        var crossCount = crossSymbols.Count;

        // Gate 1 — count sanity on BOTH sources independently, BEFORE comparison (C-4). Fires before
        // any DB touch, so no security is registered on a held run.
        if (primaryCount < min || primaryCount > max || crossCount < min || crossCount > max)
        {
            var reason = $"count sanity breach: primary={primaryCount}, crosscheck={crossCount}, band=[{min},{max}]";
            return Hold(asOf, primaryCount, crossCount, reason, observedAt, primary.Source);
        }

        // Gate 2 — agreement: the two rosters must name exactly the same members.
        var onlyPrimary = primarySymbols.Except(crossSymbols).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var onlyCross = crossSymbols.Except(primarySymbols).OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (onlyPrimary.Count > 0 || onlyCross.Count > 0)
        {
            var reason =
                $"divergence: only-in-primary=[{string.Join(",", onlyPrimary)}], " +
                $"only-in-crosscheck=[{string.Join(",", onlyCross)}]";
            return Hold(asOf, primaryCount, crossCount, reason, observedAt, primary.Source);
        }

        // Apply — agreement path only. Resolve primary symbols → security_ids (registers new adds).
        var primaryIds = new HashSet<long>();
        foreach (var symbol in primarySymbols)
        {
            primaryIds.Add(securities.ResolveOrRegister(symbol, Exchange, asOf));
        }

        // THE FORWARD ROSTER this reconcile is authoritative for — the same set the funnel trades, so a
        // name outside it (an S&P 500 member the historical seed co-mingled into this table) is neither
        // an add nor a drop here. Reading `WHERE removed_on IS NULL` instead is what produced the
        // 402-name mass eviction and the invisible-adds leak; see the interface doc.
        var currentIds = membershipRead.MembersAsOf(asOf).ToHashSet();

        var addIds = primaryIds.Where(id => !currentIds.Contains(id)).OrderBy(id => id).ToList();
        var dropIds = currentIds.Where(id => !primaryIds.Contains(id)).OrderBy(id => id).ToList();

        var openById = db.IndexMembership.Where(m => m.RemovedOn == null).ToList()
            .GroupBy(m => m.SecurityId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var id in addIds)
        {
            // An entrant that ALREADY has an open interval (it is in the S&P 500 history but was outside
            // the slice) needs no second row — it re-enters the forward universe through the slice
            // snapshot the caller advances. Writing another open row would deepen the double-open-row
            // state the co-mingling created rather than adding information.
            if (openById.ContainsKey(id)) continue;
            db.IndexMembership.Add(new IndexMembershipRow { SecurityId = id, AddedOn = asOf, RemovedOn = null });
        }
        foreach (var id in dropIds)
        {
            // Universe exit: close the CURRENT spell — the open row with the latest added_on. Never
            // delete; never a delist CA (decision #5). Ordering is the fix, not decoration: the previous
            // `.First()` over an unordered list happened to pick the forward row only because forward
            // rows carry lower rowids, so an index change would silently have closed a historical spell
            // and rewritten as-of membership for dates decades back.
            if (!openById.TryGetValue(id, out var open)) continue;
            open.OrderByDescending(m => m.AddedOn, StringComparer.Ordinal).First().RemovedOn = asOf;
        }

        db.IndexMembershipLog.Add(new IndexMembershipLogRow
        {
            AsOf = asOf,
            SourceCount = primaryCount,
            CrosscheckCount = crossCount,
            Agreed = 1,
            AddsJson = JsonSerializer.Serialize(addIds),
            DropsJson = JsonSerializer.Serialize(dropIds),
            Note = null,
            // Per-row provenance (6.4): WHEN the roster was fetched and WHICH primary produced it.
            // as_of is the session this reconcile ran for; observed_at is when the data arrived, and
            // telling those apart is the whole of finding 197.
            ObservedAt = observedAt,
            Source = primary.Source,
        });

        db.SaveChanges();
        return new MembershipReconcileResult(true, null, primaryCount, crossCount, addIds, dropIds);
    }

    private static HashSet<string> CanonicalSet(MembershipSnapshot snap) =>
        snap.Members.Select(m => m.CanonicalSymbol).ToHashSet(StringComparer.Ordinal);

    private MembershipReconcileResult Hold(
        string asOf, int primaryCount, int crossCount, string reason, string? observedAt, string source)
    {
        db.IndexMembershipLog.Add(new IndexMembershipLogRow
        {
            AsOf = asOf,
            SourceCount = primaryCount,
            CrosscheckCount = crossCount,
            Agreed = 0,
            AddsJson = null,
            DropsJson = null,
            Note = reason,
            ObservedAt = observedAt,
            Source = source,
        });
        db.SaveChanges();
        return new MembershipReconcileResult(false, reason, primaryCount, crossCount, [], []);
    }
}
