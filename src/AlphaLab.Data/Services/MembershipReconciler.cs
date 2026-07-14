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
/// </summary>
public interface IMembershipReconciler
{
    MembershipReconcileResult Reconcile(
        MembershipSnapshot primary, MembershipSnapshot crossCheck, string asOf, int[] countBand);
}

/// <summary>EF-backed <see cref="IMembershipReconciler"/>. Uses <see cref="ISecurityMaster"/> to
/// resolve/register canonical symbols → security_ids; writes only <c>index_membership</c> +
/// <c>index_membership_log</c>.</summary>
public sealed class MembershipReconciler(AlphaLabDbContext db, ISecurityMaster securities) : IMembershipReconciler
{
    private const string Exchange = "US";

    public MembershipReconcileResult Reconcile(
        MembershipSnapshot primary, MembershipSnapshot crossCheck, string asOf, int[] countBand)
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
            return Hold(asOf, primaryCount, crossCount, reason);
        }

        // Gate 2 — agreement: the two rosters must name exactly the same members.
        var onlyPrimary = primarySymbols.Except(crossSymbols).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var onlyCross = crossSymbols.Except(primarySymbols).OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (onlyPrimary.Count > 0 || onlyCross.Count > 0)
        {
            var reason =
                $"divergence: only-in-primary=[{string.Join(",", onlyPrimary)}], " +
                $"only-in-crosscheck=[{string.Join(",", onlyCross)}]";
            return Hold(asOf, primaryCount, crossCount, reason);
        }

        // Apply — agreement path only. Resolve primary symbols → security_ids (registers new adds).
        var primaryIds = new HashSet<long>();
        foreach (var symbol in primarySymbols)
        {
            primaryIds.Add(securities.ResolveOrRegister(symbol, Exchange, asOf));
        }

        var currentOpen = db.IndexMembership.Where(m => m.RemovedOn == null).ToList();
        var currentIds = currentOpen.Select(m => m.SecurityId).ToHashSet();

        var addIds = primaryIds.Where(id => !currentIds.Contains(id)).OrderBy(id => id).ToList();
        var dropIds = currentIds.Where(id => !primaryIds.Contains(id)).OrderBy(id => id).ToList();

        foreach (var id in addIds)
        {
            db.IndexMembership.Add(new IndexMembershipRow { SecurityId = id, AddedOn = asOf, RemovedOn = null });
        }
        foreach (var id in dropIds)
        {
            // Universe exit: stamp removed_on on the open row. Never delete; never a delist CA (decision #5).
            currentOpen.First(m => m.SecurityId == id).RemovedOn = asOf;
        }

        db.IndexMembershipLog.Add(new IndexMembershipLogRow
        {
            AsOf = asOf,
            SourceCount = primaryCount,
            CrosscheckCount = crossCount,
            Agreed = 1,
            AddsJson = JsonSerializer.Serialize(addIds),
            DropsJson = JsonSerializer.Serialize(dropIds),
            Note = null
        });

        db.SaveChanges();
        return new MembershipReconcileResult(true, null, primaryCount, crossCount, addIds, dropIds);
    }

    private static HashSet<string> CanonicalSet(MembershipSnapshot snap) =>
        snap.Members.Select(m => m.CanonicalSymbol).ToHashSet(StringComparer.Ordinal);

    private MembershipReconcileResult Hold(string asOf, int primaryCount, int crossCount, string reason)
    {
        db.IndexMembershipLog.Add(new IndexMembershipLogRow
        {
            AsOf = asOf,
            SourceCount = primaryCount,
            CrosscheckCount = crossCount,
            Agreed = 0,
            AddsJson = null,
            DropsJson = null,
            Note = reason
        });
        db.SaveChanges();
        return new MembershipReconcileResult(false, reason, primaryCount, crossCount, [], []);
    }
}
