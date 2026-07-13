# AlphaLab — Complete Design Package (v1.9.7)

This is the **full, self-contained** design package — everything needed to build AlphaLab from
scratch. It supersedes the v1.9.6 upload: all v1.9.1 → v1.9.7 consistency fixes and the
multi-arena capability (D71, now fully propagated through the build scaffolding as FR-37) are merged
**in place**, so every file here is current. Nothing external is required.

Start with `START_HERE.md`, then `docs/README_v1.9.md` (the file map and how to drive the build).

## What's in here

**Orientation**
- `START_HERE.md` — the entry point.
- `docs/README_v1.9.md` — file map, mockup guide, and the step-by-step build workflow.
- `docs/CLAUDE.md` — hard rules, solution layout, commands (the constitution the build obeys).
- `docs/AlphaLab_Explained_Plain_Language.pdf` — a plain-language explainer for a non-specialist.

**The design**
- `docs/MASTER_DESIGN_v1.9.md` (+ `.pdf`) — the comprehensive document: decisions D1–D73,
  architecture, golden rules, math appendix, UI boundary. *(The two PDFs are reading-convenience
  snapshots; the `.md` files are authoritative and carry the v1.9.4 fixes.)*
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
- `docs/PROGRESS.md` — the phase-gate checklist to tick as you go.
- `docs/SETUP_v1.9.md` — day-zero environment + provider setup.
- `docs/RUNBOOK_v1.9.md` — operating the lab, backups, and running more than one arena.
- `docs/DB_RELOCATION.md` — ops runbook for moving the SQLite file(s) to another directory/drive
  (a config edit + file move; the deployed base is `E:\AlphaLabDatabase`).
- `docs/FUTURE_DB_MIGRATION.md` — contingency plan for ever leaving SQLite for a server RDBMS
  (a different job from relocation; closed until needed).

**Strategy & evaluation detail**
- `docs/STRATEGY_CATALOG_v1.9.md` — the strategy families and the equal-weight benchmark.
- `docs/OVERFITTING_MONITOR_v1.9.md` — the eight-signal overfitting monitor.
- `docs/DESIGN_IMPROVEMENTS_v1.9.md` — the honest-metrics rationale and power tables.
- `docs/UX_GUIDELINES_v1.9.md` — the UX honesty rules (UX-1…UX-13, incl. the arena no-merge rule).

**UI mockups (reference for the Phase 3 screens)**
- `docs/paper_trading_ui_mockups.html`
- `docs/decision_and_strategies_mockups.html`
- `docs/allocation_journal_ops_ux_mockups.html`
- `docs/lab_honesty_ux_mockups.html`

**Revision history**
- `docs/CHANGELOG_v1.9.md` — every consistency finding and decision, v1.9.1 through v1.9.8.

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
  the shipped skeleton at commit `462b8fd` (rationale in `docs/CHANGELOG_v1.9.md`; review prose not
  retained): the Blazor client now renders all 13 non-parameterized §21 screens, not 8 (P0-1, the one
  unmet DoD claim); the design-time factory comment matches the `E:`-literal three-spots reality
  (P0-2); `ci.ps1` enforces the **full** reference graph at the `<ProjectReference>` level and its git
  call is EAP-safe (P0-3); the resolver tests assert two-arena path distinctness (P0-4, 39-count
  intact); the review-file references redirect to the CHANGELOG since review prose is not retained as
  files (P0-5); and a missing `Arenas` registry now raises a visible config-error banner instead of a
  silent self-call (P0-6, fail-closed rule 10). No architecture, schema, or decision change; two
  decision proposals (FR-23 hypotheses action; the Phase-4 detection-power sweep) logged in PROGRESS.
- The mockups are byte-identical to the original v1.9.1 upload (never needed changes). SCHEMA
  received its first post-v1.9.1 edit in v1.9.7 (the `config` composite PK + invariant notes).
