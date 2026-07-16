# AlphaLab — v1.9 Documentation Package (README)

*Design revision v1.9 — consolidated and build-ready. Nothing implemented yet; the version is a design revision, not a release.*

*v1.9.1 consistency errata merged (CHANGELOG findings 59–75; decisions D68–D69); v1.9.2 consistency errata merged (findings 76–86; decision D70); v1.9.3 multi-arena capability added (decision D71, companion `ARENA_ARCHITECTURE_v1.9.3.md`); v1.9.4 arena-integration errata merged (findings 92–99; FR-37); v1.9.5 post-Phase-0 errata merged (findings 100–106 — DB base relocated per `DB_RELOCATION.md`, D49 collision repaired, `Api.Bind` retired); v1.9.6 rebuild-safety errata merged (findings 107a–107f — the six Phase-0 fixes back-ported into the BUILD Phase-0 prompt so a from-scratch build is correct first-pass); v1.9.7 deep-dive errata merged (findings 108–121; decisions D72–D73; FR-38 — WAL at schema startup, the versioned `config` PK, OnDemand job drain + crash-safe heartbeat, the named regime proxy feed, edge-plant-survival + joint-false-alarm calibration bounds, turnover-match verification; the review behind the pass is traced finding-by-finding in `CHANGELOG_v1.9.md`, its prose not retained); v1.9.8 Phase-0 skeleton review errata merged (findings 122–127 = P0-1…P0-6 — the client renders all 13 non-parameterized §21 screens, `ci.ps1` enforces the full reference graph with an EAP-safe git call, the factory comment matches the `E:`-literal reality, the resolver tests assert two-arena distinctness, and a missing `Arenas` registry fails closed with a visible banner; review prose not retained); v1.9.9 Phase-1 completion reconciliation merged (findings 128–137; decisions **D74–D75** — Phase 1 shipped, checkpoints 1.0–1.10: index-membership drop ≠ delisting, the canonical EODHD dash-form ticker identity, and the first live backfill's provider findings); v1.9.10 + v1.9.11 Phase-1 review remediation merged (findings 138–152 — two fresh-eyes code reviews fixing fail-open defects, config-consistency + CI-hygiene gaps, the read-only `--preflight` live-source check, and the `REBUILD.md` runbook; no schema/config-key change; three schema-change proposals + the S&P 500 widening gap parked for **D76**); v1.9.12 doc/config reconciliation merged (findings 153–159 — this errata narrative + the doc indexes rolled current, the Backfill CLI's `Eodhd`/`Backfill` config sections documented; no schema/config-key change); v1.9.13 pre-Phase-2 schema decisions merged (findings 160–162; decisions **D76–D78** — `corporate_actions` versioned + read-at-watermark (D76, closing the Phase-4 replay future-leak), a `data_quality_flags` table (D77), and a cross-sectional bar read + `ix_bars_date` (D78); three snapshot-first EF migrations; decided range now D1–D78; 236 tests); v1.9.14 membership provenance merged (finding 163, contract-only — the two index rosters archive under a dated `asOf` instead of `"latest"`, mirroring P1R-4; no schema/D-number; resolves one of the two open proposals, only the S&P 500 widening stays open; 237 tests); v1.9.15 Phase-0/1 BUILD/CONFIG reconciliation merged (findings 164–167 — the Phase-1 heading's dropped FR-38, a stale `GSPC.INDX` index-EOD ⚠VERIFY already resolved in INTEGRATIONS §9, the three-vs-four connection-string-spot split (finding-138 straggler, fixed phase-aware), and the cost model misdated to Phase 1→2; docs only, no schema/config-key/test change, 237 tests; a fifth item — D42's Phase-2/6 claim — reported for a phasing decision, finding 168); v1.9.16 FR-11 sizing phasing merged (finding 169 — resolves the finding-168 report: FR-11 / D42 Ledoit–Wolf covariance sizing, claimed by both Phase 2 and Phase 6, split partial→full per the FR-13/FR-18 convention — Phase 2 (partial) / Phase 6 full; docs only, no schema/config-key/test change, 237 tests); file names retain the v1.9 label.*

*This package is sufficient for building the entire system through Claude Code, solo, with no undocumented decisions. The gap-closure pass (decisions D50–D56) is already merged into every document — there is no separate addendum to reconcile.*

## 1. What each file is, in plain terms

**Tier 1 — Design (what to build and why):**
| Doc | Role |
|---|---|
| `ARENA_ARCHITECTURE_v1.9.3.md` | **How AlphaLab supports multiple isolated universes ("arenas").** Defines D71: one universe per arena, separate DB + process per arena, arena-scoped calibration, an arena-switcher frontend that never merges leaderboards, and a step-by-step "add an arena" checklist. Additive; no schema change; the S&P 500 build is unaffected. |
| `MASTER_DESIGN_v1.9.md` | **The comprehensive document.** Decisions D1–D78, architecture, the daily funnel, data sourcing, golden rules, the plain-language math appendix (§19), the gap-closure specs (§20), and the **UI-boundary specs (§21 `AlphaLab.Api`, §22 honesty read-models)** that make the front end swappable |
| `STRATEGY_CATALOG_v1.9.md` | Every strategy's exact spec, the `IModel` contract, acceptance criteria |
| `DESIGN_IMPROVEMENTS_v1.9.md` | Metrics/evaluation math in full, factor research, sizing/costs, LLM economics, Arena Replay, the power-reality tables |
| `DESIGN_IMPROVEMENTS_EXPLAINED.md` | The plain-language "why" companion to `DESIGN_IMPROVEMENTS_v1.9.md` (onboarding; section numbers match the spec) |
| `OVERFITTING_MONITOR_v1.9.md` | The eight anti-self-deception signals, statuses, wiring, MDE derivation |
| `BUILD_AND_PROMPTS_v1.9.md` | FR-1…FR-38, the gated phase plan, and the **ready-to-paste Claude Code prompt for each phase** |

**Tier 2 — Build scaffolding (what stops Claude Code from improvising):**
| Doc | Role |
|---|---|
| `SETUP_v1.9.md` | **Read first, before any code.** Prerequisites, accounts, secrets, day-zero verification checklist |
| `CLAUDE.md` | **(repo root)** — the standing rules every Claude Code session loads automatically |
| `PROGRESS.md` | **(repo root)** — session log + phase-gate checklists |
| `SCHEMA_v1.9.md` | Full SQLite DDL — the single source of truth for every table |
| `CONFIG_REFERENCE_v1.9.md` | Every config key, default, unit, and owning decision |
| `INTEGRATIONS_v1.9.md` | Exact external endpoints (EODHD, IVV CSV, Ken French, FRED, Anthropic, Alpaca) with ⚠VERIFY flags |
| `TEST_PLAN_v1.9.md` | The fixture library + FR-mapped test inventory (§8 = the canonical 39-case Phase-0 inventory a rebuild must reproduce) |
| `UX_GUIDELINES_v1.9.md` | The thirteen interface rules (UX-1…UX-13) as build specs |
| `RUNBOOK_v1.9.md` | Operations: daily cycle, catch-up, backups, incident playbook |
| `DB_RELOCATION.md` | Ops runbook: moving the SQLite database file(s) to another directory/drive — a config edit + file move, guarded by `ConfigConsistencyTests`. The deployed base is a literal absolute path (`E:\AlphaLabDatabase`); the `{Arena.Id}` token stays regardless of base |
| `FUTURE_DB_MIGRATION.md` | Contingency: what changes if SQLite is ever replaced by a server RDBMS — a different job from relocation, kept closed until needed |
| `REBUILD.md` | Ops runbook: from a fresh clone to a working arena (the *data* bootstrap; sibling to `DB_RELOCATION.md` / `FUTURE_DB_MIGRATION.md`; includes the `--preflight` live-source check) |
| `CHANGELOG_v1.9.md` | Review-finding → decision → doc-section traceability across all design passes — including the *why* behind every v1.9.7–v1.9.11 change (review prose itself is not retained; findings are folded into the docs) |

## 2. The four HTML mockups — what they are and how they're used
`paper_trading_ui_mockups.html`, `decision_and_strategies_mockups.html`, `lab_honesty_ux_mockups.html`, `allocation_journal_ops_ux_mockups.html`

These are **static visual references, not code to run or deploy**. Open any of them in a browser to *see* what each screen should look like. Their job in the build: when a phase prompt says "per `lab_honesty_ux_mockups.html`", Claude Code opens that file from `docs/`, reads its HTML/CSS, and reproduces the layout, palette, and components in the real Blazor pages. You never edit them; the enforceable rules they illustrate live in `UX_GUIDELINES_v1.9.md`, which the phase prompts cite. In short: **mockups = the picture, UX_GUIDELINES = the rules, the phase prompt = the instruction to build it.**

## 3. How to drive Claude Code with this package (step by step)

1. **Read `SETUP_v1.9.md` and complete it** (accounts, secrets, day-zero endpoint checks). ~1 hour, free tier.
2. **Get the repo.** This repository is the pre-code baseline — clone it (or copy its contents into a fresh private repo). The layout is already in place, **nothing to copy or rearrange**: `CLAUDE.md` + `PROGRESS.md` at the **root**, the design set (all `.md`) and the four mockups (`.html`) under `docs/`, no code yet. (Starting a fresh repo? commit the baseline: "docs v1.9, pre-code baseline".)
3. **Open Claude Code in the repo, turn Plan Mode on**, and paste the **Phase 0 prompt** from `BUILD_AND_PROMPTS_v1.9.md` §4, verbatim. Review the plan it proposes, approve, let it execute.
4. **One phase per work stream, in order (0 → 1 → 2 → 3 → 3.5 → 4 → 5 → 6 → 7 → 8).** No phase starts while the previous phase's Definition of Done is red. Each phase's prompt cites the FRs and the exact doc sections to read.
5. **Per-phase doc diet** — tell Claude Code to read only these (keeps context lean):
   - Phase 0: BUILD §1–3, MASTER §4–5 + §21–22 (stack/architecture + the UI boundary + Worker/API process model, D57–D60), ARENA_ARCHITECTURE_v1.9.3 §3–4 (the {Arena.Id} path token + the Web arena registry, FR-37), SCHEMA (infra tables + D69 note), this README, CLAUDE.md
   - Phase 1: MASTER §13–14 + §20.5, SCHEMA, INTEGRATIONS, TEST_PLAN §2
   - Phase 2: MASTER §6, §13.6–13.7, §20.1, §20.4, CATALOG §2–3, §5.1, CONFIG (Costs/Regime), TEST_PLAN §3
   - Phase 3: CATALOG §5.2, DESIGN_IMPROVEMENTS §1 + §3.5, MONITOR S2/S3/S6 + App. C, MASTER §20.2–20.3 + §21–22, UX_GUIDELINES UX-1..6/9/10, lab_honesty mockup, TEST_PLAN §4
   - Phase 3.5: RUNBOOK §3–4
   - Phase 4: DESIGN_IMPROVEMENTS §5, MONITOR §0 + App. A, MASTER §20.7, TEST_PLAN §5
   - Phase 5: MASTER §7 + §20.4, DESIGN_IMPROVEMENTS §4, INTEGRATIONS §5, CONFIG (Llm)
   - Phase 6: CATALOG §6, §8–9, DESIGN_IMPROVEMENTS §1.3–1.4, §3, INTEGRATIONS §3–4
   - Phase 7: MASTER §15 + §20.6, RUNBOOK, UX_GUIDELINES UX-7..11, all four mockups
   - Phase 8: CATALOG §7 (the gate first), INTEGRATIONS §1 (fundamentals row)
6. **End every session:** tests green (or honestly red in PROGRESS.md), a PROGRESS.md entry, a commit.
7. **New decisions** (anything the docs don't cover) get the next D-number appended to MASTER §2 in the same table format — the docs stay the single source of truth as the build evolves.

> Research/paper-trading only. Not investment advice.
