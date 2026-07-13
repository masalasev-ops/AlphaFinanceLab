# PROGRESS.md — the build's honest ledger (design revision v1.9)

*Repo root. Updated every working session — what shipped, what's red, what was deliberately deferred, and any decision proposals. This file is the month-one discipline instrument (MASTER §17.1): if it stops being truthful, the phase gates stop working.*

## Current state
- **Phase:** 0 COMPLETE (skeleton) through the v1.9.8 gates; ready for Phase 1.
- **Blocking:** none.
- **Last session:** 2026-07-13 (later) — v1.9.8 skeleton review fix-up (P0-1…P0-6): the client renders all 13 non-parameterized screens (was 8), `ci.ps1` enforces the full reference graph with an EAP-safe git call, the factory comment matches the `E:`-literal reality, the resolver tests assert two-arena distinctness, and a missing `Arenas` registry fails closed with a visible banner; `tools/ci.ps1` green (build + **39 tests** + guards). See the two session-log entries.

## Phase gates (a phase is DONE only when every box is checked and committed)

### Phase 0 — Skeleton
- [x] Solution layout per CLAUDE.md incl. AlphaLab.Api (D57); CI green (build + tests + greps + the **full reference-graph guard** over every src project at the `<ProjectReference>` level, plus the AlphaLab.Web source-level `using`-isolation grep — P0-3)
- [x] AlphaLab.Worker: OnDemand launch catches up (nothing yet) and exits cleanly; --serve idles (sole writer, D59/D61)
- [x] AlphaLab.Api boots and serves OpenAPI (/openapi/v1.json) + Scalar UI (/scalar/v1), localhost; error-envelope middleware; dev CORS for the AlphaLab.Web origin (Api:CorsAllowedOrigins); stub read endpoints return empty read-models with ReadModelStamp status=no_run_yet (D66)
- [x] ConnectionStrings resolved via the shared DbPathResolver ({Arena.Id} token from the Arena config block, FR-37/D71, + {LocalAppData} token via Environment.GetFolderPath still supported — no env-var reads, D67 — + directory create); Worker, Api, and the EF design-time factory all open the SAME arena-namespaced file (this deployment: `E:\AlphaLabDatabase\sp500\alphalab.db`); bare `dotnet ef` defaults to sp500
- [x] Arena identity wired (FR-37/D71): Arena.Id=sp500 in config; logs tagged arena=sp500; AlphaLab.Web loads the one-entry Arenas registry and targets its baseUrl; FR37 tests green
- [x] EF infra-only InitialCreate (runs/catchup_log/config/worker_state/jobs — D59/D60; worker_state row seeded) + snapshot script working
- [x] Empty-DB Blazor client renders every **non-parameterized §21 screen (Home + 13)** by calling AlphaLab.Api (NFR-3, P0-1); the two parameterized screens (`/strategies/{id}`, `/why-trade/{strategyId}/{date}`) are deliberately excluded from the flat catalog (drilled into from a real row) and noted in a `ScreenCatalog` comment
- [x] appsettings.Secrets.json gitignored & untracked; no key patterns in committed files (D67)
- [x] v1.9.7 fix-up (finding 118): `SchemaStartup` executes `PRAGMA journal_mode=WAL` post-migrate, verifies `wal`, fails startup otherwise; `R1_SchemaStartup_EnablesWal` green
- [x] v1.9.7 fix-up (finding 108): `config` PK is composite `(key, version)` in `InitialCreate` (built fresh, no hand-edit — `Version` is `ValueGeneratedNever()`); two versions of one key insertable, duplicate `(key,version)` rejected by the store (`SchemaFidelityTests`)
- [x] v1.9.7 fix-up (finding 119): `tools/migrate.ps1` resolves the DB path from the Worker appsettings and passes `--connection` — snapshots and migrates the same file (verified: snapshot written, update idempotent, exit 0)
- [x] v1.9.7 fix-up (finding 121): index.html title `AlphaLab`; layout tutorial link removed (header renders arena DisplayName); ScreenCatalog `EmptyHint` per screen + day-one home note (UX-8c); Api comments say "reader plus bounded Phase-3 command writes (D59)"; `.gitignore` DB comment present
- [x] v1.9.8 fix-up (P0-1): `ScreenCatalog` carries all 13 non-parameterized §21 screens (was 8) — the empty-DB client renders every one; the 2 parameterized screens excluded by design (comment)
- [x] v1.9.8 fix-up (P0-2): `AlphaLabDbContextFactory` comment matches the `E:`-literal three-spots reality (no longer invites the `%LOCALAPPDATA%` "fix" that reddens `ConfigConsistencyTests`)
- [x] v1.9.8 fix-up (P0-3): `ci.ps1` enforces the **full** reference graph at the `<ProjectReference>` level (`Assert-ReferenceGraph`, not just Web) and its `git ls-files` call is EAP-safe (git-absent falls back to a working-tree scan, never throws)
- [x] v1.9.8 fix-up (P0-4): the two resolver tests assert two-arena path distinctness (`sp500` vs `sp100`, `Assert.NotEqual`); canonical **39** count unchanged; TEST_PLAN §8 erratum records the substituted bare-factory case
- [x] v1.9.8 fix-up (P0-6): `ArenaRegistry.IsFallback` + a visible config-error banner when the `Arenas` registry is missing (fail-closed, hard rule 10) instead of a silent self-call; `FromEntries_Empty` asserts the flag

### Phase 1 — Data foundation
- [x] Security master + ticker_history; FX-TickerChange green (checkpoint 1.1)
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
- [ ] (v1.9.8, C-2) S3 percentile-threshold sampling band archived alongside the curves (~±1.5 members at M=200) — evidence for a future "should M be 500?" question

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

### 2026-07-13 (Phase 1) — Checkpoint 1.2: EODHD provider + versioned bars + watermark read (FR-1/FR-2)
**Shipped.** `IMarketDataProvider`/`EodhdMarketDataProvider` (FR-1): `GetEod/Dividends/Splits` via the resilient client + raw-cache archive; pure static `ParseEod`/`ParseDividends`/`ParseSplits`/`ParseSplitRatio` (INTEGRATIONS §1 shapes). `/eod` stores raw OHLCV + `adjusted_close`→`adj_close` only — `adj_open/adj_high/adj_low` left NULL (EODHD has no adjusted OHL); split string ratio parsed on `/` (`"4.000000/1.000000"`→4.0), fails loudly on drift. `IBarIngestionService`/`BarIngestionService` (FR-2/D40): correction inserts `version = MAX(version)+1`, never UPDATE/DELETE; identical re-fetch is idempotent. `IBarReadService`/`BarReadService`: latest version WHERE `observed_at ≤ watermark` (ordinal ISO compare).
**Verified.** **FX-BarCorrection green** (v1 day 10; v2 correction observed day 15): a day-12 watermark reproduces v1 byte-identically; a day-16 watermark sees v2; append-only (2 versions, prior untouched); idempotent re-fetch; adj-OHL NULL. Parse tests cover /eod field mapping, the split-ratio rule (incl. reverse split + malformed-fails-loudly), and /div ex-date + adjusted/unadjusted. Data tests **49** (was 33); `tools/ci.ps1` green (bars append-only grep clean; **71** total).
**Red / known-broken:** none.
**Deferred (deliberately):** the "EODHD bars backfill (S&P 100) + daily delta" half of the DoD box is the backfill CLI (checkpoint 1.10) + the live run (user, decision #1) — so that box stays unchecked though versioned bars + FX-BarCorrection are done. **Real-payload parse fixtures pending:** the /eod, /splits, /div parse tests currently run on INTEGRATIONS-shape-faithful payloads; asking the user for the actual 2026-07-13 captures to drop into tests/Fixtures as byte-real inputs.
**Next:** 1.3 — corporate-actions dividends + splits ingestion (FR-3).

### 2026-07-13 (Phase 1) — Checkpoint 1.1: security master + ticker_history (FR-3)
**Shipped.** `ISecurityMaster`/`SecurityMaster` in `AlphaLab.Data/Services`: permanent `security_id` identity (D39), time-ranged `ticker_history` aliases, `ResolveAsOf` (interval `valid_from ≤ asOf < valid_to`, ordinal ISO compare in memory — no dependence on EF string.Compare translation), `Register`, `ResolveOrRegister`, and `RecordTickerChange` (closes the open alias, opens a new one under the SAME id, renames `securities.current_symbol`, writes a `corporate_actions(type='ticker_change')` row — all in one atomic SaveChanges, zero identity break). Added a shared `TestDb` helper (throwaway migrated on-disk SQLite).
**Verified.** **FX-TickerChange green** (ACME→ACMX on day 40 of an 80-day series, position held through): same id on both sides of the rename, valid_from-inclusive/valid_to-exclusive boundary, one security + two aliases + one ticker_change action (zero churn), and 80 continuous bar rows joining to the single id with no break. Data tests **33** (was 30); full suite green via `dotnet test`.
**Red / known-broken:** none.
**Next:** 1.2 — `IMarketDataProvider`/`EodhdMarketDataProvider` + versioned-bar ingestion + watermark read service + `FX-BarCorrection`.

### 2026-07-13 (Phase 1) — Checkpoint 1.0: data-domain schema + shared plumbing
**Shipped.** The Phase-1 schema foundation and the provider plumbing every later checkpoint stands on. **Nine data-domain tables** added to `AlphaLabDbContext` and migration `20260713232437_Phase1DataFoundation` (SCHEMA §Identity & Market Data + §v1.8, verbatim): `securities` (+ partial unique `ux_securities_active_symbol WHERE delisted_on IS NULL`), `ticker_history` (+ `ix_ticker_hist_symbol`), `sector_changes`, `bars` (PK `(security_id,date,version)` + `ix_bars_observed`), `corporate_actions` (8-value `type` CHECK; `cash_per_share` decimal→TEXT D69, `ratio` REAL), `index_membership_log`, `index_membership`, `trading_calendar` (`session` CHECK), `api_usage_log`. Deferred to Phase 2 (unbuilt by design): `regime_labels`, `regime_episodes`, `features`, `factor_returns`, `factor_refresh_log`, the `ux_runs_ok_forward` partial index. **Rule-14 hand-edit** applied to exactly the three new bare-INTEGER-PK tables (`securities.security_id`, `corporate_actions.action_id`, `index_membership_log.log_id`) — `.Annotation("Sqlite:Autoincrement", true)` stripped; the other six new tables are composite/TEXT PKs and needed no edit. **Shared plumbing** in `AlphaLab.Data`: `Http/ResilientHttpClient` (hand-rolled, no Polly — 30s timeout, 3 retries exp-backoff + injectable jitter, circuit-break after 5 consecutive failures), `Http/RawCache` (`FileRawCache` + `NullRawCache`, archives to `tools/raw-cache/{source}/{date}/`, gitignored), `Services/ApiUsageLog` (`ApiUsageHeadroom.HasHeadroom` ≥50% rule + `ApiUsageLogWriter` upsert).
**Verified.** `tools/ci.ps1` green — build + **52 tests** (was 39: Data 30, Worker 8, Api 6, Core 5, +3 placeholders) + guards. **1.0 acceptance gate (the fragile step) passed:** `SchemaFidelityTests.Schema_IntegerPrimaryKeys_HaveNoAutoincrement` green on all three new tables + the `sqlite_sequence`-never-created backstop; `.Designer.cs`/`ModelSnapshot.cs` left untouched by the hand-edit; `dotnet ef migrations has-pending-model-changes` = "No changes …" (no `PendingModelChangesWarning`). New tests: schema fidelity (14-table set, Phase-1 CHECKs, PK auto-assign, type-CHECK rejects unknown), plumbing (retry/circuit/non-2xx, raw-cache write, headroom theory, usage-log upsert). Updated `SchemaStartupTests` to the 14-table migrated set.
**Red / known-broken:** none (the two transitive NU1903 advisories persist, non-blocking).
**Deferred (deliberately):** the Phase-2 regime/feature/factor tables above; providers + services (land in 1.1–1.10). Session scope is checkpoints 1.0–1.3, then a review stop before 1.4.
**Next:** 1.1 — security master + `ticker_history` service (`ISecurityMaster`) + `FX-TickerChange`.

### 2026-07-13 (later) — Phase 0 skeleton review fix-up (v1.9.8, P0-1…P0-6)
**Shipped.** Applied the six findings of the 2026-07-13 deep-dive review of the shipped skeleton (findings **P0-1…P0-6**; the review prose is not retained — all findings folded into the docs + CHANGELOG v1.9.8). **P0-1 (High, the one unmet DoD claim):** `ScreenCatalog` now carries all **13** non-parameterized §21 screens (added trades, go-live-log, regimes, risk, admin-interventions with UX-8c hints; was 8) — the empty-DB client renders every one; the 2 parameterized screens are excluded by design (comment). **P0-2:** the `AlphaLabDbContextFactory` doc comment matches the `E:`-literal three-spots reality. **P0-3:** `ci.ps1` gains `Assert-ReferenceGraph` (full graph at the `<ProjectReference>` level, per BUILD 0.1) and an EAP-safe `git ls-files` call. **P0-4:** the two resolver tests assert `sp500` vs `sp100` path distinctness (`Assert.NotEqual`); 39-count intact. **P0-5:** the dangling `docs/reviews/DEEP_DIVE_REVIEW_2026-07-12.md` references (8 sites) redirect to `CHANGELOG_v1.9.md` — policy: review prose is not kept as files, findings live in the docs. **P0-6:** `ArenaRegistry.IsFallback` + a fail-closed config-error banner when the `Arenas` registry is missing (hard rule 10) instead of a silent self-call.
**Verified.** `tools/ci.ps1` green — build + **39 tests** + guards (now incl. the full reference-graph guard). No architecture/schema/decision change; D1–D73 unchanged.
**Deferred (deliberately) — UI workstream (D65, due before Phase 7 exit):** the review's §B UI recommendations are now captured in the docs (they were **not** implemented — D65 sanctions API-only operation through Phase 4): the v5 design-token table + the semantic-color reservation (`--gold`=live, `--violet`=replay, `--band`=population) live in `UX_GUIDELINES_v1.9` ("Visual system — design tokens"); the shell-theming pass is documented as deferred BUILD **checkpoint 0.7g** (dark chrome, mono numerics, self-hosted fonts, restyled empty-state cards, `NotFound.razor` in-voice, Bootstrap-dist trim) — do it whenever the shell's look is worth the time. Residual-risk recommendations captured: C-2 (S3 percentile sampling band → MONITOR 8½ + Phase-4 gate), C-3 (verdict-time expectation → root README), C-4 (IVV CSV header fixture → Phase-1 prompt), C-5 (GSPC.INDX EOD verify — already on the Phase-1 checklist, no action); C-1 and C-6 logged as decision proposals below.
**Decision proposals (need a D-number):** FR-23 "hypotheses" research action has no §21/`jobs.kind` home; Phase-4 detection-power sweep across ~3 alpha levels. Both recorded below.
**Next session starts with:** Phase 1 (data foundation) — MASTER §13–14 + §20.5, SCHEMA, INTEGRATIONS, TEST_PLAN §2.

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
- **(2026-07-13, from the v1.9.8 review / A.3-2 / C-6) FR-23's "hypotheses" research action has no home.** FR-23 lists research-assistant endpoints as "briefs, **hypotheses**, skeptic", but §21's command list has only `POST /analysis/brief` + `POST /analysis/skeptic`, and the `jobs.kind` CHECK allows only `('replay','analysis_brief','analysis_skeptic')`. Before Phase 5 planning, decide: either add a snapshot-gated migration extending the CHECK + a §21 command entry (per finding 121's "enum CHECKs extend only via migration"), or strike "hypotheses" from FR-23. No action until then.
- **(2026-07-13, from the v1.9.8 review / C-1) Phase-4 detection-power sweep.** `Calibration.Plant.AlphaAnnualPct` defaults to a single 2.0% edge, so the Phase-4 report certifies edge-plant *survival* at one alpha but never answers "how big must an edge be before this lab certifies it within my patience horizon?". Proposal: sweep the edge plant across ~3 alpha levels (e.g. 2/4/8% ann, same seeds/plant) and archive an empirical detection-power section (P(promoted by t | α) curves + median days-to-promotion per level), validating the analytic MDE end-to-end against the machinery. Changes a Phase-4 deliverable (D64 territory) → awaiting a D-number before editing D64.
