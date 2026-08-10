# The confirmation slice — RESULT

**D117 clause 2 is DISCHARGED.** The S6 band-conformance remedy (D138) is scored.

- Run: 2026-08-10, `replay-calibrate --from 2006-01-03 --to 2007-12-31 --reset --report-only`
- Watermark: **pinned** `2026-07-24T22:00:00Z` (D141 pre-flight part 1)
- Arena: a throwaway byte copy of `sp500`, destroyed at the end; its id appears nowhere here (D139)
- Falsifiers: pre-registered **before** the copy was taken, in
  `2026-08-09-confirmation-slice-falsifiers.md`, committed `add423a`

---

## 1. The result

| Check | Stored | Recomputed | Differing |
|---|---:|---:|---:|
| **Parity, `DirectRead` tier** ||||
| `overfitting_status` | 9,223 | 9,223 | **0** |
| `go_live_log(Promoted)` | 50 | 50 | **0** |
| `go_live_log(WouldRevert)` | 494 | 494 | **0** |
| **Parity, `DerivedBand` tier (band 25/75 — bands DERIVED from `control_equity`)** ||||
| `overfitting_status` | 9,223 | 9,223 | **0** |
| `go_live_log(Promoted)` | 50 | 50 | **0** |
| `go_live_log(WouldRevert)` | 494 | 494 | **0** |

502 sessions simulated by THIS invocation, **0 pre-existing** — so this is not the zero-session
artefact D139 refuses. Config rows frozen: **none** (report-only honoured). The arithmetic was
RESOLVED from the generation as `jensen` (D140), reproducing 50/50 stored promotions, rather than
supplied by the operator.

**What this establishes, precisely:** the harness reproduces a generation that was PRODUCED under the
corrected S6 rule. Every prior parity run scored a generation produced under the OLD rule, where the
changed branch never executed — which is the structural gap D117 clause 2 exists to close, and the
reason parity alone was never accepted as sufficient.

**What it does NOT establish:** that the corrected rule is the RIGHT rule. That is D138's argument.
This run validates the harness on the changed path.

---

## 2. Why the green is not vacuous — the coverage floor, measured

Pre-registered in §5 of the falsifiers document, measured on the re-simulated rows before the copy
was destroyed:

| | Quantity | Result |
|---|---|---|
| **V1** | rows where the band was consulted | **8,020 (87.0 %)** |
| **V2** | **RESCUED rows** — inside the band ∧ `t < −1.0` | **1,756** — decisive gate, met |
| **V3** | `insufficient_track` | 1,203 (13.0 %) — not dominant |

V2 was pre-declared as the gate that decides whether a green parity means anything: those are exactly
the rows the remedy changed. Had V2 been 0, this run would have been reported **INCONCLUSIVE**, not
green, regardless of parity.

## 3. The remedy's measured effect, same window, old rule vs corrected

Generation 2's stored S6 tokens for this window were captured from the byte copy BEFORE `--reset`
destroyed them, so this is a like-for-like comparison on identical sessions and subjects:

| S6 contribution | gen 2 (old rule) | slice (corrected) | Δ |
|---|---:|---:|---:|
| `critical_neg_alpha` | 3,113 | **286** | **−2,827 (−91 %)** |
| `elevated_neg_alpha` | 1,438 | **678** | −760 |
| `inband` | 139 | **900** | +761 |
| `elevated_inband` | 60 | **1,055** | +995 |
| `none` | 3,270 | 5,101 | +1,831 |
| `insufficient_track` | 1,203 | 1,203 | **0** |
| **total** | **9,223** | **9,223** | **0** |

The total is identical and `insufficient_track` is unchanged to the row — the untouched branch
reproduces exactly while the changed branch moves. That is the signature D138 predicted: strategies
sitting at the median of their own cost-matched band are no longer called anti-predictive on a t-stat
measured against zero.

## 4. THE FALSIFICATION PROBE — a red was reachable, and was produced

The `DerivedBand` green needed its own check, because `MonitorRecompute.RecomputeS6` returns the
STORED row unchanged whenever `StrategyWindow` or `MemberBand` yields null. A run in which the bands
mostly failed to build would therefore ALSO pass — the vacuity trap one level below the coverage floor.

Deliberate probe: re-run at **band 45/55** instead of 25/75. If derivation is load-bearing, a narrower
band must move the answer.

| Probe (band 45/55) | Stored | Recomputed | Differing |
|---|---:|---:|---:|
| `overfitting_status` | 9,223 | 9,223 | **979** |
| `go_live_log(WouldRevert)` | 494 | 605 | **111** |

**It went red.** So the bands are genuinely derived from `control_equity` and genuinely drive the
verdict, and the 25/75 agreement is the harness's independent derivation MATCHING the live run's
computed bands — not a fall-through. This is the check that converts "parity passed" into evidence.

## 5. Cohort separation under the corrected rule (this window)

| horizon | `anti` | `noedge` | separation |
|---|---|---|---|
| 1 year | 35/50 | 23/50 | **0.24** |
| 3 years / full | 45/50 | 36/50 | **0.18** |

Detection speed: `anti` median **147** sessions to first Suspect vs `noedge` **210** — a gap of
**−63 sessions**, negative being the D63 direction (anti caught sooner than merely edgeless).

**Stated as a limit rather than a claim:** generation 2's recompute reports recorded a separation of
0.00, but over a TWENTY-year window where the ever-Suspect predicate saturates. These figures are a
two-year window under the corrected rule. **The two are not comparable**, and no old-vs-new separation
claim is made here — the old rule's separation on THIS window was not measured before `--reset`
removed the rows. What is comparable is §3's token distribution, which was captured first.

## 6. The three pre-registered failure modes, resolved

- **A — streak/track state reconstructed differently.** Not observed. The harness carries its own
  streak counters while the live run queries prior stored rows; 9,223 statuses agree exactly across
  402 subjects, including the `WouldRevert` rows that depend on the consecutive-Suspect count.
- **B — different inputs.** Not observed. No refusal, no unclassifiable parameter, and no
  truncation-limited subjects (nothing retired in this generation, so every subject's rows run
  full-length and the recompute is valid in both directions).
- **C — real bands vs the fixtures' synthetic sweep.** **This was the slice's actual value and it is
  now discharged in both halves.** The live re-simulation computed bands from real member window
  alphas and rescued 1,756 rows (§2); the harness derived them independently and agreed exactly (§1),
  with §4 proving the derivation was live rather than bypassed.

## 7. Cost, measured — and a note on the procedure

| phase | duration |
|---|---|
| `DeleteReplayGeneration` (`--reset`) | **~26 min** |
| Re-simulation, 502 sessions | **~87 min** (4.9 s/session early, decaying to ~9 s/session) |
| **Total** | **1 h 53 m** |

**The delete is ~23 % of the run and is pure setup**: `--reset` destroys the whole twenty-year
generation in order to re-simulate two years, because it is the only flag that forces re-simulation.
That is a property of D117's slice procedure worth recording — the cost scales with the GENERATION,
not with the window. It did not matter here; it would matter for a slice run often.

## 8. Safety envelope — verified, not asserted

- Production `sp500` **byte-identical throughout**: `3,887,775,744` bytes, mtime `2026-08-03 15:22:59`,
  sha256 `21598bd4e6cd71e9d86bd4895d7f413ceaec69a38f9ab9cbf94ec8d3ec22afb4`, checked before the copy,
  during the run, and after.
- The copy was verified COMPLETE before it was read: size match, identical sha256, and
  `PRAGMA integrity_check → ok`.
- All writes landed on the throwaway copy, which was destroyed. Its id appears in no committed file.
- `Accounts.StartingCash` confirmed at **one version** (v1, 2006-01-03) — D141 pre-flight part 3.

**One deviation from a pristine reproduction, recorded rather than buried:** the live store was found
to be one migration behind (`20260807132833_Phase64MembershipProvenance`, D137), which fails the
pending-migration guard in every writer. It was applied **to the throwaway copy only**; production was
not migrated. The migration adds two nullable columns to `index_membership_log` with no backfill, and
nothing on the monitor, funnel, ledger, population or gate path reads them — so it cannot affect this
comparison. **Production still needs that migration applied, snapshot-first, as an operator act.**
