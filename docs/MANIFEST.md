# AlphaLab — Complete Design Package (v1.9.16)

This is the **full, self-contained** design package — everything needed to build AlphaLab from
scratch. It supersedes the v1.9.6 upload: all v1.9.1 → v1.9.16 consistency fixes and the
multi-arena capability (D71, now fully propagated through the build scaffolding as FR-37) are merged
**in place**, so every file here is current. Nothing external is required.

Start with `START_HERE.md`, then `docs/README_v1.9.md` (the file map and how to drive the build).

## What's in here

**Orientation**
- `START_HERE.md` — the entry point.
- `docs/README_v1.9.md` — file map, mockup guide, and the step-by-step build workflow.
- `README.md` (repo root) — the GitHub landing page: pitch, status, architecture, clone/build/run.
- `CLAUDE.md` (repo root) — hard rules, solution layout, commands (the constitution the build obeys).

**The design**
- `docs/MASTER_DESIGN_v1.9.md` — the comprehensive document: decisions D1–D78,
  architecture, golden rules, math appendix, UI boundary.
- `docs/ARENA_ARCHITECTURE_v1.9.3.md` — how AlphaLab supports multiple isolated universes
  ("arenas"); decision D71. Additive, no schema change; the S&P 500 build is unaffected.
- `docs/SCHEMA_v1.9.md` — the exact database schema (the rule-14 source of truth).
- `docs/CONFIG_REFERENCE_v1.9.md` — all config keys, the connection string, secrets model, Arena block.
- `docs/INTEGRATIONS_v1.9.md` — every external data feed, named + validated + fail-closed.

**Build & test**
- `docs/BUILD_AND_PROMPTS_v1.9.md` — FR-1…FR-38, the gated phase plan, and the ready-to-paste
  Claude Code prompt for each phase (Phase 0 hardened for .NET 10 / EF Core 10 and arena-aware
  per FR-37).
- `docs/TEST_PLAN_v1.9.md` — the fixtures and tests each phase must pass (§8 is the canonical
  39-case Phase-0 test inventory; the BUILD Phase-0 prompt is structured as checkpoints 0.1–0.6).
- `PROGRESS.md` (repo root) — the phase-gate checklist to tick as you go.
- `docs/SETUP_v1.9.md` — day-zero environment + provider setup.
- `docs/RUNBOOK_v1.9.md` — operating the lab, backups, and running more than one arena.
- `docs/DB_RELOCATION.md` — ops runbook for moving the SQLite file(s) to another directory/drive
  (a config edit + file move; the deployed base is `E:\AlphaLabDatabase`).
- `docs/FUTURE_DB_MIGRATION.md` — contingency plan for ever leaving SQLite for a server RDBMS
  (a different job from relocation; closed until needed).
- `docs/REBUILD.md` — ops runbook: from a fresh clone to a working arena (the *data* bootstrap;
  sibling to DB_RELOCATION / FUTURE_DB_MIGRATION; includes the `--preflight` live-source check).

**Strategy & evaluation detail**
- `docs/STRATEGY_CATALOG_v1.9.md` — the strategy families and the equal-weight benchmark.
- `docs/OVERFITTING_MONITOR_v1.9.md` — the eight-signal overfitting monitor.
- `docs/DESIGN_IMPROVEMENTS_v1.9.md` — the honest-metrics rationale and power tables.
- `docs/DESIGN_IMPROVEMENTS_EXPLAINED.md` — the plain-language "why" companion to the above (onboarding; section numbers match the spec).
- `docs/UX_GUIDELINES_v1.9.md` — the UX honesty rules (UX-1…UX-13, incl. the arena no-merge rule).

**UI mockups (reference for the Phase 3 screens)**
- `docs/paper_trading_ui_mockups.html`
- `docs/decision_and_strategies_mockups.html`
- `docs/allocation_journal_ops_ux_mockups.html`
- `docs/lab_honesty_ux_mockups.html`

**Revision history**
- `docs/CHANGELOG_v1.9.md` — every consistency finding and decision, v1.9.1 through v1.9.16.

## Revision state
- v1.9.1 errata (findings 59–75; D68–D69) — merged.
- v1.9.2 errata (findings 76–86; D70) — merged.
- v1.9.3 multi-arena capability (findings 87–91; D71) + Phase 0 hardening — merged.
- v1.9.4 arena-integration consistency errata (findings 92–99; FR-37) — merged. Propagates D71
  through the build scaffolding: Phase 0 resolves the `{Arena.Id}` path token, the Web client
  carries the one-entry `Arenas` registry (no bare `Api:BaseUrl`), the API port lives in the
  committed `Urls` key, and the stale UX/D/FR ranges are repaired.
- v1.9.5 post-Phase-0 consistency errata (findings 100–106) — merged. Recorded after Phase 0
  shipped: the database base relocated to a literal absolute path (`E:\AlphaLabDatabase`) per the
  new `docs/DB_RELOCATION.md`, with every doc now stating the base as configurable; the Phase-8
  fundamentals decision takes the next free D-number (the D49 collision repaired); the dead
  `Api.Bind` key retired in favor of `Urls`; the RUNBOOK's migration-guard claim aligned to the
  actual `tools/migrate.ps1` contract; ARENA §5 pinned to per-arena config directories (D67);
  `DB_RELOCATION.md` + `FUTURE_DB_MIGRATION.md` added to every documentation map.
- v1.9.6 rebuild-safety errata (findings 107a–107f) — merged. Back-ports the six Phase-0
  code-review fixes (reader-skips-dir-create Api DB wiring; a shared SchemaStartup that migrates in
  both Worker modes; corrected launch profiles/ports; no dead `appsettings.Development.json` /
  `Api:Bind`; hardened CI greps) into the BUILD Phase-0 prompt + DoD + PROGRESS gate + TEST_PLAN,
  so a from-scratch build is correct on the first pass. No schema or decision change.
- v1.9.7 deep-dive errata (findings 108–121; decisions D72–D73; FR-38) — merged. A full review of
  the design + Phase-0 code (rationale traced in `docs/CHANGELOG_v1.9.md`; review prose not retained): WAL is established and verified at schema
  startup; `config` gains the composite `(key, version)` PK so versioned config rows are
  implementable; the Worker's process model is completed (OnDemand drains queued jobs; a crashed
  `run_in_progress` flag is heartbeat-recovered — D72); the regime proxy becomes a named, validated,
  fallback-bearing feed (D73/FR-38, INTEGRATIONS §9); the Phase-4 calibration gains an
  edge-plant-survival floor and a joint any-signal false-alarm bound; the control populations gain
  turnover-match verification; the allocator floor gets its feasibility rule; and every Phase-0 fix
  is back-ported into the BUILD Phase-0 prompt so a from-scratch rebuild is correct first-pass.
- v1.9.8 Phase-0 skeleton review errata (findings 122–127 = P0-1…P0-6) — merged. A second review, of
  the shipped Phase-0 skeleton (rationale in `docs/CHANGELOG_v1.9.md`; review prose not
  retained): the Blazor client now renders all 13 non-parameterized §21 screens, not 8 (P0-1, the one
  unmet DoD claim); the design-time factory comment matches the `E:`-literal three-spots reality
  (P0-2); `ci.ps1` enforces the **full** reference graph at the `<ProjectReference>` level and its git
  call is EAP-safe (P0-3); the resolver tests assert two-arena path distinctness (P0-4, 39-count
  intact); the review-file references redirect to the CHANGELOG since review prose is not retained as
  files (P0-5); and a missing `Arenas` registry now raises a visible config-error banner instead of a
  silent self-call (P0-6, fail-closed rule 10). No architecture, schema, or decision change; two
  decision proposals (FR-23 hypotheses action; the Phase-4 detection-power sweep) logged in PROGRESS.
- v1.9.9 Phase-1 completion doc-reconciliation (findings 128–137; decisions **D74–D75**) — merged. Phase 1
  shipped (checkpoints 1.0–1.10); its two decided-but-unnumbered decisions are recorded —
  index-membership drop ≠ delisting (**D74**) and the canonical EODHD dash-form ticker identity via
  `SymbolNormalizer` (**D75**) — with the first live backfill's findings: the Wikipedia descriptive-`User-Agent`
  provider rule, the aborted-run usage-flush-in-`finally`, and the EODHD per-endpoint call-cost table + the
  1,000-req/min limit (INTEGRATIONS §1 **VERIFIED 2026-07-15**, with an endpoint-weighting requirement raised
  for Phase 2). No architecture or schema change; the two open review proposals (C-6, C-1) stay undecided in PROGRESS.
- v1.9.10 Phase-1 review remediation (findings 138–146) — merged. A second fresh-eyes review of the sealed
  Phase-1 repo fixed fail-open code defects (dividend cash fails closed on a null `unadjustedValue`;
  `api_usage_log` accumulates + headroom checks the day total; `GetSeries` date-range pushdown; raw payloads
  archive under the observation day; the documented 30-day raw-cache retention implemented) plus a
  `ConfigConsistencyTests` fourth-copy guard and a GitHub Actions CI mirror + report-only vuln audit — no
  schema/migration/config-key change. Three schema-change proposals (extend D40 to `corporate_actions`; a
  `data_quality_flags` table; a cross-sectional bar read path + `ix_bars_date`) parked for **D76**.
- v1.9.11 Phase-1 review remediation, cont. (findings 147–152) — merged. A third pass corrected a live
  REBUILD §5 arena-id error (shipped alone), added an `Arena:Id` cross-process guard, rejected `--universe
  sp500` at parse, added a read-only `--preflight` live-source check, and registered/reconciled `REBUILD.md`
  — no schema/migration/config-key change. The S&P 500 widening gap (finding 151) parked for **D76**.
- v1.9.12 doc/config reconciliation (findings 153–159) — merged. Rolled the version narrative (this title +
  Revision state, MASTER §version-note, START_HERE, README) and the CHANGELOG-coverage line to v1.9.12; added
  the missing `REBUILD.md` + `DESIGN_IMPROVEMENTS_EXPLAINED.md` rows to `docs/README_v1.9.md` and the root
  `README.md` to this file list; corrected two line-number-as-section refs in PROGRESS (§13.5, §15), the stale
  root-README test count (200 → 223), and the resolved push-state note; documented the Backfill CLI's
  `Eodhd`/`Backfill` config sections in `CONFIG_REFERENCE`. No schema or decision change; finding 151's
  D70-widening `CONFIG_REFERENCE` claim stays parked as an open proposal. (This roll supersedes finding 152's
  deliberately-unrolled manifest title.)
- v1.9.13 pre-Phase-2 schema decisions (findings 160–162; decisions **D76–D78**) — merged. Settles the three
  parked proposals Phase 2 builds on, each a snapshot-first EF migration (rule 14) with SCHEMA updated in the
  same pass: **D76** — `corporate_actions` versioned append-only + read-at-watermark (mirrors bars/D40; closes
  the Phase-4 replay future-leak and preserves dividend restatements); **D77** — a `data_quality_flags` table +
  store seam so the FR-6 gate's findings persist and reach §15's Data-health screen; **D78** — a cross-sectional
  (date-major) bar read + `ix_bars_date`. Decided range now **D1–D78**; the S&P 500 widening and membership
  provenance stay open (un-numbered; "D76 territory" retired now the cluster is split). Test count 223 → 236.
- v1.9.14 membership provenance (finding 163) — merged. **Contract-only, no schema, no D-number.** The two
  membership rosters (iShares OEF/IVV holdings, Wikipedia cross-check) archived their raw payloads under a
  literal `"latest"` that overwrote every run, so "what did the index report on date X" was unanswerable.
  Threaded an observation-date `asOf` into `IIndexMembershipProvider.GetMembersAsync` and archive under it
  (dated partitions) — mirroring the P1R-4 equity/proxy fix. Resolves one of the two remaining open proposals
  (only the S&P 500 widening stays open). Test count 236 → 237. (This pass also caught two v1.9.13 narrative
  stragglers: this title/body + the CHANGELOG-coverage line were still at v1.9.12, and the root `README.md`
  test count still read 223.)
- v1.9.15 Phase-0/1 BUILD/CONFIG reconciliation (findings 164–167) — merged. **Docs only, no schema, no
  migration, no config-key, no test change; count stays 237.** A fresh-eyes review of the Phase-0/1 build
  prompts against SCHEMA/CONFIG/INTEGRATIONS/TEST_PLAN/MASTER fixed four inconsistencies: the Phase-1 heading
  dropped FR-38 (164); a stale `GSPC.INDX` index-EOD ⚠VERIFY that INTEGRATIONS §9 had already resolved (165);
  the connection-string copy-count split three-vs-four across BUILD 0.3 / CONFIG / TEST_PLAN — a finding-138
  straggler, fixed phase-aware (three at Phase 0, four from the Phase-1 Backfill CLI) (166); and the cost model
  misdated to Phase 1 instead of Phase 2 (167). A fifth item — D42 Ledoit–Wolf covariance claimed by both
  Phase 2 (FR-11) and Phase 6 — is **reported, not fixed** (168), needing a BUILD phasing decision.
- v1.9.16 FR-11 sizing phasing (finding 169) — merged. **Docs only, no schema/migration/config-key/test
  change; count stays 237.** Resolves the finding-168 report: FR-11 (inverse-vol sizing using Ledoit–Wolf
  covariance, D42) was claimed by both Phase 2 and Phase 6. Split partial→full per the FR-13/FR-18 convention
  (grounded in the `Sizing.Mode` enum — `inverse_vol` / `equal(dummies)` / `kelly(P6+)` — and DESIGN_IMPROVEMENTS
  §3.1): Phase 2 gets FR-11 "(partial)" (the dummies' simple/equal sizing), Phase 6 gets FR-11 "full" (inverse-vol
  + LW covariance). A BUILD-phasing edit, not a new decision; D42 unchanged.
- The mockups are byte-identical to the original v1.9.1 upload (never needed changes). SCHEMA
  received its first post-v1.9.1 edit in v1.9.7 (the `config` composite PK + invariant notes).
