# CLAUDE.md — AlphaLab (paper trading research lab)

## What this is
Project name: **AlphaLab** (root namespace `AlphaLab.*`, D62).
Personal C#/.NET 10 paper-trading research laboratory. SQLite (single file, WAL),
EODHD market/reference data, Claude as a batched text-reading research assistant.
Research only — never investment advice, never real orders, never real money.
The UI is a swappable client of AlphaLab.Api (D57); Blazor is the reference client.
All honesty-carrying presentation logic lives in serializable read-models (D58),
computed once in C# and rendered verbatim by whatever front end is attached.
The daily pipeline runs in AlphaLab.Worker — the sole DB writer (D59). AlphaLab.Api is a thin
read/command boundary under fixed contract conventions (D60): /api/v1, uniform error
envelope, 202+job_id for long-running commands, read-models stamped run_id+watermark,
money as strings/minor-units (never floats).

## Documentation map (read the phase's diet per docs/README_v1.9.md §3 — not everything)
- docs/MASTER_DESIGN_v1.9.md — decisions D1–D84, architecture, golden rules, math appendix, UI boundary (§21–22)
- docs/ARENA_ARCHITECTURE_v1.9.3.md — multi-arena isolation (D71): one universe per arena, arena-namespaced storage, arena-scoped calibration, the no-merge frontend rule
- docs/STRATEGY_CATALOG_v1.9.md — IModel contracts + per-strategy acceptance criteria
- docs/DESIGN_IMPROVEMENTS_v1.9.md — metrics math, costs/sizing, LLM economics, Arena Replay
- docs/OVERFITTING_MONITOR_v1.9.md — eight signals, thresholds, MDE derivation
- docs/BUILD_AND_PROMPTS_v1.9.md — FR-1..38, phase plan, phase prompts (Phase 0 is structured as checkpoints 0.1–0.6 with the pinned versions, the no-AUTOINCREMENT migration hand-edit, the 404→D60 fallback, and the ASCII-only .ps1 rule)
- docs/SCHEMA_v1.9.md — the ONLY source of truth for table shapes (never invent columns)
- docs/CONFIG_REFERENCE_v1.9.md — the ONLY source of truth for config keys/defaults
- docs/INTEGRATIONS_v1.9.md — the ONLY source of truth for external endpoints
- docs/TEST_PLAN_v1.9.md — fixture library + FR-mapped test inventory
- docs/RUNBOOK_v1.9.md — operations, backup/restore drill
- docs/DB_RELOCATION.md — ops runbook: relocating the SQLite file(s) to another directory/drive (config edit + file move; ConfigConsistencyTests guards the four connection-string edit spots + Arena:Id agreement)
- docs/FUTURE_DB_MIGRATION.md — contingency: leaving SQLite for a server RDBMS (a different job from relocation; closed until needed)
- docs/REBUILD.md — ops runbook: from a fresh clone to a working arena (the data bootstrap, sibling to DB_RELOCATION/FUTURE_DB_MIGRATION; --preflight live-source check, sp500-widening caveat)
- docs/UX_GUIDELINES_v1.9.md — interface rules UX-1..UX-14 as build specs
- docs/SETUP_v1.9.md — prerequisites, D49 launch tier, secrets, day-zero checklist
- docs/DESIGN_IMPROVEMENTS_EXPLAINED.md — plain-language "why" companion to DESIGN_IMPROVEMENTS (section numbers match the spec)
- docs/CHANGELOG_v1.9.md — every consistency finding + decision (D1–D84), the provenance trace
- Navigation (not part of any phase diet): START_HERE.md (entry point), docs/README_v1.9.md (file map + build workflow), docs/MANIFEST.md (package manifest + revision state)
- Mockups (visual direction for the GUI): docs/alphalab_ux_mockups.html — the single consolidated UX mockup (every screen)

## Hard rules (violations are bugs, not style)
1. Forward paper P&L judges strategies. Replay (`run_kind='replay'`) judges only the
   machinery; it is quarantined from every forward view, gate input, and chart.
2. All identity is `security_id`. Tickers are time-ranged aliases via `ticker_history`.
3. Bars are versioned append-only. Never UPDATE or DELETE a bar row (CI greps enforce).
   Every run records a watermark; all reads resolve latest-version <= watermark.
4. Point-in-time everywhere via IFeatureView(asOf, watermark). Every new feature or
   label ships a leakage test in the same PR (TEST_PLAN fragment F-LEAK).
5. Costs always on: commission + spread bucket + k·σ·√(Q/ADV), participation cap 2% ADV
   with rejection logging. Cost-model version stamps every trade row.
6. Alpha = beta-adjusted (Jensen's) vs the cap-weight account, Newey–West errors,
   French RF. Never display or act on a head-to-head gap without its NW-corrected MDE;
   inside the MDE the verdict is TooEarly.
7. Strategies declare Horizon + ExitPolicy. Stage 4 closes ONLY via ExitPolicy or forced
   corporate-action/guardrail events. Zero-score names are never selectable.
8. Frozen parameters: any change to a live strategy forks a new strategy_id and
   increments trials_registry. Never tune a live strategy against the monitor.
9. Random populations are the null. Every strategy is ranked against its matched
   population; the cost-free population is display-only.
10. Fail closed: missing risk input → reject the order with a logged reason; unmapped
    corporate action or bar-stoppage-without-event → freeze position + alert.
    Nothing is ever silently defaulted or mispriced.
11. Secrets live in ONE gitignored file: appsettings.Secrets.json (D67; SETUP_v1.9 §5).
    Config builder = AddJsonFile(appsettings.json) + AddJsonFile(appsettings.Secrets.json,
    optional:true) — NO AddEnvironmentVariables, NO AddUserSecrets, no env vars anywhere.
    Keys: Secrets:EodhdApiToken, Secrets:AnthropicApiKey, optional Alpaca pair. Never in
    the committed appsettings.json, the repo, logs, or the DB. appsettings.Secrets.json is
    gitignored (CI grep also scans committed files for key patterns). Never commit/log/echo keys.
12. The daily run is the D53 staged pipeline: fetch (no DB writes) -> ONE atomic write
    transaction per trading day -> LLM batch post-commit in its own transaction.
    Multi-day recovery replays missed sessions (trading calendar, D54) in order,
    one write transaction per day, idempotently (catchup_log); no LLM for past days.
13. Claude is forward-only, batched (Message Batches API), prompt-cached, budgeted;
    INewsProvider enforces the news budget BEFORE any token is spent. Replay has no
    LLM path by construction.
14. Schema changes go through versioned EF migrations with a pre-migration DB snapshot;
    SCHEMA_v1.9.md is updated in the same PR.
15. No manual writes outside the D55 admin actions (typed-confirmed, validated like
    provider rows, audited to admin_actions). Direct DB edits are a rule violation.
16. Every candidate is pre-registered (D52): CandidateFactory requires a linked,
    immutable hypothesis (claim + metric + evidence window) or an explicit
    'unregistered' flag rendered permanently on the strategy card.
17. Every UI talks to the system ONLY through AlphaLab.Api (D57). AlphaLab.Web (and any future
    client) references AlphaLab.Api's contract, never AlphaLab.Evaluation/AlphaLab.Data directly.
    The API is a thin projection + command layer: no statistics, no thresholds, no
    verdict logic in it.
18. Honesty lives in read-models, not the UI (D58). MDE dimming, verdict chips, tiers,
    population percentiles, allocation clamps, and replay quarantine are resolved into
    serializable DTO fields in AlphaLab.Core/AlphaLab.Evaluation and rendered verbatim by the
    client. UX tests are read-model unit tests (framework-agnostic), not browser tests.
    A forward-screen read-model can never contain a replay row.
19. The daily pipeline and catch-up run ONLY in AlphaLab.Worker, the sole DB writer (D59).
    Default Worker.Mode=OnDemand (D61): a launch runs catch-up through the last completed
    session and exits; Scheduled mode (Quartz, resident) is an optional config flip. Both
    modes do identical work via catch-up (D47) — the trigger is the only difference.
    AlphaLab.Api never schedules or runs long work on a request thread; long-running commands
    (replay, LLM briefs/skeptic) enqueue a Worker job and return 202+job_id. A command
    during a run returns 409 (or is queued) — never races the daily write transaction.
20. API contract conventions are fixed (D60): base path /api/v1; error envelope
    { error:{code,message,details?} } with 400/404/409/422/503; money & ratios as
    strings or integer minor units, never floats; UTC ISO-8601; every read-model stamped
    with its run_id + watermark. Breaking changes bump /v2 and the OpenAPI version.
    Ledger money is C# decimal persisted as TEXT in SQLite (D69) — never double/REAL.
21. Verdict language matches the channel (D63): the population channel yields
    IndistinguishableFromRandom (the separation state, computed in read-models per
    MASTER §20.8) — never "proven no edge"; fast-kill claims belong only to the
    trade-level expectancy track and anti-predictive S3/S6 breaches. Calibration
    curves require the D64 plants (regime-conditional, autocorrelated, multi-seed —
    never constant drift) and are not trusted without the plant-sensitivity check
    archived in the calibration report (MASTER §20.9).
22. Universe (D65/D70): forward operation runs the S&P 100 slice through Phase 4 sign-off,
    sourced from the OEF holdings CSV + Wikipedia S&P 100 cross-check (fail closed, count
    sanity 99–103), then widens to the S&P 500 by config flip + backfill delta. Arena Replay
    NEVER runs on the slice — replay/calibration always use S&P 500 as-of membership
    (community CSV at launch, D49) with every historical member's bars backfilled for the
    replay window as a Phase 4 prerequisite. (This is the sp500 arena's instance of the
    per-arena rule in rule 23.)
23. Arenas are isolated (D71; ARENA_ARCHITECTURE_v1.9.3.md): one universe per arena; each
    arena has its own SQLite file, Worker+Api pair, and snapshot/backup dirs, all namespaced
    by Arena.Id ({Arena.Id} token in the connection string — FR-37). Calibration (cost model,
    covariance, control populations, verdict/threshold curves, EW benchmark, replay report)
    is arena-scoped and NEVER copied between arenas. No UI merges arenas into one ranking
    (UX-13; side-by-side panels only). D70 generalizes per-arena: replay always uses the
    ARENA'S full-universe as-of membership, never a bootstrap slice.
24. Worker liveness + config versioning (D72, v1.9.7): an OnDemand launch drains queued jobs
    AFTER catch-up (never inside a daily write transaction); the running Worker heartbeats
    worker_state.heartbeat_at, and a stale run_in_progress flag is cleared on launch (orphaned
    run marked 'failed') — never left to 409 the command path. config rows are append-only-
    versioned: a change INSERTs (key, version+1); never UPDATE or DELETE a config row
    (finding 108); the current value is MAX(version) per key.

## Workflow
- Plan Mode for anything multi-file; wait for plan approval before writing code.
- Strict milestone scope: implement ONLY the current phase's FRs. If a change outside
  the phase seems necessary, stop and record it in PROGRESS.md as a proposal.
- Every phase ends with: tests green, a working demo, a PROGRESS.md entry, a commit.
- Test names cite FRs (e.g. FR10_ParticipationCap_RejectsAndLogs); new decisions get
  D-numbers appended to MASTER §2.
- Prefer boring, explicit code over cleverness; determinism beats elegance.

## Solution layout
src/AlphaLab.Core (domain: models, funnel, ledger, ExitPolicy, read-model DTOs D58) ·
src/AlphaLab.Data (EF Core, providers: Eodhd*, Ivv*, Wikipedia*, Alpaca*, French*) ·
src/AlphaLab.Strategies (IModel implementations) ·
src/AlphaLab.Evaluation (metrics, MDE, gate, allocator, monitor, populations, replay,
  read-model builders that resolve the honesty rules into DTO fields) ·
src/AlphaLab.Llm (IAnalysisProvider, INewsProvider budget, research assistant) ·
src/AlphaLab.Worker (Generic Host: Quartz scheduler + D53 staged pipeline + D47 catch-up;
  the SOLE DB writer — D59) ·
src/AlphaLab.Api (ASP.NET Core minimal-API under /api/v1: read endpoints per screen +
  command endpoints; localhost-only default; OpenAPI; the ONLY thing any UI talks to;
  contract conventions D60; enqueues Worker jobs for long-running commands — D57) ·
src/AlphaLab.Web (standalone Blazor WebAssembly client of AlphaLab.Api; swappable for Angular/React/mobile) ·
tests/ (mirrors src; fixtures under tests/Fixtures per TEST_PLAN_v1.9.md) ·
docs/ (this documentation set) · tools/ (backfill CLI, backup, snapshot scripts, ci.ps1,
  audit-dividend-unadjusted.ps1) · .github/workflows/ (GitHub Actions: runs tools/ci.ps1 on push + PR)

## Commands (once Phase 0 lands)
- `dotnet test` — full suite (CI runs this + the greps)
- `dotnet run --project tools/Backfill -- --universe sp100` — data backfill
- `dotnet run --project src/AlphaLab.Worker` — run the daily update on demand (catch up → exit); the sole writer.
    Add `--serve` (or set Worker.Mode=Scheduled) to keep it resident with the Quartz schedule.
- `dotnet run --project src/AlphaLab.Api` — the API (localhost); OpenAPI at /openapi/v1.json, Scalar UI at /scalar/v1 (/swagger redirects)
- `dotnet run --project src/AlphaLab.Web` — the reference Blazor client (talks to AlphaLab.Api)
- `tools/snapshot-db.ps1` — pre-migration snapshot (Windows/PowerShell first-class; takes `-Arena`, default `sp500` — D71)