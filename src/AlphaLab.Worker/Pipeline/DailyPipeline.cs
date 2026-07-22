using System.Globalization;
using AlphaLab.Core.Config;
using AlphaLab.Core.Domain;
using AlphaLab.Core.Funnel;
using AlphaLab.Core.Ledger;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;
using AlphaLab.Evaluation;
using AlphaLab.Evaluation.Allocator;
using AlphaLab.Evaluation.Monitor;
using AlphaLab.Evaluation.Populations;
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
    PopulationsOptions populations,
    GateOptions gate,
    AllocatorOptions allocator,
    ArenaOptions arena,
    WorkerOptions worker,
    TimeProvider clock,
    ILogger<DailyPipeline> logger,
    IEnumerable<IPipelineDayExtension> extensions,
    PipelineEvaluationToggle evaluationToggle)
{
    // The daily fetch window: enough sessions of context that the FR-6 outlier / reconciliation checks
    // have neighbours around the just-closed bar. NOT a config knob (it is an internal fetch bound, not a
    // documented threshold); a stop-and-report seam if the daily EODHD-call budget (2.12) argues for the
    // /eod-bulk-last-day path or a shorter window once the historical gate has run once.
    private const int FetchContextSessions = 40;

    // The finding-115 turnover-match window: the trailing sessions over which a strategy's realized
    // turnover is compared to its matched population's (≈ one month).
    private const int TurnoverWindowSessions = 21;

    private const string RunKindLive = "live";

    /// <summary>
    /// Process one trading day. <paramref name="runKind"/> is the runs.run_kind ('live' or 'catchup' —
    /// both are FORWARD, so the ledger writes RunKind.Live for both, D37). Idempotent per day via the
    /// watermark + ux_runs_ok_forward; a re-run of an already-'ok' forward day is caught by the partial
    /// unique index at Stage-2 commit.
    ///
    /// WATERMARK (D92, finding 194): an explicit <paramref name="watermark"/> wins (the replay /
    /// reproduce seam — a caller that already knows what "now" must mean). Otherwise a 'catchup' day
    /// stamps the TRUE observation instant — the day is being recovered later, and pretending it was
    /// observed at {asOf}T22:00:00Z is exactly the PIT fiction replay must not reason over
    /// (FX-CatchupObservedAt). A 'live' day keeps the session-derived {asOf}T22:00:00Z: the run happens
    /// the same evening, so the stamp is a bounded approximation of the truth, and it is what keeps a
    /// same-day re-fetch a value-diff no-op.
    /// </summary>
    public async Task<DailyRunResult> RunDayAsync(
        string asOf, string runKind = RunKindLive, string? watermark = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);
        var asOfDate = ParseDate(asOf);
        // The D37 collapse: 'live'/'catchup' are both FORWARD (ledger kind Live); 'replay' is Replay.
        // ledgerKind drives every ledger read/write; its token ('live'|'replay') is what the judged
        // artifacts (control_equity, power_reports, overfitting_*, allocation_log) carry.
        var ledgerKind = LedgerMapping.ParseRunKind(runKind);
        var runKindToken = LedgerMapping.RunKindToken(ledgerKind);
        watermark ??= string.Equals(runKind, "catchup", StringComparison.Ordinal)
            ? NowIso()                 // captured ONCE — every write of the day carries the same instant
            : $"{asOf}T22:00:00Z";

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
        var stage2Start = clock.GetTimestamp();
        using (var txn = db.Database.BeginTransaction())
        {
            try
            {
                // POST-COMMIT heartbeat refresh ONLY (v1.9.20 finding NN) — this write sits INSIDE the
                // Stage-2 transaction, so it is invisible to every other connection until commit and lands
                // already stale by the transaction's duration. It is NOT a mid-transaction liveness signal:
                // during a long Stage 2 the HeartbeatService's own-connection beats block on the write lock
                // too, so heartbeat_at effectively freezes at its pre-transaction value. The protections
                // against a false stale-positive are the run-open stamp (step 2's committed txn), the 5×
                // StaleRunThresholdSeconds headroom, and the 3× slow-transaction warning after commit.
                StampHeartbeat();

                IngestStaged(staged, watermark);

                // Regime label (reads the proxy series at the watermark — includes today's just-ingested
                // proxy bar). Fails closed to "no label today" (logged), never aborting the run: the label
                // is not a Phase-2 funnel input (regime-halt guardrails are Phase 7). Per run kind (D93):
                // a replay maintains its own label rows + episode chain.
                var label = regime.ComputeAndSave(asOf, watermark, runKindToken);
                if (!label.Computed) logger.LogInformation("{AsOf}: regime label not computed — {Reason}", asOf, label.Reason);

                // Seed the dummy roster (idempotent): the three baseline/dummy strategies + their
                // accounts under this run kind (a replay opens its OWN accounts, D37).
                new DummyRoster(db, ledger).Seed(asOf, runKind: ledgerKind);

                var features = new BarFeatureView(barReads, calendar, asOfDate, watermark, costs);
                var broker = new VirtualBroker(new CostModel(costs));

                foreach (var account in ledger.GetAccounts(ledgerKind))
                {
                    // Plants (FR-36) are equity-only fixtures: PlantEquityStep computes their day below;
                    // they have no funnel plan, no orders, no book — skipping is by design, not a warning.
                    if (AlphaLab.Evaluation.Calibration.PlantCohorts.IsPlantId(account.StrategyId)) continue;
                    await RunAccountDayAsync(account, ledgerKind, asOfDate, asOf, watermark, features, broker, ct).ConfigureAwait(false);
                }

                // The random control populations (D36) are part of the day: one compact equity row per
                // member, computed against the SAME feature view + cost model as the accounts, inside
                // this atomic transaction, under the run kind's token (a replay population never touches
                // the forward one — run_kind is in control_equity's PK). Batched — one bulk insert, no
                // per-member EF round-trip (§5.2).
                ComputePopulations(asOfDate, asOf, features, runKindToken);

                // Pipeline day extensions (Phase 4/4.5): none registered forward; the replay composition
                // registers PlantEquityStep here, inside the atomic day (a throw rolls the day back).
                foreach (var extension in extensions)
                {
                    extension.AfterPopulations(new PipelineDayContext(asOf, asOfDate, watermark, runKindToken, features));
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

        // ---- Step 5: the 21-day evaluation (D31/D48), AFTER the daily write commits, in its own
        // transaction — the heavier cadence work is amortized and stays out of the <60s daily budget.
        // It runs BEFORE run_in_progress is cleared so the API's 409 liveness guard (D72/rule 19) stays
        // live through this post-commit write — a candidate command must not race the evaluation's INSERTs. ----
        RunEvaluationIfDue(asOf, asOfDate, watermark, runKindToken);

        // ---- Step 4: small txn — clear run_in_progress (success only; AFTER the evaluation write). ----
        ClearRunInProgress();

        // Canary for the sp500 widen + replay scale: a day whose write transaction runs long enough to
        // approach the stale threshold is worth a warning even when it succeeds (D72). 3× the heartbeat
        // interval is well inside the StaleRunThresholdSeconds headroom.
        var stage2Elapsed = clock.GetElapsedTime(stage2Start);
        if (stage2Elapsed > TimeSpan.FromSeconds(worker.HeartbeatSeconds * 3))
        {
            logger.LogWarning(
                "{AsOf}: Stage-2 transaction took {Seconds:F1}s (> 3× the {Heartbeat}s heartbeat) — watch this as the universe/replay scale grows.",
                asOf, stage2Elapsed.TotalSeconds, worker.HeartbeatSeconds);
        }

        logger.LogInformation("{AsOf}: run {RunId} committed ok ({Flags} quality flag(s) persisted).", asOf, runId, staged.FlagCount);
        return new DailyRunResult(true, false, runId, asOf, staged.FlagCount, null);
    }

    // ---- Step 0 helpers: assemble Stage 1's read-only inputs ----

    private Stage1Request BuildStage1Request(DateOnly asOfDate, string asOf, string watermark)
    {
        var from = ResolveFetchFrom(asOfDate);
        var expected = calendar.SessionsBetween(ParseDate(from), asOfDate).Select(Iso).ToList();

        var members = membership.MembersAsOf(asOf);
        var cwProxyId = ResolveConfigLong(CapWeightProxy.ProxySecurityIdConfigKey, watermark);

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

        var regimeProxyId = ResolveConfigLong(RegimeProxyIngestion.ProxyConfigKey, watermark);
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

    // The incremental-fetch cursor — one MAX(date) query (finding 193), never a full-series scan.
    private string? LastStoredDate(long securityId, string asOf, string watermark) =>
        barReads.LastStoredDate(securityId, asOf, watermark);

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
        Account account, RunKind kind, DateOnly asOfDate, string asOf, string watermark,
        BarFeatureView features, VirtualBroker broker, CancellationToken ct)
    {
        var plan = Phase2StrategyRegistry.For(account.StrategyId);
        if (plan is null)
        {
            logger.LogWarning("{AsOf}: account {Id} runs unknown strategy '{Strategy}' — skipped (rule 10).", asOf, account.AccountId, account.StrategyId);
            return;
        }

        // (a) Corporate actions effective since the prior session — the (prev, asOf] window, finding 192,
        // so a weekend/holiday effective date applies on the next session — BEFORE fills/funnel (D53 order).
        var caPrevSession = calendar.PreviousSession(asOfDate);
        caApplier.ApplyForAccount(account.AccountId, kind, asOf, watermark,
            caPrevSession is { } cps ? Iso(cps) : null);

        // (b) Fill the orders decided on the PRIOR session at today's open (the T+1 half of decide-at-close-T).
        FillPriorOrders(account.AccountId, kind, asOfDate, asOf, features, broker);

        // (c) The book post-CA/post-fill, and its equity at today's close.
        var held = ledger.GetPositions(account.AccountId);
        var cash = ComputeCash(account.AccountId, kind);
        var equity = cash + MarkToMarket(held, asOfDate, features);

        // (d) Decide today's orders (unless there is no next session to fill them).
        var fillOn = calendar.NextSession(asOfDate);
        if (fillOn is null)
        {
            logger.LogWarning("{AsOf}: no next session in the calendar — recording equity but deciding no orders.", asOf);
        }
        else
        {
            var universe = ResolveUniverse(plan.Universe, asOf, watermark);
            var inputs = new FunnelInputs
            {
                IndexMembers = universe,
                Held = held,
                Equity = equity,
                Cash = cash, // D84: new opens are sized against cash on hand, never total equity.
                FillOn = fillOn.Value,
                SessionsSinceInception = SessionsSinceInception(account.AccountId, asOfDate, kind),
            };
            var outcome = await FunnelRunner.RunAsync(plan.Model, features, inputs, plan.Guardrails, plan.Sizing, ct).ConfigureAwait(false);
            ledger.RecordDecision(account.AccountId, asOf, outcome.Snapshot.ToJson(), kind);
        }

        // (e) The day's equity point (idempotent per account/as_of/run_kind).
        ledger.RecordEquityPoint(account.AccountId, asOf, equity, cash, kind);

        // (f) The day's END-OF-DAY BOOK (D90). `held` was captured at (c), after corporate actions and
        // after the T+1 fills, and the funnel does not mutate positions — so it IS the book at this
        // session's close. Recording it is what makes the NEXT session reproducible: `positions` is
        // current state that corporate actions rewrite in place (split share counts, merger
        // conversions, spin-off lines) with no reversible trade row, so without this row a past day's
        // pre-trade book is unrecoverable and NFR-1 cannot hold for anything the ledger touches.
        // Inside the Stage-2 transaction, so a rolled-back day writes no snapshot (D59 sole writer).
        ledger.RecordPositionSnapshot(account.AccountId, asOf, held, kind);
    }

    private void FillPriorOrders(long accountId, RunKind kind, DateOnly asOfDate, string asOf, BarFeatureView features, VirtualBroker broker)
    {
        var prevSession = calendar.PreviousSession(asOfDate);
        if (prevSession is null) return;

        var priorJson = ledger.GetDecisionJson(accountId, Iso(prevSession.Value), kind);
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

            switch (OrderFill.Fill(order, mkt, accountId, broker, kind))
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
    // All basis money math is decimal via BasisMath (D69, finding 195) — never a double ratio.
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
                CostBasis = BasisMath.AddBuy(existing?.CostBasis ?? 0m, trade.RawFillPrice, trade.Shares),
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
                CostBasis = BasisMath.ReduceForSale(existing.CostBasis, newShares, existing.Shares),
            });
        }
    }

    // The random control populations (D36 / STRATEGY_CATALOG §5.2). Members are lightweight ledger-only
    // accounts: one compact control_equity scalar per member per day, bulk-inserted. Each member's day is
    // reconstructible from its prior equity + the deterministic (familySeed, memberIndex, date) draws, so
    // no held-set state is persisted. Populations start at the same nominal capital as the dummy accounts.
    private void ComputePopulations(DateOnly asOfDate, string asOf, BarFeatureView features, string runKindToken)
    {
        var costModel = new CostModel(costs);
        var market = new PopulationMarket(features, membership, calendar, costModel, costs.AdvWindowDays);
        var engine = new PopulationEngine(market);
        var familyMap = new PopulationSeeder(db, populations).Seed(costs.ModelVersion);
        var writer = new ControlEquityWriter(db);

        var prevSession = calendar.PreviousSession(asOfDate);
        var prevDate = prevSession is { } p ? Iso(p) : null;

        var points = new List<ControlEquityWriter.Point>();
        foreach (var (family, populationId) in familyMap)
        {
            var prior = writer.LatestEquity(populationId, asOf, runKindToken);
            for (var m = 0; m < family.Size; m++)
            {
                // Inception is decided PER MEMBER, not per family: a member with no prior equity is on its
                // own inception day (an initial buy, no prior return) even if the rest of the family already
                // has history. This is what makes a Size increase safe — a newly-added member (m ≥ the old
                // size) starts fresh instead of being handed a non-null prevDate that fabricates a spurious
                // first-day gross return and turnover against holdings it never had.
                var memberInception = !prior.TryGetValue(m, out var priorEquity);
                if (memberInception) priorEquity = DummyRoster.DefaultStartingCash;
                var day = engine.Step(family, m, priorEquity, memberInception ? null : prevDate, asOf);
                points.Add(new ControlEquityWriter.Point(populationId, m, day.Equity));
            }
        }

        writer.Write(asOf, points, runKindToken);
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

    // The 21-day evaluation cadence (D31). Session count since inception = the committed 'ok' forward runs
    // (today's is committed by now). Run the evaluation step (metrics → MDE → power_reports → gate/monitor/
    // allocator) in its OWN transaction — never inside the daily write. SELF-HEALING (D48): the trigger
    // compares elapsed cadences to evaluations already completed (one overfitting_status date per
    // evaluation), so a cadence whose evaluation crashed is re-driven on the next launch rather than lost.
    private void RunEvaluationIfDue(string asOf, DateOnly asOfDate, string watermark, string runKindToken)
    {
        // The seeding backtest engine (4.10) amputates the judging half: no gate, no monitor, no
        // allocator — the IBacktestEngine never judges promotions (its FX test pins zero such rows).
        if (!evaluationToggle.Enabled) return;

        // Nothing promotable yet ⇒ no evaluation to run; do NOT treat the boundary as missed (that would
        // re-fire every launch, writing nothing). A candidate/live strategy must exist first.
        if (!db.Strategies.Any(s => s.Status == "candidate" || s.Status == "live")) return;

        // The cadence counts sessions of ITS OWN kind: forward = the committed forward runs; replay =
        // the committed replay runs of the current generation (D95 — a reset clears them with the rest).
        var sessionsSinceInception = runKindToken == RunKindLive
            ? db.Runs.Count(r => r.Status == "ok" && (r.RunKind == "live" || r.RunKind == "catchup"))
            : db.Runs.Count(r => r.Status == "ok" && r.RunKind == runKindToken);
        var evaluationsCompleted = db.OverfittingStatus
            .Where(o => o.RunKind == runKindToken)
            .Select(o => o.AsOf)
            .Distinct()
            .Count();
        if (!new EvaluationScheduler(gate).IsEvaluationDue(sessionsSinceInception, evaluationsCompleted)) return;

        try
        {
            using var txn = db.Database.BeginTransaction();
            var evaluations = new EvaluationStep(db, gate).Run(asOf, runKind: runKindToken);
            // The overfitting monitor (S2/S3/S6) runs in the same evaluation transaction. Phase-3: all
            // promotable strategies are matched to the daily cost-on population (the default null).
            var matchedPopulation = db.ControlPopulations
                .Where(p => p.Family == "daily" && p.CostsOn)
                .Select(p => (long?)p.PopulationId)
                .FirstOrDefault();
            var monitored = new OverfittingMonitor(db, gate).Run(asOf, EvaluationStep.DefaultBenchmarkStrategyId, matchedPopulation, runKindToken, watermark);

            // Turnover-match verification (finding 115): re-simulate the daily population's turnover vs each
            // strategy's trades over the recent window, and persist the status-neutral caveat rows.
            var windowDates = LastSessions(asOfDate, TurnoverWindowSessions);   // last 21 sessions
            if (windowDates.Count >= 2)
            {
                var features = new BarFeatureView(barReads, calendar, asOfDate, watermark, costs);
                var market = new PopulationMarket(features, membership, calendar, new CostModel(costs), costs.AdvWindowDays);
                var dailyFamily = PopulationFamilies.ForPhase3(populations).First(f => f is { Name: "daily", CostsOn: true });
                new TurnoverMatchStep(db, populations.TurnoverMatchTolerancePct)
                    .Run(asOf, windowDates, new PopulationEngine(market), dailyFamily, EvaluationStep.DefaultBenchmarkStrategyId, runKindToken);
            }

            // The ensemble allocator (D51) reads the gate + monitor outputs and persists allocation_log.
            var allocation = new AllocationStep(db, gate, allocator).Run(asOf, runKindToken);
            txn.Commit();

            logger.LogInformation("{AsOf}: evaluation (session {Session}) — {Pairs} pair(s) scored, {Monitored} monitored, {Allocated} allocated.",
                asOf, sessionsSinceInception, evaluations.Count, monitored.Count, allocation.Rows.Count);
        }
        catch (Exception ex)
        {
            // The trading day is already committed 'ok'. The evaluation runs post-commit in its own
            // transaction (kept out of the <60s daily budget), so a failure rolls back ONLY the evaluation
            // and MUST NOT fail the daily run. The self-healing trigger re-drives it on the next launch.
            logger.LogError(ex, "{AsOf}: post-commit evaluation failed and was rolled back; it will be retried on the next launch.", asOf);
        }
    }

    // The last <paramref name="n"/> sessions ending at (and including) asOf, oldest first — the turnover
    // window for the finding-115 match.
    private IReadOnlyList<string> LastSessions(DateOnly asOf, int n)
    {
        var list = new List<string>(n);
        var cursor = calendar.IsTradingDay(asOf) ? asOf : calendar.PreviousSession(asOf);
        for (var i = 0; i < n && cursor is { } c; i++)
        {
            list.Add(Iso(c));
            cursor = calendar.PreviousSession(c);
        }
        list.Reverse();
        return list;
    }

    // ---- ledger math ----

    // Cash reconciles from events + fills (there is no stored balance column): deposits/dividends/merger
    // cash are cash_events; buys/sells (with costs) are trade CashDeltas.
    private decimal ComputeCash(long accountId, RunKind kind) =>
        ledger.GetCashEvents(accountId, kind).Sum(e => e.Amount)
        + ledger.GetTrades(accountId, kind).Sum(t => t.CashDelta);

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

    private int SessionsSinceInception(long accountId, DateOnly asOf, RunKind kind)
    {
        var opening = ledger.GetCashEvents(accountId, kind)
            .Where(e => e.Type == CashEventType.Deposit)
            .Select(e => e.AsOf)
            .FirstOrDefault();
        if (opening is null) return 0;
        return Math.Max(0, calendar.SessionsBetween(ParseDate(opening), asOf).Count - 1);
    }

    private IReadOnlyList<SecurityId> ResolveUniverse(UniverseScope scope, string asOf, string watermark)
    {
        if (scope == UniverseScope.CapWeightProxy)
        {
            var cw = ResolveConfigLong(CapWeightProxy.ProxySecurityIdConfigKey, watermark);
            if (cw is { } id) return [new SecurityId(id)];
            logger.LogWarning("Cap-weight proxy security_id is unresolved ('{Key}' has no config row) — the CW benchmark holds cash this run.", CapWeightProxy.ProxySecurityIdConfigKey);
            return [];
        }
        return membership.MembersAsOf(asOf).Select(id => new SecurityId(id)).ToList();
    }

    // ---- config + timestamps ----

    // Run-scoped config reads resolve AS-OF the run's watermark (D96, resolving P14a): a config row
    // appended after this session committed is invisible to a re-run of it — which is what keeps
    // reproduce-day and replay config-faithful once the Phase-4 calibration starts writing rows.
    private long? ResolveConfigLong(string key, string watermark) =>
        new ConfigReadService(db).ResolveLongAsOf(key, watermark);

    private WorkerStateRow WorkerStateRow() =>
        db.WorkerState.First(w => w.Id == 1);

    // Advance heartbeat_at on the CURRENT (Stage-2) context/transaction — visible only AT commit, and by
    // then already stale by the transaction's duration (finding NN: a refresh, not a liveness signal). No
    // SaveChanges here — the change is tracked and committed with the rest of the day (a rollback correctly
    // discards it; the OpenRun stamp from step 2's committed txn survives).
    private void StampHeartbeat() => WorkerStateRow().HeartbeatAt = NowIso();

    private string NowIso() => clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static DateOnly ParseDate(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
