# UX_DESIGN_SYSTEM_v1.9 — the component catalog

> **What this doc is, and is not.** This is the *visual assembly layer* only — the anatomy of the recurring UI components and how they read the honesty read-models. It is the bridge between two documents that already exist and are authoritative:
>
> - **Colour, type, and their meaning** live in `UX_GUIDELINES_v1.9.md` → "Visual system — design tokens". This doc **does not restate the palette**; it references those tokens (`--gold`, `--cyan`/`--band`, `--violet`, `--replay`, `--up`/`--down`, `--amber`, `--disp`/`--body`/`--mono`) by name and never redefines a value. If a colour question arises, UX_GUIDELINES wins.
> - **What each screen must render** lives in `UX_GUIDELINES_v1.9.md` rules UX-1…UX-15. This doc **does not restate a rule's behaviour**; it says which component satisfies it and what the component looks like.
>
> The one thing neither of those documents carries is *component anatomy* — "given the backend hands me a `verdict_chip` field, exactly what element renders it, in which token colours, at what size." That gap is what this doc fills. Nothing here introduces new honesty behaviour; if a rule here and a UX-rule ever disagree, UX_GUIDELINES is authoritative and this doc is the bug.

---

## The binding principle (why this doc can be short)

Per **D57/D58**, the UI computes nothing. Every honesty decision — dimming, tier, percentile, verdict, clamp, quarantine — is already resolved into a serializable read-model DTO field in `AlphaLab.Core`/`AlphaLab.Evaluation`, and **the UI renders that field verbatim**. So a component is never "logic + style"; it is *only* style bound to a named field. That is why this catalogue is a table of field → element → tokens, not a pile of conditionals. A component that branches on a threshold, sorts a tier, or decides a dimming is a **bug** — that decision belongs in the read-model (D58), and CI already forbids `AlphaLab.Web` from referencing the evaluation logic that would let it.

Every component below names the exact read-model field it binds. If a field is not in the read-model, the component does not compute it — it is added to the DTO first.

---

## Token usage rules (pointers, not redefinitions)

These restate *nothing* from UX_GUIDELINES; they only pin how the catalogue uses the tokens, so a builder does not have to re-derive it per component.

- **Numerics** — every number (α, %, days, MDE, percentile) renders in `--mono`. No exceptions; a number in the body face is a bug.
- **Semantic colours are load-bearing, never decorative.** `--gold` = *live* only. `--cyan`/`--band` = *the matched population* only (and the math-seat accent, which never overlaps a band region). `--replay` = *replay quarantine* only. `--violet` = *AI-seat identity* (carries no trust meaning; reusable freely on AI-seat surfaces). Using any of these for ornament makes a screenshot lie — see UX_GUIDELINES "Reserve the semantic colours from day one."
- **`--up`/`--down` (green/red) are reserved for verdicts and warnings, never raw P&L.** Raw returns render in neutral `--ink` with `▲`/`▼` glyphs (UX-5).
- **Surfaces:** `--bg` app ground, `--panel`/`--panel2` cards and table surfaces, `--line` hairlines. Radius and spacing scale below.

### Scale (the one place this doc *adds* a token, because UX_GUIDELINES leaves it implicit)

Derived from the reference mockup; add these to the token set when the shell is themed (Phase 0.7g):

| token | value | use |
|---|---|---|
| `--r-chip` | `999px` | chips, pills |
| `--r-card` | `12px` | cards, panels |
| `--r-cell` | `6px` | metric cells, small inset blocks |
| `--sp-1 … --sp-5` | `4 / 8 / 12 / 16 / 24px` | the spacing step; components use these, never arbitrary px |
| type scale | `11 / 12.5 / 14 / 16 / 20 / 28px` | caption / label / body-sm / body / lede / display-sm |

If these ever conflict with a value added to UX_GUIDELINES later, UX_GUIDELINES wins and this table is corrected.

---

## Component catalogue

Each entry: **the read-model field it binds → the element → the token treatment → the UX-rule it satisfies.** Blazor components live in `AlphaLab.Web`; each is a `.razor` + a scoped `.razor.css` using only the tokens above. None takes a threshold, a sort, or a computed flag as a parameter — only resolved read-model fields.

### 1. `VerdictChip` — binds `verdict_chip`, `tier` (UX-1)
The highest-contrast element in a strategy row. Renders the `verdict_chip` string verbatim in one of three fixed treatments keyed off the field's own value, not a recomputation:

- `earned > MDE` → filled `--gold` text on a faint gold wash, `★` leading glyph. Gold *only* here and on the live encoding — never elsewhere.
- `too early` → outline chip, `--ink` text at reduced weight, no colour. It is a neutral state, not a warning.
- `Suspect — vetoed` → `--down` text/border, `✕` glyph. This is the one place a verdict wears red.

Shape: `--r-chip`, `--mono`, label size (12.5px), `--sp-1` vertical / `--sp-2` horizontal padding. The chip never sorts or ranks; `tier` grouping is done by the row container (component 8), not here.

### 2. `MetricCell` — binds the α DTO `{value, display, prefix, reason, mde}` (UX-1, UX-6)
The single most important component, because it is where "a number is never shown without its uncertainty" becomes pixels. It renders **verbatim**:

- `value` in `--mono`.
- if `display == "dimmed"` → reduced contrast (≈55% `--ink`) **and** render `prefix` (`~`) ahead of the value. The cell does **not** decide dimming — it reads `display`. `reason` (`inside_mde`) drives the UX-6 microcopy tooltip ("smallest gap this track can judge: ±<mde> ann").
- `mde` always available to the teach-in-place affordance; never hidden.

A `MetricCell` that computes whether to dim is a bug — `display` is authoritative.

### 3. `PercentileChip` — binds `population_percentile` (UX-4c)
"97th pct of 200 matched randoms," `--mono`, on every strategy row. Tinted with `--cyan`/`--band` at low opacity (the population colour), never green/red. Clicking opens the S3 percentile path. Carries the calibration-vintage caveat string verbatim when the read-model supplies it (finding 120).

### 4. `SeparationChip` — binds `separation_state` (UX-12, D63)
Renders the state verbatim: `distinguishable` → quiet `--up` (the one non-verdict green use, sanctioned by UX-12); `emerging` → neutral `--ink`; `none` (past `SeparationMinTrackDays`) → the **`IndistinguishableFromRandom`** chip on `--replay`-adjacent slate-grey with its day count string ("no separation from 200 matched randoms after 417 days"). **No red, no alarm** — it is a finding. Always renders **beside** the `VerdictChip`, never instead of it (the read-model provides both; the row places them side by side).

### 5. `PopulationBand` — binds the population `{p5, p50, p95, n}` band object (UX-4a/b)
One shaded area under equity/alpha charts and **one** table row (never per-member rows). Fill `--cyan`/`--band` at 10%, dashed edges, colourblind-safe — never green/red fills. The cost-free population renders only as a labelled reference band.

### 6. `PairedComparisonGauge` — binds the paired-difference read-model (UX-14, M.1)
The estimate dot inside its ±MDE band. Two raw totals rendered **small and dimmed** (they are not the point); the paired-difference gauge carries the verdict via an embedded `VerdictChip`/`SeparationChip`. Used for both the AI-contestant-vs-twin case and the near-clone case. Accent by context: `--violet` when a seat is involved (AI identity), `--cyan` otherwise. **This is the component the mockup's "pair A/B/C/D" cards render** — see the reference mockup for the finished look.

### 7. `CloneChip` — binds the near-clone flag (`≈ <other>`) (UX-14)
Small `--mono` chip on a leaderboard row for a live member of a near-clone pair. Neutral treatment; it is a diversity signal, not a warning.

### 8. `StrategyRow` / `TierGroup` — binds `StrategyRow` incl. `seat`, `verdict_chip`, `separation_state`, `population_percentile`, `caveat` (UX-1, and the §23.6 `seat` badge)
The leaderboard row. Composes components 1–4 and 7. Rows group into the four **tiers** from the read-model (`distinguishable-above / not-yet-distinguishable / below-or-flagged / reference`) with **no ordinal rank inside a tier** — the group is a container, it does not sort. The `seat` field (`'math' | 'ai'`) drives a small identity badge: `--cyan` for math, `--violet` for AI. The turnover cost-match `caveat` renders verbatim when present (finding 115).

### 9. `EvidenceMeter` — binds the `TooEarly` progress read-model + pairing-tightness (UX-2, the signature element)
Days-accrued vs days-needed for the current gap to clear the NW-MDE, plus the implied calendar date string ("verdict possible ≈ Feb 2027") and the pairing-tightness chip (σ_LR, days-to-verdict). All inputs come from `power_reports` via the read-model; the meter computes none of them. This is the product's signature element — give it the visual weight the mockup does.

### 10. `HealthPill` — binds the health-signal read-model (UX-5, progressive disclosure)
Expands in place: status → each signal in **plain language** ("backtest gap," "above its noise floor?," "edge decay") with the S-code and threshold as secondary text → evidence chart one level deeper. **Icon + text always, never colour alone.** Strong `--up`/`--down` only for verdict/warning, never raw P&L.

### 11. `AllocationBar` / `DerivationRow` — binds the allocation read-model incl. each `clamp_bound` (UX-9, D51)
One horizontal stacked bar (`--gold` = Live; baselines/populations never appear — they are not allocatable). Below it, one derivation row per strategy: `α̂ ± se → α̃ (shrunk) → target → applied`, with any clamp that bound rendered as a labelled chip **on the arrow it affected** (the read-model attributes the clamp to the arrow; the row places it).

### 12. `ReplayField` — binds `quarantined: true` (UX-8a, D65)
Any replay content sits on the hatched slate-grey `--replay` field with the standing watermark ("REPLAY — validates the machinery · never evidence a strategy works"). **Never co-plotted with forward lines; never on Strategies or the Go-live log.** The component is a wrapper: if `quarantined`, it applies the field and watermark unconditionally. This is a honesty rail, not a style choice.

### 13. `DataHealthGrid` — binds the Data-health read-model (UX-11a, D77)
Fixed grid of named feeds (bars, actions, membership + cross-check, sectors, factors, calendar, LLM budget, API headroom). Each cell: freshness, last-validation result, watermark, and a link to its log table. Add the `data_quality_flags` slot (D77). This is the surface that makes "a number stands on a named, dated source" visible.

### 14. `AdminPanel` — binds the admin-action contract (UX-11c, D55)
Distinct panel, **`--amber` treatment, never verdict `--gold`.** D55 typed confirmation, a preview of the exact rows to be written, audit trail beneath. The amber-not-gold rule is load-bearing: an admin action must never wear the colour that means "a strategy earned this."

### 15. `ReadModelStampBanner` / `EmptyState` — binds `ReadModelStamp` (D66, UX-8c)
Every screen reads the stamp first. `status == "no_run_yet"` → the designed day-one teaching state (what's running, when first verdicts become possible, what to do meanwhile) — never a bare "no data." `status == "stamped"` → render, showing `run_id` + `watermark` in the UX-3 glance. The component branches on `status`; it never treats a null field as "probably no run."

### 16. `PlannedBadge` — for design-intent surfaces (build-phasing honesty)
A small `--amber`-outline "design intent · not yet built" badge for panels whose read-models are specified but not yet wired (e.g. the allocator-value-add and researcher-yield KPI cards, which are later-phase read-models). Keeps the built-vs-designed distinction visible on the surface itself, matching how the docs separate the two everywhere else.

### 17. `CohortCurvePanel` - binds `CohortMaturationReadModel` (UX-15, D88)
One line per admission cohort on the Analysis/Journal research surface: median D36 population percentile vs track length t in trading days, the x-axis labeled as age, never a calendar date. Forward cohort medians render in `--cyan` inside their `--band` shading; a segment with `display == "dimmed"` renders at reduced contrast (~55% `--ink`) with a `~` prefix and its `reason` (`thin_cohort` / `inside_mde`) as the microcopy tooltip - the panel does **not** decide dimming, it reads `display` (the MetricCell rule). Cohorts with `quarantined: true` render only inside `ReplayField` (12), physically separated from the forward axes. Arena-stamped in the corner; never merges arenas (UX-13). Wears `PlannedBadge` (16) until the Phase-3 read-model lands. Reference look: `docs/mockups/cohort_curve_panel.html` (illustrative; the consolidated mockup absorbs it in the UI workstream). A `CohortCurvePanel` that computes its own dimming, drops retired members, or co-plots a replay cohort is a bug.

---

## How a builder uses this doc

1. Read the screen's UX-rule(s) in `UX_GUIDELINES_v1.9.md` — *what must render and why.*
2. Read the screen's read-model DTO (`AlphaLab.Core`/`AlphaLab.Evaluation`) — *the exact fields available.*
3. For each field, use the component above — *the element and token treatment.*
4. If a needed value is not in the DTO, **add it to the read-model first** (D58) — never compute it in the component.
5. Cross-check the finished look against `alphalab_ux_mockups.html` (the reference Blazor client) — but the *rules* are enforced in the read-models, so the mockup is a look reference, not a source of truth.

## What this doc deliberately does not do

- It does not redefine any colour, font, or semantic-colour meaning — those are UX_GUIDELINES' Visual-system table.
- It does not restate any UX-rule's behaviour — those are UX-1…UX-15.
- It does not put any honesty logic in a component — that is D58's read-models.
- It is a look/assembly reference; the enforcement points remain the read-model tests (`AlphaLab.Evaluation.Tests`), not browser tests.
