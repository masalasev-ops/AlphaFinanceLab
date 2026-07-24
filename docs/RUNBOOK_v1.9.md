# RUNBOOK_v1.9 — operating the laboratory

**Processes (D59/D61).** `AlphaLab.Worker` is the *only* writer to the database. **Default mode is OnDemand (D61):** you open it each evening (a shortcut or `dotnet run --project src/AlphaLab.Worker`), it computes what sessions it missed, runs them in order, **executes any queued jobs (replay, analysis — D72, v1.9.7), takes the daily backup,** and exits. Your machine does **not** need to be always on. `AlphaLab.Api` (localhost) and `AlphaLab.Web` (the UI) are optional and read-mostly — start them when you want to look at results; stopping them never affects the update. If you ever move to an always-on box, set `Worker.Mode=Scheduled` (or run with `--serve`) and Quartz triggers the run automatically at session-close+offset — same work, different trigger. All processes open the same SQLite file in WAL mode.

*Day-2 operations for a single-operator Windows machine. Everything here is scripted under `tools/`; this doc is the human procedure layer.*

## 1. The daily cycle (automatic)
1. **Trigger — you launch `AlphaLab.Worker` in the evening (OnDemand default, D61), or a Scheduled run fires at session close + `Calendar.RunAfterCloseOffsetMinutes` (D54; ET-anchored, DST-safe).** Either way, `AlphaLab.Worker` (the sole DB writer, D59) first catches up any missed completed sessions, then runs the **staged pipeline (D53)** for each:
   - **Stage 1 — fetch (no DB writes):** all provider calls; raw payloads to `tools/raw-cache/`; quality gate validates staged data; a hard failure aborts before any row is written (tomorrow's catch-up recovers).
   - **Stage 2 — commit (ONE atomic write transaction):** new-run row (watermark stamped) → bars delta (versioned) → corporate actions → membership refresh + cross-check → features → regime label (D50) → per-account funnel (all strategies + populations) → fills at next-open queue → metrics/MDE → monitor/gate/allocator (cadence days) → run row `ok`.
   - **Stage 3 — LLM (post-commit):** the Batches job is submitted after Stage 2 commits; results land in their own small transaction whenever ready; a late/failed batch is a no-read day (never a blocker).
2. **Backup (§3):** in OnDemand mode (the default) the backup runs as the final step of each Worker launch; in Scheduled mode a resident 02:00 job performs it. Either way there is at most one backup file per calendar day.
3. **GUI Data-health screen** is the morning glance: last run status, watermark, cross-check agreement, factor-data freshness, budget spend, frozen positions, capacity rejections.

## 2. When the machine was off (catch-up, D47)
On next start the orchestrator detects missed trading days and replays them strictly in order, one transaction per day (bars → actions → membership as-of → funnel). Missed sessions come from the trading calendar (D54); catch-up runs Stages 1–2 per day, never Stage 3 (D16). Nothing to do manually; verify afterwards: `catchup_log` covers every missed day, Data-health shows green. If a day fails mid-catch-up, the transaction rolled back — fix the cause (usually a provider outage; see §5) and restart; recovery resumes at the failed day.

## 3. Backups
- Per Worker launch (OnDemand, the default) or nightly at 02:00 (Scheduled): WAL checkpoint → file-copy of `alphalab.db` to `backups/alphalab-{date}.db` (skip if today's file exists) → retain 30 days (config). All paths are arena-namespaced under the configured DB base (`<DbBase>/{Arena.Id}/backups/` — D71; this deployment's base is `E:/AlphaLabDatabase`, separators normalized to the running OS per v1.9.36, see `DB_RELOCATION.md`); each arena's Worker backs up its own file only.
- Pre-migration: a snapshot is mandatory before any EF migration — and enforced **by construction**: `tools/migrate.ps1` is the ONLY sanctioned schema-application path (rule 14) and performs the `tools/snapshot-db.ps1` snapshot itself as its first step (snapshot → `dotnet ef database update`). Never run `dotnet ef database update` directly against a non-empty DB. The guarantee is verified **on disk**, not taken on the script's word (finding 265): the snapshot copy is size-verified, an indeterminate "does the store exist?" answer (a transient antivirus/indexer lock) throws instead of reading as "fresh install", and `migrate.ps1` refuses to migrate an existing store unless the run just produced a NEW snapshot file.
- **Off-machine (weekly):** `pwsh tools/backup-offsite.ps1 -Arena sp500 -Destination <UNC | external drive | cloud-mount>` copies the arena's newest local backup (chosen **by the date in the filename**, the same rule `LocalBackup` prunes by — never mtime, which a copy or a restore rewrites) and **verifies** it by size + SHA-256, printing the hash. It never opens the database, so it is safe to run while the Worker is running. It fails loudly on a missing/blank destination, a missing backups directory, no backup files, or a hash mismatch — an off-site routine that silently does nothing is worse than none, because it also removes your reason to check. Log each copy in PROGRESS.md monthly.
- **Nightly, unattended:** `pwsh tools/register-nightly-backup.ps1 -Arena sp500` prints the exact `Register-ScheduledTask` commands for an 02:00 daily task, and registers them only if you add `-Install`. The task runs the Worker's **OnDemand** launch (`dotnet run --project src/AlphaLab.Worker`, no `--serve`), which drives the whole D72 order — stale-run recovery → catch-up → job drain → `LocalBackup` → exit — so a backup lands even on a day you never open the lab. **Not `--serve`:** Scheduled mode currently registers Quartz with zero jobs, so a resident Worker would idle and never back up (tracked in PROGRESS as a Phase-2/D61 leftover). Safe nightly: `LocalBackup` is idempotent per calendar day and catch-up is a no-op once current.
- **Verify WAL end to end:** `dotnet run --project src/AlphaLab.Worker -- verify-wal --arena sp500` asserts `journal_mode=wal` **and** that a checkpoint completes — the assumption `LocalBackup` rests on when it folds the WAL into the main file so a plain file copy is a consistent snapshot. It reads the pragma, never sets it (a verifier that repaired what it checks could never report the defect), and exits non-zero with a named reason otherwise.
- **Prove a past day still reproduces:** `dotnet run --project src/AlphaLab.Worker -- reproduce-day --date <yyyy-MM-dd> [--arena sp500]` re-runs that committed session from its stored watermark into a throwaway copy and compares decisions, fills, equity and population draws byte for byte (NFR-1, MASTER §13.5). Read-only against the arena and needs no API token. Worth running after any restore, and after any incident that touched the store.

## 4. Restore drill (rehearse quarterly — logged in PROGRESS.md)
1. Stop the app (nothing may hold the store — the Worker is the sole writer, but stop the Api too). 2. Copy the target backup over `alphalab.db`, keeping the broken file as `alphalab-broken-{date}.db`, **and delete the `alphalab.db-wal` / `alphalab.db-shm` sidecars**: the backup is taken after a `wal_checkpoint(TRUNCATE)`, so it is complete on its own, and a surviving newer WAL would replay exactly the transactions you are restoring away. 3. Start the Worker; it detects the store is behind and catch-up replays the missing sessions automatically (D47). 4. Verify: equity curves continuous, `runs` gapless after catch-up, `position_snapshots` present for each recovered session (D90 — without it the recovered days are not reproducible), and spot-check one account's trades vs the go-live log. Then run `verify-wal` and a `reproduce-day` on a recovered session (§3).

The drill *is* the test that backups work; an unrehearsed backup is a hope, not a plan. This exact path is now also an automated test — `FX-RestoreThenContinue` (TEST_PLAN §3) restores a real backup over a real store and asserts catch-up resumes with no data loss and no double-count — but the automated version does not prove *your* backup files are readable on *your* disk. Keep rehearsing it.

## 5. Incident playbook
| Symptom | Action |
|---|---|
| Daily run failed: provider HTTP errors | Nothing lost (transaction rolled back). Check EODHD status/plan limits; rerun or let tomorrow's catch-up recover. Persistent ⇒ switch `Data.Provider` fallback for bars; membership fails closed on its own |
| Membership cross-check divergence alert | Usually IVV lag (T+1) or an index change mid-flight. If it persists > 2 days: inspect `index_membership_log` diffs, confirm against the S&P press release, apply via the **D55 membership-override admin action** (typed confirmation, audit row) |
| Frozen position (unmapped corporate action) | Look up the event (issuer IR / EDGAR), then use the **D55 admin-intervention panel** (Risk screen): typed confirmation → row preview → validated write with `source='manual'` + `admin_actions` audit row → scoped ledger re-run; the freeze clears when the action processes. No direct DB edits (Golden Rule 29) |
| LLM budget exhausted / batch failed | Reads degrade per D24 order automatically; a missed day is a no-read day (neutral), never a blocker. Check `llm_budget_log` |
| Factor refresh checksum/continuity failure | Attribution panel shows stale-data note automatically (D41); retry next day; the trading path is unaffected (diagnostic-only) |
| Bar cross-check tolerance alarm | Inspect the sampled names; if EODHD revised, versions arrive naturally (D40); if systematic, open a provider ticket and widen the sample temporarily |
| DB corruption / disk failure | §4 restore drill, latest backup + catch-up |
| Command path returns 409 but no run is active | The Worker crashed mid-run and left `run_in_progress=1`. Launch the Worker: it clears the stale flag (heartbeat older than `Worker.StaleRunThresholdSeconds`), marks the orphaned run `failed`, and recovers via catch-up (D72, v1.9.7). Data-health shows "stale run detected" |
| A replay/analysis job sits `queued` | Expected on an OnDemand deployment: jobs execute at the next Worker launch, after catch-up (D72, v1.9.7). Launch the Worker to run it now; `GET /jobs/{job_id}` shows queue position |

## 6. Key rotation & security
Rotate `Secrets:EodhdApiToken` / `Secrets:AnthropicApiKey` by editing the gitignored `appsettings.Secrets.json` and restarting (D67); keys never live in the committed repo, logs, or the DB (CI grep). AlphaLab.Api binds to localhost only (the WASM client is static files served locally; dev CORS allows only its origin) — this is a personal tool; do not expose it.

## 7. Upgrades & maintenance
- **EF migrations:** snapshot → migrate → verify → update SCHEMA_v1.9.md in the same commit.
- **Monitor threshold changes:** only via versioned `config` rows with a reason, and only after re-running the Phase-4 replay validation suite (MONITOR §5).
- **New strategy / fork:** through CandidateFactory (registers the trial); never by editing a live strategy's config_json (D17).
- **.NET / package updates:** monthly, on a branch, full test suite + one shadow daily run before merging.
- **Monthly hygiene (15 min):** review capacity rejections, guardrail fire counts, budget spend trend, backup restore-drill schedule, and write the PROGRESS.md ops note.

---

## Running more than one arena (D71)

Each arena (e.g. `sp500`, later `russell2000`) is a separate lab: its own database file, its own
Worker+Api instances, its own snapshots/backups — all namespaced under `Arena.Id`
(`<DbBase>\{Arena.Id}\…`, where the base comes from `ConnectionStrings:AlphaLab`; see
`DB_RELOCATION.md`). `tools/snapshot-db.ps1` and `tools/migrate.ps1` take an `-Arena`
parameter (default `sp500`) and operate on that arena's file only. A strategy or code fix lands once
in the shared code checkout and benefits every arena on its next run — arenas differ only in data and
calibration, never in logic. Calibration (cost model, covariance, control populations, verdict
thresholds, replay report) is **arena-scoped and never copied between arenas** — recalibrate each new
arena from its own data. Full spec: `ARENA_ARCHITECTURE_v1.9.3.md`.

## 8. Phase-4 sign-off: the D70 backfill + `replay-calibrate` (v1.9.39; checkpoint 4.11)

The Phase-4 build (checkpoints 4.1-4.10) ships the machinery; this section is the OPERATOR run that
earns the DoD. Every step is resumable; nothing here touches forward rows (the replay generation is
run_kind='replay', quarantined).

1. **Apply the pending migrations, snapshot-first (rule 14).** The live arena still has M4
   (`Phase35PositionSnapshots`) pending, and Phase 4 adds M5 (`Phase4Replay`):
   `tools/snapshot-db.ps1 -Arena sp500` then `pwsh tools/migrate.ps1 -Arena sp500`.
   (M5's D94 precondition fails loudly if any `corporate_actions.processed_on` was ever written —
   it never was; a failure there means the store is not what we think and needs investigation.)
   **Expected warning, not an error:** M5 prints *"The migration operation 'PRAGMA foreign_keys = 0;'
   … cannot be executed in a transaction"*. That is EF's standard SQLite TABLE-REBUILD pattern for
   the D93 `regime_labels` PK change — a PRAGMA is a no-op inside a transaction, so EF must toggle
   it outside one (and this arena runs with foreign keys OFF anyway, finding 145, so the toggle
   guards nothing here). The rebuild's DROP+RENAME is still its own atomic transaction; the risk the
   warning describes (a kill between the two transactions leaves `ef_temp_regime_labels` behind and
   the re-run fails on it) is exactly what the snapshot taken in this same step is for — restore it
   and re-run. The step succeeded iff the script ends with the green `Migration applied to arena …`
   line; verify with `dotnet dotnet-ef migrations list --connection … --no-build` (no `(Pending)`
   marks). Applied to the live arena 2026-07-23.
2. **The D70 historical backfill** (hours; EODHD spend ~3 calls/name — check the headroom):
   `dotnet run --project tools/Backfill -- --historical sp500 --from <window-start> --to <window-end>`
   The window must cover >= Replay.ValidationYears (15y). The fja05680 CSV comes from
   `Backfill:HistoricalMembershipUrl` (point it at the real URL or a downloaded copy — the committed
   value is the test fixture). Review the coverage artifact
   (`docs/calibration/sp500/historical-coverage-*.json`): gate exclusions and TICKER-REUSE SUSPECTS
   are OUT of the replay universe fail-closed until you resolve them (re-run after fixes — the run is
   idempotent and the artifact is deterministic, so a re-run is a clean git diff). Commit the artifact.
   Do NOT re-run the FORWARD bootstrap (`--universe sp100`) after this until the widening lands: the
   universe-blind reconciler would stamp removed_on on the ~400 non-slice members (P1).
3. **The proxy-only backfill (v1.9.43, finding 274) — the regime warm-up + benchmark depth** (minutes;
   EODHD spend ~2 calls). The D70 pass left GSPC + OEF at the Phase-1 20y window (starting mid-2006), so
   the replay has NO pre-2006-01-03 regime warm-up (D73 needs ≥956 sessions before the start) and a
   ~6-month front gap: `dotnet run --project tools/Backfill -- --proxy-only` (default `--years 25`).
   It extends GSPC + OEF backward and touches NOTHING else (no membership reconcile — the P1 hazard). Then
   VERIFY (the CLI prints this): GSPC has ≥956 distinct sessions before 2006-01-03, OEF a bar on every
   replay session, and a short replay's `regime_labels` begin at/near 2006-01-03.
4. **De-risk BEFORE the ~4-day run (v1.9.42 two-pass fix).** (a) **Stage-1 offline gate:**
   `replay-calibrate --report-only` on the aborted-run store re-scores the stored S3 paths through the
   fixed Pass-2 logic — confirm `noedge_curve_breach_validate < NoEdgeCurveBreachMaxFrac` and
   `curve_based_edge_survival ≥ CurveBasedEdgeSurvivalFloor`, and **commit that report as evidence FIRST**
   (`--reset` deletes the sessions it reads). (b) **Snapshot** (`tools/snapshot-db.ps1 -Arena sp500`) so
   those sessions stay recoverable. (c) **Stage-2 smoke run** (~4-8h, a 1-2y window): assert no plant
   retires and every plant emits rows across the whole window (its verification reads `Insufficient` by
   design — a mechanics smoke test). Only then spend the full run.
5. **The full-scale calibration run** (hours → a day; deterministic; resumable — a crash or stop resumes by
   re-running the same command):
   `dotnet run --project src/AlphaLab.Worker -- replay-calibrate --reset --from <start> --to <end> [--learn-through <boundary>]`
   `--learn-through` is the FR-42 learn/validate split (a runtime parameter, deliberately no CONFIG key);
   the curves build from the learn side, the curve-based validate checks read the validate side.
6. **Review the archived report** (`docs/calibration/sp500/<date>-calibration.md`): the verification table
   must be ALL-GREEN at full scale (Insufficient is only legitimate at CI scale). **v1.9.42 changed how to
   read it:** plants are no longer retired during calibration, so `would_be_edge_survival` (from the
   would-be-retire log) and the curve-based metrics are the survival/false-alarm gates — the documented
   "raise `Monitor.S6.AutoRetireEvals` and re-run" loop was proven NOT to converge (finding 270; it is
   gone). The **per-rung detection-power curve** (`edge_plant_detected` Detail: monthly@2/4/8/16 promotions)
   is the primary finding — read that, not the gate colour; the rule-selected primary is the smallest
   monthly rung clearing the offline floor. Record the `joint_false_alarm` with its comparability caveat
   (it is NOT independent validation post-Change-3 — only the curve-based metric is).
7. **Commit the report** (+ the coverage artifact) and fill the PROGRESS Phase-4 gate box with the
   measured numbers (detection-speed / days-to-indistinguishability medians, would-be-survival + curve-based
   fractions, the per-rung detection-power outcome, the joint false-alarm fraction with per-signal
   contributions + its comparability caveat).
8. **The D87 sign-off item:** record in PROGRESS whether a verified-depth S&P 400/600 as-of-membership
   source is confirmed (the S&P 1500 widening target) — else the S&P 500 stands. Verified at sign-off,
   never silently at the flip.
9. **After sign-off:** `Replay.PrunePerMemberLedgersAfterSignoff` sanctions pruning the per-member
   replay ledgers (control_equity + plant equity rows); the runs, power_reports, frozen curves and the
   report stay. The forward widen (`Universe:Bootstrap:Universe` flip + backfill delta) remains a
   SEPARATE post-sign-off action.

The walk-forward seeding mode (`IBacktestEngine`) shares the replay generation rules: a seeding run
over an evaluated generation (or vice versa) needs `--reset` — mixing evaluated and unevaluated days
in one generation corrupts its cadence bookkeeping.
