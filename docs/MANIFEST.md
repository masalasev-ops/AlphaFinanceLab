# AlphaLab — Complete Design Package (revision v1.9)

This is the **full, self-contained** design package. Design revision v1.9. Build status is live, not pre-implementation: `PROGRESS.md` holds the current phase, test count, and the open-item list, and `docs/CHANGELOG_v1.9.md` holds the full pass-by-pass history (v4/v5/v6 onward — every finding and decision). **`docs/DECISIONS_v1.9.md` is the decision register and is the count** — no range is restated here. Every file here is current; nothing external is required.

Start with `START_HERE.md`, then `docs/README_v1.9.md` (the file map and how to drive the build).

## What's in here

**Orientation**
- `START_HERE.md` — the entry point.
- `docs/README_v1.9.md` — file map, mockup guide, and the step-by-step build workflow.
- `README.md` (repo root) — the GitHub landing page: pitch, status, architecture, clone/build/run.
- `CLAUDE.md` (repo root) — hard rules, solution layout, commands (the constitution the build obeys).

**The design**
- `docs/DECISIONS_v1.9.md` — the decision register (its length is the count) plus the
  design-refinement history; extracted verbatim from MASTER §0/§2 in v1.9.90. A register
  row is changed only by another register row (rule 25/D109; `tools/check-register.ps1`).
- `docs/MASTER_DESIGN_v1.9.md` — the comprehensive document: architecture, golden rules,
  math appendix, UI boundary, the Signal Library (§24); its §0/§2 are pointer stubs to
  `DECISIONS_v1.9.md`.
- `docs/ARENA_ARCHITECTURE_v1.9.3.md` — how AlphaLab supports multiple isolated universes
  ("arenas"); decision D71. Additive, no schema change; the S&P 500 build is unaffected.
- `docs/SCHEMA_v1.9.md` — the exact database schema (the rule-14 source of truth).
- `docs/CONFIG_REFERENCE_v1.9.md` — all config keys, the connection string, secrets model, Arena block.
- `docs/INTEGRATIONS_v1.9.md` — every external data feed, named + validated + fail-closed.

**Build & test**
- `docs/BUILD_AND_PROMPTS_v1.9.md` — FR-1…FR-46, the gated phase plan, and the ready-to-paste
  Claude Code prompt for each phase (Phase 0 hardened for .NET 10 / EF Core 10 and arena-aware
  per FR-37).
- `docs/TEST_PLAN_v1.9.md` — the fixtures and tests each phase must pass (§8 is the canonical
  39-case Phase-0 test inventory; the BUILD Phase-0 prompt is structured as checkpoints 0.1–0.6).
- `PROGRESS.md` (repo root) — the phase-gate checklist to tick as you go.
- `docs/SETUP_v1.9.md` — day-zero environment + provider setup.
- `docs/RUNBOOK_v1.9.md` — operating the lab, backups, and running more than one arena.
- `docs/DB_RELOCATION.md` — ops runbook for moving the SQLite file(s) to another directory/drive
  (a config edit + file move; the deployed base is `E:/AlphaLabDatabase`, with separators normalized
  to the running OS — v1.9.36 — so the same template serves a cloud/Linux move).
- `docs/FUTURE_DB_MIGRATION.md` — contingency plan for ever leaving SQLite for a server RDBMS
  (a different job from relocation; closed until needed).
- `docs/REBUILD.md` — ops runbook: from a fresh clone to a working arena (the *data* bootstrap;
  sibling to DB_RELOCATION / FUTURE_DB_MIGRATION; includes the `--preflight` live-source check).

**Strategy & evaluation detail**
- `docs/STRATEGY_CATALOG_v1.9.md` — the strategy families and the equal-weight benchmark.
- `docs/OVERFITTING_MONITOR_v1.9.md` — the eight-signal overfitting monitor.
- `docs/DESIGN_IMPROVEMENTS_v1.9.md` — the honest-metrics rationale and power tables.
- `docs/DESIGN_IMPROVEMENTS_EXPLAINED.md` — the plain-language "why" companion to the above (onboarding; section numbers match the spec).
- `docs/UX_GUIDELINES_v1.9.md` — the UX honesty rules (UX-1…UX-20, incl. the arena no-merge rule, the paired-comparison screen, and the signal-library panel).
- `docs/UX_DESIGN_SYSTEM_v1.9.md` — the component catalogue: each honesty read-model field → its Blazor component, element, and token treatment. The visual-assembly layer under UX_GUIDELINES' tokens and UX-1…UX-16.

**Post-Phase-8 roadmap**
- `docs/POST_PHASE8_IMPROVEMENTS.md` - what each post-Phase-8 improvement is and why it earns its slot (the what and why; companion to the plan below).
- `docs/POST_PHASE8_PLAN.md` - the post-Phase-8 build sequence: the passes in order and the hooks that exist when post-8 begins (including the Phase 4.5 signal digest, D91).

**UI mockups (reference for the Phase 3 screens)**
- `docs/alphalab_ux_mockups.html` (the single consolidated UX mockup — every screen; supersedes the earlier per-topic mockup files)
- `docs/mockups/cohort_curve_panel.html` (UX-15 / D88) and `docs/mockups/signal_library_panel.html` (UX-16 / D91/D108) — standalone panel mockups added AFTER the consolidation; each is the "Reference look" its UX rule cites, and the consolidated file absorbs them when the UI workstream next regenerates.

**Diagrams**
- `docs/diagrams/alphalab-architecture.svg` — the one-page RESEARCH-FLOW picture: market history → the field (human strategies + the AI contestant) → the measured-alongside group (random crowds, the twin, the rule grader) → the judging layer → verdict + lessons → the money split, dashboards, and the AI researcher loop, with the sealed calibration run off to the side. No technical (projects/sole-writer/Api-boundary) diagram exists; authoring one is a recorded non-goal until wanted (finding 335 corrected CLAUDE.md's copy of this line in v1.9.70; this copy caught up in v1.9.91). Not part of any phase reading diet.
- `docs/diagrams/two-loops.svg` — the TWO-LOOPS picture: Loop A (a generation's AI trader vs its frozen clone, differenced — "does the AI trade better than its clone?", with the one-year figure marked DERIVED, NOT MEASURED) and Loop B (chart strategies vs the random crowd, feeding the AI researcher's proposals and your approval), separated by the "never alters a running trader" wall with exactly one gate: a NEW pair, earning from scratch (D126/D127; referenced from MASTER §23.3). Not part of any phase reading diet.

**Revision history**
- `docs/CHANGELOG_v1.9.md` — every consistency finding and decision, v1.9.1 through the last `## v1.9.x` heading in the file itself (no v1.9.44/v1.9.45 were issued). The file is the coverage statement; no endpoint is restated here.

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
- v1.9.17–v1.9.21 (the Phase-2 build passes, lettered findings A–NN, decisions D72–D82, golden rule 32) — merged;
  the per-pass detail lives in `docs/CHANGELOG_v1.9.md` and `PROGRESS.md` (the finding-173 rule: no duplicated
  inline status here).
- v1.9.22 strategy-catalog expansion + AI-pass follow-through (findings 174–177, **recorded retroactively in
  v1.9.23** — the four commits shipped without CHANGELOG rows) — merged. Catalog §6.5–6.7 (ResidualMomentum,
  TimeSeriesMomentum, BettingAgainstBeta) + §12.5 references; UX-14 + the replay recolour (violet→slate-grey,
  `--violet` reassigned to AI-seat identity); TEST_PLAN §6 AI-seat fixtures; DESIGN_IMPROVEMENTS §4 three-seat rewrite.
- v1.9.23 reconciliation pass (findings 174–199; decision **D83**) — merged. **Docs only; tests stay 528.**
  Retroactive v1.9.22 CHANGELOG section; BUILD reconciled to MASTER §23 (the retired blend-A/B and sentiment-score
  framings removed from Phase 5/6); the three new strategies propagated (BUILD Phase 6, MASTER §9, DESIGN_IMPROVEMENTS §3.1);
  D83 resolves the D41 contradiction (factor returns as §6.5's availability-lagged signal input); catalog
  completions (SignFlip, AllPositive, seeded tie-break, `RandomPop-Event`, §6.7 seams, §12 trials arithmetic);
  monitor AI-seat handling (§3½); eight Phase-2-review code findings recorded with deadlines (190–197);
  proposals P8–P11 opened.
- v1.9.24 reconciliation pass (findings 200–206) — merged. **Docs only; tests stay 528; no new decision.**
  Completes finding 188's sweep: the retired sentiment-score / blend-A/B framing removed from four further
  MASTER locations (rule 28, §23.1, the D46 row, §0 item 12), the §3 mental-model diagram (the fused
  "Claude read" node), and DESIGN_IMPROVEMENTS §4.1 (the same construct finding 188's DI §6 sweep missed —
  finding 206); the stale "(pre-implementation)" title qualifier dropped; D73's already-resolved
  `⚠VERIFY` index-EOD marker swept (finding 165 had fixed only BUILD); cosmetic — D49 added to the §2
  provenance list, §10 attribution gains the D83 dual-role pointer.
- v1.9.25 funnel cash constraint + basis math (decision **D84**; resolves findings 190 & 195) — merged.
  **A code pass; behaviour change — Stage 5 sizes new opens against available cash (D84), scaled to fit,
  never total equity; tests 528 → 539.** `Sizing.Size` gains an `availableCash` ceiling; `FunnelRunner`
  passes cash for opens / equity for a whole-book rebalance; `DailyPipeline` threads the account's cash and
  moves the sell-leg basis math to decimal `BasisMath` (finding 195, D69). Proposal **P12** opened.
- v1.9.26 twin-scorer pass (decision **D85**; proposal **P13**; finding 207) — merged. **Docs only; tests stay 539;
  no code/schema/config change.** Fixes the Stage-2 scorer D81 left open: the no-LLM twin scores by a **frozen
  equal-weight z-score blend of the same pack features the contestant sees** (MASTER §2/§23.3), a `config_json`
  frozen param with ordered degenerate-day handling (drop zero-variance features → equal-score fallback + flag if
  none survive or <2 names → never NaN), not replay-admissible on its own. STRATEGY_CATALOG §9, CONFIG_REFERENCE
  (Ai NOTE), TEST_PLAN §6 (`D85_*` fixtures + extended `FX-TwinPairing`) updated; the rejected foils
  (pure equal-scoring; a tuned rule — p-hacking) recorded as **P13**; ORIENTATION.md already named the blend.
- v1.9.37 Phase-3.5 save/continue hardening (decision **D90**; proposal **P14**; findings 219–228) — merged.
  **Code + scripts + docs; one EF migration (`Phase35PositionSnapshots`, operator step M4); no new config key;
  tests 721 → 754.** Completes the checkpoint skipped between Phase 3 and Phase 4. `reproduce-day --date`
  re-runs a committed session from its stored watermark into a rewound throwaway copy and proves the
  decisions/fills/equity/population draws byte-identical (NFR-1, MASTER §13.5) — read-only against the arena
  and with no network call, replaying Stage 1 from the store's own versioned bars/actions. That needed **D90**
  `position_snapshots`: the end-of-day book, written in the same Stage-2 transaction as `equity_curve`, because
  `positions` is current state that corporate actions rewrite in place with no reversible trade row. `verify-wal`
  asserts WAL is active AND that a checkpoint completes (reading the pragma, never setting it).
  `tools/backup-offsite.ps1` (newest-by-filename-date, SHA-256 verified, fails loudly) and
  `tools/register-nightly-backup.ps1` (an 02:00 **OnDemand** launch — not `--serve`, which would idle) close the
  off-machine and unattended gaps. `FX-RestoreThenContinue` automates the RUNBOOK §4 drill.
- v1.9.38 Phase-4.5 Signal Library registration (decision **D91**; FR-43..46; proposal **P15**; findings 229-239) - merged.
  **Docs only; no code, no migration; tests stay 754.** Registers the Signal Library as Phase 4.5 (after
  Phase 4 replay, before Phase 5; the Phase-3.5 fractional precedent): seven pre-registered cross-sectional
  signals graded daily by Spearman rank-IC over the Stage-1 pool as-of on adjusted total returns; rolling
  1y/5y trend with Newey-West bands (lag = horizon) and a pre-registered trend flag stated in significance
  units; two tables (`signals`, `signal_ic`) recorded in SCHEMA with the EF migration deferred to the 4.5
  build; descriptive only, never an allocator, gate, sizing, or eligibility input. Phase 6 IModels wrap the
  same ISignal implementations (`FX-SignalParity`); Phase 5 consumes a per-signal digest line through the
  evidence-prior seam; Phase 8 fundamental signals register into the same harness (`report_available_date`).
  The source orientation doc was distributed and deleted; MASTER §24 is the authority. The two post-Phase-8
  roadmap docs joined the tracked corpus in this pass.
- v1.9.39 the Phase-4 build (checkpoints 4.1–4.10 on `feat/phase4-arena-replay`; decisions **D92–D99**;
  findings 240–247; migration **M5**; 754 → 819 tests) + the doc sweep and the ORIENTATION three-pictures
  section. v1.9.40 the Phase-4 adversarial-review fix pass (findings **248–262**, all fixed pre-merge with
  regression tests; 819 → 839 tests) — the review register lives in CHANGELOG v1.9.40.
- **v1.9.41–43 the pre-4.11 fix pass** (branch `chore/pre-4.11-dataquality-sun-bsc`; **findings 263–274**;
  no migration; → **862 tests**). v1.9.41: the D70 historical backfill's data-quality tail — SUN excluded as
  wrong-company + BSC cleared (finding 266), the ops EF log filter (267), and physically-impossible vendor
  PRICE bars guarded at read time + rejected at ingestion (268/269, proposal P17). **v1.9.42 the two-pass
  calibration machinery fix (B2+B3, decisions D100–D101, findings 270–273):** a first full-scale run proved
  the machinery froze nothing (it retired the D64 plants on uncalibrated flat-anchor verdicts, truncating the
  curves it builds); the fix stops *acting* on those verdicts while recording the would-be retires, brings the
  flat-anchor fallback into D63 conformance, ADDS out-of-sample curve-based metrics with their own keys, and
  rule-selects a per-cadence plant strength ladder. v1.9.43 the proxy-only backfill mode (finding 274) — the
  regime warm-up + benchmark depth without the membership-reconcile mass-eviction hazard. The full `--reset`
  calibration is the operator's de-risk-then-sign-off sequence (RUNBOOK §8).
- **v1.9.46–v1.9.49 the pre-freeze evidence sequence** (findings 279–289; decisions **D102 (superseded by D107)–D106**).
  v1.9.46: `joint_false_alarm` made reported-not-gating (**D102** (superseded by D107), findings 279–280), the D103 evidence
  pass (findings 282–283), and the in-flight run's own diagnostics (findings 284–287, incl. the
  raw-gap-vs-Jensen's-alpha defect, finding 285). v1.9.47: AI decision transparency specified before
  implementation (**D104–D105**). v1.9.48: the recompute harness adopted (**D106**). v1.9.49: the 4.11
  run COMPLETES — generation 1 recorded (5,031 sessions), the freeze blocked as predicted
  (findings 288–289; D103 re-scoped).
- **v1.9.50 the category decision + the freeze record — PHASE 4 SIGNED OFF (2026-07-31)**. **D107**
  (supersedes D102 (superseded by D107)): membership is by what a check ASSERTS, so pass-1-verdict checks are reported-only
  and never freeze-gating. The (cont.) entry is the freeze record: the resume recomputed ZERO sessions
  (5,031 already-committed skipped), AllGreen=True over the gating set (9/9 gating Pass), the report
  archived under `docs/calibration/sp500/`, and **five append-only config rows FROZEN** at
  `2026-07-31T12:57:26Z`, with the finding-285 caveat ATTACHED to the freeze record.
- **v1.9.51 the Phase-4.5 reconciliation** (findings 290–293) — docs only. D92–D107 read and classified
  against the unspent 4.5 prompt (four touch it, eleven do not); horizon 126 CLOSED on the statistic;
  the forward widen must land before go-live; the FR-46 read-model must accept an as-of; P15's panel
  timing DEFERRED to the UI workstream on a non-D65 justification. `UX-16` reserved by name.
- **v1.9.52 Phase 4.5 (Signal Library) CODE-COMPLETE** (**D108**; findings 294–304) — D108 fixes the
  trend flag's uniform 5-year window and its t reference, plus the IC-pool defect (294), `ISignal`'s
  home in `AlphaLab.Core` (295), the effective-sample floor (296), the ORIENTATION staleness lesson
  (297), and the pin verb the refusal had no satisfier for (299). The (cont.) entries carry the
  operator run's own findings (300–301) and the corpus reconciliation that followed (302–304).
- **v1.9.53 the detectability floor — a failure to reject becomes readable** (finding 305) — an amendment
  to checkpoint **4.5.4**, which was REOPENED rather than filed under 4.5.5, because it adds a field to
  `SignalPanelRow` and an obligation to UX-16. `gone` and "too thin to tell" had been rendering
  identically; every flag now publishes a **minimum detectable IC**, `MDIC = (t_{1−α,df} + t_{power,df})·se`,
  computed at read time because both df and the standard error depend on the window available. Adds the
  OPTIONAL versioned config row `SignalLibrary.MinDetectablePower` — optional deliberately, since it scales
  a published *diagnostic* rather than gating a verdict, so an absent row withholds the floor with a stated
  reason instead of blocking a run. **No D-number:** it applies an existing discipline (rule 6, D89,
  Amendment 2.2) to a surface that had not inherited it — the shape of finding 296, not of D108.
- **v1.9.54 the overlap correction was applied twice — every signal standard error was √k too wide**
  (finding 306) — found by the finding-305 floor **on its first contact with real data**, immediately after
  the 20-year backfill completed (67,580 rows, 5,010 sessions, 2006-02-02 → 2025-12-31). **Caught by
  reductio before any theory:** the published MDIC came back at **1.03** for `bab:L252`, and a rank
  correlation is bounded in [−1, +1] — a floor above 1.0 asserts the test could only have detected a
  better-than-perfect correlation. That single number proved a defect existed before anyone knew which
  formula was wrong. No D-number; D108's derivation is untouched.
- **v1.9.55 a register row is changed only by another register row** (**D109**, **rule 25**; supersedes
  **D87**, superseded by D109) — the widening decision itself: breadth arrives as **SEPARATE ARENAS**
  under D71, never as an
  in-place enlargement of an arena holding a live experiment; the Russell 2000 rejection is **narrowed, not
  lifted**. Adds the **Status column** to all 111 register rows and `tools/check-register.ps1` with four
  checks (3a/3b/3c/3d), each **proven to fire, not merely to pass**, plus two stated limitations (3b applies
  to `superseded-by` only, since amendments produced 200+ legitimate hits; 3d cannot be a general scanner).
  63 violations found and deliberately **not** fixed in that pass — the enforcement is a tool rather than a
  review checklist because three prior sweeps had passed while the defect was present.
- **v1.9.56 the Signal Library's first full-scale read — fourteen `gone` verdicts, and the floor exceeds the
  signal in every one** (finding 307) — 1.97× at best, 116× at worst, median 6.9×. *"That is a statement
  about the INSTRUMENT, not the anomalies."* **Nothing was tuned in response**, which is the entry's point.
  The `n_eff ≥ 10` floor proved load-bearing, and the seven signals were measured to carry only about **two
  signals' worth of independent evidence** (PC80 = 2, N_eff ≈ 2.1) — which bears directly on the trials tax.
  One question recorded and NOT decided: re-scoping the library from descriptive to veto would change the
  D91 boundary and needs its own decision row.
- **v1.9.57 the proposal-quality score — the researcher is graded PER PROPOSAL** (**D110**; findings
  308–310) — two per-proposal scores published side by side and **never blended**: the **detectability
  margin** (`expected_effect_ann` ÷ the D89 floor the gate already computes and discards), which is
  tax-confounded and therefore recorded but not read until a control exists; and **calibration skill**, a
  proper **log** score on a new pre-registered `prior_prob` against the leave-one-out base rate, which is
  tax-robust. Log rather than Brier because Brier saturates at 0 — the ceiling the decision exists to
  remove. **Finding 309** records a live bind: `Research.ForkBudgetPerYear = 6` is nowhere derived, and the
  resolution is the per-arena tax rather than a bigger budget. **Finding 310** — the register guard's own
  baseline was line-anchored and broke on its first real use (10 false positives, 0 real).
- **v1.9.58 retiring the stale citations** (findings 311–312) — **311:** D46's Status was mis-set to
  `superseded-by`; it is **`amended-by`** — what died was the *framing* (the sentiment score and the
  with/without-Claude A/B) while the news budget, Batches, prompt caching and per-task tiering all survive,
  so twelve correct citations had been made to read as defects. **312:** 23 sites asserted the superseded
  S&P 1500 plan as current fact — including `CLAUDE.md` rule 22, `ORIENTATION.md`, `REBUILD.md` and a
  **runtime message string** in `HistoricalBackfill.cs` — rewritten rather than annotated, with five more
  found by a semantic sweep the tool could never catch. **The ratchet reached zero: 63 → 51 → 28 → 0**;
  `register-baseline.txt` **DELETED** and `ci.ps1` now calls `check-register` **bare**. Stated cost: every
  future supersession must retire ALL its citations before its PR can go green.
- **v1.9.59 the stranded prohibition — a live constraint was sitting inside a superseded row** (**D111**,
  finding 313) — the rule *a Quality strategy must never be tested in this arena* was inside D87 (superseded by D109)
  while `Quality` stayed on the Phase-8 roster. **D111 carries it forward BROADENED to a principle** — a
  strategy must not be tested in an arena whose membership rule screens on the same characteristic the
  strategy scores — and **gated on verifying the published index methodology, failing closed meanwhile**.
  Momentum, MeanReversion, ResMom, TSMOM, Breakout, LowVol, BAB **and Value** are explicitly NOT blocked.
  New owed documentation item: record per arena which characteristics its inclusion rule screens on, in
  `INTEGRATIONS_v1.9.md`. Lesson: *retiring a row is not the same job as retiring what it was carrying.*
- **v1.9.60 Phase-5 PREP** (**D112**, **D113**; findings 314–322) — docs + decisions only, no `src/`, no
  migration; tests stay 938. **D112** closes **P8**: the researcher refuses once overdue outcomes reach
  `Research.MaxConcurrentCandidates` — the grace window's shape on a derived bound, **no new key**.
  **D113** makes D110's control arm a **paper control differenced on the evidence-prior seam**, withdraws
  the doubled-tax premise (verified in code), and **amends D110** to floor-at-**assessment**. Also: the
  per-task model tier finally chosen and dated (`claude-opus-5` / `claude-haiku-4-5`); the **5.1–5.8
  checkpoint decomposition** recorded, the corpus having cited "checkpoint 5.7" in three places while the
  rest were defined nowhere; the DoD fixture list repaired (4 → 9 + 2, finding 315); the Phase-5 doc diet
  refreshed to lead with §23 (316); the INTEGRATIONS §5 Batches ⚠VERIFY closed against the published
  reference, the **live** smoke test still owed at 5.1 (318).
- **v1.9.61–v1.9.68 — Phase 5 built, checkpoint by checkpoint** (all 2026-08-01; the per-checkpoint
  record is CHANGELOG v1.9.61 onward; back-filled here at v1.9.70, finding 332 — this trail had stopped at
  v1.9.60). 5.1 the Batches/caching provider seam + M7 (`IResilientHttpSender`/`IModelTransport` resolve
  finding 323; 317/319/320 closed by building); 5.2 the D46 news budget as an unbypassable decorator; 5.3
  the LLM as pipeline Stage 3, forward-composition-only; **5.4 the pack contract + seam + M8 — its
  CHANGELOG section was never written at the time and exists RETROACTIVELY (finding 332), and its
  components shipped UNWIRED (finding 330, reconciled v1.9.70)**; 5.5 `ai_decisions` persist-before-use +
  rule 32 made structural; 5.6 the researcher seat + the D112 evidence diet + M9 (the corpus's first table
  REBUILD, finding 324); 5.7 the D110 proposal inputs + the D113 paper control + M10; 5.8 the
  reconciliation (findings 325–327; the mocked-month figure $0.98 modelled; the live smoke red — later
  green at v1.9.69).
- **v1.9.69 — the live smoke test, run for the first time** (2026-08-01): green on BOTH pinned tiers after
  two live-only defects — the alias/snapshot pricing collision (finding 328, fixed by
  longest-prefix `PricingFor`) and the smoke test having exercised the one tier the lab never calls
  (finding 329). INTEGRATIONS §5 is live-confirmed.
- **v1.9.70 — the drift reconciliation** (2026-08-01; D114, D115; findings 330–335): the researcher pack
  path WIRED (it had been built and referenced by nothing — D113's arms were an undifferenced, unblinded
  pair); D114 subject-keys the AI-seat records and makes the placebo blind; D115 tombstones superseded
  register rows (D87 (superseded by D109) and D102 (superseded by D107) compressed to gravestones, live content verified rescued); the false v1.9.68
  "digest is wired" closure struck (finding 333); `Research` config in both processes (finding 334); the
  architecture SVG's rule-grades edge re-drawn off the judging layer and CLAUDE.md's diagram description
  corrected (finding 335); this MANIFEST trail back-filled (finding 332).
- **v1.9.71 — the plausibility ceiling** (2026-08-01; D116; findings 336–337): the detectability gate stops
  refusing in one direction only — a ceiling of `top swept rung × the ladder's own geometric step` (32 %/yr
  here), derived from the frozen C-1 row rather than authored, with three fail-open valves; the researcher's
  pack carries BOTH ends of the band (cp-1.0 → cp-1.1, prompt rs-1.0 → rs-1.1, taken while the store held zero
  proposals so no margin series lost comparability); and **finding 336** records that the arena's own frozen
  curves put the gate on its `+∞` branch at the then-configured 3-year horizon (D121 later set 10) — every registered candidate refuses
  until generation-2 recalibration, a fact no document had multiplied out. No threshold tuned, no migration.
- **v1.9.72 — the recompute harness, built** (2026-08-02; D117 amends D106; findings 338–340): MASTER §25's
  D106 harness exists — score a monitor- or gate-rule change by re-deriving verdicts from the stored
  generation instead of paying a multi-day replay. **D117 settles §25.5's two open questions**: report-only
  (no rows, ever), and a recomputed number IS sign-off evidence for retire-exempt subjects but only when
  `FX-RecomputeParity` AND a **confirmation slice** both agree — parity exercises the unchanged path, so it
  structurally cannot validate the changed one. Three findings came out of building against the spec: **338**
  (retire-exemption is load-bearing for recomputability, not incidental), **339** (§25.2's tier table had no
  row for the alpha-definition change its own prose listed as covered), **340** (S6's negative-alpha
  threshold is `derived-band`, not `direct-read` — the branch that fires never records band membership).
  `derived-band` is classified and REFUSED out loud, which is §25.2's own instruction rather than a gap.
- **v1.9.73 — the harness answers the question it was run for** (2026-08-02; finding 342; no new decision —
  this completes D117/§25.5(b)'s stated capability rather than changing it): the v1.9.72 harness reported
  COUNTS and stopped short of conclusions. Scoring finding 285 moved 65 of 75 promotions; scoring finding 280
  moved 2,946 statuses — and neither report could say whether the detection floor moved, whether the gate
  reopens, or whether the cohorts separated. Added: the **C-1 detection-power curve** rebuilt from recomputed
  promotions with α*(H) derived by the gate's own selection rule; a **cohort separation** table (anti-rate −
  noedge-rate, the only number that judges a finding-280 fix); promotion diffs classified **moved / gained /
  LOST** with every LOST subject listed in full; and the example cap raised from 10 to 40 after a 65-row
  change sampled only the alphabetically-first cohort. **Findings 343 and 344 are the instrument correcting
  itself twice on live data**: the separation metric first SATURATED (ever-Suspect over 20 years catches every
  cohort — finding 289's EVER-predicate lesson, inside the tool built to prevent that class of error), then,
  once horizon-bounded, read a verdict from a horizon where both cohorts sat ONE PLANT apart at the ceiling and
  called a noise-level sign flip an improvement. Saturation is now defined against `1/n`, the measurement's own
  resolution, and the verdict refuses to name a direction below it.
- **v1.9.74 — finding 285 FIXED: the gate's effect and its MDE become one estimator pair** (2026-08-02;
  **D118** amends D48; finding 345): `EvaluationStep` had computed a raw active-return gap with no beta term
  since Phase 3, against D26 and hard rule 6, feeding the gate, the allocator's weights and the Strategies
  screen alike. The fix is not the numerator alone — judging Jensen's α against the MDE of the β = 1
  difference series pairs an intercept with the noise of a different estimator, which is what the recompute
  harness itself did first (finding 345) and why its curve was a lower bound. One `NeweyWest.Ols` fit now
  yields the effect, the MDE and the persisted σ together, with σ defined so the allocator's shrinkage SE and
  the detectability floor keep measuring the noise of what the gate actually judges. First rule change in this
  corpus PRICED before it landed (35 promotions earlier, 30 gained, 0 lost).
- **v1.9.75 — the `derived-band` tier, built: finding 280's remaining candidate becomes scorable**
  (2026-08-02; no new decision — implements D117 clause 4's third tier): finding 280 named two knobs, and
  v1.9.73 measured the cheap one out. The remaining one — S6's negative-alpha threshold — was REFUSED by the
  harness, correctly and by prediction (finding 340: a row that took the negative branch never evaluated band
  membership, so moving the threshold drops rows into a check whose input was never stored). `BandInputs`
  re-derives the member band from `control_equity` and each subject's window from `equity_curve`,
  point-in-time, with the band memoised per session rather than per subject. **Validated by a no-op band spec
  reproducing generation 1 exactly — 0 differing across 95,600 statuses.** The refusal is SCOPED rather than
  lifted: a band-tier spec with no inputs still refuses, because token recovery is valid only in the case the
  tier is not needed for.
- **v1.9.76 — the horizon table, and its contamination caveat** (2026-08-02; finding 348; no new decision;
  this row back-filled at v1.9.77 — the pass itself omitted it): the C-1 curves measured at 1y/3y/5y/10y/15y/20y
  PLATEAU (2 %/yr is 0.10 at one year AND twenty), so patience is not the lever. **Marked CONTAMINATED the
  same day:** the noise the curves rest on carries a data defect, so the table stands as a direction only
  until generation 2 re-derives it (the v1.9.77 diagnosis is the follow-through). **RE-DERIVED at v1.9.82
  and the caveat LIFTED:** on clean curves α\*(10 y) = 6.947 %/yr, so the strong claim ("cannot adjudicate
  at any patience") is REFUTED; the direction partly survives, since 2 %/yr is still 0.14 at ten years.
- **v1.9.77 — the contamination's root cause: three defects, none of them the ones guessed** (2026-08-02;
  **D119** amends D86, **D120** new; findings 349–353): the store predates its own R2 guard (55 securities,
  1,763 >×10 jump-days — ACS flaps $0.15↔$25 and moved the EW basket ±33 %/day); the missing-bar freeze
  marked at cost basis against its own "last print" wording (the OEF 2014-04-22 −27 %/+37 % pair — D119
  makes the mark independent of the frozen flag); HNZ's dividends are stored ×100 on a real price series.
  Built the report-only `store-sweep` verb (D120): 1,208 audited, 39 recommended, 28 excluded after a
  name-by-name price review (11 kept — a detector hit is a claim, not a verdict), `Universe:Exclusions`
  1 → 29. Generation 2 on the cleaned roster is the gate to re-deriving finding 348.
- **v1.9.78 — the replay is 9.3x faster and byte-identical** (2026-08-02; findings 354–355; no new
  decision, no behaviour change): generation 2 was about to cost another ~4.5 days. Profiling (not
  guessing) found the run single-threaded and CPU-bound at 89 % of ONE core on a 16-core box — and then
  found the cost was not arithmetic at all but EF write patterns: `SaveChanges` per plant (400 a
  session), tracked existence reads (~20 k rows a session), and ~1,500 no-op `SaveChanges` a session in
  ingestion, each re-running `DetectChanges` over the whole tracker. 63.5 s -> 6.86 s a session;
  **~4.5 days -> 9.6 hours**. Proved to be speed only: 13 tables, 37,182 rows, identical SHA-256 before
  and after. Parallelism deliberately NOT taken — the run stopped being the constraint.
- **v1.9.79 — the detectability horizon is TEN years, pre-registered before the curves existed**
  (2026-08-02; **D121** amends D89; findings 356–357): the admission floor is `z·TE/√H`, so the horizon
  decides what may be PROPOSED. At generation 1's contaminated noise the 3-year floor sat ABOVE D116's
  32 %/yr ceiling — finding 336's closed gate, restated analytically. Ten years puts it near 7 %/yr.
  **Chosen while the run was ~10 % through and no curve existed to tune against.** Also finding 357: the
  ledger's reads were tracked, a third instance of the EF quadratic; and the contamination confirmed GONE
  on live data (tracking error 38.80 → 9.49 %/yr, 17 impossible days → 0).
- **v1.9.80 — the replay's two real hot spots, found by measuring and not by reading** (2026-08-03;
  findings 358–360; no decision): the compelling suspect from READING the code, `ComputeCash`, appeared in
  **0 of 40** live stack samples. The real 65 % was `LatestEquity` scanning every historical row per
  population per session and `DataQualityFlagStore` saving once per security. Proved unchanged against the
  live run rather than a fixture: **5,521,912 rows across 11 tables at matching SHA-256**. **finding 360**
  retracts this pass's own 24.30 s/session and 4.4-day figures — `dotnet-stack` suspends its target, so
  forty samples plus a test suite plus a 3.9 GB copy were charged to the thing being timed.
- **v1.9.81 — the post-generation-2 sequence, in one place instead of four** (2026-08-03; no decision):
  the obligations that fire when generation 2 stops were already recorded, across four documents, with no
  single reading of "what happens when the run finishes". Consolidated into PROGRESS's `NEXT` block.
- **v1.9.82 — generation 2 lands, and D103 is taken at its own trigger** (2026-08-03; **D103** reserved
  → active; findings 361–362): 5,031/5,031 sessions frozen. **The gate REOPENS — α\*(10 y) = 6.947 %/yr**
  against generation 1's *unreachable*, giving a live band of **[6.95 %, 32 %]/yr**; detection at 4 %/yr
  went 5/50 → 35/50. D121's theory-only prediction of ~7.08 % was met by measurement at 6.947 %. **D103's
  trigger (b) fired exactly as written** (6/50 = 12 % against "8 %, or 4 or more of 50"), and the form taken
  restores `CONFIG_REFERENCE`'s declared RATE: 12 % Fail → 2.6 % Pass, corroborated by the independently
  implemented point-level metric reading 2.9 % on the same paths. Four config rows frozen at version 2;
  `Monitor.S6.AutoRetireEvals` verified NOT re-seeded (D98, first freeze only). **finding 362:** the failing
  report was regenerated in place before being committed, breaking the archive rule this corpus states.
- **v1.9.83 — `Calibration.ReportRef` never verified against the repo** (2026-08-03; finding 363; no
  decision): the Worker writes reports with CRLF and hashes those bytes, while `.gitattributes` normalises
  them to LF — so the committed artefact has never hashed to the frozen value, **including generation 1's,
  frozen 2026-07-31**. The row whose job is to make a freeze auditable was auditing nothing. Fixed with a
  scoped `-text` rule and proved by round-trip: both reports deleted, restored via `git checkout`, both
  hashes now reproduce.
- **v1.9.84 — the post-generation-2 staleness sweep** (2026-08-03; no decision): every live statement the
  run falsified, rectified in place rather than annotated — MASTER §20.3's "must be re-read" instruction
  (now read), the Phase-6 PROMPT (which still said the gate was CLOSED and self-contradicted on the tier
  count), PROGRESS's gate-CLOSED and finding-348 bullets, and the test whose documentation claimed to pin
  "the arena's OWN frozen curves" while seeding generation 1's. Archives — the CHANGELOG's history, the
  dated calibration reports — were deliberately NOT rewritten: they are evidence.
- **v1.9.88 — Phase 5.5: the construction question, answered by measuring** (2026-08-04; **D123** new, FR-47; findings 369–371): D122 made the expected effect a measured property of the CONSTRUCTION, so this measured which one. The report-only `construction-study` verb builds a monthly-rebalanced top tail vs the equal-weight scored universe (long-only) and top-minus-bottom (long-short) for all seven registered signals over 6,287 sessions. **The answer is NO:** two signals gain materially (`bab:L252` 1.60x, `lowvol:L252` 1.71x), three are WORSE, and the best anywhere — `bab:L252` at 51 years to detect — is five times the ten-year horizon. The larger result is the bar itself: at H=10 the arena can only adjudicate an information ratio ≥ **0.886**, and the best of fourteen measured pairs is 0.392 — a gap that belongs to the SIGNALS and the HORIZON, not the construction, which is why changing the construction could not close it. Two process findings: the report's first headline (comparing FLOORS) was refuted by its own smoke run, since long-short is ~2× leverage and scales TE and effect together; and the all-seven fixture PASSED on geometric ramps while four of the scorers were numerically degenerate.
- **v1.9.89 — who the gate pairs against, and over which sessions** (2026-08-04; no decision; findings 372–373): an operator question about a mid-life fork ("doesn't it start from zero?") sent a read into the gate rather than the design text. **372, closed here:** the pairing domain is the **common-date intersection** of the two equity curves (`CurveMath.AlignedReturns`), so a fork is judged on its own sessions at the same annualized **rate** as a long incumbent — the short track costs power, not standing — but the rule lived only in a code comment and no fixture covered it. DESIGN_IMPROVEMENTS §1.2 gains the paragraph; `FX-PairedWindowIsTheOverlap` makes it executable. **373, recorded and NOT resolved:** `EvaluationStep` pairs every strategy against `buyhold:cw` and `DailyPipeline` never overrides that default, so no candidate is ever paired against Live — while MASTER §8, MASTER §20.2 and DESIGN_IMPROVEMENTS §3.5 say "vs Live" in five places, and `AllocationStep` feeds that benchmark-paired verdict to a tilt cap documented as firing on *TooEarly vs Live*. Evidence leans toward the code (hard rule 6 names the cap-weight account; the allocator's shrinkage toward a cross-sectional mean requires a common benchmark), but resolving it either way touches what D31 means, so under rule 25 it waits for a decision. **No design text was edited for 373.**
- **v1.9.90 — the decision register extracted to `docs/DECISIONS_v1.9.md`** (2026-08-04; no decision, no finding; mechanical only): MASTER's §0 (design-refinement history) and §2 (the decisions log — preamble, pass-index table, all 123 rows) moved **verbatim** into the new file, §0 leading; MASTER keeps one-line pointer stubs under both headings and now opens on §1. Exactly one locational repoint inside the moved blocks (§0's "banners above" → MASTER's front-matter) and two declared edits outside them (MASTER:5's "§2 below"; ORIENTATION's tail pointer). `check-register.ps1` reads the new path (identical shape: 123 rows, 107/11/5), keeps its old MASTER blind spot byte-for-byte (the latent D38-without-D122 3b hit is on record for the next pass), and gains check **3e**: a "MASTER §2" citation may survive only inside the frozen historical zones, whose whitelist in the script is the one extensible place they are named.
- **v1.9.91 — the decisions pass** (2026-08-05; **D124–D129** new — D124 amends D110, D125 amends D81, D126 amends D82, D127 amends D91; findings 374–379): the researcher graded honestly under finding 370's bar (calibration skill declared STRUCTURALLY SILENT; the margin and D88's cohort curve survive); the twin paired difference made a SEPARATE instrument with its own §1.2 KPI row and NW-MDE (unmeasured today — the gate has never read it); the researcher boundary stated as FREEZE (propose new, never alter running); the D127 shortlist rule (hard pass/fail + cross-signal dispersion + a seeded random slice; the ONE second-moment exception to §24.5, with the factor-exposure guard; the signal set frozen per contestant); the sanctioned proposal triggers (D128) and the typed proposal shape (D129). MASTER §23.4 gains the single authoritative researcher scope block (every other statement is now a pointer); ORIENTATION gains the invariants card and loses its six "proposes the next strategy" errors; CLAUDE.md gains hard rule 26 (the consequence field, warn-only in `check-register` 3f); UX-17–20; BUILD Phase 6 pulls the drawdown breaker forward and adds the behavioural reads + two-light UI; TEST_PLAN §6 gains eight Phase-6-paired spec fixtures; three pre-edit sweep artifacts archived in `docs/calibration/`. Register: 129 rows, D1..D129.
- **v1.9.92 — the annual budget** (2026-08-05; **D130** new, amends D24; findings 380–382): `Llm.AnnualBudgetUsd` (100) becomes THE one authored spend number; every other spend cap is DERIVED (contestant 0.20/day, researcher 2.83/month, global 0.39/day, 130,000 tokens/day — finding 320's knob finally enforced) and recomputed by `FX-BudgetDerivation`, so a hand-edited derived cap fails the suite; one PRE-REGISTERED ESTIMATE (the per-task `ExpectedOutputTokens` seeds, MODELLED, with a derived p90 recalibration trigger) feeds the pre-flight estimator instead of the 8192 API ceiling, fixing the finding-380 LOCKOUT (the guard had refused calls the budget could afford). The shortlist's budget-affordable N (~416) clears the structural D24 scope cap (25), which is what binds; the derived size freezes per generation (Phase 6 fixture). Batch recorded as the cost lever (doubles headroom, changes no cap). `ForkBudgetPerYear` stays authored by finding 309's recorded resolution. The 3-E bound sweep archived in `docs/calibration/`. Register: 130 rows, D1..D130. **The three-PR register pass is complete.**
- **v1.9.93 — check 3e fixed on the engine CI actually runs** (2026-08-05; no decision; finding 383): GitHub CI (pwsh 7) had been red since v1.9.91 while local runs (Windows PowerShell 5.1) were green — the 3e section map's `[int]` keys missed against `Select-String`'s pwsh-boxed `LineNumber`, and the self-test could not catch it because it probed the map with the map's own keys. Fixed with one shared exemption function, an `[int]` cast, and a real-hit probe that throws on the next engine divergence; process note: the remote check is part of the gate.
- The mockups were consolidated into the single `alphalab_ux_mockups.html` in the v1.9.21/v1.9.22 passes
  (the earlier per-topic and v2 files are gone; the consolidated file gained the UX-14 paired-comparison block
  and the slate-grey replay tokens in v1.9.22). SCHEMA received its first post-v1.9.1 edit in v1.9.7
  (the `config` composite PK + invariant notes).
