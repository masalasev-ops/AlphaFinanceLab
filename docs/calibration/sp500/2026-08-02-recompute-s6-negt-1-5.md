# Recompute report — arena `sp500` — 2026-08-02

*D106 recompute harness (MASTER §25), settlements D117. Run kind `replay`. **Report-only — no rows were written** (D117 clause 1).*

## Specification

- **Spec:** `s6-negt-1.5: monitor.s6.negative_alpha_t=-1.5`
- **Tier:** `DerivedBand` — the inputs this change requires (§25.2 as amended by D117)
- **Subjects recomputed:** 401

## Known limits carried with this run

- **`GateOptions` is not as-of resolvable** (§25.1). `MinTrackDays` and the MDE parameters are bound from appsettings at composition, not versioned config rows, so reproducing this generation's promotions rests on the `Gate` block being unchanged since it ran — an assumption the harness cannot verify from the store.
- **Truncation-limited subjects EXCLUDED** (D117 clause 3, finding 338): `threshold:sma50`. Each retired during the generation, so it left the promotable set and stopped emitting rows; the sessions after that were never recorded and the "would not have retired" direction is not recomputable. Named rather than silently dropped.

## Artefacts

| Artefact | Stored | Recomputed | Differing |
|---|---:|---:|---:|
| `overfitting_status` | 95600 | 95600 | **15379** |
| `go_live_log(Promoted)` | 75 | 75 | **0** |
| `go_live_log(WouldRevert)` | 31327 | 20089 | **11238** |

## Cohort separation — the finding-280 measurement

*D63 is asymmetric: `anti` SHOULD be caught, `noedge` should NOT — "S3 never flags a merely edgeless strategy" (OVERFITTING_MONITOR §3). Finding 280 measured both at 50/50 **live at session 639** (~2.5y), which is why this is reported at several horizons: the ever-Suspect predicate SATURATES, and over a full 20-year window every cohort reaches it. A single full-window number would discriminate nothing — finding 289's window-monotonicity lesson, applied to a different EVER predicate.*

### 1 year (252 sessions)

| cohort | n | ever-Suspect stored | ever-Suspect recomputed |
|---|---:|---|---|
| `anti` | 50 | 49/50 | **49/50** |
| `noedge` | 50 | 50/50 | **50/50** |
| `edge` | 250 | 93/250 | **72/250** |
| `naive` | 50 | 48/50 | **46/50** |

Separation (anti − noedge): **-0.02 → -0.02**  — *SATURATED: both judged cohorts are within one plant of the ceiling, so this horizon cannot discriminate and the sign of its separation is noise*

### 3 years (756 sessions)

| cohort | n | ever-Suspect stored | ever-Suspect recomputed |
|---|---:|---|---|
| `anti` | 50 | 50/50 | **50/50** |
| `noedge` | 50 | 50/50 | **50/50** |
| `edge` | 250 | 205/250 | **160/250** |
| `naive` | 50 | 50/50 | **50/50** |

Separation (anti − noedge): **0.00 → 0.00**  — *SATURATED: both judged cohorts are within one plant of the ceiling, so this horizon cannot discriminate and the sign of its separation is noise*

### full window

| cohort | n | ever-Suspect stored | ever-Suspect recomputed |
|---|---:|---|---|
| `anti` | 50 | 50/50 | **50/50** |
| `noedge` | 50 | 50/50 | **50/50** |
| `edge` | 250 | 248/250 | **222/250** |
| `naive` | 50 | 50/50 | **50/50** |

Separation (anti − noedge): **0.00 → 0.00**  — *SATURATED: both judged cohorts are within one plant of the ceiling, so this horizon cannot discriminate and the sign of its separation is noise*

### Detection SPEED — median sessions to first Suspect

*The ever-Suspect rates above saturate; this does not. `anti_detection_speed` is named for speed but is itself an EVER predicate ("<50 % of anti plants ever Suspect"), so this is the first thing in the corpus that measures what that name says.*

| cohort | n | median sessions stored → recomputed | never flagged stored → recomputed |
|---|---:|---|---|
| `anti` | 50 | 126 → **126** | 0 → **0** |
| `noedge` | 50 | 126 → **126** | 0 → **0** |
| `edge` | 250 | 420 → **441** | 2 → **28** |
| `naive` | 50 | 126 → **126** | 0 → **0** |

**Speed gap (anti median − noedge median): 0 → 0 sessions.** NEGATIVE is the D63 direction — anti caught sooner than merely edgeless.

Unchanged: this rule change does not alter how much sooner anti-predictive plants are caught than edgeless ones.

**Verdict (read from the shortest non-saturated horizon):**

**Not readable — every horizon is saturated.** The instrument cannot judge this change, and that is a statement about the MEASUREMENT, not evidence that the change did nothing. A shorter horizon or a per-evaluation flag rate is needed before any finding-280 candidate can be scored.

## C-1 detection power — recomputed vs frozen (horizon 3y = 756 sessions, power 80 %)

*The monthly edge ladder IS the C-1 sweep (Change 4 / D101 — daily cannot promote under its cost drag). Same denominator, same session-index grid and same selection rule as the frozen curve, or the two would not be comparable.*

| rung | seeds | promoted (stored → recomputed) | P(promoted by H) stored → recomputed | median sessions stored → recomputed |
|---:|---:|---|---|---|
| 2 %/yr | 50 | 1 → **1** | 0.02 → **0.02** | 105 → **105** |
| 4 %/yr | 50 | 5 → **5** | 0.10 → **0.10** | 105 → **105** |
| 8 %/yr | 50 | 26 → **26** | 0.30 → **0.30** | 315 → **315** |
| 16 %/yr | 50 | 43 → **43** | 0.42 → **0.42** | 861 → **861** |

- **α\*(H) implied by the FROZEN promotions:** **unreachable (+∞)** — no rung reaches the power at this horizon
- **α\*(H) implied by the RECOMPUTED promotions:** **unreachable (+∞)** — no rung reaches the power at this horizon

**The gate stays CLOSED** (finding 336). Detection may have improved, but no rung reaches the power within the horizon under these rules either, so the floor is still unreachable and no candidate is admissible. Reopening it needs a larger effect, a longer horizon under its own decision, or a different change — never a lowered bar.

### Differences — `overfitting_status`

- plant:anti:daily:-2:0@2006-05-03: stored 'warning' → recomputed 'healthy'
- plant:anti:daily:-2:0@2006-07-03: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:0@2007-09-04: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:0@2007-10-03: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:1@2006-06-02: stored 'warning' → recomputed 'healthy'
- plant:anti:daily:-2:1@2006-11-30: stored 'suspect' → recomputed 'healthy'
- plant:anti:daily:-2:1@2007-01-03: stored 'suspect' → recomputed 'healthy'
- plant:anti:daily:-2:1@2007-02-02: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:1@2007-03-06: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:1@2007-09-04: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2006-05-03: stored 'warning' → recomputed 'healthy'
- plant:anti:daily:-2:10@2006-07-03: stored 'suspect' → recomputed 'healthy'
- plant:anti:daily:-2:10@2006-08-02: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2006-08-31: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2009-06-04: stored 'warning' → recomputed 'healthy'
- plant:anti:daily:-2:10@2009-08-04: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2015-01-06: stored 'suspect' → recomputed 'healthy'
- plant:anti:daily:-2:10@2015-02-05: stored 'healthy' → recomputed 'warning'
- plant:anti:daily:-2:10@2015-05-07: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2015-06-08: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2015-11-04: stored 'warning' → recomputed 'healthy'
- plant:anti:daily:-2:10@2016-01-06: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2016-05-06: stored 'warning' → recomputed 'healthy'
- plant:anti:daily:-2:10@2016-07-07: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2016-08-05: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2016-10-05: stored 'suspect' → recomputed 'healthy'
- plant:anti:daily:-2:10@2016-11-03: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2016-12-05: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2018-01-05: stored 'warning' → recomputed 'healthy'
- plant:anti:daily:-2:10@2018-03-08: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2018-08-07: stored 'warning' → recomputed 'healthy'
- plant:anti:daily:-2:10@2018-10-05: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2019-06-10: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2020-05-08: stored 'suspect' → recomputed 'healthy'
- plant:anti:daily:-2:10@2020-06-09: stored 'healthy' → recomputed 'warning'
- plant:anti:daily:-2:10@2020-09-08: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2020-10-07: stored 'healthy' → recomputed 'warning'
- plant:anti:daily:-2:10@2022-01-06: stored 'suspect' → recomputed 'healthy'
- plant:anti:daily:-2:10@2023-05-10: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2023-07-12: stored 'suspect' → recomputed 'healthy'
- … (further differences elided; the COUNT is authoritative)

### Differences — `go_live_log(WouldRevert)`

- plant:anti:daily:-2:0@2006-10-02: stored would-revert, none recomputed
- plant:anti:daily:-2:0@2007-10-03: stored would-revert, none recomputed
- plant:anti:daily:-2:0@2007-11-01: stored would-revert, none recomputed
- plant:anti:daily:-2:0@2007-12-03: stored would-revert, none recomputed
- plant:anti:daily:-2:0@2008-01-03: stored would-revert, none recomputed
- plant:anti:daily:-2:1@2007-01-03: stored would-revert, none recomputed
- plant:anti:daily:-2:1@2007-02-02: stored would-revert, none recomputed
- plant:anti:daily:-2:1@2007-03-06: stored would-revert, none recomputed
- plant:anti:daily:-2:1@2007-04-04: stored would-revert, none recomputed
- plant:anti:daily:-2:1@2007-05-04: stored would-revert, none recomputed
- plant:anti:daily:-2:1@2007-06-05: stored would-revert, none recomputed
- plant:anti:daily:-2:1@2007-09-04: stored would-revert, none recomputed
- plant:anti:daily:-2:1@2007-10-03: stored would-revert, none recomputed
- plant:anti:daily:-2:1@2007-11-01: stored would-revert, none recomputed
- plant:anti:daily:-2:1@2007-12-03: stored would-revert, none recomputed
- plant:anti:daily:-2:10@2006-10-02: stored would-revert, none recomputed
- plant:anti:daily:-2:10@2006-10-31: stored would-revert, none recomputed
- plant:anti:daily:-2:10@2006-11-30: stored would-revert, none recomputed
- plant:anti:daily:-2:10@2009-11-02: stored would-revert, none recomputed
- plant:anti:daily:-2:10@2015-01-06: stored would-revert, none recomputed
- plant:anti:daily:-2:10@2015-08-06: stored would-revert, none recomputed
- plant:anti:daily:-2:10@2016-10-05: stored would-revert, none recomputed
- plant:anti:daily:-2:10@2016-11-03: stored would-revert, none recomputed
- plant:anti:daily:-2:10@2016-12-05: stored would-revert, none recomputed
- plant:anti:daily:-2:10@2017-01-05: stored would-revert, none recomputed
- plant:anti:daily:-2:10@2017-02-06: stored would-revert, none recomputed
- plant:anti:daily:-2:10@2017-03-08: stored would-revert, none recomputed
- plant:anti:daily:-2:10@2019-09-09: stored would-revert, none recomputed
- plant:anti:daily:-2:10@2020-05-08: stored would-revert, none recomputed
- plant:anti:daily:-2:11@2010-06-04: stored would-revert, none recomputed
- plant:anti:daily:-2:11@2012-11-02: stored would-revert, none recomputed
- plant:anti:daily:-2:11@2015-02-05: stored would-revert, none recomputed
- plant:anti:daily:-2:11@2015-03-09: stored would-revert, none recomputed
- plant:anti:daily:-2:11@2015-04-08: stored would-revert, none recomputed
- plant:anti:daily:-2:11@2015-05-07: stored would-revert, none recomputed
- plant:anti:daily:-2:11@2015-06-08: stored would-revert, none recomputed
- plant:anti:daily:-2:11@2015-07-08: stored would-revert, none recomputed
- plant:anti:daily:-2:11@2015-08-06: stored would-revert, none recomputed
- plant:anti:daily:-2:11@2015-09-04: stored would-revert, none recomputed
- plant:anti:daily:-2:11@2017-07-07: stored would-revert, none recomputed

## Verdict

This run scored a **rule change**, so a difference is the product, not a fault. Before any recomputed number is treated as sign-off evidence, D117 clause 2 requires BOTH: `FX-RecomputeParity` holding under the current rules, AND a **confirmation slice** — a narrow `replay-calibrate --from/--to` under these corrected rules, agreeing with the harness over that same window. Parity exercises the UNCHANGED path; only the confirmation slice exercises this one, which is why one does not substitute for the other.

