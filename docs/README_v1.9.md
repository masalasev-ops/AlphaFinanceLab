# AlphaLab — v1.9 Documentation Package (README)

*Design revision v1.9. Build status is live, not pre-implementation — the build is well past the phases this package specifies. Read no phase, count, or D-range from this file: `PROGRESS.md` holds the current phase, test count and the open-item list; `docs/CHANGELOG_v1.9.md` holds the full pass-by-pass history and every finding; `docs/DECISIONS_v1.9.md` is the decision register **and is the count**.*

*This package is sufficient for building the entire system through Claude Code, solo, with no undocumented decisions. The gap-closure pass (decisions D50–D56) is already merged into every document — there is no separate addendum to reconcile.*

## 1. What each file is, in plain terms

**Tier 1 — Design (what to build and why):**
| Doc | Role |
|---|---|
| `ARENA_ARCHITECTURE_v1.9.3.md` | **How AlphaLab supports multiple isolated universes ("arenas").** Defines D71: one universe per arena, separate DB + process per arena, arena-scoped calibration, an arena-switcher frontend that never merges leaderboards, and a step-by-step "add an arena" checklist. Additive; no schema change; the S&P 500 build is unaffected. |
| `DECISIONS_v1.9.md` | **The decision register** (extracted from MASTER §0/§2 in v1.9.90) — the register itself is the count, never a number quoted here — plus the design-refinement history that narrates how it was reached |
| `MASTER_DESIGN_v1.9.md` | **The comprehensive document.** Architecture, the daily funnel, data sourcing, golden rules, the plain-language math appendix (§19), the gap-closure specs (§20), the **UI-boundary specs (§21 `AlphaLab.Api`, §22 honesty read-models)** that make the front end swappable, the AI-seat spec (§23), and the Signal Library spec (§24, D91) |
| `STRATEGY_CATALOG_v1.9.md` | Every strategy's exact spec, the `IModel` contract, acceptance criteria |
| `DESIGN_IMPROVEMENTS_v1.9.md` | Metrics/evaluation math in full, factor research, sizing/costs, LLM economics, Arena Replay, the power-reality tables |
| `DESIGN_IMPROVEMENTS_EXPLAINED.md` | The plain-language "why" companion to `DESIGN_IMPROVEMENTS_v1.9.md` (onboarding; section numbers match the spec) |
| `OVERFITTING_MONITOR_v1.9.md` | The eight anti-self-deception signals, statuses, wiring, MDE derivation |
| `BUILD_AND_PROMPTS_v1.9.md` | FR-1…FR-47, the gated phase plan, and the **ready-to-paste Claude Code prompt for each phase** |
| `POST_PHASE8_IMPROVEMENTS.md` | The post-Phase-8 improvement roadmap: what each candidate improvement is and why it earns its slot (companion: `POST_PHASE8_PLAN.md` holds the build sequence) |
| `POST_PHASE8_PLAN.md` | The post-Phase-8 build sequence: the passes in order, the hooks that exist when post-8 begins (including the Phase 4.5 signal digest, D91), and the open items |

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
| `UX_GUIDELINES_v1.9.md` | The twenty interface rules (UX-1…UX-20) as build specs |
| `UX_DESIGN_SYSTEM_v1.9.md` | The component catalogue: each honesty read-model field → its Blazor component, element, and token treatment (entries 1–19). The visual-assembly layer *under* `UX_GUIDELINES_v1.9.md` — it references those tokens and rules, never redefines them |
| `RUNBOOK_v1.9.md` | Operations: daily cycle, catch-up, backups, incident playbook |
| `DB_RELOCATION.md` | Ops runbook: moving the SQLite database file(s) to another directory/drive — a config edit + file move, guarded by `ConfigConsistencyTests`. The deployed base is a literal absolute path (`E:/AlphaLabDatabase`); separators are normalized to the running OS (v1.9.36), so the same template works on a Linux VM and the `{Arena.Id}` token stays regardless of base |
| `FUTURE_DB_MIGRATION.md` | Contingency: what changes if SQLite is ever replaced by a server RDBMS — a different job from relocation, kept closed until needed |
| `REBUILD.md` | Ops runbook: from a fresh clone to a working arena (the *data* bootstrap; sibling to `DB_RELOCATION.md` / `FUTURE_DB_MIGRATION.md`; includes the `--preflight` live-source check) |
| `CHANGELOG_v1.9.md` | Review-finding → decision → doc-section traceability across all design passes — including the *why* behind every v1.9.7–v1.9.11 change (review prose itself is not retained; findings are folded into the docs) |

## 2. The HTML mockups — what they are and how they're used
`alphalab_ux_mockups.html` (one consolidated file — every screen), plus two standalone panel references under `docs/mockups/`: `cohort_curve_panel.html` (UX-15, D88) and `signal_library_panel.html` (UX-16, D91/D108). The consolidated file absorbs both panels when the UI workstream next regenerates.

These are **static visual references, not code to run or deploy**. Open any of them in a browser to *see* what each screen should look like. Their job in the build: when a phase prompt says "per `alphalab_ux_mockups.html`", Claude Code opens that file from `docs/`, reads its HTML/CSS, and reproduces the layout, palette, and components in the real Blazor pages. You never edit them; the enforceable rules they illustrate live in `UX_GUIDELINES_v1.9.md`, which the phase prompts cite. In short: **mockups = the picture, UX_GUIDELINES = the rules, the phase prompt = the instruction to build it.**

## 3. How to drive Claude Code with this package (step by step)

1. **Read `SETUP_v1.9.md` and complete it** (accounts, secrets, day-zero endpoint checks). ~1 hour, free tier.
2. **Get the repo.** Clone the repository. The layout is already in place, **nothing to copy or rearrange**: `CLAUDE.md` + `PROGRESS.md` at the **root**, the design set (all `.md`) and the mockups (`.html`) under `docs/`. The reference build is at **Phase 6, checkpoint 6.5a** (v1.9.113) — Phases 0–5.5 are done and Phase 4 is signed off; `PROGRESS.md` is the live status. For a from-scratch rebuild see `docs/REBUILD.md`.
3. **Open Claude Code in the repo, turn Plan Mode on**, and paste the **Phase 0 prompt** from `BUILD_AND_PROMPTS_v1.9.md` §4, verbatim. Review the plan it proposes, approve, let it execute.
4. **One phase per work stream, in order (0 → 1 → 2 → 3 → 3.5 → 4 → 4.5 → 5 → 6 → 7 → 8).** No phase starts while the previous phase's Definition of Done is red. Each phase's prompt cites the FRs and the exact doc sections to read.
5. **Per-phase doc diet** — tell Claude Code to read only these (keeps context lean):
   - Phase 0: BUILD §1–3, MASTER §4–5 + §21–22 (stack/architecture + the UI boundary + Worker/API process model, D57–D60), ARENA_ARCHITECTURE_v1.9.3 §3–4 (the {Arena.Id} path token + the Web arena registry, FR-37), SCHEMA (infra tables + D69 note), this README, CLAUDE.md
   - Phase 1: MASTER §13–14 + §20.5, SCHEMA, INTEGRATIONS, TEST_PLAN §2
   - Phase 2: MASTER §6, §13.6–13.7, §20.1, §20.4, CATALOG §2–3, §5.1, CONFIG (Costs/Regime), TEST_PLAN §3
   - Phase 3: CATALOG §5.2, DESIGN_IMPROVEMENTS §1 + §3.5, MONITOR S2/S3/S6 + App. C, MASTER §20.2–20.3 + §21–22, UX_GUIDELINES UX-1..6/9/10, `alphalab_ux_mockups.html` (the honesty screens), TEST_PLAN §4
   - Phase 3.5: RUNBOOK §3–4
   - Phase 4: DESIGN_IMPROVEMENTS §5, MONITOR §0 + App. A, MASTER §20.7, TEST_PLAN §5
   - Phase 4.5: MASTER §24, SCHEMA (signals + signal_ic), CONFIG (SignalLibrary), TEST_PLAN §5
   - Phase 5: MASTER **§23 (the authoritative AI-seat spec, incl. §23.8)** + §7 + §20.4 + §24.6, DESIGN_IMPROVEMENTS §4, INTEGRATIONS §5, CONFIG (Llm, Ai, Research), SCHEMA (`analysis_cache`, `news_items`, `llm_budget_log`, `ai_context_packs`, `ai_decisions`, `journal_entries`, `jobs`), TEST_PLAN §6
     *(this line listed only the first four entries until v1.9.60 — it predated D79–D82, D104/D105 and D110, so the diet omitted the section that is now authoritative for the phase, finding 316)*
   - Phase 6: **`docs/phase6/README.md` first** (the checkpoint decomposition), then CATALOG **§2–§3** (the IModel contract + the shared selection stage), **§6**, **§8–9**, **§10** (the anti-clone rule), **§12** (the trials arithmetic that decides who registers on day one) and **§13** (the acceptance checklist this phase’s DoD is written against); DESIGN_IMPROVEMENTS §1.3–1.4, §3; INTEGRATIONS §3–4; **OVERFITTING_MONITOR §2, §3, §3½, §4**; **MASTER §23 in full** (BUILD calls it the authoritative AI-seat spec) and **§24.5–24.6**; **DECISIONS_v1.9** (D124–D154 are all new since the last phase — twenty-two of them landed INSIDE Phase 6, including the thirteen 6.5a remediation rows D142–D154, so a reader who stops at D132 misses the corrections to the very rails this phase builds on); **TEST_PLAN §6–§7**; **UX_GUIDELINES UX-5, UX-17–UX-20**; CONFIG_REFERENCE and SCHEMA as the two sources of truth they are. *(Corrected at 6.1, v1.9.94 — finding 316 recurring: the old line named four sections and omitted the spec BUILD itself calls authoritative.)*
   - Phase 7: MASTER §15 + §20.6, RUNBOOK, UX_GUIDELINES UX-7..11 + UX-16, `alphalab_ux_mockups.html` + the standalone panel mockups under `docs/mockups/` (`cohort_curve_panel.html`, `signal_library_panel.html`)
   - Phase 8: CATALOG §7 (the gate first), INTEGRATIONS §1 (fundamentals row)
6. **End every session:** tests green (or honestly red in PROGRESS.md), a PROGRESS.md entry, a commit.
7. **New decisions** (anything the docs don't cover) get the next D-number appended to `docs/DECISIONS_v1.9.md` in the same table format — the docs stay the single source of truth as the build evolves.
8. **Then ask, for every new decision: "does this change a prompt that has not been spent yet?"** If it touches a phase already built, the decision row is the record and the prompt is history — leave it. If it touches a phase **nobody has built from yet**, the prompt is still load-bearing and must carry the change, or someone will build the superseded design from it. That single question is what separates a harmless back-fill from a live gap: `BUILD_AND_PROMPTS` drifted to D91 while MASTER reached D105, and the only consequential part of that drift was the unspent Phase-5 prompt, which would have produced an AI seat with no leakage invariant.

> Research/paper-trading only. Not investment advice.
