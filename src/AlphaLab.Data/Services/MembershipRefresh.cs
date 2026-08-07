using AlphaLab.Data.Providers;

namespace AlphaLab.Data.Services;

/// <summary>What one membership refresh did. <see cref="FetchError"/> non-null ⇒ nothing was fetched and
/// nothing was written; the stored roster stands and the freshness reading goes stale, which is the
/// honest outcome rather than a failure of the trading day.</summary>
public sealed record MembershipRefreshOutcome(
    MembershipReconcileResult? Reconcile, string? ObservedAt, string? Source, string? FetchError)
{
    public bool Fetched => FetchError is null;
    public bool Applied => Reconcile is { Applied: true };
}

/// <summary>
/// The forward membership refresh (FR-4/FR-6 · finding 197), as ONE definition with TWO callers — the
/// Phase-1 bootstrap CLI and the Worker's launch step. 6.2's registry precedent applies verbatim: two
/// copies of "how the roster is refreshed" would let the bootstrap and forward operation diverge on the
/// count band, the slice maintenance or the sector application, and no test would name the divergence.
///
/// **FETCH AND APPLY ARE SEPARATE ON PURPOSE (rule 12 / D53).** <see cref="FetchAsync"/> performs zero
/// DB writes; <see cref="Apply"/> does every write and is meant to run inside the caller's transaction.
/// That is the shape `Stage1Fetch` → `PersistQualityFlags` already uses, and it is what lets the Worker
/// keep "fetch does no writes" true while still refusing to leave the roster frozen.
///
/// **THE PROVIDERS ANSWER FOR NOW, NEVER FOR A PAST DATE.** INTEGRATIONS §2/§2b drop `asOfDate` from the
/// holdings request deliberately ("the same freeze trap") — the returned roster is CURRENT, and `asOf`
/// is only the label the raw payload is archived under. So a refresh may be applied ONLY under a date
/// that is genuinely current: stamping today's roster with a recovered session's date would fabricate
/// as-of membership. The caller owns that choice; this type states the constraint so the choice cannot
/// be made in ignorance of it.
/// </summary>
public sealed class MembershipRefresh(
    AlphaLabDbContext db,
    IIndexMembershipProvider primary,
    IIndexMembershipProvider crossCheck)
{
    /// <summary>Fetch both rosters. ZERO DB writes (rule 12). <paramref name="onFetched"/> is the
    /// caller's API-usage counter, invoked per source the moment that fetch succeeds — not after both —
    /// so a cross-check failure still records the primary's spent call.</summary>
    public async Task<(MembershipSnapshot Primary, MembershipSnapshot Cross)> FetchAsync(
        string asOf, Action<string>? onFetched = null, CancellationToken ct = default)
    {
        var p = await primary.GetMembersAsync(asOf, ct).ConfigureAwait(false);
        onFetched?.Invoke(p.Source);
        var c = await crossCheck.GetMembersAsync(asOf, ct).ConfigureAwait(false);
        onFetched?.Invoke(c.Source);
        return (p, c);
    }

    /// <summary>
    /// Reconcile + apply: the diff, the sectors, and the slice snapshot. WRITES — call inside the
    /// caller's transaction. <paramref name="observedAt"/> is the true fetch instant, stamped on the
    /// audit row so the freshness reading can distinguish it from the run date (finding 197).
    /// </summary>
    public MembershipReconcileResult Apply(
        MembershipSnapshot primaryRoster, MembershipSnapshot crossRoster,
        string asOf, int[] countBand, string universe, string observedAt)
    {
        var forwardRead = new SliceScopedMembershipRead(
            new IndexMembershipReadService(db), db, new UniverseOptions { Bootstrap = { Universe = universe } });

        var result = new MembershipReconciler(db, new SecurityMaster(db), forwardRead)
            .Reconcile(primaryRoster, crossRoster, asOf, countBand, observedAt);

        if (result.Applied)
        {
            ApplySectorsFrom(primaryRoster, asOf);
            MaintainSliceSnapshot(result, asOf);
        }
        return result;
    }

    private const string Exchange = "US";

    private void ApplySectorsFrom(MembershipSnapshot roster, string asOf)
    {
        var master = new SecurityMaster(db);
        var assignments = new List<SectorAssignment>();
        foreach (var m in roster.Members)
        {
            if (string.IsNullOrWhiteSpace(m.Sector)) continue;
            var id = master.ResolveAsOf(m.CanonicalSymbol, Exchange, asOf);
            if (id is { } securityId) assignments.Add(new SectorAssignment(securityId, m.Sector));
        }
        if (assignments.Count > 0) new SectorIngestion(db).ApplySectors(assignments, asOf);
    }

    /// <summary>The slice snapshot must FOLLOW the reconciled roster — a post-snapshot index ADD would
    /// otherwise be silently dropped by <see cref="SliceScopedMembershipRead"/>'s intersection until the
    /// sp500 widen (removals applied, adds vanished: an asymmetric leak). Next version = previous slice
    /// + applied adds − applied drops. Append-only versioned (finding 108); the read resolves the
    /// version as-of each session date, so reproduce-day of an earlier committed day still sees the
    /// slice THAT day traded.</summary>
    private void MaintainSliceSnapshot(MembershipReconcileResult result, string asOf)
    {
        if (result.Adds.Count == 0 && result.Drops.Count == 0) return;
        var current = db.Config.Where(c => c.Key == HistoricalBackfillRunner.SliceConfigKey).AsEnumerable()
            .OrderByDescending(c => c.Version).FirstOrDefault();
        if (current is null) return;   // pre-backfill store: no snapshot exists, the raw read IS the slice

        var ids = System.Text.Json.JsonSerializer.Deserialize<List<long>>(current.ValueJson)?.ToHashSet() ?? [];
        foreach (var add in result.Adds) ids.Add(add);
        foreach (var drop in result.Drops) ids.Remove(drop);

        db.Config.Add(new Entities.ConfigRow
        {
            Key = HistoricalBackfillRunner.SliceConfigKey,
            ValueJson = System.Text.Json.JsonSerializer.Serialize(ids.OrderBy(x => x).ToList()),
            Version = current.Version + 1,
            ChangedOn = asOf,
            Reason = $"membership reconcile: +{result.Adds.Count}/-{result.Drops.Count} applied to the forward slice (rule 22).",
        });
        db.SaveChanges();
    }
}
