# UX_GUIDELINES_v1.9 — the interface rules, as build specs

*Companion to the consolidated mockup file `alphalab_ux_mockups.html` (its Allocation / Journal / Data-health screens cover UX-9…UX-11). **These rules are enforced in the serializable read-models (D58), not in any UI framework** — each rule below is resolved into DTO fields in `AlphaLab.Core`/`AlphaLab.Evaluation`, served by `AlphaLab.Api` (D57), and rendered verbatim by whatever client is attached (Blazor today; Angular/React/mobile equally). The mockups show the reference Blazor client; the rules hold for all clients because a client cannot recompute them.*

*Original companion note (historical):* earlier per-topic mockups set the visual direction — palette, gold-means-live, plain names, health pills, MDE line — and a later honesty pass fixed the UX contradictions the design review surfaced. All of that is now consolidated into `alphalab_ux_mockups.html`. This doc states each rule, why it exists, and where Claude Code implements it. Phases 3 and 7 cite these as **UX-1 … UX-14** (UX-1…UX-8 originally; UX-9…11 added in v1.8, UX-12 in v1.9, UX-13 in v1.9.3, UX-14 in v1.9.22); read-model tests in TEST_PLAN §8 enforce the testable ones.*

**UX-13 — Arenas never merge (D71c).** When more than one arena exists, the UI selects one **active arena** and every screen renders that arena only, with a provenance line naming its control population, cost-model version, and calibration id. A cross-arena view is permitted **only** as clearly-separated side-by-side panels, each with its own provenance and its own sort — **never a single sorted ranking that mixes arenas**. Ranking a strategy from one universe against a strategy from another compares numbers produced under different cost models and different control populations — the same category error UX forbids between cadence families. There is no "sort all strategies across all arenas" control. *(v1.9.3; full spec in ARENA_ARCHITECTURE_v1.9.3 §4. Specified in text only — the consolidated `alphalab_ux_mockups.html` shows an arena label but not the switcher or the provenance line, since both bind only when a second arena exists. Follow this rule's text, not the mockup's absence.)*

### UX-14 — paired comparison & near-clone visibility
**Rule:** any two strategies that share most of their exposure are comparable by **differencing their daily returns** (M.1) — the shared market/mechanics/costs cancel, leaving a paired statistic with a far tighter MDE than either standalone verdict. The Paired-comparison screen renders this for **two cases from one mechanic**, and is not AI-specific: (a) an **AI contestant vs its no-LLM twin** (pricing the seat's contribution, MASTER §23.3), and (b) a **near-clone pair** flagged by the §10 anti-clone rule (e.g. Betting-Against-Beta vs Low-Vol, Time-Series Momentum vs Breakout). Each case shows the two raw totals small and dimmed, then the paired-difference gauge (estimate dot inside its ± MDE band) carrying the verdict, on the same 21-day cadence and `IndistinguishableFromRandom`/`TooEarly` discipline as every other verdict. Additionally, a strategy that is a **live member of a near-clone pair** carries a clone chip (`≈ <other>`) on the Strategies leaderboard, and the roster shows each strategy's **factor family** (trend / reversal / risk / AI) so an operator sees the roster's diversity and where the clone risk sits. Per §10, both members of a pair stay live **only** while their paired difference is outside its MDE; a pair that sits inside the band indefinitely is a retire-one signal, surfaced here, never auto-actioned.

## The governing principle
The v5 screens were shaped like a leaderboard; the system's own statistics (MASTER §1.1) say most leaderboard gaps are unjudgeable for months-to-years. **The UI must make the epistemically honest reading the visually easiest reading.** Every rule below is that principle applied to one surface.

---

### UX-1 — Verdicts outrank returns; tiers replace ranks
**Rule:** the verdict chip (`★ earned > MDE` / `too early` / `✕ Suspect — vetoed`) is the highest-contrast element in every strategy row; α values whose head-to-head gap sits inside the MDE render at reduced contrast with a `~` prefix; rows group into **tiers** (distinguishable-above / not-yet-distinguishable / below-or-flagged / reference) with no ordinal rank inside a tier.
**Why:** users read hierarchy, not footnotes; ordinal ranks inside the MDE band assert an order the data cannot support.
**Where:** Strategies screen (Phase 3 GUI); read-model test `UX1_InsideMde_MetricCell_IsDimmedWithTilde`; tier assignment comes from the gate's verdicts, never from sorting by return.

### UX-2 — The evidence meter (the signature element)
**Rule:** every head-to-head in `TooEarly` shows a progress meter: days accrued vs ≈days needed for the *current* gap to clear the NW-corrected MDE, plus the implied calendar date ("verdict possible ≈ Feb 2027 at current pairing"). Recomputed at each evaluation; the date moves with the gap.
**Why:** `TooEarly` as a dead-end message corrodes the daily ritual; the same message as visible accrual sustains it. The target is pure arithmetic — Appendix C run backwards: `T_needed = (2.8·σ_LR·252 / gap)²`.
**Where:** Strategies rows + comparison panels (Phase 3); hero treatment in the mockup; `power_reports` already stores every input.
**v1.9.7 addendum (finding 117):** the meter also renders a **pairing-tightness chip** — the pair's current σ_LR and "days to verdict at the current gap" — because pairing tightness is the operator's single biggest controllable lever on verdict speed (MASTER §1.1: verdict time scales with 1/σ_LR²; near-twin candidates that differ in one component reach verdicts in years, loose pairs in decades). This is pure surfacing: every input is already in `power_reports`, and the design lesson it teaches — *propose siblings, not strangers* — is the cheapest power upgrade the operator controls.

### UX-3 — The morning glance is an attention queue
**Rule:** the top of Live (and the mobile **home** screen) answers three questions in ten seconds: run status + watermark; **needs-you** items (frozen positions, membership divergence, budget degradation) with links to resolve; **changed-since-last-visit** (health transitions, gate verdicts, auto-retires), with a badge count on mobile. Positions demote below the fold on mobile.
**Why:** the daily job is the product's heartbeat; the UI's job each morning is triage, not admiration. Live per-position P&L on the mobile home rewards tick-watching a paper account — the wrong habit.
**Where:** Live screen + mobile home (Phase 3 minimal: run status + transitions; Phase 7 full: needs-you queue wired to §13.6 freezes and FR-4 divergence). Requires a trivial `last_seen` marker per screen.

### UX-4 — The population is a band and a percentile, everywhere
**Rule:** the 200-member matched population renders as (a) **one** table row showing its 5–95% band (never per-member rows), (b) a shaded band under every equity/alpha chart, and (c) a **percentile chip** on every strategy row ("97th pct of 200 matched randoms"). The cost-free population appears only as a labeled reference band.
**Why:** the percentile is the most human-legible statistic in the system — more intuitive than the alpha it summarizes; the band makes "living above your noise floor" something you *see*.
**Where:** Strategies, Live chart, monitor S3 panel (Phase 3); band styling colorblind-safe (cyan-tinted area + dashed edges, never green/red fills).
**v1.9.7 addendum (finding 120):** while the forward universe is the D70 S&P 100 slice, the S3 panel carries the calibration-vintage caveat string from MASTER §20.7 ("curves calibrated on S&P 500 as-of membership …") — served in the read-model, rendered verbatim.

### UX-5 — Progressive disclosure for health; plain words before codes
**Rule:** health pills expand in place: status → each signal in **plain language** ("backtest gap", "above its noise floor?", "edge decay") with the S-code and threshold as secondary text → evidence chart one level deeper. Pills always pair **icon + text**, never color alone; strong red/green is reserved for verdicts and warnings, not raw P&L (returns use neutral ink + ▲▼ glyphs).
**Why:** "Warning (S1)" is expert shorthand; ~8% of men can't rely on the red/green channel; and P&L-colored dopamine fights the system's judged-on-alpha ethos.
**Where:** every health pill (Phase 3 minimal, Phase 6 full signal set); plain-name mapping table lives beside the monitor config.

### UX-6 — Teach in place (Appendix M piped into microcopy)
**Rule:** statistical terms carry one-line plain microcopy at point of use — "smallest gap this track can judge: ±4.2% ann" — with an info affordance opening the relevant Appendix M entry. Applies to MDE, percentile, deflated Sharpe, calibration, episode counts.
**Why:** the master doc's math primer exists; the UI should be its delivery mechanism at the exact moment of need.
**Where:** all screens; copy strings centralized so the microcopy and Appendix M never drift apart.

### UX-7 — Near-misses and live exits on "Why this trade"
**Rule:** the decision screen additionally shows **what almost happened today** — names just under the buy bar, orders trimmed by the participation cap, rejections by guardrails, each with its reason and a link to the log row — and the exit plan renders **live distance-to-exit** ("rule: rank < 80 · currently rank 34 · held 11d"), not just the rule.
**Why:** nothing builds trust in a filter like seeing what it declined; a trade isn't legible until you can see how close its end is.
**Where:** Why-this-trade (Phase 7); data already exists in `decisions`, `capacity_rejections`, and guardrail logs — this is pure surfacing.

### UX-8 — Honest states: replay quarantine, regime anecdotes, day one
**Rule:** (a) replay content sits on a hatched slate-grey field (`--replay`) with an explicit watermark ("REPLAY — validates the machinery · never evidence a strategy works"), is never co-plotted with forward lines, and never appears on Strategies or the Go-live log; (b) every regime-conditional figure carries its **episode count**, and n < 3 wears an **anecdote** badge; (c) the empty/day-one state is a designed teaching screen — what's running (benchmarks + controls), when first verdicts become possible, what to do meanwhile — never a bare "no data".
**Why:** a replay screenshot must be unable to impersonate a track record; n=1 regime stories must look like stories; and NFR-3's "renders empty" should become "teaches while empty".
**Where:** replay views (Phase 4), regime dashboard (Phase 7), all screens' empty states (Phase 0 shell onward); tests `UX8_ReplayNeverCoplotted`, `FR24_RegimeClaims_CarryEpisodeCount`.

---

### UX-9 — Allocation: every weight shows its arithmetic (v1.8, D51)
**Rule:** the Allocation screen renders the book as one horizontal stacked bar (gold = Live; baselines/populations are not allocatable and never appear); below it, one derivation row per strategy — `α̂ ± se → α̃ (shrunk) → target → applied` — with any clamp that bound (TooEarly cap, Suspect decay, band, floor/ceiling) rendered as a labeled chip **on the arrow it affected**; a weight-history strip with band-move markers, each linking to its `allocation_log` row.
**Why:** the primary improvement mechanism must be as auditable on screen as the gate is; a weight whose derivation isn't visible is a black box in the one place the system acts continuously.
**Where:** Allocation (Phase 7 full; read-only minimal acceptable in Phase 3); view-model test `UX9_ClampShown_WhenBinding`.

### UX-10 — The journal is a workflow, not a notes app (v1.8, D52)
**Rule:** (a) Analysis is two panes — left: research-assistant actions (today's regime brief, request a bull/bear brief, run the skeptic) each showing its **budget cost before dispatch** and landing output as a linked journal entry; right: the journal by kind, hypothesis rows showing their pre-declared metric/window and a status chip (`open` / `outcome due` — nagging / `confirmed·refuted·inconclusive`). (b) Creating a candidate from anywhere routes through the **pre-registration modal** (hypothesis, or explicit `unregistered` which renders permanently on the strategy card).
**Why:** the operator-learning KPI is only real if pre-registration is the path of least resistance and outcome closure is unavoidable.
**Where:** Analysis + Journal (Phase 7); the CandidateFactory modal ships with Phase 3's factory; tests `FR28_*`.

### UX-11 — Ops surfaces: glance, drill, act (v1.8, D54/D55)
**Rule:** (a) **Data health** is a fixed grid of named feeds (bars, actions, membership + cross-check, sectors, factors, calendar, LLM budget, API headroom), each with freshness, last-validation result, and watermark, each cell linking to its log table. (b) **Replay** has exactly three affordances — configure/launch, a progress strip, archived calibration/validation reports — all on the hatched slate-grey quarantine field (`--replay`, UX-8a) with the standing watermark text. (c) **Admin interventions** live behind a distinct panel with the D55 typed confirmation, a preview of the exact rows to be written, and the audit trail rendered beneath — amber treatment, never verdict gold.
**Why:** fail-closed needs a front end where resolving a freeze is easy, auditable, and impossible to do accidentally; replay must be operable without ever looking like a track record.
**Where:** Data-health (Phase 7 full; Phase 1 minimal freshness list), Replay (Phase 4), Admin (Phase 7); tests `FR31_*`.

### UX-12 — The separation state: non-separation is said out loud (v1.9, D63)
**Rule:** every promotable strategy row and card renders its `separation_state` verbatim from the read-model: `distinguishable` (quiet green), `emerging` (neutral), and — once track ≥ `Verdicts.SeparationMinTrackDays` — `none` renders the **`IndistinguishableFromRandom` chip** with its day count ("no separation from 200 matched randoms after 417 days"), slate-gray, always **beside** the gate verdict, never replacing it. `TooEarly` + chip read together as: *too early to confirm an edge, and so far indistinguishable from luck.* The chip carries no red/alarm treatment — it is a finding, not a failure — and clicking it opens the strategy's percentile path against the D56 curves.
**Why:** the population channel cannot falsify a cost-matched edgeless strategy (D63) — without this chip, the honest common case degenerates into years of silent `TooEarly`, which reads as "the lab has nothing to say." Non-separation *is* the lab's fast product; the UI must say it plainly.
**Where:** Strategies + Live + strategy cards (Phase 3 read-model; rendered wherever the UI workstream lands per D65); test `UX12_SeparationChip_RendersWhenTrackExceedsMinAndStateNone`. *(Note: this rule is specified in text only — the consolidated `alphalab_ux_mockups.html` does depict the separation chip on the Strategies screen; follow this rule's text.)*

## Visual system — design tokens (v5 direction) *(client-side; not a read-model rule)*

Component anatomy — the exact element, size, and token treatment that renders each honesty read-model field (verdict_chip, MetricCell, separation_state, population_percentile, …) — lives in UX_DESIGN_SYSTEM_v1.9.md. That doc references these tokens; it never redefines them. This table remains authoritative for colour, type, and their meaning.

*Consolidated from the mockup set (now the single `alphalab_ux_mockups.html`) so the palette is one named reference, not scattered across rules. The mockups are the authoritative visual source; this table restates their tokens. Unlike UX-1…UX-14, this is a **client rendering** concern, not a read-model rule — but three tokens are **semantic honesty encodings**, and reserving them matters from day one (see below).*

| Token | Value | Role |
|---|---|---|
| `--bg` | `#0E1220` | app background |
| `--panel` / `--panel2` | `#161B2E` / `#121729` | cards, table surfaces |
| `--ink` | `#E8ECF5` | body text |
| `--line` | `#2A3350` | hairlines, table rules |
| `--gold` | `#E9B44C` | **live-strategy encoding only** (marker, left edge, glow) — UX-9 |
| `--cyan` / `--band` | `#4CC9E0` / 10% | population bands (colorblind-safe) — UX-4; also the **math-seat** accent (a seat marker never overlaps a band region, so no conflict) |
| `--violet` | `#8B7CF6` | **AI-seat encoding** (seat strip, pills, row edge, decision-audit) — §23 |
| `--replay` | `#5A6B8C` | replay quarantine field (muted/inert on purpose — must never read as live) — UX-8a |
| `--up` / `--down` | `#3FD68C` / `#F2555A` | reserved for verdicts/warnings, **not** raw P&L — UX-5 |
| `--amber` | `#E2A23B` | admin/intervention treatment — UX-11c |
| `--disp` / `--body` / `--mono` | Archivo / Inter / IBM Plex Mono | display / body / **all numerics** |

**Reserve the semantic colors from day one.** `--gold` (means *live*), `--replay` (means *replay quarantine*), and `--band`/`--cyan` (means *the matched population*) are **honesty devices, not decoration** — gold-means-live and slate-grey-means-replay are how a screenshot is prevented from lying (UX-8a/UX-9). `--violet` is the **AI-seat** encoding (the seats are priced by the same arena, golden rule 32, so violet is an identity marker, not a trust signal — it carries no honesty meaning and may be reused freely for AI-seat surfaces). Even before the data screens are built, no surface may reuse `--gold`, `--replay`, or `--band`/`--cyan` for ornament, or a later screen inherits a false signal. All numerics render in the mono face (`--mono`); strong red/green (`--up`/`--down`) is reserved for verdicts/warnings, never raw P&L (UX-5).

**Shell theming may lead; data screens follow (D65).** Applying these tokens to the app *chrome* (dark sidebar/panel surfaces, hairline top bar, the arena name in the display face, mono numerics, the Replay nav item's slate-grey chip, empty-state notices as quiet panel cards) is a **shell-only** job that can be done any time — it is the deferred BUILD checkpoint **0.7g**, distinct from building tables/charts/data screens (the D65 UI workstream due before Phase 7). If pixel-faithfulness to the mockups is wanted, **self-host** the three font files under `wwwroot` rather than adding a CDN dependency to a localhost-only tool. Housekeeping to fold in when theming: trim the committed Bootstrap dist (or drop Bootstrap once the token system lands — the mockups need only the custom CSS), and keep `NotFound.razor`'s copy in the app's voice (not the raw template sentence).

## What deliberately does not change
The plain-naming system, gold-means-live, the mono data typography, dual benchmarks on-screen, the four-part decision explanation, and the "no number stands alone" footer discipline — the v5 direction stays; v6 re-weights it so the honesty devices lead.

## Phase wiring
Phase 0: empty-state copy framework (UX-8c). Phase 3: UX-1, UX-2, UX-3 (minimal), UX-4, UX-5 (minimal), UX-6, UX-10's pre-registration modal, UX-9 minimal, UX-12 (read-model; FR-35). Phase 4: UX-8a, UX-11b. Phase 6: UX-5 (full). Phase 7: UX-3 (full), UX-7, UX-8b, UX-9 full, UX-10 full, UX-11a/c. UX-13 carries no phase obligation in the single-arena build — it binds the moment a second arena is registered (a future config + instance operation, ARENA §6); the calibration-provenance line may ship any time after Phase 3 alongside UX-4's chips. Screens may lag per D65 (API-only (Scalar) sanctioned until Phase 4 sign-off; hard deadline Phase 7 exit) — read-models and their tests never lag. TEST_PLAN §8 gains `UX1_InsideMde_MetricCell_IsDimmedWithTilde`, `UX8_ReplayNeverCoplotted`, and `UX12_SeparationChip_RendersWhenTrackExceedsMinAndStateNone`.

> Research/paper-trading only. Not investment advice.
