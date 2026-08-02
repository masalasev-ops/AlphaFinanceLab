# Recompute report — arena `sp500` — 2026-08-02

*D106 recompute harness (MASTER §25), settlements D117. Run kind `replay`. **Report-only — no rows were written** (D117 clause 1).*

## Specification

- **Spec:** `alpha-jensen-coherent: gate.alpha_definition=jensen`
- **Tier:** `EquityDerived` — the inputs this change requires (§25.2 as amended by D117)
- **Subjects recomputed:** 401

## Known limits carried with this run

- **`GateOptions` is not as-of resolvable** (§25.1). `MinTrackDays` and the MDE parameters are bound from appsettings at composition, not versioned config rows, so reproducing this generation's promotions rests on the `Gate` block being unchanged since it ran — an assumption the harness cannot verify from the store.
- **Truncation-limited subjects EXCLUDED** (D117 clause 3, finding 338): `threshold:sma50`. Each retired during the generation, so it left the promotable set and stopped emitting rows; the sessions after that were never recorded and the "would not have retired" direction is not recomputable. Named rather than silently dropped.

## Artefacts

| Artefact | Stored | Recomputed | Differing |
|---|---:|---:|---:|
| `overfitting_status` | 95600 | 95600 | **0** |
| `go_live_log(Promoted)` | 75 | 91 | **57** |
| `go_live_log(WouldRevert)` | 31327 | 31327 | **0** |

### How the promotion set changed

- **Moved** (same edge, different date): 41
- **Gained** (found by the new rule, never by the old): 16
- **LOST** (found by the old rule, NOT by the new): 0

A LOST promotion is the one direction that argues AGAINST a rule change — an edge the arena used to find and would stop finding. There are none here, so the change is strictly additive on this artefact.


## Cohort separation — the finding-280 measurement

*D63 is asymmetric: `anti` SHOULD be caught, `noedge` should NOT — "S3 never flags a merely edgeless strategy" (OVERFITTING_MONITOR §3). Finding 280 measured both at 50/50 **live at session 639** (~2.5y), which is why this is reported at several horizons: the ever-Suspect predicate SATURATES, and over a full 20-year window every cohort reaches it. A single full-window number would discriminate nothing — finding 289's window-monotonicity lesson, applied to a different EVER predicate.*

### 1 year (252 sessions)

| cohort | n | ever-Suspect stored | ever-Suspect recomputed |
|---|---:|---|---|
| `anti` | 50 | 49/50 | **49/50** |
| `noedge` | 50 | 50/50 | **50/50** |
| `edge` | 250 | 93/250 | **93/250** |
| `naive` | 50 | 48/50 | **48/50** |

Separation (anti − noedge): **-0.02 → -0.02**  — *SATURATED: both judged cohorts are within one plant of the ceiling, so this horizon cannot discriminate and the sign of its separation is noise*

### 3 years (756 sessions)

| cohort | n | ever-Suspect stored | ever-Suspect recomputed |
|---|---:|---|---|
| `anti` | 50 | 50/50 | **50/50** |
| `noedge` | 50 | 50/50 | **50/50** |
| `edge` | 250 | 205/250 | **205/250** |
| `naive` | 50 | 50/50 | **50/50** |

Separation (anti − noedge): **0.00 → 0.00**  — *SATURATED: both judged cohorts are within one plant of the ceiling, so this horizon cannot discriminate and the sign of its separation is noise*

### full window

| cohort | n | ever-Suspect stored | ever-Suspect recomputed |
|---|---:|---|---|
| `anti` | 50 | 50/50 | **50/50** |
| `noedge` | 50 | 50/50 | **50/50** |
| `edge` | 250 | 248/250 | **248/250** |
| `naive` | 50 | 50/50 | **50/50** |

Separation (anti − noedge): **0.00 → 0.00**  — *SATURATED: both judged cohorts are within one plant of the ceiling, so this horizon cannot discriminate and the sign of its separation is noise*

**Verdict (read from the shortest non-saturated horizon):**

**Not readable — every horizon is saturated.** The instrument cannot judge this change, and that is a statement about the MEASUREMENT, not evidence that the change did nothing. A shorter horizon or a per-evaluation flag rate is needed before any finding-280 candidate can be scored.

## C-1 detection power — recomputed vs frozen (horizon 3y = 756 sessions, power 80 %)

*The monthly edge ladder IS the C-1 sweep (Change 4 / D101 — daily cannot promote under its cost drag). Same denominator, same session-index grid and same selection rule as the frozen curve, or the two would not be comparable.*

| rung | seeds | promoted (stored → recomputed) | P(promoted by H) stored → recomputed | median sessions stored → recomputed |
|---:|---:|---|---|---|
| 2 %/yr | 50 | 1 → **5** | 0.02 → **0.10** | 105 → **84** |
| 4 %/yr | 50 | 5 → **12** | 0.10 → **0.20** | 105 → **84** |
| 8 %/yr | 50 | 26 → **30** | 0.30 → **0.38** | 315 → **273** |
| 16 %/yr | 50 | 43 → **44** | 0.42 → **0.52** | 861 → **252** |

- **α\*(H) implied by the FROZEN promotions:** **unreachable (+∞)** — no rung reaches the power at this horizon
- **α\*(H) implied by the RECOMPUTED promotions:** **unreachable (+∞)** — no rung reaches the power at this horizon

**The gate stays CLOSED** (finding 336). Detection may have improved, but no rung reaches the power within the horizon under these rules either, so the floor is still unreachable and no candidate is admissible. Reopening it needs a larger effect, a longer horizon under its own decision, or a different change — never a lowered bar.

### Differences — `go_live_log(Promoted)`

- plant:edge:monthly:16:0: MOVED EARLIER — stored @2006-11-30 → recomputed @2006-10-02
- plant:edge:monthly:16:1: MOVED EARLIER — stored @2023-10-10 → recomputed @2021-03-10
- plant:edge:monthly:16:11: MOVED EARLIER — stored @2016-03-08 → recomputed @2014-12-04
- plant:edge:monthly:16:12: MOVED EARLIER — stored @2018-03-08 → recomputed @2017-12-05
- plant:edge:monthly:16:13: MOVED EARLIER — stored @2006-10-31 → recomputed @2006-10-02
- plant:edge:monthly:16:15: MOVED EARLIER — stored @2009-06-04 → recomputed @2007-03-06
- plant:edge:monthly:16:17: GAINED — recomputed promotion @2022-09-08, none stored
- plant:edge:monthly:16:2: MOVED EARLIER — stored @2016-09-06 → recomputed @2015-01-06
- plant:edge:monthly:16:20: MOVED LATER — stored @2006-07-03 → recomputed @2006-08-02
- plant:edge:monthly:16:26: MOVED EARLIER — stored @2010-09-02 → recomputed @2010-06-04
- plant:edge:monthly:16:27: MOVED EARLIER — stored @2010-11-02 → recomputed @2006-05-03
- plant:edge:monthly:16:28: MOVED EARLIER — stored @2009-10-02 → recomputed @2009-09-02
- plant:edge:monthly:16:29: MOVED EARLIER — stored @2017-07-07 → recomputed @2016-08-05
- plant:edge:monthly:16:3: MOVED EARLIER — stored @2006-07-03 → recomputed @2006-06-02
- plant:edge:monthly:16:30: MOVED EARLIER — stored @2022-02-07 → recomputed @2021-05-10
- plant:edge:monthly:16:31: MOVED EARLIER — stored @2021-03-10 → recomputed @2019-08-08
- plant:edge:monthly:16:32: MOVED EARLIER — stored @2006-10-31 → recomputed @2006-10-02
- plant:edge:monthly:16:33: MOVED EARLIER — stored @2009-07-06 → recomputed @2007-04-04
- plant:edge:monthly:16:35: MOVED EARLIER — stored @2007-03-06 → recomputed @2007-01-03
- plant:edge:monthly:16:39: MOVED EARLIER — stored @2007-01-03 → recomputed @2006-11-30
- plant:edge:monthly:16:41: MOVED EARLIER — stored @2012-08-31 → recomputed @2012-08-02
- plant:edge:monthly:16:47: MOVED EARLIER — stored @2013-10-04 → recomputed @2006-08-31
- plant:edge:monthly:16:5: MOVED EARLIER — stored @2019-06-10 → recomputed @2018-01-05
- plant:edge:monthly:16:8: MOVED EARLIER — stored @2011-06-03 → recomputed @2006-05-03
- plant:edge:monthly:16:9: MOVED EARLIER — stored @2016-06-07 → recomputed @2015-03-09
- plant:edge:monthly:2:19: GAINED — recomputed promotion @2006-05-03, none stored
- plant:edge:monthly:2:24: GAINED — recomputed promotion @2006-07-03, none stored
- plant:edge:monthly:2:28: GAINED — recomputed promotion @2006-05-03, none stored
- plant:edge:monthly:2:36: MOVED EARLIER — stored @2006-06-02 → recomputed @2006-05-03
- plant:edge:monthly:2:39: GAINED — recomputed promotion @2006-05-03, none stored
- plant:edge:monthly:4:12: GAINED — recomputed promotion @2006-05-03, none stored
- plant:edge:monthly:4:14: GAINED — recomputed promotion @2021-05-10, none stored
- plant:edge:monthly:4:17: MOVED EARLIER — stored @2006-06-02 → recomputed @2006-05-03
- plant:edge:monthly:4:21: GAINED — recomputed promotion @2007-06-05, none stored
- plant:edge:monthly:4:28: GAINED — recomputed promotion @2006-05-03, none stored
- plant:edge:monthly:4:35: GAINED — recomputed promotion @2006-05-03, none stored
- plant:edge:monthly:4:45: MOVED EARLIER — stored @2006-07-03 → recomputed @2006-06-02
- plant:edge:monthly:4:46: GAINED — recomputed promotion @2017-05-08, none stored
- plant:edge:monthly:4:8: GAINED — recomputed promotion @2006-05-03, none stored
- plant:edge:monthly:4:9: MOVED EARLIER — stored @2006-07-03 → recomputed @2006-05-03
- … (further differences elided; the COUNT is authoritative)

## Verdict

This run scored a **rule change**, so a difference is the product, not a fault. Before any recomputed number is treated as sign-off evidence, D117 clause 2 requires BOTH: `FX-RecomputeParity` holding under the current rules, AND a **confirmation slice** — a narrow `replay-calibrate --from/--to` under these corrected rules, agreeing with the harness over that same window. Parity exercises the UNCHANGED path; only the confirmation slice exercises this one, which is why one does not substitute for the other.

