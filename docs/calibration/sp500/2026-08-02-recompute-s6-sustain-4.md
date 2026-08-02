# Recompute report — arena `sp500` — 2026-08-02

*D106 recompute harness (MASTER §25), settlements D117. Run kind `replay`. **Report-only — no rows were written** (D117 clause 1).*

## Specification

- **Spec:** `s6-sustain-4: monitor.s6.sustain_evals=4`
- **Tier:** `DirectRead` — the inputs this change requires (§25.2 as amended by D117)
- **Subjects recomputed:** 401

## Known limits carried with this run

- **`GateOptions` is not as-of resolvable** (§25.1). `MinTrackDays` and the MDE parameters are bound from appsettings at composition, not versioned config rows, so reproducing this generation's promotions rests on the `Gate` block being unchanged since it ran — an assumption the harness cannot verify from the store.
- **Truncation-limited subjects EXCLUDED** (D117 clause 3, finding 338): `threshold:sma50`. Each retired during the generation, so it left the promotable set and stopped emitting rows; the sessions after that were never recorded and the "would not have retired" direction is not recomputable. Named rather than silently dropped.

## Artefacts

| Artefact | Stored | Recomputed | Differing |
|---|---:|---:|---:|
| `overfitting_status` | 95600 | 95600 | **2946** |
| `go_live_log(Promoted)` | 75 | 75 | **0** |
| `go_live_log(WouldRevert)` | 31327 | 29987 | **1340** |

## Cohort separation — the finding-280 measurement

*D63 is asymmetric: `anti` SHOULD be caught, `noedge` should NOT — "S3 never flags a merely edgeless strategy" (OVERFITTING_MONITOR §3). Finding 280 measured both at 50/50 **live at session 639** (~2.5y), which is why this is reported at several horizons: the ever-Suspect predicate SATURATES, and over a full 20-year window every cohort reaches it. A single full-window number would discriminate nothing — finding 289's window-monotonicity lesson, applied to a different EVER predicate.*

### 1 year (252 sessions)

| cohort | n | ever-Suspect stored | ever-Suspect recomputed |
|---|---:|---|---|
| `anti` | 50 | 49/50 | **49/50** |
| `noedge` | 50 | 50/50 | **49/50** |
| `edge` | 250 | 93/250 | **72/250** |
| `naive` | 50 | 48/50 | **46/50** |

Separation (anti − noedge): **-0.02 → 0.00**  — *SATURATED: both judged cohorts are within one plant of the ceiling, so this horizon cannot discriminate and the sign of its separation is noise*

### 3 years (756 sessions)

| cohort | n | ever-Suspect stored | ever-Suspect recomputed |
|---|---:|---|---|
| `anti` | 50 | 50/50 | **50/50** |
| `noedge` | 50 | 50/50 | **50/50** |
| `edge` | 250 | 205/250 | **166/250** |
| `naive` | 50 | 50/50 | **50/50** |

Separation (anti − noedge): **0.00 → 0.00**  — *SATURATED: both judged cohorts are within one plant of the ceiling, so this horizon cannot discriminate and the sign of its separation is noise*

### full window

| cohort | n | ever-Suspect stored | ever-Suspect recomputed |
|---|---:|---|---|
| `anti` | 50 | 50/50 | **50/50** |
| `noedge` | 50 | 50/50 | **50/50** |
| `edge` | 250 | 248/250 | **219/250** |
| `naive` | 50 | 50/50 | **50/50** |

Separation (anti − noedge): **0.00 → 0.00**  — *SATURATED: both judged cohorts are within one plant of the ceiling, so this horizon cannot discriminate and the sign of its separation is noise*

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

- plant:anti:daily:-2:0@2006-07-03: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:0@2007-07-05: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:1@2006-10-02: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2006-07-03: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2007-06-05: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2008-07-03: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2009-08-04: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2012-03-05: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2013-07-08: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2014-10-06: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2015-05-07: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2016-01-06: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2016-07-07: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2018-03-08: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2018-10-05: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2019-06-10: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2020-09-08: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2022-01-06: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2023-05-10: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2023-11-08: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2024-08-12: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:10@2025-03-14: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:11@2006-07-03: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:11@2008-08-04: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:11@2009-09-02: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:11@2010-11-02: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:11@2013-07-08: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:11@2014-10-06: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:11@2016-09-06: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:11@2017-04-06: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:11@2018-08-07: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:11@2019-02-07: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:11@2019-07-10: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:11@2020-09-08: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:11@2021-08-09: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:11@2022-09-08: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:11@2023-05-10: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:11@2023-11-08: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:11@2025-03-14: stored 'suspect' → recomputed 'warning'
- plant:anti:daily:-2:12@2006-07-03: stored 'suspect' → recomputed 'warning'
- … (further differences elided; the COUNT is authoritative)

### Differences — `go_live_log(WouldRevert)`

- plant:anti:daily:-2:0@2006-10-02: stored would-revert, none recomputed
- plant:anti:daily:-2:0@2007-10-03: stored would-revert, none recomputed
- plant:anti:daily:-2:1@2007-01-03: stored would-revert, none recomputed
- plant:anti:daily:-2:10@2006-10-02: stored would-revert, none recomputed
- plant:anti:daily:-2:10@2007-09-04: stored would-revert, none recomputed
- plant:anti:daily:-2:10@2008-10-02: stored would-revert, none recomputed
- plant:anti:daily:-2:10@2009-11-02: stored would-revert, none recomputed
- plant:anti:daily:-2:10@2012-06-04: stored would-revert, none recomputed
- plant:anti:daily:-2:10@2013-10-04: stored would-revert, none recomputed
- plant:anti:daily:-2:10@2015-01-06: stored would-revert, none recomputed
- plant:anti:daily:-2:10@2015-08-06: stored would-revert, none recomputed
- plant:anti:daily:-2:10@2016-10-05: stored would-revert, none recomputed
- plant:anti:daily:-2:10@2019-09-09: stored would-revert, none recomputed
- plant:anti:daily:-2:11@2006-10-02: stored would-revert, none recomputed
- plant:anti:daily:-2:11@2009-12-02: stored would-revert, none recomputed
- plant:anti:daily:-2:11@2011-02-02: stored would-revert, none recomputed
- plant:anti:daily:-2:11@2013-10-04: stored would-revert, none recomputed
- plant:anti:daily:-2:11@2015-01-06: stored would-revert, none recomputed
- plant:anti:daily:-2:11@2017-07-07: stored would-revert, none recomputed
- plant:anti:daily:-2:11@2019-10-08: stored would-revert, none recomputed
- plant:anti:daily:-2:11@2025-06-13: stored would-revert, none recomputed
- plant:anti:daily:-2:12@2006-10-02: stored would-revert, none recomputed
- plant:anti:daily:-2:12@2007-12-03: stored would-revert, none recomputed
- plant:anti:daily:-2:12@2008-10-02: stored would-revert, none recomputed
- plant:anti:daily:-2:12@2010-06-04: stored would-revert, none recomputed
- plant:anti:daily:-2:12@2013-09-05: stored would-revert, none recomputed
- plant:anti:daily:-2:12@2015-01-06: stored would-revert, none recomputed
- plant:anti:daily:-2:12@2017-08-07: stored would-revert, none recomputed
- plant:anti:daily:-2:12@2018-10-05: stored would-revert, none recomputed
- plant:anti:daily:-2:12@2019-10-08: stored would-revert, none recomputed
- plant:anti:daily:-2:12@2023-08-10: stored would-revert, none recomputed
- plant:anti:daily:-2:13@2009-12-02: stored would-revert, none recomputed
- plant:anti:daily:-2:13@2013-09-05: stored would-revert, none recomputed
- plant:anti:daily:-2:13@2015-10-06: stored would-revert, none recomputed
- plant:anti:daily:-2:13@2017-12-05: stored would-revert, none recomputed
- plant:anti:daily:-2:13@2019-01-08: stored would-revert, none recomputed
- plant:anti:daily:-2:13@2019-10-08: stored would-revert, none recomputed
- plant:anti:daily:-2:13@2021-12-07: stored would-revert, none recomputed
- plant:anti:daily:-2:13@2023-07-12: stored would-revert, none recomputed
- plant:anti:daily:-2:13@2025-08-14: stored would-revert, none recomputed

## Verdict

This run scored a **rule change**, so a difference is the product, not a fault. Before any recomputed number is treated as sign-off evidence, D117 clause 2 requires BOTH: `FX-RecomputeParity` holding under the current rules, AND a **confirmation slice** — a narrow `replay-calibrate --from/--to` under these corrected rules, agreeing with the harness over that same window. Parity exercises the UNCHANGED path; only the confirmation slice exercises this one, which is why one does not substitute for the other.

