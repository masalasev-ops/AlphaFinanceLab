using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Providers;

namespace AlphaLab.Data.Services;

/// <summary>
/// What one HISTORICAL backfill run should do — the D70 Phase-4 replay prerequisite: every historical
/// S&amp;P 500 member inside the replay window (including delisted names) gets bars + corporate actions,
/// driven by the fja05680 community-CSV as-of membership. Distinct from <see cref="BackfillOptions"/>
/// (the FORWARD slice bootstrap): this mode never touches the forward reconciler and the forward mode
/// never seeds historical membership (co-mingling would mass-evict, see BackfillRunner.RunAsync).
/// </summary>
public sealed record HistoricalBackfillOptions
{
    /// <summary>Replay universe. Only 'sp500' is valid (D70: replay always uses S&amp;P 500 as-of
    /// membership; the S&amp;P 1500 extension is D87-contingent, decided at sign-off).</summary>
    public string Universe { get; init; } = "sp500";

    /// <summary>Replay-window lower bound (ISO yyyy-MM-dd) — bars are fetched from here.</summary>
    public string From { get; init; } = default!;

    /// <summary>Replay-window upper bound (ISO yyyy-MM-dd).</summary>
    public string To { get; init; } = default!;

    /// <summary>Operator-supplied CSV path override (CLI --csv); null ⇒ Backfill:HistoricalMembershipUrl.</summary>
    public string? CsvPath { get; init; }

    /// <summary>The date the CURRENT forward slice is read at for the pre-ingest snapshot (today; the
    /// slice's added_on stamps are recent, so a historical date would read an empty roster).</summary>
    public string SliceAsOf { get; init; } = default!;

    /// <summary>The TRUE observation instant stamped on every ingested row (D92 honesty — a historical
    /// backfill observes decades of data NOW; there is no session evening to approximate).</summary>
    public string ObservedAt { get; init; } = default!;

    /// <summary>EODHD daily-call plan limit for the ≥50% headroom check (null = unknown).</summary>
    public int? ApiPlanLimit { get; init; }

    /// <summary>Canonical symbols to SKIP on ingest (Universe:Exclusions, finding 266): a name here is
    /// skipped-and-recorded before any fetch, exactly like a ticker-reuse suspect, so a re-run reproduces
    /// its exclusion. The escape hatch for single-spell symbol reuse the &gt;2y disjoint-spell heuristic
    /// cannot see. Case-insensitive; empty by default.</summary>
    public IReadOnlyList<string> Exclusions { get; init; } = [];

    /// <summary>Walk the plan, no network, no writes.</summary>
    public bool DryRun { get; init; }
}

/// <summary>CLI parsing for the historical mode. Same fail-closed discipline as <see cref="BackfillArgs"/>.</summary>
public static class HistoricalBackfillArgs
{
    /// <summary>Parse <c>--historical sp500 --from d --to d [--csv path] [--dry-run]</c>.
    /// <paramref name="todayIso"/> supplies SliceAsOf.</summary>
    public static HistoricalBackfillOptions Parse(IReadOnlyList<string> args, string todayIso)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(todayIso);

        string? universe = null, from = null, to = null, csv = null;
        var dryRun = false;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--historical": universe = RequireValue(args, ref i); break;
                case "--from": from = RequireValue(args, ref i); break;
                case "--to": to = RequireValue(args, ref i); break;
                case "--csv": csv = RequireValue(args, ref i); break;
                case "--dry-run": dryRun = true; break;
                default: throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }

        if (universe != "sp500")
        {
            throw new ArgumentException(
                $"--historical universe must be 'sp500' (got '{universe ?? "(none)"}'): replay always uses " +
                "S&P 500 as-of membership (D70). The S&P 1500 extension is D87-contingent, decided at Phase-4 sign-off.");
        }
        var f = RequireIsoDate("--from", from);
        var t = RequireIsoDate("--to", to);
        if (string.CompareOrdinal(f, t) >= 0) throw new ArgumentException($"--from ({f}) must precede --to ({t}).");

        return new HistoricalBackfillOptions
        {
            Universe = universe, From = f, To = t, CsvPath = csv, SliceAsOf = todayIso, DryRun = dryRun,
        };
    }

    private static string RequireIsoDate(string flag, string? value)
    {
        if (value is null || !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw new ArgumentException($"{flag} requires an ISO date (yyyy-MM-dd); got '{value ?? "(none)"}'.");
        }
        return value;
    }

    private static string RequireValue(IReadOnlyList<string> args, ref int i)
    {
        if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Missing value for '{args[i]}'.");
        }
        return args[++i];
    }
}

// ---- The durable coverage artifact (D97): every list pre-sorted, NO wall-clock field, so the same
// store + window + CSV serializes byte-identically — a re-run is a clean git diff. Wall-clock
// provenance (swept_at) lives in the Backfill.HistoricalGateSweep config row, not here.

public sealed record MemberCoverage(
    string Symbol, long SecurityId, int ExpectedMemberSessions, int StoredSessions,
    double CoveragePct, string? FirstBar, string? LastBar);

public sealed record GateExclusion(string Symbol, long SecurityId, IReadOnlyList<string> Rejects);

public sealed record TickerReuseSuspect(string Symbol, long SecurityId, IReadOnlyList<string> Spells, double GapYears);

public sealed record UnresolvableMember(string Symbol, long SecurityId, string Reason);

/// <summary>An operator-listed exclusion (Universe:Exclusions, finding 266): a symbol skipped on ingest
/// because its in-window bars are the wrong company (single-spell reuse the disjoint-spell heuristic
/// cannot catch). Distinct from <see cref="TickerReuseSuspect"/> (heuristic-caught, two spells).</summary>
public sealed record OperatorExclusion(string Symbol, long SecurityId, string Reason);

public sealed record HistoricalCoverageReport(
    string Universe, string From, string To, string MembershipCsvSha256,
    int MembersInWindow,
    IReadOnlyList<MemberCoverage> Coverage,
    IReadOnlyList<GateExclusion> GateExclusions,
    IReadOnlyList<TickerReuseSuspect> TickerReuseSuspects,
    IReadOnlyList<UnresolvableMember> Unresolvable,
    IReadOnlyList<OperatorExclusion> OperatorExclusions)
{
    /// <summary>Canonical serialization — declaration-ordered properties over pre-sorted lists, NO
    /// run-mechanics counters (fetched-vs-skipped differs between a first run and an idempotent
    /// re-run; coverage facts do not) ⇒ deterministic bytes. The D97 re-run-identical property is
    /// asserted on exactly this rendering.</summary>
    public string ToCanonicalJson() =>
        JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    public string Sha256() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ToCanonicalJson())));
}

/// <summary>
/// The D70 historical backfill orchestrator (Phase 4, checkpoint 4.3). Steps: (1) snapshot the CURRENT
/// forward slice into the versioned config row <c>Universe.Bootstrap.SliceSecurityIds</c> BEFORE any
/// historical ingest (idempotent — never re-snapshotted once written, else a re-run after ingestion
/// would freeze ~500 names as "the slice"); (2) seed the fja05680 as-of membership; (3) enumerate every
/// member whose spell overlaps the window; (4) flag + EXCLUDE ticker-reuse suspects fail-closed (the
/// same canonical symbol with disjoint spells &gt; 2 years apart is likely two different companies —
/// EODHD resolves the symbol to ONE history, so ingesting would attribute the wrong company's bars);
/// (5) per member: fetch, GATE THE STAGED SERIES IN-MEMORY (D97 — a Reject excludes the name from
/// ingestion entirely, fail closed per name), else ingest with the TRUE observed_at; (6) write the
/// deterministic coverage report + the <c>Backfill.HistoricalGateSweep</c> marker config row.
/// The exclusion set is DETERMINISTIC: the gate is pure over the fetched data, so an idempotent re-run
/// reproduces the identical exclusion list — replay never depends on which attempt ingested the data.
/// </summary>
public sealed class HistoricalBackfillRunner(
    AlphaLabDbContext db,
    IMarketDataProvider marketData,
    IDataQualityGate gate,
    Action<string>? log = null)
{
    /// <summary>The versioned config row holding the pre-ingest forward-slice security_ids (JSON array).
    /// <see cref="SliceScopedMembershipRead"/> intersects the forward roster with it (rule 22).</summary>
    public const string SliceConfigKey = "Universe.Bootstrap.SliceSecurityIds";

    /// <summary>The D97 idempotence marker: {universe, from, to, artifact_sha256, excluded[]} + swept_at.</summary>
    public const string GateSweepConfigKey = "Backfill.HistoricalGateSweep";

    /// <summary>Disjoint spells further apart than this flag a ticker-reuse suspect.</summary>
    private const double TickerReuseGapYears = 2.0;

    private readonly Dictionary<string, int> _apiCalls = new(StringComparer.Ordinal);

    private void Log(string message) => log?.Invoke(message);
    private void Count(string source, int n = 1) => _apiCalls[source] = _apiCalls.GetValueOrDefault(source) + n;

    public async Task<HistoricalCoverageReport> RunAsync(HistoricalBackfillOptions o, string csv, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(o);
        ArgumentException.ThrowIfNullOrWhiteSpace(csv);
        if (o.Universe != "sp500")
        {
            throw new ArgumentException($"Historical backfill universe must be 'sp500' (D70); got '{o.Universe}'.");
        }

        var csvSha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(csv)));
        if (o.DryRun)
        {
            Log($"[dry-run] historical {o.Universe} bars {o.From}..{o.To} csv sha256={csvSha[..12]}… — no network, no writes.");
            return new HistoricalCoverageReport(o.Universe, o.From, o.To, csvSha, 0, [], [], [], [], []);
        }

        try
        {
            SnapshotSliceStep(o.SliceAsOf);

            var snapshots = HistoricalMembershipCsvParser.Parse(csv);
            var intervals = new HistoricalMembershipIngestion(db).Ingest(snapshots);
            Log($"[historical] {snapshots.Count} snapshots -> {intervals} membership intervals (idempotency is the ingestion's).");

            // Every member whose spell overlaps [From, To] — half-open [added_on, removed_on).
            var spells = db.IndexMembership.ToList()
                .Where(m => string.CompareOrdinal(m.AddedOn, o.To) <= 0
                            && (m.RemovedOn is null || string.CompareOrdinal(m.RemovedOn, o.From) > 0))
                .GroupBy(m => m.SecurityId)
                .ToDictionary(g => g.Key, g => g.OrderBy(m => m.AddedOn, StringComparer.Ordinal).ToList());

            var symbols = db.Securities.Where(s => spells.Keys.Contains(s.SecurityId))
                .ToDictionary(s => s.SecurityId, s => s.CurrentSymbol);

            var calendar = new CalendarService(db);
            var coverage = new List<MemberCoverage>();
            var gateExclusions = new List<GateExclusion>();
            var suspects = new List<TickerReuseSuspect>();
            var unresolvable = new List<UnresolvableMember>();
            var operatorExclusions = new List<OperatorExclusion>();
            var fetched = 0;
            var skippedCovered = 0;

            // Symbol-ordered walk ⇒ the report and every log line are deterministic.
            foreach (var (id, memberSpells) in spells.OrderBy(kv => symbols.GetValueOrDefault(kv.Key), StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var symbol = symbols.GetValueOrDefault(id);
                if (symbol is null)
                {
                    unresolvable.Add(new UnresolvableMember("(unregistered)", id, "security row has no symbol"));
                    continue;
                }

                // (3.5) Operator exclusion list (Universe:Exclusions, finding 266): skip-and-record before
                // any fetch, identically to a ticker-reuse suspect. The escape hatch for single-spell symbol
                // reuse the disjoint-spell heuristic below cannot see (e.g. SUN: old Sunoco's ticker reused
                // by Sunoco LP, whose in-window bars are the wrong company).
                if (o.Exclusions.Contains(symbol, StringComparer.OrdinalIgnoreCase))
                {
                    operatorExclusions.Add(new OperatorExclusion(symbol, id, "Universe:Exclusions"));
                    Log($"[exclude] {symbol} (id={id}): operator exclusion (Universe:Exclusions) — EXCLUDED from ingest (finding 266).");
                    continue;
                }

                // (4) Ticker-reuse suspects: fail closed until the operator resolves the identity.
                if (ReuseGapYears(memberSpells) is { } gap && gap > TickerReuseGapYears)
                {
                    suspects.Add(new TickerReuseSuspect(symbol, id,
                        memberSpells.Select(s => $"[{s.AddedOn},{s.RemovedOn ?? "open"})").ToList(), Math.Round(gap, 2)));
                    Log($"[reuse] {symbol} (id={id}): disjoint spells {gap:F1}y apart — EXCLUDED until identity is resolved (fail closed).");
                    continue;
                }

                var expected = ExpectedMemberSessions(calendar, memberSpells, o.From, o.To);
                var stored = StoredDates(id, o.From, o.To);

                // Already fully covered (e.g. a forward-slice member whose 20y backfill spans the window):
                // skip the fetch — the data is in hand and a re-fetch would be a value-diff no-op anyway.
                if (expected.Count > 0 && expected.All(stored.Contains))
                {
                    skippedCovered++;
                    coverage.Add(Coverage(symbol, id, expected, stored));
                    continue;
                }

                // (5) Fetch + D97 in-memory gate + ingest.
                IReadOnlyList<EodBar> bars;
                IReadOnlyList<DividendEvent> dividends;
                IReadOnlyList<SplitEvent> splits;
                try
                {
                    bars = await marketData.GetEodAsync(symbol, o.From, o.To, o.SliceAsOf, ct).ConfigureAwait(false);
                    Count("eodhd", EodhdEndpointCost.Eod);
                    dividends = await marketData.GetDividendsAsync(symbol, o.From, o.SliceAsOf, ct).ConfigureAwait(false);
                    Count("eodhd", EodhdEndpointCost.Div);
                    splits = await marketData.GetSplitsAsync(symbol, o.From, o.SliceAsOf, ct).ConfigureAwait(false);
                    Count("eodhd", EodhdEndpointCost.Splits);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    unresolvable.Add(new UnresolvableMember(symbol, id, ex.Message));
                    Log($"[historical] {symbol} (id={id}): fetch failed — {ex.Message} (recorded, continuing).");
                    continue;
                }

                var report = gate.Evaluate(symbol, bars, ActionShells(id, dividends, splits));
                if (report.HasRejects)
                {
                    var rejects = report.Flags
                        .Where(f => f.Severity == QualitySeverity.Reject)
                        .Select(f => $"{f.Issue} {f.Date}: {f.Detail}")
                        .OrderBy(s => s, StringComparer.Ordinal)
                        .ToList();
                    gateExclusions.Add(new GateExclusion(symbol, id, rejects));
                    Log($"[gate] {symbol} (id={id}): {rejects.Count} REJECT flag(s) — EXCLUDED from ingestion (D97, fail closed).");
                    continue;
                }

                var written = new BarIngestionService(db).IngestEod(id, bars, o.ObservedAt);
                var ca = new CorporateActionIngestion(db);
                var divs = ca.IngestDividends(id, dividends, o.ObservedAt);
                var spl = ca.IngestSplits(id, splits, o.ObservedAt);
                fetched++;

                var storedAfter = StoredDates(id, o.From, o.To);
                coverage.Add(Coverage(symbol, id, expected, storedAfter));
                Log($"[historical] {symbol} (id={id}): {written} bars, {divs} dividends, {spl} splits " +
                    $"({storedAfter.Count}/{expected.Count} member sessions covered).");

                // One shared context serves all ~500 members; without this, every ingested bar row
                // stays tracked and each member's SaveChanges walks the whole accumulated set —
                // a near-O(N^2) DetectChanges shape with multi-GB tracker memory at full scale
                // (Phase-4 review). Everything is committed by here; detaching loses nothing.
                db.ChangeTracker.Clear();
            }

            var result = new HistoricalCoverageReport(
                o.Universe, o.From, o.To, csvSha,
                MembersInWindow: spells.Count,
                Coverage: coverage,          // already symbol-ordered by the walk
                GateExclusions: gateExclusions,
                TickerReuseSuspects: suspects,
                Unresolvable: unresolvable,
                OperatorExclusions: operatorExclusions);

            WriteGateSweepMarker(o, result);
            Log($"[done] historical backfill: {fetched} fetched, {skippedCovered} already covered, " +
                $"{gateExclusions.Count} gate-excluded, {suspects.Count} ticker-reuse suspect(s), " +
                $"{operatorExclusions.Count} operator-excluded, {unresolvable.Count} unresolvable.");
            return result;
        }
        finally
        {
            FlushApiUsage(o);
        }
    }

    // ---- (1) the pre-ingest slice snapshot: written ONCE, ever ----
    private void SnapshotSliceStep(string sliceAsOf)
    {
        if (db.Config.Any(c => c.Key == SliceConfigKey))
        {
            Log("[slice] Universe.Bootstrap.SliceSecurityIds already snapshotted — left untouched (idempotent).");
            return;
        }
        var ids = new IndexMembershipReadService(db).MembersAsOf(sliceAsOf).OrderBy(x => x).ToList();
        if (ids.Count == 0)
        {
            // A store with no forward slice (a replay-only rebuild) has nothing to preserve; the
            // SliceScopedMembershipRead treats the missing row as pass-through.
            Log("[slice] no current members to snapshot (fresh store) — skipped.");
            return;
        }
        db.Config.Add(new ConfigRow
        {
            Key = SliceConfigKey,
            ValueJson = JsonSerializer.Serialize(ids),
            Version = 1,
            ChangedOn = sliceAsOf,
            Reason = "D70/rule 22: the forward S&P 100 slice, snapshotted BEFORE historical S&P 500 ingest widens index_membership.",
        });
        db.SaveChanges();
        Log($"[slice] snapshotted {ids.Count} forward-slice members into '{SliceConfigKey}' v1.");
    }

    // ---- (4) helper: the largest gap between consecutive disjoint spells, in years ----
    private static double? ReuseGapYears(IReadOnlyList<IndexMembershipRow> spells)
    {
        if (spells.Count < 2) return null;
        double? worst = null;
        for (var i = 1; i < spells.Count; i++)
        {
            if (spells[i - 1].RemovedOn is not { } removed) continue; // overlapping/open — not disjoint
            var gapDays = DateOnly.ParseExact(spells[i].AddedOn, "yyyy-MM-dd", CultureInfo.InvariantCulture).DayNumber
                          - DateOnly.ParseExact(removed, "yyyy-MM-dd", CultureInfo.InvariantCulture).DayNumber;
            var years = gapDays / 365.25;
            if (worst is null || years > worst) worst = years;
        }
        return worst;
    }

    // Sessions the member SHOULD have bars for: calendar sessions inside spells ∩ [from, to]
    // (spells are half-open, so the session must be strictly before removed_on).
    private static HashSet<string> ExpectedMemberSessions(
        CalendarService calendar, IReadOnlyList<IndexMembershipRow> spells, string from, string to)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in spells)
        {
            var lo = string.CompareOrdinal(s.AddedOn, from) > 0 ? s.AddedOn : from;
            // "<= 0": removed_on == to still bounds the spell (removed_on is EXCLUSIVE — the removal
            // date is the first non-member day). The old "< 0" counted `to` as an expected member
            // session for a name delisted exactly at the window end, so its coverage could never reach
            // 100% and every re-run re-fetched it (Phase-4 review).
            var hiExclusive = s.RemovedOn is { } r && string.CompareOrdinal(r, to) <= 0 ? r : null;
            var hi = hiExclusive is { } h
                ? DateOnly.ParseExact(h, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(-1)
                : DateOnly.ParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var loDate = DateOnly.ParseExact(lo, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (loDate > hi) continue;
            foreach (var d in calendar.SessionsBetween(loDate, hi))
            {
                expected.Add(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }
        }
        return expected;
    }

    private HashSet<string> StoredDates(long securityId, string from, string to) =>
        db.Bars.Where(b => b.SecurityId == securityId
                           && string.Compare(b.Date, from) >= 0
                           && string.Compare(b.Date, to) <= 0)
            .Select(b => b.Date)
            .Distinct()
            .ToHashSet(StringComparer.Ordinal);

    private static MemberCoverage Coverage(string symbol, long id, HashSet<string> expected, HashSet<string> stored)
    {
        var have = expected.Count(stored.Contains);
        var ordered = stored.OrderBy(d => d, StringComparer.Ordinal).ToList();
        return new MemberCoverage(symbol, id, expected.Count, have,
            expected.Count == 0 ? 0 : Math.Round(100.0 * have / expected.Count, 2),
            ordered.Count > 0 ? ordered[0] : null,
            ordered.Count > 0 ? ordered[^1] : null);
    }

    // The gate's reconciliation check wants the action feed as rows; these shells carry exactly the
    // fields it reads (type + ex/effective date) — they are never persisted.
    private static List<CorporateActionRow> ActionShells(long id, IReadOnlyList<DividendEvent> dividends, IReadOnlyList<SplitEvent> splits)
    {
        var shells = new List<CorporateActionRow>(dividends.Count + splits.Count);
        foreach (var d in dividends.Where(d => !string.IsNullOrWhiteSpace(d.Date)))
        {
            shells.Add(new CorporateActionRow { SecurityId = id, Type = "dividend", ExDate = d.Date, EffectiveDate = d.Date, ObservedAt = "shell" });
        }
        foreach (var s in splits.Where(s => !string.IsNullOrWhiteSpace(s.Date)))
        {
            shells.Add(new CorporateActionRow { SecurityId = id, Type = "split", EffectiveDate = s.Date, ObservedAt = "shell" });
        }
        return shells;
    }

    // ---- (6) the versioned marker row: append-only (a re-run appends the next version) ----
    private void WriteGateSweepMarker(HistoricalBackfillOptions o, HistoricalCoverageReport report)
    {
        var current = db.Config.Where(c => c.Key == GateSweepConfigKey).AsEnumerable()
            .OrderByDescending(c => c.Version).FirstOrDefault();
        db.Config.Add(new ConfigRow
        {
            Key = GateSweepConfigKey,
            ValueJson = JsonSerializer.Serialize(new
            {
                universe = o.Universe,
                from = o.From,
                to = o.To,
                artifact_sha256 = report.Sha256(),
                excluded = report.GateExclusions.Select(g => g.Symbol).ToList(),
                ticker_reuse = report.TickerReuseSuspects.Select(s => s.Symbol).ToList(),
                operator_excluded = report.OperatorExclusions.Select(e => e.Symbol).ToList(),
                swept_at = o.ObservedAt,
            }),
            Version = (current?.Version ?? 0) + 1,
            ChangedOn = o.ObservedAt,
            Reason = "D97: the historical backfill's in-memory gate sweep marker (window + exclusions + artifact hash).",
        });
        db.SaveChanges();
    }

    private void FlushApiUsage(HistoricalBackfillOptions o)
    {
        if (_apiCalls.Count == 0) return;
        var writer = new ApiUsageLogWriter(db);
        foreach (var (source, calls) in _apiCalls)
        {
            writer.Record(o.SliceAsOf, source, calls, o.ApiPlanLimit);
        }
        db.SaveChanges();
        _apiCalls.Clear();
    }
}

/// <summary>
/// Rule 22 / D70 forward-slice preservation: once the historical S&amp;P 500 membership lands,
/// <c>index_membership.MembersAsOf(today)</c> resolves ~500 names — but the FORWARD universe stays the
/// S&amp;P 100 slice through Phase-4 sign-off. This decorator intersects the as-of roster with the
/// slice snapshot (<see cref="HistoricalBackfillRunner.SliceConfigKey"/>) while
/// <c>Universe:Bootstrap:Universe == "sp100"</c>. The post-sign-off widen is the config flip — the
/// filter dissolves on any other value. The REPLAY composition (D95) registers the RAW
/// <see cref="IndexMembershipReadService"/> instead: replay never runs on the slice (rule 22).
/// A missing snapshot row is pass-through: a store that never ingested historical membership has
/// nothing to scope away, and fabricating an empty universe would be a silent fail-open in reverse.
///
/// The intersection is DATE-AWARE (Phase-4 review): the slice row is versioned — the backfill writes
/// v1, and every membership reconcile that changes the S&amp;P 100 roster appends the next version —
/// and this read resolves the version as-of the requested date (changed_on &lt;= date; the row's
/// changed_on IS a session-comparable date). So a post-snapshot index ADD flows into the forward
/// universe at its reconcile date instead of vanishing behind a frozen set, and a reproduce-day of an
/// earlier committed session resolves the slice THAT day saw. A date before the first snapshot uses
/// v1 — the closest recorded approximation of the pre-snapshot slice scope (bounded to the ~100
/// names, never the ingested ~500).
/// </summary>
public sealed class SliceScopedMembershipRead(
    IIndexMembershipRead inner,
    AlphaLabDbContext db,
    UniverseOptions options) : IIndexMembershipRead
{
    private List<(string ChangedOn, HashSet<long> Ids)>? _versions;

    public IReadOnlyList<long> MembersAsOf(string date)
    {
        var members = inner.MembersAsOf(date);
        if (!string.Equals(options.Bootstrap.Universe, "sp100", StringComparison.Ordinal)) return members;

        _versions ??= db.Config.Where(c => c.Key == HistoricalBackfillRunner.SliceConfigKey).AsEnumerable()
            .OrderBy(c => c.Version)
            .Select(c => (c.ChangedOn, JsonSerializer.Deserialize<List<long>>(c.ValueJson)?.ToHashSet() ?? []))
            .ToList();
        if (_versions.Count == 0) return members;

        var slice = _versions[0].Ids;
        foreach (var (changedOn, ids) in _versions)
        {
            if (string.CompareOrdinal(changedOn, date) <= 0) slice = ids;
            else break;
        }
        return members.Where(slice.Contains).ToList();
    }
}

/// <summary>
/// Replay-roster deny-list (finding 266): removes <see cref="UniverseOptions.Exclusions"/> symbols from
/// the as-of membership so a security whose in-window bars are the WRONG company — single-spell ticker
/// reuse the backfill's &gt;2y disjoint-spell heuristic cannot catch (e.g. SUN: old Sunoco's ticker reused
/// by Sunoco LP) — is never rostered in replay, and its already-ingested bars are therefore never read.
/// This is the rule-3-compliant substitute for deleting the bars (which is forbidden): the bars stay,
/// inert, because the security leaves the roster. Registered ONLY in the replay composition
/// (ReplayRunner), wrapping the RAW <see cref="IndexMembershipReadService"/> that D70/rule 22 mandate for
/// replay; the forward pipeline uses <see cref="SliceScopedMembershipRead"/> and is unaffected. On a fresh
/// store a future backfill never ingests the excluded symbols (they have no bars → inert like a
/// ticker-reuse suspect), so there this filter is a harmless no-op. Excluded symbols resolve to
/// security_ids once, case-insensitively and fail-closed (a reused symbol may map to more than one
/// security row); an empty list is a pass-through.
/// </summary>
public sealed class ExclusionScopedMembershipRead(
    IIndexMembershipRead inner,
    AlphaLabDbContext db,
    UniverseOptions options) : IIndexMembershipRead
{
    private HashSet<long>? _excludedIds;

    public IReadOnlyList<long> MembersAsOf(string date)
    {
        var members = inner.MembersAsOf(date);
        if (options.Exclusions is not { Length: > 0 }) return members;

        _excludedIds ??= ResolveExcludedIds();
        if (_excludedIds.Count == 0) return members;
        return members.Where(id => !_excludedIds.Contains(id)).ToList();
    }

    private HashSet<long> ResolveExcludedIds()
    {
        var wanted = new HashSet<string>(options.Exclusions, StringComparer.OrdinalIgnoreCase);
        return db.Securities.AsEnumerable()
            .Where(s => wanted.Contains(s.CurrentSymbol))
            .Select(s => s.SecurityId)
            .ToHashSet();
    }
}
