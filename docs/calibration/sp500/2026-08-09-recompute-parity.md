# Recompute report — arena `sp500` — 2026-08-09

*D106 recompute harness (MASTER §25), settlements D117. Run kind `replay`. **Report-only — no rows were written** (D117 clause 1).*

## Specification

- **Spec:** `parity (no overrides — generation 1's rules)`
- **Tier:** `DirectRead` — the inputs this change requires (§25.2 as amended by D117)
- **Subjects recomputed:** 401

## Known limits carried with this run

- **`GateOptions` is not as-of resolvable** (§25.1). `MinTrackDays` and the MDE parameters are bound from appsettings at composition, not versioned config rows, so reproducing this generation's promotions rests on the `Gate` block being unchanged since it ran — an assumption the harness cannot verify from the store.
- **Truncation-limited subjects EXCLUDED** (D117 clause 3, finding 338): `threshold:sma50`. Each retired during the generation, so it left the promotable set and stopped emitting rows; the sessions after that were never recorded and the "would not have retired" direction is not recomputable. Named rather than silently dropped.

## Artefacts

| Artefact | Stored | Recomputed | Differing |
|---|---:|---:|---:|
| `overfitting_status` | 95600 | 95600 | **0** |
| `go_live_log(Promoted)` | 144 | 151 | **94** |
| `go_live_log(WouldRevert)` | 18533 | 18533 | **0** |

### How the promotion set changed

- **Moved** (same edge, different date): 83
- **Gained** (found by the new rule, never by the old): 9
- **LOST** (found by the old rule, NOT by the new): 2

**Every LOST subject is listed below in full, never sampled** — it is the direction that argues against the change, so it is the last thing an example cap may elide:

- plant:edge:monthly:2:28 (stored @2006-05-03)
- plant:edge:monthly:2:36 (stored @2006-05-03)

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
| 2 %/yr | 50 | 9 → **11** | 0.14 → **0.16** | 1386 → **2289** |
| 4 %/yr | 50 | 35 → **40** | 0.52 → **0.48** | 2058 → **2289** |
| 8 %/yr | 50 | 50 → **50** | 0.90 → **0.98** | 1197 → **1260** |
| 16 %/yr | 50 | 50 → **50** | 0.98 → **1.00** | 819 → **903** |

- **α\*(H) implied by the FROZEN promotions:** 6.95 %/yr
- **α\*(H) implied by the RECOMPUTED promotions:** 6.56 %/yr

Both sides imply a reachable floor; this change moves its level rather than its existence.

### What each candidate PATIENCE HORIZON would buy

*`Gate.DetectabilityHorizonYears` is what puts the floor out of reach (finding 336), and it is an appsettings value rather than a spec parameter — so without this table the only way to ask "what would 5 years buy?" is to EDIT the threshold and re-run, which is the shape of change rule 8 exists to make deliberate. Reading it off a table keeps the question separate from the act. Current setting: **10 years**.*

| horizon | P(promoted) per rung, recomputed | α\*(H) frozen | α\*(H) recomputed |
|---|---|---|---|
| **1y** (252) | 2%: 0.00 · 4%: 0.04 · 8%: 0.26 · 16%: 0.30 | **unreachable (+∞)** — no rung reaches the power at this horizon | ****unreachable (+∞)** — no rung reaches the power at this horizon** |
| **2y** (504) | 2%: 0.00 · 4%: 0.04 · 8%: 0.28 · 16%: 0.36 | **unreachable (+∞)** — no rung reaches the power at this horizon | ****unreachable (+∞)** — no rung reaches the power at this horizon** |
| **3y** (756) | 2%: 0.00 · 4%: 0.04 · 8%: 0.32 · 16%: 0.40 | **unreachable (+∞)** — no rung reaches the power at this horizon | ****unreachable (+∞)** — no rung reaches the power at this horizon** |
| **5y** (1260) | 2%: 0.02 · 4%: 0.08 · 8%: 0.52 · 16%: 0.82 | 14.77 %/yr | **15.47 %/yr** |
| **10y** (2520) | 2%: 0.16 · 4%: 0.48 · 8%: 0.98 · 16%: 1.00 | 6.95 %/yr | **6.56 %/yr** |
| **15y** (3780) | 2%: 0.16 · 4%: 0.74 · 8%: 0.98 · 16%: 1.00 | 6.00 %/yr | **5.00 %/yr** |
| **20y** (5040) | 2%: 0.22 · 4%: 0.80 · 8%: 1.00 · 16%: 1.00 | 5.33 %/yr | **4.00 %/yr** |

**The shortest horizon at which the floor becomes reachable is 5 years** (α\* 15.47 %/yr). That is the number the patience decision is about: it is what the arena would have to be willing to wait before it could adjudicate ANY pre-registered claim. Choosing it is a decision about patience and takes its own D-number — never a config edit to make an unwelcome answer go away (rule 8, D110 R3).

### Differences — `go_live_log(Promoted)`

- plant:edge:monthly:16:10: MOVED LATER — stored @2006-06-02 → recomputed @2009-08-04
- plant:edge:monthly:16:15: MOVED LATER — stored @2007-04-04 → recomputed @2009-05-05
- plant:edge:monthly:16:16: MOVED LATER — stored @2007-06-05 → recomputed @2010-12-02
- plant:edge:monthly:16:17: MOVED EARLIER — stored @2012-07-03 → recomputed @2012-03-05
- plant:edge:monthly:16:19: MOVED EARLIER — stored @2016-05-06 → recomputed @2016-01-06
- plant:edge:monthly:16:21: MOVED LATER — stored @2010-01-04 → recomputed @2010-02-03
- plant:edge:monthly:16:24: MOVED LATER — stored @2010-03-05 → recomputed @2010-06-04
- plant:edge:monthly:16:25: MOVED LATER — stored @2011-11-01 → recomputed @2012-07-03
- plant:edge:monthly:16:27: MOVED LATER — stored @2006-05-03 → recomputed @2010-11-02
- plant:edge:monthly:16:28: MOVED LATER — stored @2009-11-02 → recomputed @2010-01-04
- plant:edge:monthly:16:30: MOVED LATER — stored @2007-07-05 → recomputed @2007-12-03
- plant:edge:monthly:16:32: MOVED LATER — stored @2006-10-02 → recomputed @2006-10-31
- plant:edge:monthly:16:33: MOVED LATER — stored @2009-04-03 → recomputed @2009-05-05
- plant:edge:monthly:16:35: MOVED LATER — stored @2007-04-04 → recomputed @2010-03-05
- plant:edge:monthly:16:38: MOVED LATER — stored @2006-05-03 → recomputed @2006-06-02
- plant:edge:monthly:16:39: MOVED LATER — stored @2006-05-03 → recomputed @2007-02-02
- plant:edge:monthly:16:4: MOVED LATER — stored @2008-03-05 → recomputed @2008-05-05
- plant:edge:monthly:16:40: MOVED LATER — stored @2006-11-30 → recomputed @2007-02-02
- plant:edge:monthly:16:41: MOVED LATER — stored @2010-05-05 → recomputed @2010-06-04
- plant:edge:monthly:16:43: MOVED LATER — stored @2009-05-05 → recomputed @2009-09-02
- plant:edge:monthly:16:8: MOVED LATER — stored @2010-12-02 → recomputed @2011-04-04
- plant:edge:monthly:2:1: MOVED EARLIER — stored @2022-11-07 → recomputed @2021-05-10
- plant:edge:monthly:2:11: GAINED — recomputed promotion @2015-02-05, none stored
- plant:edge:monthly:2:18: GAINED — recomputed promotion @2015-09-04, none stored
- plant:edge:monthly:2:27: MOVED LATER — stored @2010-08-04 → recomputed @2010-09-02
- plant:edge:monthly:2:39: GAINED — recomputed promotion @2022-11-07, none stored
- plant:edge:monthly:2:4: GAINED — recomputed promotion @2015-04-08, none stored
- plant:edge:monthly:2:46: MOVED EARLIER — stored @2022-12-07 → recomputed @2022-05-09
- plant:edge:monthly:2:7: MOVED LATER — stored @2011-07-05 → recomputed @2013-01-04
- plant:edge:monthly:4:0: MOVED EARLIER — stored @2015-04-08 → recomputed @2015-01-06
- plant:edge:monthly:4:10: MOVED EARLIER — stored @2017-06-07 → recomputed @2016-10-05
- plant:edge:monthly:4:11: MOVED EARLIER — stored @2017-01-05 → recomputed @2014-02-05
- plant:edge:monthly:4:12: MOVED LATER — stored @2006-05-03 → recomputed @2016-12-05
- plant:edge:monthly:4:13: MOVED EARLIER — stored @2015-01-06 → recomputed @2014-11-04
- plant:edge:monthly:4:16: MOVED EARLIER — stored @2022-12-07 → recomputed @2021-03-10
- plant:edge:monthly:4:17: MOVED LATER — stored @2006-06-02 → recomputed @2015-03-09
- plant:edge:monthly:4:19: MOVED LATER — stored @2010-12-02 → recomputed @2016-03-08
- plant:edge:monthly:4:20: MOVED LATER — stored @2013-04-08 → recomputed @2013-05-07
- plant:edge:monthly:4:21: MOVED LATER — stored @2011-01-03 → recomputed @2011-03-04
- plant:edge:monthly:4:23: MOVED EARLIER — stored @2021-03-10 → recomputed @2018-06-07
- … (further differences elided; the COUNT is authoritative)
- plant:edge:monthly:2:28: LOST — stored promotion @2006-05-03, none recomputed
- plant:edge:monthly:2:36: LOST — stored promotion @2006-05-03, none recomputed

## Verdict

**`FX-RecomputeParity` FAILED.** Per §25.3 the harness is **NOT USED for its purpose and generation 2 stands**. The equality is never relaxed to a tolerance: this routes to investigating which input is impure. It is a finding about the store, not a fixture to soften.

