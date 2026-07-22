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
- Pre-migration: a snapshot is mandatory before any EF migration — and enforced **by construction**: `tools/migrate.ps1` is the ONLY sanctioned schema-application path (rule 14) and performs the `tools/snapshot-db.ps1` snapshot itself as its first step (snapshot → `dotnet ef database update`). Never run `dotnet ef database update` directly against a non-empty DB.
- Off-machine: weekly copy of the latest backup to cloud/external storage (manual or scheduled; log it in PROGRESS.md monthly).

## 4. Restore drill (rehearse quarterly — logged in PROGRESS.md)
1. Stop the app. 2. Copy target backup over `alphalab.db` (keep the broken file as `alphalab-broken-{date}.db`). 3. Start; the app detects the DB date < today and runs catch-up automatically. 4. Verify: equity curves continuous, `runs` gapless after catch-up, spot-check one account's trades vs the go-live log. The drill *is* the test that backups work; an unrehearsed backup is a hope, not a plan.

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
