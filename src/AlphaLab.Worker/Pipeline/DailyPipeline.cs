using System.Globalization;
using AlphaLab.Core.Config;
using AlphaLab.Core.Domain;
using AlphaLab.Core.Funnel;
using AlphaLab.Core.Ledger;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;
using AlphaLab.Strategies;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker.Pipeline;

/// <summary>The outcome of one <see cref="DailyPipeline.RunDayAsync"/>. Aborted ⇒ NOTHING was written
/// (a Stage-1 reject left zero rows, FR-29); Committed ⇒ the run row is 'ok' and run_in_progress is clear.</summary>
public sealed record DailyRunResult(
    bool Committed,
    bool Aborted,
    long? RunId,
    string AsOf,
    int FlagCount,
    string? AbortReason);

/// <summary>
/// The D53 staged daily pipeline (checkpoint 2.10) — the FIRST committed write to the live store, and the
/// only place a trading day is processed. Hosted in AlphaLab.Worker, the sole DB writer (D59).
///
/// THE RUN-ROW LIFECYCLE resolves four constraints that collide (FR-29's zero-write-on-Stage-1-failure,
/// data_quality_flags.run_id NOT NULL, D72's run_in_progress written outside the daily transaction, and
/// ux_runs_ok_forward being partial on status='ok'). Exactly one ordering satisfies all four (RUNBOOK §1):
///
///   1. STAGE 1 — fetch + gate. ZERO writes. A reject leaves literally zero rows (no run row yet).
///   2. small txn — INSERT runs(status='running', watermark); worker_state.run_in_progress=1. Commit,
///      so a crash mid-day stays visible to the next launch (what D72's stale-run recovery presupposes).
///   3. STAGE 2 — ONE atomic transaction (§20.4 order): bars → actions → regime label → the funnel +
///      fills per account → quality flags (run_id now exists — D77) → runs.status='ok' → catchup_log if
///      catchup. Commit. A crash here rolls the whole day back, leaving a 'running' row +
///      run_in_progress=1 (FX-CrashedRun's fixture) for the next launch to recover.
///   4. small txn — clear run_in_progress. Only on success; a crash leaves it set for recovery.
///
/// "Run row last" (D53) is honoured as FINALISATION last: the row exists as 'running' from step 2 and is
/// only flipped to 'ok' at the end of the Stage-2 transaction.
///
/// MEMBERSHIP is READ here (MembersAsOf feeds the funnel's universe); the daily OEF/Wikipedia REFRESH is
/// a stated seam left for the catch-up/operator work — the roster is stable intraday and wiring the
/// membership providers into the forward Worker duplicates the Backfill CLI's composition (which is the
/// D70-widening job, finding 151). The funnel always trades the current stored roster.
/// </summary>
public sealed class DailyPipeline(
    AlphaLabDbContext db,
    Stage1Fetch stage1,
    ILedgerStore ledger,
    IBarReadService barReads,
    ICorporateActionReadService caReads,
    IRegimeLabelService regime,
    CorporateActionApplier caApplier,
    ICalendarService calendar,
    IIndexMembershipRead membership,
    IDataQualityFlagStore flagStore,
    CostsOptions costs,
    ArenaOptions arena,
    TimeProvider clock,
    ILogger<DailyPipeline> logger)
{
    // The daily fetch window: enough sessions of context that the FR-6 outlier / reconciliation checks
    // have neighbours around the just-closed bar. NOT a config knob (it is an internal fetch bound, not a
    // documented threshold); a stop-and-report seam if the daily EODHD-call budget (2.12) argues for the
    // /eod-bulk-last-day path or a shorter window once the historical gate has run once.
    private const int FetchContextSessions = 40;

    private const string RunKindLive = "live";

    /// <summary>
    /// Process one trading day. <paramref name="runKind"/> is the runs.run_kind ('live' or 'catchup' —
    /// both are FORWARD, so the ledger writes RunKind.Live for both, D37). Idempotent per day via the
    /// session-derived watermark + ux_runs_ok_forward; a re-run of an already-'ok' forward day is caught
    /// by the partial unique index at Stage-2 commit.
    /// </summary>
    public async Task<DailyRunResult> RunDayAsync(string asOf, string runKind = RunKindLive, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);
        var asOfDate = ParseDate(asOf);
        // Session-derived watermark (D47/2.11 convention) — deterministic, NEVER UtcNow, so a re-fetch is
        // a value-diff no-op and the day is reproducible.
        var watermark = $"{asOf}T22:00:00Z";

        using var arenaScope = logger.BeginArenaScope(arena);

        // ---- Step 1: STAGE 1 — fetch + gate. ZERO writes. ----
        var request = BuildStage1Request(asOfDate, asOf, watermark);
        var staged = await stage1.FetchAsync(request, ct).ConfigureAwait(false);

        if (staged.HasRejects)
        {
            // FR-29 + rule 10: a fail-closed reject aborts BEFORE the run row is written. Nothing persisted.
            var rejects = staged.All
                .SelectMany(s => s.Report.Flags.Where(f => f.Severity == QualitySeverity.Reject)
                    .Select(f => $"{s.Symbol} {f.Date}: {f.Detail}"));
            var reason = "Stage 1 gate rejected the day's data (fail closed): " + string.Join("; ", rejects);
            logger.LogError("{AsOf}: {Reason} — no run row written.", asOf, reason);
            return new DailyRunResult(false, true, null, asOf, staged.FlagCount, reason);
        }

        // ---- Step 2: small txn — run row + run_in_progress (committed so a crash stays visible). ----
        var runId = OpenRun(asOf, runKind, watermark);

        // ---- Step 3: STAGE 2 — ONE atomic transaction. ----
        using (var txn = db.Database.BeginTransaction())
        {
            try
            {
                IngestStaged(staged, watermark);

                // Regime label (reads the proxy series at the watermark — includes today's just-ingested
                // proxy bar). Fails closed to "no label today" (logged), never aborting the run: the label
                // is not a Phase-2 funnel input (regime-halt guardrails are Phase 7).
                var label = regime.ComputeAndSave(asOf, watermark);
                if (!label.Computed) logger.LogInformation("{AsOf}: regime label not computed — {Reason}", asOf, label.Reason);

                // Seed the dummy roster (idempotent): the three baseline/dummy strategies + their accounts.
                new DummyRoster(db, ledger).Seed(asOf);

                var features = new BarFeatureView(barReads, calendar, asOfDate, watermark, costs);
                var broker = new VirtualBroker(new CostModel(costs));

                foreach (var account in ledger.GetAccounts(RunKind.Live))
                {
                    await RunAccountDayAsync(account, asOfDate, asOf, watermark, features, broker, ct).ConfigureAwait(false);
                }

                PersistQualityFlags(runId, staged, watermark);

                FinaliseRun(runId, runKind, asOf);

                db.SaveChanges();
                txn.Commit();
            }
            catch (Exception ex)
            {
                // Roll the WHOLE day back — the run row stays 'running' and run_in_progress stays 1
                // (FX-CrashedRun), for the next launch's stale-run recovery (D72/2.12) to mark failed.
                txn.Rollback();
                logger.LogError(ex, "{AsOf}: Stage 2 failed and was rolled back; run {RunId} left 'running' for recovery.", asOf, runId);
                throw;
            }
        }

        // ---- Step 4: small txn — clear run_in_progress (success only). ----
        ClearRunInProgress();

        logger.LogInformation("{AsOf}: run {RunId} committed ok ({Flags} quality flag(s) persisted).", asOf, runId, staged.FlagCount);
        return new DailyRunResult(true, false, runId, asOf, staged.FlagCount, null);
    }

    // ---- Step 0 helpers: assemble Stage 1's read-only inputs ----

    private Stage1Request BuildStage1Request(DateOnly asOfDate, string asOf, string watermark)
    {
        var from = ResolveFetchFrom(asOfDate);
        var expected = calendar.SessionsBetween(ParseDate(from), asOfDate).Select(Iso).ToList();

        var members = membership.MembersAsOf(asOf);
        var cwProxyId = ResolveConfigLong(CapWeightProxy.ProxySecurityIdConfigKey);

        // The tradeable fetch set = the index roster ∪ the cap-weight ETF proxy (a security we hold+price
        // in the CW account but which may not be an index member).
        var targetIds = new List<long>(members);
        if (cwProxyId is { } cw && !targetIds.Contains(cw)) targetIds.Add(cw);

        var securities = new List<Stage1Target>(targetIds.Count);
        foreach (var id in targetIds)
        {
            var symbol = db.Securities.Find(id)?.CurrentSymbol;
            if (symbol is null)
            {
                logger.LogWarning("{AsOf}: security_id {Id} has no symbol — skipped from the fetch.", asOf, id);
                continue;
            }
            securities.Add(new Stage1Target(id, symbol, caReads.GetActionsAsOf(id, watermark), LastStoredDate(id, asOf, watermark)));
        }

        var regimeProxyId = ResolveConfigLong(RegimeProxyIngestion.ProxyConfigKey);
        ProxyTarget? proxy = regimeProxyId is { } pid
            ? new ProxyTarget(pid, db.Securities.Find(pid)?.CurrentSymbol ?? "GSPC", LastStoredDate(pid, asOf, watermark))
            : null;

        return new Stage1Request(asOf, from, watermark, watermark, expected, securities, proxy);
    }

    private string ResolveFetchFrom(DateOnly asOf)
    {
        var cursor = calendar.IsTradingDay(asOf) ? asOf : calendar.PreviousSession(asOf) ?? asOf;
        for (var i = 0; i < FetchContextSessions && calendar.PreviousSession(cursor) is { } prev; i++) cursor = prev;
        return Iso(cursor);
    }

    private string? LastStoredDate(long securityId, string asOf, string watermark)
    {
        var series = barReads.GetSeries(securityId, "0001-01-01", asOf, watermark);
        return series.Count == 0 ? null : series[^1].Date;
    }

    // ---- Step 2: open the run row (committed in its own small transaction) ----

    private long OpenRun(string asOf, string runKind, string watermark)
    {
        using var txn = db.Database.BeginTransaction();
        var run = new RunRow
        {
            AsOf = asOf,
            RunKind = runKind,
            Watermark = watermark,
            StartedAt = NowIso(),
            Status = "running",
        };
        db.Runs.Add(run);
        db.SaveChanges();

        var state = WorkerStateRow();
        state.RunInProgress = 1;
        state.CurrentRunId = run.RunId;
        state.HeartbeatAt = NowIso();
        db.SaveChanges();

        txn.Commit();
        return run.RunId;
    }

    // ---- Step 3 helpers ----

    private void IngestStaged(StagedDay staged, string watermark)
    {
        var barIngest = new BarIngestionService(db);
        var caIngest = new CorporateActionIngestion(db);

        foreach (var s in staged.Securities)
        {
            barIngest.IngestEod(s.SecurityId, s.Bars, watermark);
            caIngest.IngestDividends(s.SecurityId, s.Dividends, watermark);
            caIngest.IngestSplits(s.SecurityId, s.Splits, watermark);
        }

        if (staged.Proxy is { } proxy)
        {
            // The regime proxy's bars carry the eodhd_gspc source (an index EOD, no corporate actions).
            barIngest.IngestEod(proxy.SecurityId, proxy.Bars, watermark, RegimeProxySource.EodhdGspc);
        }
    }

    private async Task RunAccountDayAsync(
        Account account, DateOnly asOfDate, string asOf, string watermark,
        BarFeatureView features, VirtualBroker broker, CancellationToken ct)
    {
        var plan = Phase2StrategyRegistry.For(account.StrategyId);
        if (plan is null)
        {
            logger.LogWarning("{AsOf}: account {Id} runs unknown strategy '{Strategy}' — skipped (rule 10).", asOf, account.AccountId, account.StrategyId);
            return;
        }

        // (a) Corporate actions effective today, BEFORE fills/funnel (D53 order).
        caApplier.ApplyForAccount(account.AccountId, RunKind.Live, asOf, watermark);

        // (b) Fill the orders decided on the PRIOR session at today's open (the T+1 half of decide-at-close-T).
        FillPriorOrders(account.AccountId, asOfDate, asOf, features, broker);

        // (c) The book post-CA/post-fill, and its equity at today's close.
        var held = ledger.GetPositions(account.AccountId);
        var cash = ComputeCash(account.AccountId);
        var equity = cash + MarkToMarket(held, asOfDate, features);

        // (d) Decide today's orders (unless there is no next session to fill them).
        var fillOn = calendar.NextSession(asOfDate);
        if (fillOn is null)
        {
            logger.LogWarning("{AsOf}: no next session in the calendar — recording equity but deciding no orders.", asOf);
        }
        else
        {
            var universe = ResolveUniverse(plan.Universe, asOf);
            var inputs = new FunnelInputs
            {
                IndexMembers = universe,
                Held = held,
                Equity = equity,
                FillOn = fillOn.Value,
                SessionsSinceInception = SessionsSinceInception(account.AccountId, asOfDate),
            };
            var outcome = await FunnelRunner.RunAsync(plan.Model, features, inputs, plan.Guardrails, plan.Sizing, ct).ConfigureAwait(false);
            ledger.RecordDecision(account.AccountId, asOf, outcome.Snapshot.ToJson(), RunKind.Live);
        }

        // (e) The day's equity point (idempotent per account/as_of/run_kind).
        ledger.RecordEquityPoint(account.AccountId, asOf, equity, cash, RunKind.Live);
    }

    private void FillPriorOrders(long accountId, DateOnly asOfDate, string asOf, BarFeatureView features, VirtualBroker broker)
    {
        var prevSession = calendar.PreviousSession(asOfDate);
        if (prevSession is null) return;

        var priorJson = ledger.GetDecisionJson(accountId, Iso(prevSession.Value), RunKind.Live);
        if (priorJson is null) return; // the account's first decision has not been made yet (inception)

        var snapshot = DecisionSnapshot.FromJson(priorJson);
        foreach (var order in snapshot.Stage6Orders)
        {
            if (order.FillOn != asOf) continue; // only today's fills (consecutive-session processing keeps these aligned)

            var mkt = new MarketInputs
            {
                RawPrice = features.RawOpen(order.SecurityId, asOfDate) is { } o ? (decimal)o : null,
                Adv21Shares = features.Adv21Shares(order.SecurityId),
                Adv21Notional = features.Adv21Notional(order.SecurityId),
                SigmaDaily = features.RealizedVolDaily(order.SecurityId, costs.AdvWindowDays),
            };

            switch (OrderFill.Fill(order, mkt, accountId, broker, RunKind.Live))
            {
                case FillResult.Filled f:
                    PostFill(accountId, f.Trade);
                    if (f.Clip is { } clip)
                    {
                        ledger.RecordCapacityRejection(accountId, order.SecurityId, asOf,
                            clip.IntendedShares, clip.AllowedShares, clip.Adv21Shares);
                    }
                    break;

                case FillResult.Rejected r:
                    if (r.Clip is { } rc)
                    {
                        ledger.RecordCapacityRejection(accountId, order.SecurityId, asOf,
                            rc.IntendedShares, rc.AllowedShares, rc.Adv21Shares);
                    }
                    logger.LogInformation("{AsOf}: order for {Security} not filled — {Reason}", asOf, order.SecurityId, r.Reason);
                    break;
            }
        }
    }

    // Post a funnel fill to the book: raw-price basis (D30), proportional basis reduction on a sell.
    private void PostFill(long accountId, Trade trade)
    {
        ledger.RecordTrade(trade);
        var existing = ledger.GetPosition(accountId, trade.SecurityId);

        if (trade.Side == TradeSide.Buy)
        {
            ledger.UpsertPosition(new Position
            {
                AccountId = accountId,
                SecurityId = trade.SecurityId,
                Shares = (existing?.Shares ?? 0) + trade.Shares,
                CostBasis = (existing?.CostBasis ?? 0m) + trade.RawFillPrice * (decimal)trade.Shares,
                OpenedOn = existing?.OpenedOn ?? trade.FilledOn,
            });
            return;
        }

        // Sell: reduce the held position. A close sells the whole line (newShares ≈ 0 ⇒ row removed).
        if (existing is null)
        {
            throw new InvalidOperationException(
                $"Fill sells {trade.Shares} of security {trade.SecurityId} in account {accountId} but the position is not held — " +
                "the funnel planned a close/trim against a book the ledger disagrees with.");
        }
        var newShares = existing.Shares - trade.Shares;
        if (newShares <= 1e-9)
        {
            ledger.UpsertPosition(existing with { Shares = 0 }); // deletes the row (positions is state, not a log)
        }
        else
        {
            ledger.UpsertPosition(existing with
            {
                Shares = newShares,
                CostBasis = existing.CostBasis * (decimal)(newShares / existing.Shares),
            });
        }
    }

    private void PersistQualityFlags(long runId, StagedDay staged, string watermark)
    {
        foreach (var s in staged.All.Where(s => s.Report.Flags.Count > 0))
        {
            flagStore.Save(runId, s.SecurityId, s.Report.Flags, watermark);
        }
    }

    private void FinaliseRun(long runId, string runKind, string asOf)
    {
        var run = db.Runs.First(r => r.RunId == runId);
        run.Status = "ok";
        run.FinishedAt = NowIso();

        if (string.Equals(runKind, "catchup", StringComparison.Ordinal))
        {
            db.CatchupLog.Add(new CatchupLogRow { AsOf = asOf, RecoveredAt = NowIso(), RunId = runId });
        }
    }

    // ---- Step 4 ----

    private void ClearRunInProgress()
    {
        using var txn = db.Database.BeginTransaction();
        var state = WorkerStateRow();
        state.RunInProgress = 0;
        db.SaveChanges();
        txn.Commit();
    }

    // ---- ledger math ----

    // Cash reconciles from events + fills (there is no stored balance column): deposits/dividends/merger
    // cash are cash_events; buys/sells (with costs) are trade CashDeltas.
    private decimal ComputeCash(long accountId) =>
        ledger.GetCashEvents(accountId, RunKind.Live).Sum(e => e.Amount)
        + ledger.GetTrades(accountId, RunKind.Live).Sum(t => t.CashDelta);

    // Mark the book at today's raw close. A held name with no bar today (a frozen/halted position) is
    // valued at its average cost basis — neither a gain nor a loss recognised — rather than dropped;
    // the dummies always hold priced names, so this fallback never fires for them.
    private decimal MarkToMarket(IReadOnlyList<Position> held, DateOnly asOf, BarFeatureView features)
    {
        var total = 0m;
        foreach (var p in held)
        {
            total += features.RawClose(p.SecurityId, asOf) is { } c
                ? (decimal)c * (decimal)p.Shares
                : p.CostBasis;
        }
        return total;
    }

    private int SessionsSinceInception(long accountId, DateOnly asOf)
    {
        var opening = ledger.GetCashEvents(accountId, RunKind.Live)
            .Where(e => e.Type == CashEventType.Deposit)
            .Select(e => e.AsOf)
            .FirstOrDefault();
        if (opening is null) return 0;
        return Math.Max(0, calendar.SessionsBetween(ParseDate(opening), asOf).Count - 1);
    }

    private IReadOnlyList<SecurityId> ResolveUniverse(UniverseScope scope, string asOf)
    {
        if (scope == UniverseScope.CapWeightProxy)
        {
            var cw = ResolveConfigLong(CapWeightProxy.ProxySecurityIdConfigKey);
            if (cw is { } id) return [new SecurityId(id)];
            logger.LogWarning("Cap-weight proxy security_id is unresolved ('{Key}' has no config row) — the CW benchmark holds cash this run.", CapWeightProxy.ProxySecurityIdConfigKey);
            return [];
        }
        return membership.MembersAsOf(asOf).Select(id => new SecurityId(id)).ToList();
    }

    // ---- config + timestamps ----

    private long? ResolveConfigLong(string key)
    {
        var current = db.Config.Where(c => c.Key == key).AsEnumerable()
            .OrderByDescending(c => c.Version).FirstOrDefault();
        return current is not null && long.TryParse(current.ValueJson, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
    }

    private WorkerStateRow WorkerStateRow() =>
        db.WorkerState.First(w => w.Id == 1);

    private string NowIso() => clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static DateOnly ParseDate(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
