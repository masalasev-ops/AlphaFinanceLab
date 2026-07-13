# PROGRESS.md — the build's honest ledger (design revision v1.9)

*Repo root. Updated every working session — what shipped, what's red, what was deliberately deferred, and any decision proposals. This file is the month-one discipline instrument (MASTER §17.1): if it stops being truthful, the phase gates stop working.*

## Current state
- **Phase:** 0 COMPLETE (skeleton) through the v1.9.7 gates; ready for Phase 1.
- **Blocking:** none.
- **Last session:** 2026-07-13 — Phase 0 complete: 8 src + 7 test projects, infra-only schema (WAL + composite `config` PK + plain `INTEGER PRIMARY KEY`), Worker/Api/Web wired end-to-end; `tools/ci.ps1` green (build + **39 tests** + guard greps). See the session-log entry.

## Phase gates (a phase is DONE only when every box is checked and committed)

### Phase 0 — Skeleton
- [x] Solution layout per CLAUDE.md incl. AlphaLab.Api (D57); CI green (build + tests + greps + AlphaLab.Web-isolation grep)
- [x] AlphaLab.Worker: OnDemand launch catches up (nothing yet) and exits cleanly; --serve idles (sole writer, D59/D61)
- [x] AlphaLab.Api boots and serves OpenAPI (/openapi/v1.json) + Scalar UI (/scalar/v1), localhost; error-envelope middleware; dev CORS for the AlphaLab.Web origin (Api:CorsAllowedOrigins); stub read endpoints return empty read-models with ReadModelStamp status=no_run_yet (D66)
- [x] ConnectionStrings resolved via the shared DbPathResolver ({Arena.Id} token from the Arena config block, FR-37/D71, + {LocalAppData} token via Environment.GetFolderPath still supported — no env-var reads, D67 — + directory create); Worker, Api, and the EF design-time factory all open the SAME arena-namespaced file (this deployment: `E:\AlphaLabDatabase\sp500\alphalab.db`); bare `dotnet ef` defaults to sp500
- [x] Arena identity wired (FR-37/D71): Arena.Id=sp500 in config; logs tagged arena=sp500; AlphaLab.Web loads the one-entry Arenas registry and targets its baseUrl; FR37 tests green
- [x] EF infra-only InitialCreate (runs/catchup_log/config/worker_state/jobs — D59/D60; worker_state row seeded) + snapshot script working
- [x] Empty-DB Blazor client renders every screen by calling AlphaLab.Api (NFR-3)
- [x] appsettings.Secrets.json gitignored & untracked; no key patterns in committed files (D67)
- [x] v1.9.7 fix-up (finding 118): `SchemaStartup` executes `PRAGMA journal_mode=WAL` post-migrate, verifies `wal`, fails startup otherwise; `R1_SchemaStartup_EnablesWal` green
- [x] v1.9.7 fix-up (finding 108): `config` PK is composite `(key, version)` in `InitialCreate` (built fresh, no hand-edit — `Version` is `ValueGeneratedNever()`); two versions of one key insertable, duplicate `(key,version)` rejected by the store (`SchemaFidelityTests`)
- [x] v1.9.7 fix-up (finding 119): `tools/migrate.ps1` resolves the DB path from the Worker appsettings and passes `--connection` — snapshots and migrates the same file (verified: snapshot written, update idempotent, exit 0)
- [x] v1.9.7 fix-up (finding 121): index.html title `AlphaLab`; layout tutorial link removed (header renders arena DisplayName); ScreenCatalog `EmptyHint` per screen + day-one home note (UX-8c); Api comments say "reader plus bounded Phase-3 command writes (D59)"; `.gitignore` DB comment present

### Phase 1 — Data foundation
- [ ] Security master + ticker_history; FX-TickerChange green
- [ ] EODHD bars backfill (S&P 100) + daily delta; versioned bars; FX-BarCorrection green
- [ ] Membership per D49 launch wiring (IVV primary + Wikipedia cross-check; EODHD provider built but dormant); FX-MembershipDiverge/Agree green
- [ ] D70 S&P 100 slice sourced (OEF CSV + Wikipedia S&P 100 cross-check, count sanity 99–103); fja05680 community CSV ingested into historical membership; FX-AsOfMembership green
- [ ] Sector ingestion + change log; quality gate + FX-QualityGate green
- [ ] Trading calendar seeded + ICalendarService (FR-30); FX-HolidayOutage, FX-HalfDay green
- [ ] INTEGRATIONS_v1.9 ⚠VERIFY items confirmed and file updated
- [ ] Regime proxy feed (FR-38/D73, v1.9.7): `GSPC.INDX` backfilled ≥3.8y + SPY.US returns cross-check; `Regime.ProxySecurityId` resolved from `Regime.ProxySource`; `FX-RegimeProxyBackfill` green (label fails closed pre-warm-up)

### Phase 2 — Funnel, ledger, costs, catch-up
- [ ] Six-stage funnel + ExitPolicy executor; FX-ZeroScore, FX-ExitOnly green
- [ ] D43 cost model; FX-CostModel green
- [ ] Corporate-action semantics complete; FX-Dividend/Split/Merger*/Spinoff/Delist/Unmapped green
- [ ] B&H CW+EW + ThresholdModel dummies live; B&H total-return acceptance green
- [ ] Staged pipeline hosted in AlphaLab.Worker (D53/D59/FR-29/FR-34): Stage-1 failure writes nothing; no-overlapping-writers + runs-without-API green; FX-StagedPipeline green
- [ ] Regime-label service (D50/FR-26); FX-RegimeHysteresis green
- [ ] Catch-up protocol; FX-Outage5d green
- [ ] D72 process model (v1.9.7): OnDemand drains queued jobs after catch-up (`FX-JobDrain` green); heartbeat + stale-run recovery (`FX-CrashedRun` green); `ux_runs_ok_forward` partial index created (finding 109)

### Phase 3 — The honest arena
- [ ] Populations (3 families + cost-free); FX-PopDeterminism, FX-PopBands green
- [ ] FX-SyntheticEdge >95th pct; FX-SyntheticNoEdge uniform
- [ ] Metrics service + NW-MDE; FX-MDE-AR1 green
- [ ] Gate (Promoted/Refused/TooEarly); FX-TooEarly, FX-PairedWin green
- [ ] Honesty read-models (D58) + AlphaLab.Api read endpoints (FR-32/33); FR33_ForwardReadModel_ContainsNoReplayRow green
- [ ] Separation state (D63, FR-35) in the read-models; FX-SeparationChip + UX12 read-model test green
- [ ] Allocator per D51 (FR-27); FR27_AllocatorSuite green
- [ ] CandidateFactory pre-registration modal (D52); FR28_Fork_RequiresHypothesisOrFlag green
- [ ] Monitor S2/S3/S6 minimal (flat S3 anchors); promotions ≤ chance on dummies
- [ ] Population compute batched; full daily run incl. populations < 60s
- [ ] Turnover-match verification (v1.9.7 finding 115): realized turnover persisted per strategy + population; cost-match caveat in `StrategyRow` + S3 panel; `FX-TurnoverMatch` green
- [ ] GUI: bands, MDE lines, percentile chips, separation chips — *deferrable per D65 (API-only (Scalar) sanctioned until Phase 4 sign-off); if deferred, logged here as a UI-workstream item due before Phase 7 exit*

### Phase 3.5 — Save/continue
- [ ] Nightly backup + WAL verified; watermark-pinned "reproduce day X" works
- [ ] Restore drill rehearsed and logged below

### Phase 4 — Arena Replay
- [ ] D70 prerequisite: bars backfilled for every historical S&P 500 member in the replay window (community-CSV as-of membership); replay runs on S&P 500 membership, never the S&P 100 slice
- [ ] Replay engine + quarantine; FX-ReplayQuarantine green
- [ ] FX-Replay15y validation suite green (D64 plants: edge / no-edge / anti-predictive, ≥50 seeds each)
- [ ] Anti-predictive detection-speed KPI recorded: ___ days median (D63)
- [ ] Days-to-indistinguishability KPI recorded: ___ days median (D63); no-edge plant never Suspect beyond the false-alarm rate
- [ ] Plant-sensitivity check archived in the calibration report; FX-PlantRealism green (D64)
- [ ] Threshold calibration report archived (docs/calibration/); config rows written & frozen
- [ ] D56 S3 trajectory curves calibrated + frozen; FX-S3Trajectory green
- [ ] Edge-plant survival KPI (v1.9.7 finding 113): fraction promotable at 5y: ___ (floor `Replay.EdgePlantSurvivalFloor5y` = 0.90) / at 10y: ___; every edge-plant auto-retire logged with its trigger
- [ ] Joint any-signal false-alarm fraction (v1.9.7 finding 114): ___ (bound `Replay.JointFalseAlarmMaxFrac` = 0.10); per-signal contribution archived in the calibration report

### Phase 5 — LLM layer
- [ ] Batches + caching + tiering; news budget; all §6 TEST_PLAN tests green
- [ ] One mocked month under budget: $___ ; live smoke test passed
- [ ] LLM runs as Stage 3 (post-commit, own transaction); assistant outputs land as journal entries

### Phase 6 — Real strategies
- [ ] Momentum / MeanReversion(+fast, trade track) / LowVol (+ optional Breakout) acceptance green
- [ ] French factors ingested; attribution panel with lag note
- [ ] Ledoit–Wolf service consumed by sizing/heat/overlay
- [ ] Blended weighted→logistic (out-of-fold provenance); Claude A/B accounts live
- [ ] Monitor S1/S4/S5/S7/S8 complete; FX-MonitorSignals, FX-AutoRetire green

### Phase 7 — Risk, regimes, observability
- [ ] Risk screen (capacity, heat, rejections, frozen); regime episodes + badges
- [ ] Data-health screen; alerting; RUNBOOK procedures verified
- [ ] UX-9 Allocation (derivation rows + clamp chips); UX9_ClampShown_WhenBinding green
- [ ] UX-10 Analysis & Journal full (outcome-closure nag); FR-28 screens green
- [ ] UX-11 admin-intervention panel (D55); FR31_ManualAction_AuditedAndScoped green

### Phase 8 — Fundamentals (contingent)
- [ ] §7.0 PIT protocol run vs EODHD Fundamentals — result: PASS / FAIL, evidence below
- [ ] If pass: D49 logged; Value/Quality + quarterly population + leakage extensions green

## Session log (newest first)

### 2026-07-13 — Phase 0 complete (skeleton)
**Shipped.** The full Phase-0 skeleton — repo + solution + all wiring, no business logic yet. `git init` + `.gitignore` (secrets / bin / obj / db). 8 src + 7 test projects in `AlphaLab.slnx` (SDK-10 XML solution); Central Package Management (`Directory.Packages.props` + `Directory.Build.props`, no `UserSecretsId`); `dotnet-ef` as a local tool. Versions pinned: `Microsoft.*`/EF/`dotnet-ef` = runtime **10.0.9**, `Scalar.AspNetCore` **2.16.11**, `Quartz.Extensions.Hosting` **3.18.2**, test stack (`Microsoft.NET.Test.Sdk 17.14.1` / `xunit 2.9.3` / `xunit.runner.visualstudio 3.1.4` / `coverlet.collector 6.0.4`).
- **Core:** D66 discriminated `ReadModelStamp`; the 15 §21 empty read-models; `AlphaLabJson` (snake_case names + enums, nulls emitted); `AlphaLab.Core.Arenas.{ArenaEntry,ArenaRegistry}` (`FromEntries` → first entry active; unit-tested, so Web references Core only).
- **Data:** `DbPathResolver` (pure `ResolvePath` + writer `Resolve`); `AddAlphaLabData(cs, arenaId, ensureDirectory)`; `AlphaLabDbContext` maps the five infra tables 1:1 to SCHEMA (four CHECKs, **no CHECK on `runs.status`**, composite `config (key,version)` PK with `Version` `ValueGeneratedNever()`, seeded `worker_state`). `InitialCreate` = five infra tables only; **hand-edited to drop `Sqlite:Autoincrement` on `run_id`/`job_id`** so both are plain `INTEGER PRIMARY KEY` per SCHEMA (rowid still auto-assigns; the snapshot keeps `ValueGeneratedOnAdd` → no `PendingModelChangesWarning`). `DefaultConnectionString` = the E: literal, byte-identical to both appsettings (three-spots rule, `ConfigConsistencyTests`).
- **Worker (sole writer, D59):** D67 config builder (`appsettings.json` + `appsettings.Secrets.json` only); `SchemaStartup : IHostedService` (registered before Quartz + the runner) migrates → `PRAGMA journal_mode=WAL` → verify `wal` or fail startup (both modes); `WorkerModeParser.Resolve` (OnDemand default; `--serve`/`Worker:Mode=Scheduled` → a Quartz stub idles); `Phase0MissedSessionResolver` (nothing to do → `StopApplication`, exit 0); every log record tagged `arena=sp500`.
- **Api (reader + bounded Phase-3 writes, D57/D59):** `AddAlphaLabData(ensureDirectory:false)` (never creates the store); native OpenAPI + Scalar served unconditionally; `/swagger`→Scalar; D60 error envelope (a 500 handler + `MapFallback`→404 `not_found` on unknown routes); dev CORS; localhost bind via `Urls` (127.0.0.1:5230); the 15 §21 stub read endpoints + `/health`, all `no_run_yet`.
- **Web (Core-only WASM):** the `Arenas` registry in `wwwroot/appsettings.json`; `ReadModelClient` targets the active `baseUrl`; empty-state pages; finding-121 hygiene (title `AlphaLab`, no tutorial links, arena `DisplayName` header, per-screen `EmptyHint`, day-one home note).
- **Tools (ASCII-only `.ps1`):** `Resolve-AlphaLabConnection.ps1`, `snapshot-db.ps1`, `migrate.ps1` (`--connection` from the Worker appsettings, gated on `$LASTEXITCODE` not stderr), `ci.ps1` (build + test + guard greps: bars word-boundary, committed key patterns, Web `using`/`<ProjectReference>` isolation).
**Verified.** `tools/ci.ps1` green — build + **39 tests** (Core 5 · Data 17 · Worker 8 · Api 6 · 3 placeholders) + guards. Worker OnDemand: `arena=sp500`, migrate, `journal_mode=wal`, nothing-to-do, exit 0 (idempotent); `--serve` idles Quartz. Api over real HTTP: `/openapi/v1.json` + `/scalar/v1` = 200; `/api/v1/strategies` = `{"stamp":{"status":"no_run_yet","run_id":null,"watermark":null,"as_of":null},"rows":[]}`; an unknown route → the D60 404 `not_found` envelope; `/api/v1/replay` `quarantined:true`; bound to `127.0.0.1` only; CORS header present for the Web origin. `migrate.ps1 -Arena sp500` snapshots then migrates the same file via `--connection`, exit 0. Web serves title `AlphaLab` + the Arenas registry. `appsettings.Secrets.json` gitignored + untracked.
**Red / known-broken:** none. Two transitive NU1903 advisories (`SQLitePCLRaw.lib.e_sqlite3 2.1.11` via EF Core; `Microsoft.OpenApi 2.0.0` via AspNetCore.OpenApi) are non-blocking (no `TreatWarningsAsErrors`) and clear on Microsoft's next 10.0.x servicing bump.
**Deferred (deliberately):** `config`/`catchup_log`/`jobs` are created-but-dormant (empty by design, not a bug). Data providers, data-domain tables, the `ux_runs_ok_forward` partial index, the D72 heartbeat/drain runtime behavior (config keys exist, behavior lands Phase 2), and `SchemaStartup`'s Phase-1 fail-fast are later phases. The browser render of the WASM screens is not automated (the client builds, the API serves it with CORS, and the served page/config were verified).
**Next session starts with:** Phase 1 (data foundation) — MASTER §13–14 + §20.5, SCHEMA, INTEGRATIONS, TEST_PLAN §2.

<!-- TEMPLATE:
### YYYY-MM-DD — Phase N
**Shipped:** …
**Red / known-broken:** …
**Deferred (deliberately):** …
**Decision proposals (need a D-number):** …
**Next session starts with:** …
-->

## Ops log (restore drills, off-machine backups, monthly hygiene)
<!-- YYYY-MM-DD — restore drill from backup {date}: PASS/FAIL, notes -->

## Decision proposals awaiting a D-number
<!-- Anything the docs don't cover. Approved ones move to MASTER §2. -->
