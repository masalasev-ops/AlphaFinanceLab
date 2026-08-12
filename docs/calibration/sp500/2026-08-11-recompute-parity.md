# Recompute report — arena `sp500` — 2026-08-11

*D106 recompute harness (MASTER §25), settlements D117. Run kind `replay`. **Report-only — no rows were written** (D117 clause 1).*

## Specification

- **Spec:** `parity (no rule overrides) · arithmetic `jensen` (resolved from the generation: reproduces 144/144 stored promotions)`
- **Tier:** `DirectRead` — the inputs this change requires (§25.2 as amended by D117)
- **Subjects recomputed:** 401

## Known limits carried with this run

- **`GateOptions` is not as-of resolvable** (§25.1). `MinTrackDays` and the MDE parameters are bound from appsettings at composition, not versioned config rows, so reproducing this generation's promotions rests on the `Gate` block being unchanged since it ran — an assumption the harness cannot verify from the store.
- **Truncation-limited subjects EXCLUDED** (D117 clause 3, finding 338): `threshold:sma50`. Each retired during the generation, so it left the promotable set and stopped emitting rows; the sessions after that were never recorded and the "would not have retired" direction is not recomputable. Named rather than silently dropped.

## Artefacts

| Artefact | Stored | Recomputed | Differing |
|---|---:|---:|---:|
| `overfitting_status` | 95600 | 95600 | **0** |
| `go_live_log(Promoted)` | 144 | 144 | **0** |
| `go_live_log(WouldRevert)` | 18533 | 18533 | **0** |

## Cohort separation — the finding-280 measurement

*D63 is asymmetric: `anti` SHOULD be caught, `noedge` should NOT — "S3 never flags a merely edgeless strategy" (OVERFITTING_MONITOR §3). Finding 280 measured both at 50/50 **live at session 639** (~2.5y), which is why this is reported at several horizons: the ever-Suspect predicate SATURATES, and over a full 20-year window every cohort reaches it. A single full-window number would discriminate nothing — finding 289's window-monotonicity lesson, applied to a different EVER predicate.*

### 1 year (252 sessions)

| cohort | n | ever-Suspect stored | ever-Suspect recomputed |
|---|---:|---|---|
| `anti` | 50 | 50/50 | **50/50** |
| `noedge` | 50 | 50/50 | **50/50** |
| `edge` | 250 | 123/250 | **123/250** |
| `naive` | 50 | 50/50 | **50/50** |

Separation (anti − noedge): **0.00 → 0.00**  — *SATURATED: both judged cohorts are within one plant of the ceiling, so this horizon cannot discriminate and the sign of its separation is noise*

### 3 years (756 sessions)

| cohort | n | ever-Suspect stored | ever-Suspect recomputed |
|---|---:|---|---|
| `anti` | 50 | 50/50 | **50/50** |
| `noedge` | 50 | 50/50 | **50/50** |
| `edge` | 250 | 200/250 | **200/250** |
| `naive` | 50 | 50/50 | **50/50** |

Separation (anti − noedge): **0.00 → 0.00**  — *SATURATED: both judged cohorts are within one plant of the ceiling, so this horizon cannot discriminate and the sign of its separation is noise*

### full window

| cohort | n | ever-Suspect stored | ever-Suspect recomputed |
|---|---:|---|---|
| `anti` | 50 | 50/50 | **50/50** |
| `noedge` | 50 | 50/50 | **50/50** |
| `edge` | 250 | 245/250 | **245/250** |
| `naive` | 50 | 50/50 | **50/50** |

Separation (anti − noedge): **0.00 → 0.00**  — *SATURATED: both judged cohorts are within one plant of the ceiling, so this horizon cannot discriminate and the sign of its separation is noise*

### Detection SPEED — median sessions to first Suspect

*The ever-Suspect rates above saturate; this does not. `anti_detection_speed` is named for speed but is itself an EVER predicate ("<50 % of anti plants ever Suspect"), so this is the first thing in the corpus that measures what that name says.*

| cohort | n | median sessions stored → recomputed | never flagged stored → recomputed |
|---|---:|---|---|
| `anti` | 50 | 126 → **126** | 0 → **0** |
| `noedge` | 50 | 126 → **126** | 0 → **0** |
| `edge` | 250 | 210 → **210** | 5 → **5** |
| `naive` | 50 | 126 → **126** | 0 → **0** |

**Speed gap (anti median − noedge median): 0 → 0 sessions.** NEGATIVE is the D63 direction — anti caught sooner than merely edgeless.

Unchanged: this rule change does not alter how much sooner anti-predictive plants are caught than edgeless ones.

**Verdict (read from the shortest non-saturated horizon):**

**Not readable — every horizon is saturated.** The instrument cannot judge this change, and that is a statement about the MEASUREMENT, not evidence that the change did nothing. A shorter horizon or a per-evaluation flag rate is needed before any finding-280 candidate can be scored.

## C-1 detection power — recomputed vs frozen (horizon 10y = 2520 sessions, power 80 %)

*The monthly edge ladder IS the C-1 sweep (Change 4 / D101 — daily cannot promote under its cost drag). Same denominator, same session-index grid and same selection rule as the frozen curve, or the two would not be comparable.*

| rung | seeds | promoted (stored → recomputed) | P(promoted by H) stored → recomputed | median sessions stored → recomputed |
|---:|---:|---|---|---|
| 2 %/yr | 50 | 9 → **9** | 0.14 → **0.14** | 1386 → **1386** |
| 4 %/yr | 50 | 35 → **35** | 0.52 → **0.52** | 2058 → **2058** |
| 8 %/yr | 50 | 50 → **50** | 0.90 → **0.90** | 1197 → **1197** |
| 16 %/yr | 50 | 50 → **50** | 0.98 → **0.98** | 819 → **819** |

- **α\*(H) implied by the FROZEN promotions:** 6.95 %/yr
- **α\*(H) implied by the RECOMPUTED promotions:** 6.95 %/yr

Both sides imply a reachable floor; this change moves its level rather than its existence.

### What each candidate PATIENCE HORIZON would buy

*`Gate.DetectabilityHorizonYears` is what puts the floor out of reach (finding 336), and it is an appsettings value rather than a spec parameter — so without this table the only way to ask "what would 5 years buy?" is to EDIT the threshold and re-run, which is the shape of change rule 8 exists to make deliberate. Reading it off a table keeps the question separate from the act. Current setting: **10 years**.*

| horizon | P(promoted) per rung, recomputed | α\*(H) frozen | α\*(H) recomputed |
|---|---|---|---|
| **1y** (252) | 2%: 0.04 · 4%: 0.14 · 8%: 0.28 · 16%: 0.38 | **unreachable (+∞)** — no rung reaches the power at this horizon | ****unreachable (+∞)** — no rung reaches the power at this horizon** |
| **2y** (504) | 2%: 0.04 · 4%: 0.14 · 8%: 0.36 · 16%: 0.46 | **unreachable (+∞)** — no rung reaches the power at this horizon | ****unreachable (+∞)** — no rung reaches the power at this horizon** |
| **3y** (756) | 2%: 0.04 · 4%: 0.14 · 8%: 0.40 · 16%: 0.50 | **unreachable (+∞)** — no rung reaches the power at this horizon | ****unreachable (+∞)** — no rung reaches the power at this horizon** |
| **5y** (1260) | 2%: 0.06 · 4%: 0.20 · 8%: 0.58 · 16%: 0.84 | 14.77 %/yr | **14.77 %/yr** |
| **10y** (2520) | 2%: 0.14 · 4%: 0.52 · 8%: 0.90 · 16%: 0.98 | 6.95 %/yr | **6.95 %/yr** |
| **15y** (3780) | 2%: 0.14 · 4%: 0.62 · 8%: 0.98 · 16%: 1.00 | 6.00 %/yr | **6.00 %/yr** |
| **20y** (5040) | 2%: 0.18 · 4%: 0.70 · 8%: 1.00 · 16%: 1.00 | 5.33 %/yr | **5.33 %/yr** |

**The shortest horizon at which the floor becomes reachable is 5 years** (α\* 14.77 %/yr). That is the number the patience decision is about: it is what the arena would have to be willing to wait before it could adjudicate ANY pre-registered claim. Choosing it is a decision about patience and takes its own D-number — never a config edit to make an unwelcome answer go away (rule 8, D110 R3).

## Verdict

**`FX-RecomputeParity` HOLDS.** All three artefacts reproduce exactly under the current rules, so the harness reproduces this generation's machinery and may be used to score a rule change (D117 clause 2, still subject to the confirmation slice before anything is frozen).

