# The confirmation slice — what would make it FAIL

**PRE-REGISTERED 2026-08-09, BEFORE the copy was taken and before the run.** Written first
deliberately: a green result is evidence only if a red one was reachable, and criteria written
after a result are criteria fitted to it. This is the same rule as *"a suite observed to pass is
not a suite shown able to fail"* — the discipline that produced the hand-edit probe on D141's
carve-out guard.

- Occasion: D117 clause 2, gating the S6 band-conformance remedy (D138) in PR #61
- Window: `--from 2006-01-03 --to 2007-12-31`, a **prefix** of generation 2, `--reset --report-only`
- Watermark: **pinned** to `2026-07-24T22:00:00Z` (D141 pre-flight part 1)
- Arena: a throwaway byte copy, destroyed afterwards; its id appears in no committed artefact

---

## 0. Why a prefix, since it constrains everything below

`--reset` deletes the whole replay generation, so re-simulation starts **cold** — no accumulated
track, no streaks. An interior window (say 2019–2020) would begin from zero while the harness
recomputes generation 2's rows for those dates carrying thirteen years of state, and the two would
disagree for reasons having nothing to do with the remedy. `ReplayRunner` corroborates the reading:
`SeedPlants(db, request.From)` seeds the plants at the window start, so the run is built to begin a
generation rather than resume one. The slice is therefore a truncated **prefix**, which shares
generation 2's own starting conditions.

## 1. The acceptance criterion

`FX-RecomputeParity` over the re-simulated window: **`Differing = 0` on all three artefacts** —
`overfitting_status`, `go_live_log(Promoted)`, `go_live_log(WouldRevert)`. Exact equality, never a
tolerance (§25.3). A failure means the harness is NOT USED and generation 2 stands; it routes to
finding which input is impure, and is a finding about the store rather than a fixture to soften.

**What green would mean:** the harness reproduces a generation that was PRODUCED under the corrected
S6 rule. Every parity run to date has scored a generation produced under the OLD rule, where the
changed branch never executed — which is precisely why D117 clause 2 does not accept parity alone.

**What green would NOT mean:** that the remedy is correct. The slice validates the HARNESS on the
changed path. Whether the corrected rule is the right rule is D138's argument, not this run's.

---

## 2. FAILURE MODE A — the harness reconstructs track/streak state differently from the live run

**The mechanism, and it is a real asymmetry rather than a hypothetical.** The live monitor resolves
streaks by QUERYING prior stored rows (`TrailingStreak`, `TrailingSuspectCount`). `MonitorRecompute`
does not read them at all — it walks each strategy's sessions in ascending order carrying its own
`belowAnchor` / `insideBand` / `negativeT` / `suspectRun` counters, on the stated reasoning that
under a changed rule the priors are themselves different, so reading them would mix generation 1's
history into generation 2's answer. **Two independent implementations of one quantity.**

**Observable failure:** `overfitting_status` differing > 0, with the differences concentrated at
sessions immediately FOLLOWING a streak transition (a reset to 0, or a crossing of the sustain bar
at 3, or `suspectRun` crossing the auto-retire patience at 4).

**Diagnostic if it fires:** the example rows carry as-of dates; check whether the differing session
is the first after a token change. A scatter of isolated differences implicates inputs (mode B); a
run of differences starting at one date and persisting implicates streak state.

## 3. FAILURE MODE B — the harness reads different inputs from the ones the live rule used

**The mechanism.** The harness re-derives verdicts from stored columns — `overfitting_checks.value`,
`contribution`, `threshold_json` — while the live run computed from equity curves, populations and
control equity. Anything the rule needs that was never persisted is unavailable, and the harness's
options are to recover it from a token or to refuse. Finding 340 is this failure already realised
once: a row that took the negative branch never evaluated band membership, so it records nothing
about which side it was on.

**Observable failure:** S6 contributions differing on rows whose `threshold_json` lacks a field the
rule reads, or a `RecomputeRefusedException` naming an unclassifiable parameter, or differing rows
clustering on subjects with sparse stored history.

**Note this run's specific exposure:** the slice re-simulates, so its rows are written by the
CURRENT schema and rule. If a field the corrected rule needs is still not persisted, parity fails
here even though the fixtures pass — because the fixtures supply the field directly.

## 4. FAILURE MODE C — the remedy behaves differently against REAL computed bands than against the fixtures' synthetic sweep

**This is the slice's actual value, and the reason a green fixture suite did not settle the question.**
The unit fixtures pass `BandPosition.Below / Inside / Above` in as an argument — they assert the
precondition rather than computing it. The slice derives band position from real `control_equity`
member window alphas via `BandInputs.MemberBand`, so it is the first test of whether the band, AS
ACTUALLY COMPUTED, puts subjects where D138's reasoning assumes.

Three distinct ways this fails, and they are not the same failure:

**C1 — the band does not sit where the argument claims.** D138 reasons that a no-edge plant is
inside its own cost-matched band *by construction*, since the band is percentiles of the same
cost-on family, so the plant sits near its median. If real bands instead place no-edge plants
**below**, the anti-predictive arm fires on them and the defect D138 exists to fix reappears under
the corrected rule.
*Observable:* `critical_neg_alpha` / `elevated_neg_alpha` tokens on `plant:noedge:*` subjects.

**C2 — the band is unavailable so often that the remedy never engages.** `MemberBand` returns null
when a session has no usable member band, and the rule then emits `insufficient_track`. A window in
which that dominates produces a **vacuous pass**: parity holds because the changed branch never ran.
*Observable:* the coverage floor in §5 not met.

**C3 — the ordering change does not bite.** The remedy's whole effect is that a subject INSIDE the
band with `t < −1.0` is now `inband` / `elevated_inband` instead of `elevated_neg_alpha` /
`critical_neg_alpha`. If no rows meet `band = Inside AND t < −1.0`, nothing was rescued and the
window cannot distinguish the corrected rule from the old one.
*Observable:* the rescued-row count in §5 being zero.

---

## 5. THE COVERAGE FLOOR — the condition that makes a green result informative

Measured on the scratch copy AFTER the slice and BEFORE it is destroyed, over the re-simulated
window's `overfitting_checks` rows with `signal = 'S6'`:

| # | Quantity | Requirement |
|---|---|---|
| **V1** | rows where the band was consulted — `contribution IN ('inband','elevated_inband','critical_neg_alpha','elevated_neg_alpha','none')` | **> 0**, and a material fraction of S6 rows rather than a handful |
| **V2** | **rescued rows** — `contribution IN ('inband','elevated_inband') AND value < -1.0` | **> 0** |
| **V3** | rows emitting `insufficient_track` | must NOT dominate the window |

**V2 IS THE DECISIVE ONE.** Those are exactly the rows the remedy changed: inside the band, negative
t, and under the old rule they would have been `elevated_neg_alpha` or worse. If **V2 = 0** the run
is reported **INCONCLUSIVE, NOT GREEN**, regardless of what parity says — a parity pass over a
window where the changed branch never executed is the confirmation-slice-shaped artefact that
confirms nothing, which is the exact failure D139 was written to refuse.

Thresholds are deliberately stated as "> 0 and material" rather than as invented numbers: no
measurement exists to derive a floor from, and a fabricated one would be an authored number
masquerading as a derived one. The counts will be REPORTED, and if they are small the run is
reported as weak evidence rather than rounded up to a pass.

---

## 6. Failure modes already closed by code, listed so their absence is not read as luck

- **Zero-session run** — refused (D139), returning before the report is written, so nothing is left
  on disk to be mistaken for evidence.
- **Unpinned watermark** — refused (D141): `--reset --report-only` against a store holding a
  committed generation with no `--watermark` is rejected. A later watermark would let the monitor
  see D98 curve rows generation 2's replay could not, silently switching S3 from the flat anchors to
  the trajectory.
- **Divergent S6 patience** — refused (P25 tripwire): the recompute reads a compile-time constant
  while the live monitor resolves the calibrated row, so any stored version differing from the
  constant stops the run rather than reproducing `WouldRevert` under the wrong rule.
- **A truncated copy** — checked before reading: size, sha256 and `PRAGMA integrity_check`. A partial
  copy of a 3.7 GiB store produces a plausible report from partial data.

## 7. Stop conditions

Halt and report rather than continue if: production's size or mtime changes at any point; the copy
fails any completeness check; the run writes to any path under the production arena; the measured
session rate implies a runtime materially beyond a working session (report the rate first, decide
second); or parity fails. **A failed slice is a result, not an obstacle** — under §25.3 it means the
harness is not used and generation 2 stands, and the S6 remedy stays unscored rather than being
scored by a softened test.
