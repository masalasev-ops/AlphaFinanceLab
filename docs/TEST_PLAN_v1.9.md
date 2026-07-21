# TEST_PLAN_v1.9 — fixture library & FR-mapped test inventory

*Claude Code: test names cite FRs (`FR10_ParticipationCap_RejectsAndLogs`). Fixtures live under `tests/Fixtures/` as deterministic builders (code, not CSVs, unless noted). This file is the inventory; the per-strategy acceptance details live in STRATEGY_CATALOG_v1.9 §13 and each strategy's section.*

> **v1.9.7 errata note (findings 108–118).** New fixtures/tests merged below: `FX-RegimeProxyBackfill` (FR-38/D73, §2); `FX-CrashedRun` + `FX-JobDrain` (FR-34/D72, §3); `FX-TurnoverMatch` (FR-12, §4); edge-plant-survival + joint-false-alarm assertions inside `FX-Replay15y` (FR-19, §5); and Phase-0 tests `R1_SchemaStartup_EnablesWal` + the `config` composite-PK fidelity assertions (§8).

## 1. Standing test fragments (apply to every relevant PR)
- **F-LEAK** — leakage: compute scores/labels at `asOf` under two watermarks (with/without future rows); assert byte-identical. Required for every new feature, label, and strategy.
- **F-DET** — determinism: run twice with identical inputs+watermark+seeds; assert identical trades/equity/monitor rows (NFR-1).
- **F-QUAR** — quarantine: insert `run_kind='replay'` rows; assert every forward view returns them zero times (FR-19).
- **F-CLOSED** — fail-closed: remove a required input; assert order rejected with logged reason, never defaulted (FR-11).

## 2. Fixture library — data layer (Phase 1)
| Fixture | Contents | Exercises |
|---|---|---|
| `FX-TickerChange` | ACME→ACMX on day 40 of an 80-day series, position held through | FR-3: zero identity break, zero churn, continuous history joins |
| `FX-BarCorrection` | v1 bar day 10; v2 correction arrives day 15 | FR-2: run pinned to day-12 watermark reproduces byte-identically; day-16 run sees v2 |
| `FX-MembershipDiverge` | Primary source says +NEWCO, cross-check doesn't (and count 512) — the pair is config-driven: IVV vs Wikipedia at launch (D49), EODHD vs IVV post-upgrade | FR-4: state held, alert raised, log row written |
| `FX-MembershipAgree` | matching add/drop | FR-4: diff applied, dates stamped, nothing deleted |
| `FX-QualityGate` | gap day, NaN close, 12σ outlier, dividend implied by adj/raw ratio but missing from event feed | FR-6: each flagged; reconciliation alarm |
| `FX-SectorReclass` | sector change mid-period | sector_changes logged; LowVol applies at next rebalance only |
| `FX-RegimeHysteresis` | proxy oscillates ±0.5% around its 200d SMA for 30 sessions | FR-26/D50: zero trend flips; a sustained +1.2% × 5-session move flips exactly once |
| `FX-RegimeProxyBackfill` | proxy series with < 3.8y of history at asOf; sibling with full warm-up | FR-38/D73 (v1.9.7): the label computation fails closed (refuses + logs) below warm-up — never a fabricated label; computes normally with full history; proxy cross-check vs SPY.US returns raises the tolerance alarm on a divergent sample |
| `FX-HolidayOutage` | outage spanning a holiday weekend | FR-30/D54: catch-up recovers only real sessions; no fabricated trading day |
| `FX-HalfDay` | early-close session (13:00 ET) | FR-30/D54: orchestrator trigger keys off the session's close time |

## 3. Fixture library — ledger & funnel (Phase 2)
| Fixture | Contents | Exercises |
|---|---|---|
| `FX-Dividend` | held name, ex-date mid-window | FR-9/D30: cash on ex-date; B&H total-return acceptance |
| `FX-Split` | 4:1 split while held | shares ×4, raw basis ÷4, equity unchanged through the event |
| `FX-MergerCash` | held target, $54.20/share effective day 30 | position closed at deal cash, costs waived, action_id stamped |
| `FX-MergerStock` | 0.85 exchange ratio into acquirer | shares converted across security_ids, basis carried |
| `FX-MergerMixed` | $10 cash + 0.5 shares | both legs, one action |
| `FX-Spinoff` | 1:5 spin-off, ratio in feed; sibling fixture with ratio missing | new position with basis allocation; missing-ratio → first-print allocation path |
| `FX-Delist` | terminal delist, last print day 62; bankruptcy variant | force-exit at last print; haircut config applied |
| `FX-Unmapped` | bars stop, no action in feed | position frozen + flagged + alert; valuation pinned at cost basis (fail closed, D86) |
| `FX-ZeroScore` | day where only 2 names score > 0, N=40 | Stage-3 invariant: 2 positions + cash, no padding |
| `FX-ExitOnly` | name falls off wish list but ExitPolicy says hold | no implicit sell |
| `FX-Outage5d` | 5 missed trading days incl. a dividend, a membership drop, a bar correction | FR-7: ordered recovery, one txn/day, idempotent re-run is a no-op, catchup_log complete |
| `FX-CostModel` | one order per liquidity bucket + one at 3% ADV | FR-10: spread bucket applied; impact = k·σ·√(Q/ADV) to 1e-9; participation excess rejected + capacity_rejections row; cost_model_version stamped |
| `FX-StagedPipeline` | provider hard-fails mid Stage 1 | FR-29/D53: zero DB writes; next-day catch-up recovers; a mocked batch result lands post-commit in its own transaction |
| `FX-CrashedRun` | `worker_state.run_in_progress=1` with a heartbeat older than `Worker.StaleRunThresholdSeconds`; no live Worker | FR-34/D72 (v1.9.7 finding 112): the next launch clears the flag, marks the orphaned `runs` row `failed`, logs the recovery, and proceeds with catch-up; the Api's 409 decision ignores the stale flag |
| `FX-JobDrain` | a job enqueued via the Api while no Worker is resident | FR-34/D72 (v1.9.7 finding 111): the next OnDemand launch executes it **after** catch-up and before exit; the job transitions queued→running→done and never runs inside a daily write transaction |

## 4. Fixture library — arena & statistics (Phase 3)
| Fixture | Contents | Exercises |
|---|---|---|
| `FX-PopDeterminism` | population run twice, same seeds/watermark | FR-12: identical member equities |
| `FX-PopBands` | 200-member banded population over 2 synthetic years | gross alpha band centered ~0 (|mean| < tolerance); net band offset ≈ −modeled cost drag |
| `FX-SyntheticEdge` | planted 3%/yr edge strategy vs its matched population | lands > 95th percentile within the calibration horizon |
| `FX-SyntheticNoEdge` | skill-free strategy with matched mechanics | percentile ~Uniform over time (KS test); never crosses S3 healthy sustained |
| `FX-MDE-AR1` | AR(1) φ=0.3 difference series | D48: NW-corrected MDE > iid MDE; regression test pinning the ratio |
| `FX-TooEarly` | true gap 1.5%/yr, 6 months of data | gate returns TooEarly; no promotion; power_reports row correct |
| `FX-PairedWin` | planted 12%/yr gap, monitor clean, 1 year | gate promotes; go_live_log evidence complete; revert path on injected regression |
| `FX-TradeTrack` | 300 synthetic trades with regime clustering | FR-15: block-bootstrap CI wider than naive iid CI; trade MDE rendered |
| `FR27_AllocatorSuite` | 4-strategy roster with mixed track lengths/statuses | FR-27/D51: short-track ⇒ ~equal weight; Suspect decays; TooEarly cap binds; band blocks sub-threshold moves; weights reconstruct from allocation_log |
| `FX-TurnoverMatch` | a rank-hysteresis momentum dummy (low churn) vs a banded population; a churn-matched sibling | FR-12 (v1.9.7 finding 115): realized annualized turnover persisted per strategy + population; the mismatched dummy renders the cost-match caveat in its `StrategyRow` read-model and on the S3 panel; the matched sibling does not |

## 5. Fixture library — replay & monitor (Phase 4+)
| Fixture | Contents | Exercises |
|---|---|---|
| `FX-Replay15y` | 15-year historical window, populations + dummies + the three D64 plants (edge / no-edge / anti-predictive; ≥50 seeds each) | FR-19/FR-36 validation suite: promotions ≤ chance (binomial bound); edge plant detected; anti-predictive plant reaches Suspect/auto-retire (detection-speed KPI recorded, D63); no-edge plant stays mid-band, breaches P_noise(t) only at the false-alarm rate, and earns `IndistinguishableFromRandom` (days-to-statement KPI recorded, D63); **(v1.9.7 findings 113–114) edge-plant survival at 5y/10y ≥ `Replay.EdgePlantSurvivalFloor5y`, every edge-plant auto-retire logged with its trigger; joint any-signal false-alarm fraction for no-edge plants ≤ `Replay.JointFalseAlarmMaxFrac`, per-signal contribution archived** |
| `FX-ReplayQuarantine` | replay run beside live rows | F-QUAR at scale; no co-plot (view-layer test) |
| `FX-AsOfMembership` | community-CSV historical membership spanning 3 known index changes | FR-4/FR-19/D70: a replay day resolves the correct as-of S&P 500 universe; the S&P 100 slice is never used as replay membership |
| `FX-Calibration` | threshold sweep in replay | calibration report generated + archived; chosen values written as versioned config rows |
| `FX-MonitorSignals` | per-signal synthetic triggers (S1 degradation, S2 deflation gap, S4 spike-vs-plateau scans, S5 PSI shift, S6 decay-into-band, S7 Brier drift, S8 divergence pair) | each signal's elevated/critical transitions + status wiring (Suspect vetoes a P&L-winning promotion) |
| `FX-AutoRetire` | 4 consecutive S6-suspect evaluations | strategy retired to observation-only; leaves promotable pool; account keeps running |
| `FX-S3Trajectory` | D64 plants (edge / no-edge / anti-predictive) under calibrated curves | D56/D63: the edge plant stays ≥ Warning at every horizon (where a flat 95th cut would flag it); the **anti-predictive** plant falls below P_noise(t) and goes Suspect; the **no-edge** plant stays mid-band — never Suspect beyond the false-alarm rate — and renders the D63 chip |
| `FX-PlantRealism` | realistic (regime-conditional, autocorrelated) vs naive constant-drift edge plants, same annualized target, same seeds count | FR-36/D64: the realistic plant's P_edge(t) sits materially below the naive plant's at t = 252d (a lumpy edge separates later); the divergence chart lands as a permanent calibration-report section; realistic curves adopted when divergence > `SensitivityMaxGapPts` |
| `FX-SeparationChip` | no-edge plant + edge plant tracked past `Verdicts.SeparationMinTrackDays` | FR-35/D63: no-edge renders `separation_state='none'` + the IndistinguishableFromRandom chip with its day count at the threshold; the edge plant's median path transitions none → emerging → distinguishable; state reconstructs from persisted percentile rows (NFR-2) |
| `FX-CohortCurve` | two forward admission cohorts born months apart (one containing a `status='retired'` member), a 2-member thin cohort, a replay plant cohort, a cohort-to-cohort median gap inside its own NW-MDE | FR-39/D88: (a) cohorts compare at equal track length t (age-aligned, never wall-clock); (b) the retired strategy stays in its cohort and keeps contributing its percentile path (no survivorship); (c) the replay cohort ships `quarantined:true` and never co-plots with forward cohorts; (d) the 2-member cohort renders `display='dimmed'`, `reason='thin_cohort'` under the default `Kpi.CohortMinStrategies`=3; (e) the sub-MDE gap renders dimmed, never as a claimed improvement |
| `FX-DetectionPower` | the C-1 sweep: the edge plant across ~3 alpha levels (e.g. 2/4/8% ann), same seeds/plant | FR-40/D89: the empirical P(promoted by t given alpha) curves + median days-to-promotion reproduce the analytic NW-MDE end-to-end; curves archived in `docs/calibration/` |
| `FX-DetectabilityGate-Refuses` | a pre-registered candidate whose `expected_effect_ann`, net of its trials-budget cost, cannot clear the NW-MDE within `Gate.DetectabilityHorizonYears` | FR-40/D89: `CandidateFactory` refuses admission with the detectability reason, a new create-path outcome beside 422/409; no strategy row is created |
| `FX-DetectabilityGate-Admits` | a pre-registered candidate whose `expected_effect_ann` clears the same floor | FR-40/D89: `CandidateFactory` admits it (the gate is not a blanket block); an `unregistered` candidate with no `expected_effect_ann` also admits under its permanent marking |
| `FX-CatchupObservedAt` | a multi-day catch-up recovering sessions observed days after the fact | finding 194 (Phase-4 prerequisite): each recovered day is stamped with the true `observed_at`, never the session-derived `{as_of}T22:00:00Z` fiction, so replay reasons over honest observation dates |
| `FX-ReplayPerRegime` | a replay run spanning several `regime_episodes`, persisted to `replay_regime_outcomes` | FR-41/D89: per-regime rows reconstruct and, aggregated, match the overall replay outcome; every row is `run_kind='replay'` and no per-regime row reaches a forward view (quarantine) |
| `FX-ReplayPartition-NoLeak` | replay history partitioned into learn and validate periods at a runtime boundary | FR-42/D89 (extends the permanent F-LEAK suite): no validate-period datum reaches any learn-period computation |

## 6. LLM layer — news read + the AI seats (Phase 5; D79-D82, spec MASTER §23)

*Daily market-level news read (D46):*
- `FR22_NewsBudget_CapsAndDedupes` — 80 raw articles in ⇒ ≤25 admitted, duplicates collapsed by title hash, each ≤2,000 chars, all pre-token.
- `FR21_CacheHit_CostsZero` — second read same (prompt_hash, model, date) spends nothing.
- `FR21_Replay_HasNoAnalysisPath` — replay runs produce zero analysis_cache rows by construction (compile-time absence preferred: the replay composition root has no IAnalysisProvider registration).
- `FR22_Budget_DegradesInOrder` — over-budget day: held names served, then cached, then neutral fallback; never a blackout; llm_budget_log.degraded = 1.
- Mocked provider for CI; one live smoke test gated by an xUnit `[Trait("Category","LiveSmoke")]` (excluded from the default run via `--filter`) — not an env flag (D67).

*The AI seats (D79-D82; every seat priced by the arena, golden rule 32):*
- `FX-PackWatermark` — a context pack built at watermark W is byte-identical regardless of later bars/actions; any post-watermark fact fails construction (D80; the AI analog of NFR-1).
- `FX-AiDecisionIsTheRow` — a re-run consumes the stored `ai_decisions` row; the provider seam proves **zero API calls** on re-run (D81 determinism; replay stays LLM-free).
- `FX-ContestantReplayRefused` — an `IArenaReplay` run refuses to admit an AI contestant seat by construction (D16/D81 forward-only; compile-time absence preferred over a runtime guard).
- `FX-TwinPairing` — the contestant and its mechanics-identical no-LLM twin share pre-filter, breadth, sizing, exits, costs, and seed; the paired daily difference is the only promotable signal, and the twin is never promoted alone (M.1/D81). **Given identical Stage-2 scores, the pair's Stages 3–6 outputs are byte-identical** — the pair differs at Stage 2 only (D85).
- `D85_TwinBlend_MatchesHandComputedZScoreAverage` — 3 names, 2 pack features, known values ⇒ the twin's per-name scores equal the equal-weight standardized (cross-sectional z-score) average, hand-computed (D85).
- `D85_TwinBlend_ZeroVarianceFeatureDropped_NoNaN` — a feature identical across the shortlist contributes 0 (dropped, step 1); the day is still scored on the surviving feature(s); no NaN leaks into selection.
- `D85_TwinBlend_AllFeaturesFlat_EqualFallback_Flagged` — a multi-name day where **every** feature is flat collapses to equal scores across the shortlist + a `degenerate_blend` flag (step-2 all-flat branch), never NaN.
- `D85_TwinBlend_SingleNameDay_EqualFallback_Flagged` — a <2-name day ⇒ equal scores + the same `degenerate_blend` flag, never a divide-by-zero (step-2 <2-name branch).
- `D85_TwinFrozenInConfig_ChangeForks` — altering the twin's scoring rule increments `trials_registry` and forks a new candidate like any frozen-policy change (rule 24/32; the twin rule is a `config_json` frozen param per §23.3 rule 2).
- `FX-BudgetAbstain` — an exhausted per-seat budget yields an empty score map ⇒ a sparse wish list (the funnel's honest "nothing scored today"), never a stale, cached, or padded decision (D24/§23.2).
- `FR23_Hypotheses_RequireParentEvidence` — `POST /api/v1/analysis/hypotheses` with no cited parent evidence ⇒ 422; an accepted proposal lands as an **unlocked** draft `journal_entries` row; only the operator's lock registers it (rule 30/D82); the fork budget decrements and renders beside the deflated-Sharpe trials count.
- Mocked model provider for all of the above in CI; the AI-seat live smoke test reuses the same `[Trait("Category","LiveSmoke")]` gate.

## 7. Strategy acceptance (Phase 6/8)
Inventory = STRATEGY_CATALOG_v1.9 §13 verbatim, plus per-strategy sections (§5–§8). Every strategy PR ships: its acceptance tests, F-LEAK, F-DET, its population hookup test, and its trials_registry row test.

## 8. GUI & ops
- `NFR3_EmptyDb_AllScreensRender` — every Blazor page renders against a fresh DB (and teaches while empty, UX-8c).
- `UX8_ReplayNeverCoplotted` — no chart component accepts both forward and replay series in one plot.
- `FR24_MDE_RenderedBesideEveryGap` — view-model test: no comparison DTO without an MDE field.
- `FR24_RegimeClaims_CarryEpisodeCount` — n<3 ⇒ anecdote badge (D45).
- `FR25_RestoreDrill` — scripted: snapshot → mutate → restore → verify (rehearsed per RUNBOOK §4, logged in PROGRESS.md).
- `FR28_Fork_RequiresHypothesisOrFlag` — CandidateFactory refuses creation without a linked hypothesis or an explicit `unregistered` flag; a locked hypothesis rejects edits outside outcome closure (D52).
- `FR31_ManualAction_AuditedAndScoped` — an invalid manual ratio is rejected; a valid one writes the domain row (`source='manual'`) + `admin_actions` row and unfreezes exactly the affected position (D55).
- `UX9_ClampShown_WhenBinding` — an allocation row whose target was bound by a clamp renders that clamp's chip on the derivation arrow (D51/UX-9).
- `FR32_ReadEndpoints_ReturnReadModelShape` — every `/api/v1/*` read endpoint returns the declared D58 read-model shape; `POST /api/v1/candidates` without a hypothesis-or-`unregistered` flag is rejected; `POST /api/v1/admin/actions` without a matching confirmation token is rejected (D57).
- `FR33_ForwardReadModel_ContainsNoReplayRow` — a forward-screen read-model built while replay rows exist provably excludes them (D58/UX-8).
- `UX1_InsideMde_MetricCell_IsDimmedWithTilde` — a MetricCell whose head-to-head gap is inside the MDE serializes with `display:"dimmed", prefix:"~", reason:"inside_mde"` (D58/UX-1). *(Framework-agnostic — replaces the old Blazor view-model test.)*
- `UX15_CohortCurve_ThinAndSubMdeDimmed_ReplayNeverCoplotted` (v1.9.34) - the `CohortMaturationReadModel` dims a thin-cohort segment and a sub-MDE cohort gap with their reasons, keeps a retired member in its cohort, and ships a replay cohort `quarantined:true` so no forward co-plot is possible (D88/UX-15; fixture `FX-CohortCurve`, §5).

- `FR34_NoOverlappingWriters` — a command issued while `worker_state.run_in_progress=1` is queued or returns 409, never racing the daily write transaction (D59).
- `FR34_LabRunsWithoutApi` — the Worker completes a daily run with the AlphaLab.Api process stopped (proves the lab advances without any UI/API up).
- `FR34_OnDemand_ReplaysGapThenExits` — after an N-day off period, an OnDemand launch replays exactly the missed *completed* sessions in order and exits (D61).
- `FR34_OnDemand_SameEveningNoop` — a second OnDemand launch the same evening does no work (idempotent, D61).
- `FR34_OnDemand_NoRunBeforeClose` — a launch before today's session close processes only through the prior completed session, never a half-day (D61).
- `FR32_LongRunningCommand_Returns202Job` — `POST /api/v1/replay` and `POST /api/v1/analysis/*` return 202+job_id and stream progress; they never block the request thread (D60).
- `FR32_ErrorEnvelope_And_Money` — validation/domain/budget failures return the D60 `{error:{code,message}}` envelope with the right status (422/409/503); money fields serialize as strings/minor-units, not floats (D60).
- `FR37_ArenaNamespacedDbPath` — two configs differing only in `Arena.Id` resolve distinct absolute DB paths under `<DbBase>\{Arena.Id}\` (the built test resolves `sp500` **and** `sp100` and `Assert.NotEqual`s the two data-source paths — P0-4; the assertion is base-agnostic — it owns the arena segment + token resolution, never the base, so relocating the DB per DB_RELOCATION.md cannot redden it). **Erratum (P0-4, v1.9.8):** the earlier "the bare EF design-time factory (no config) defaults to the `sp500` path" clause is intentionally **not** tested as written — `CreateDbContext([])` resolves as a writer (`ensureDirectory:true`) and would create `E:\AlphaLabDatabase\sp500\`, violating this section's "passes with no `E:` drive" guarantee; the pure `FR37_DefaultConnectionString_HasArenaTokenAndDataSource` substitutes for it (D71).
- `FR37_ArenaRegistry_DrivesClientBaseUrl` — the Web client's `ReadModelClient` targets the active arena's registry `baseUrl`; with one entry the first entry is active by default (D71). A companion `FromEntries_Empty_IsFlaggedFallback_ForFailClosedBanner` asserts a missing `Arenas` registry sets `IsFallback` (P0-6) — the flag drives the layout's config-error banner instead of a silent self-call (hard rule 10).
- `DbPathResolver_ResolvePath_IsPure_NoDirCreate` (v1.9.6) — `ResolvePath` resolves `{Arena.Id}`/`{LocalAppData}` tokens against a `Path.GetTempPath()`-rooted connection string with ZERO filesystem side effects (two arenas → distinct non-colliding paths); a companion asserts the side-effecting `Resolve` DOES create the directory (temp path, cleaned up). Guarantees the suite never touches the real DB base (portability — passes with no `E:` drive).
- `SchemaStartup_AppliesInitialCreate` (v1.9.6) — the shared `SchemaStartup` hosted service, run against a temp SQLite file, creates the five infra tables; proves both Worker modes migrate identically (Scheduled runs it before Quartz idles).
- `R1_SchemaStartup_EnablesWal` (v1.9.7 finding 118) — after `SchemaStartup.StartAsync` against a temp file, `PRAGMA journal_mode` on a fresh connection returns `wal`; a non-`wal` result is a startup failure (same non-zero-exit contract as a failed migration).
- `Schema_Config_CompositePk_TwoVersionsInsertable` (v1.9.7 finding 108) — the on-disk `config` DDL carries `PRIMARY KEY (key, version)`; inserting (k,1) then (k,2) both persist; the read rule (MAX(version) per key) resolves to v2.
- `FR32_ReadModel_StampedRunWatermark` — every read endpoint's payload carries the D66 `ReadModelStamp` (`status:"no_run_yet"|"stamped"`; run_id/watermark/as_of non-null iff stamped; stamped and stable after the first committed run) (D60/D66/NFR-1).

**Phase-0 tests added in the v1.9.7 parity pass. With the entries above, this makes §8 the CANONICAL Phase-0 test inventory — 39 executed cases a from-scratch build must reproduce, split `AlphaLab.Core.Tests` 5 · `AlphaLab.Data.Tests` 17 · `AlphaLab.Worker.Tests` 8 · `AlphaLab.Api.Tests` 6 · one placeholder each in `AlphaLab.{Strategies,Evaluation,Llm}.Tests` (3):**
- `Schema_IntegerPrimaryKeys_HaveNoAutoincrement` + `Schema_IntegerPrimaryKey_StillAutoAssignsOnInsert` — `runs`/`jobs` are plain `INTEGER PRIMARY KEY` (no `AUTOINCREMENT`, no `sqlite_sequence`) yet still auto-assign rowids (rule 14; SCHEMA enforcement notes).
- `Schema_ExactlyTheFiveInfraTables_Exist`, `Schema_TheFourCheckConstraints_ArePresent_AndNoneOnConfigOrCatchup`, `Schema_Defaults_ArePresent`, `WorkerState_IsSeededWithASingleRowIdOne`, `Runs_Status_IsUnconstrained_OnlyRunKindHasACheck` — the infra DDL matches SCHEMA exactly (five tables, four CHECKs, defaults, seeded singleton, no CHECK on `runs.status`).
- `Config_ConnectionString_EqualsResolverDefault` (Api + Worker) — the three connection-string spots agree **at Phase 0** — Api + Worker appsettings + the resolver const (`ConfigConsistencyTests`; DB_RELOCATION.md §2 owns the full rule — the Backfill CLI adds a **fourth** spot + assertion in Phase 1 / checkpoint 1.10).
- `FR37_DefaultConnectionString_HasArenaTokenAndDataSource` — the resolver default carries `{Arena.Id}` + a `Data Source`.
- `WorkerModeParserTests` (5, incl. `ServeFlag_OverridesConfigOnDemand`) — `--serve`/`Worker:Mode` resolution (D61).
- `NoCalendar_NoRuns_ResolvesToNothingToDo` — the Phase-0 `IMissedSessionResolver` reports nothing to catch up (D47/D61).
- `UnknownRoute_ReturnsD60ErrorEnvelope_404` — unmatched routes return the D60 envelope (`code:"not_found"`), not a framework 404.
- `Replay_ReadModel_IsAlwaysQuarantined` — `/api/v1/replay` ships `quarantined:true` (D37/D58).

**Note (D58):** the testable UX rules are now **read-model unit tests in `AlphaLab.Evaluation.Tests`**, not browser/Blazor tests — they hold for any UI client. `AlphaLab.Api.Tests` covers endpoint shape, the D60 conventions (envelope, 202-jobs, money, run/watermark stamp), and command guards; `AlphaLab.Worker.Tests` covers the single-writer and runs-without-API guarantees (D59).

## 9. CI composition
`dotnet test` (all above) + greps (`DELETE FROM bars`, `UPDATE bars`, key patterns) + the leakage suite as a named category that can never be skipped. A red leakage or quarantine test blocks merge regardless of everything else.
