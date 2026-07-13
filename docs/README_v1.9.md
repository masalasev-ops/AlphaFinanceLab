# AlphaLab — v1.9 Documentation Package (README)

*Design revision v1.9 — consolidated and build-ready. Nothing implemented yet; the version is a design revision, not a release.*

*v1.9.1 consistency errata merged (CHANGELOG findings 59–75; decisions D68–D69); v1.9.2 consistency errata merged (findings 76–86; decision D70); v1.9.3 multi-arena capability added (decision D71, companion `ARENA_ARCHITECTURE_v1.9.3.md`); v1.9.4 arena-integration errata merged (findings 92–99; FR-37); v1.9.5 post-Phase-0 errata merged (findings 100–106 — DB base relocated per `DB_RELOCATION.md`, D49 collision repaired, `Api.Bind` retired); v1.9.6 rebuild-safety errata merged (findings 107a–107f — the six Phase-0 fixes back-ported into the BUILD Phase-0 prompt so a from-scratch build is correct first-pass); v1.9.7 deep-dive errata merged (findings 108–121; decisions D72–D73; FR-38 — WAL at schema startup, the versioned `config` PK, OnDemand job drain + crash-safe heartbeat, the named regime proxy feed, edge-plant-survival + joint-false-alarm calibration bounds, turnover-match verification; the review behind the pass is archived at `reviews/DEEP_DIVE_REVIEW_2026-07-12.md`); file names retain the v1.9 label.*

*This package is sufficient for building the entire system through Claude Code, solo, with no undocumented decisions. The gap-closure pass (decisions D50–D56) is already merged into every document — there is no separate addendum to reconcile.*

## 1. What each file is, in plain terms

**Tier 1 — Design (what to build and why):**
| Doc | Role |
|---|---|
| `ARENA_ARCHITECTURE_v1.9.3.md` | **How AlphaLab supports multiple isolated universes ("arenas").** Defines D71: one universe per arena, separate DB + process per arena, arena-scoped calibration, an arena-switcher frontend that never merges leaderboards, and a step-by-step "add an arena" checklist. Additive; no schema change; the S&P 500 build is unaffected. |
| `MASTER_DESIGN_v1.9.md` | **The comprehensive document.** Decisions D1–D73, architecture, the daily funnel, data sourcing, golden rules, the plain-language math appendix (§19), the gap-closure specs (§20), and the **UI-boundary specs (§21 `AlphaLab.Api`, §22 honesty read-models)** that make the front end swappable |
| `STRATEGY_CATALOG_v1.9.md` | Every strategy's exact spec, the `IModel` contract, acceptance criteria |
| `DESIGN_IMPROVEMENTS_v1.9.md` | Metrics/evaluation math in full, factor research, sizing/costs, LLM economics, Arena Replay, the power-reality tables |
| `OVERFITTING_MONITOR_v1.9.md` | The eight anti-self-deception signals, statuses, wiring, MDE derivation |
| `BUILD_AND_PROMPTS_v1.9.md` | FR-1…FR-38, the gated phase plan, and the **ready-to-paste Claude Code prompt for each phase** |

**Tier 2 — Build scaffolding (what stops Claude Code from improvising):**
| Doc | Role |
|---|---|
| `SETUP_v1.9.md` | **Read first, before any code.** Prerequisites, accounts, secrets, day-zero verification checklist |
| `CLAUDE.md` | **→ copy to repo root.** The standing rules every Claude Code session loads automatically |
| `PROGRESS.md` | **→ copy to repo root.** Session log + phase-gate checklists |
| `SCHEMA_v1.9.md` | Full SQLite DDL — the single source of truth for every table |
| `CONFIG_REFERENCE_v1.9.md` | Every config key, default, unit, and owning decision |
| `INTEGRATIONS_v1.9.md` | Exact external endpoints (EODHD, IVV CSV, Ken French, FRED, Anthropic, Alpaca) with ⚠VERIFY flags |
| `TEST_PLAN_v1.9.md` | The fixture library + FR-mapped test inventory (§8 = the canonical 39-case Phase-0 inventory a rebuild must reproduce) |
| `UX_GUIDELINES_v1.9.md` | The thirteen interface rules (UX-1…UX-13) as build specs |
| `RUNBOOK_v1.9.md` | Operations: daily cycle, catch-up, backups, incident playbook |
| `DB_RELOCATION.md` | Ops runbook: moving the SQLite database file(s) to another directory/drive — a config edit + file move, guarded by `ConfigConsistencyTests`. The deployed base is a literal absolute path (`E:\AlphaLabDatabase`); the `{Arena.Id}` token stays regardless of base |
| `FUTURE_DB_MIGRATION.md` | Contingency: what changes if SQLite is ever replaced by a server RDBMS — a different job from relocation, kept closed until needed |
| `CHANGELOG_v1.9.md` | Review-finding → decision → doc-section traceability across all design passes |
| `reviews/DEEP_DIVE_REVIEW_2026-07-12.md` | The archived deep-dive review behind the v1.9.7 errata (findings R-1…R-18 with full rationale) — read when you want the *why* behind a v1.9.7 change |

## 2. The four HTML mockups — what they are and how they're used
`paper_trading_ui_mockups.html`, `decision_and_strategies_mockups.html`, `lab_honesty_ux_mockups.html`, `allocation_journal_ops_ux_mockups.html`

These are **static visual references, not code to run or deploy**. Open any of them in a browser to *see* what each screen should look like. Their job in the build: when a phase prompt says "per `lab_honesty_ux_mockups.html`", Claude Code opens that file from `docs/`, reads its HTML/CSS, and reproduces the layout, palette, and components in the real Blazor pages. You never edit them; the enforceable rules they illustrate live in `UX_GUIDELINES_v1.9.md`, which the phase prompts cite. In short: **mockups = the picture, UX_GUIDELINES = the rules, the phase prompt = the instruction to build it.**

## 3. How to drive Claude Code with this package (step by step)

1. **Read `SETUP_v1.9.md` and complete it** (accounts, secrets, day-zero endpoint checks). ~1 hour, free tier.
2. **Create a private GitHub repo.** Copy `CLAUDE.md` and `PROGRESS.md` to the repo **root**; copy everything else in this package (all `.md` + all `.html`) into `docs/`. Commit: "docs v1.9, pre-code baseline".
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
