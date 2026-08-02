# Recompute report — arena `sp500` — 2026-08-02

*D106 recompute harness (MASTER §25), settlements D117. Run kind `replay`. **Report-only — no rows were written** (D117 clause 1).*

## Specification

- **Spec:** `band-noop-parity: monitor.s6.band_high_pct=75, monitor.s6.band_low_pct=25`
- **Tier:** `DerivedBand` — the inputs this change requires (§25.2 as amended by D117)
- **Subjects recomputed:** 401

## Known limits carried with this run

- **`GateOptions` is not as-of resolvable** (§25.1). `MinTrackDays` and the MDE parameters are bound from appsettings at composition, not versioned config rows, so reproducing this generation's promotions rests on the `Gate` block being unchanged since it ran — an assumption the harness cannot verify from the store.
- **Truncation-limited subjects EXCLUDED** (D117 clause 3, finding 338): `threshold:sma50`. Each retired during the generation, so it left the promotable set and stopped emitting rows; the sessions after that were never recorded and the "would not have retired" direction is not recomputable. Named rather than silently dropped.

## Artefacts

| Artefact | Stored | Recomputed | Differing |
|---|---:|---:|---:|
| `overfitting_status` | 95600 | 95600 | **0** |
| `go_live_log(Promoted)` | 75 | 75 | **0** |
| `go_live_log(WouldRevert)` | 31327 | 31327 | **0** |

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
| 2 %/yr | 50 | 1 → **1** | 0.02 → **0.02** | 105 → **105** |
| 4 %/yr | 50 | 5 → **5** | 0.10 → **0.10** | 105 → **105** |
| 8 %/yr | 50 | 26 → **26** | 0.30 → **0.30** | 315 → **315** |
| 16 %/yr | 50 | 43 → **43** | 0.42 → **0.42** | 861 → **861** |

- **α\*(H) implied by the FROZEN promotions:** **unreachable (+∞)** — no rung reaches the power at this horizon
- **α\*(H) implied by the RECOMPUTED promotions:** **unreachable (+∞)** — no rung reaches the power at this horizon

**The gate stays CLOSED** (finding 336). Detection may have improved, but no rung reaches the power within the horizon under these rules either, so the floor is still unreachable and no candidate is admissible. Reopening it needs a larger effect, a longer horizon under its own decision, or a different change — never a lowered bar.

## Verdict

This run scored a **rule change**, so a difference is the product, not a fault. Before any recomputed number is treated as sign-off evidence, D117 clause 2 requires BOTH: `FX-RecomputeParity` holding under the current rules, AND a **confirmation slice** — a narrow `replay-calibrate --from/--to` under these corrected rules, agreeing with the harness over that same window. Parity exercises the UNCHANGED path; only the confirmation slice exercises this one, which is why one does not substitute for the other.

