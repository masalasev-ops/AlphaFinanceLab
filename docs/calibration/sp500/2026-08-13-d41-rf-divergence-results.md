# D41 risk-free swap — RESULTS

*Checkpoint 6.6, commit 5. Predictions were committed **before** this ran, in
`2026-08-13-d41-rf-divergence-predictions.md`. **Two predictions were confirmed and are meaningful.
Three could not be tested by the instrument used, and that is the finding.***

---

## Provenance

| | |
|---|---|
| Store | the `sp500` arena, **byte copy only** — production was never opened for write |
| Production sha256, before | `21598BD4E6CD71E9D86BD4895D7F413CEAEC69A38F9AB9CBF94EC8D3EC22AFB4` |
| Production sha256, after | `21598BD4E6CD71E9D86BD4895D7F413CEAEC69A38F9AB9CBF94EC8D3EC22AFB4` — **identical**, 3,887,775,744 bytes, no WAL/SHM sidecars left behind |
| Scratch copy | destroyed; its path and id appear nowhere in this artefact |
| Generation window | **2006-01-03 … 2025-12-31**, 5,031 sessions |
| Fetch | through `FrenchFactorProvider` + the sanctioned `IResilientBinaryFetcher` — the first real execution of the 6.6 ingest against its live source |

**Migrations, as a free operational result:** the copy was at **16** applied and took
`Phase64MembershipProvenance`, `Phase65WarningAckJournalKind` and `Phase66FactorTables` cleanly to **19**
— including M12's whole-table rebuild. The operator's pending upgrade is therefore rehearsed on
production's real shape and size rather than on a fixture.

## The fetch

**121,297 observations.** All seven SCHEMA tokens present: `CMA`, `HML`, `MKT_RF`, `RF`, `RMW`, `SMB`
(15,854 each) and `UMD` (26,173 — the momentum file starts earlier). Fingerprint
`876fa7efe21b14e380d9cf13d1804a93da4dca1ba6a2087ac7b6947ebb911f7c` over the raw zip bytes. Ingest
accepted it: `written=true, rowsAdded=121297, through=2026-06-30`, continuity **0 missing of 7,673**
sessions checked.

## Coverage — reported beside the divergence, not after it

| | |
|---|---|
| RF observations loaded | 15,854 (**1963-07-01 … 2026-06-30**) |
| Generation sessions | 5,031 |
| **Covered** | **5,031** |
| **Uncovered** | **0** |
| `FullyCovered` | **true** |

**The divergence below is measured over a fully-covered population**, not a mixed one. D41's publication
lag is real but falls *outside* the frozen window: RF runs six months past the generation's last session,
so no `MetricCell.ReasonRfPlaceholder` window exists anywhere in this measurement. Mean RF over the
series is **4.324 %/yr**.

---

## Run A — the parity baseline (`factor_returns` empty)

| Artefact | Stored | Recomputed | Differing |
|---|---|---|---|
| `overfitting_status` | 95,600 | 95,600 | **0** |
| `go_live_log(Promoted)` | 144 | 144 | **0** |
| would-reverts | 18,533 | 18,533 | **0** |
| **α\*(H)** | **0.069474** | **0.069474** | — |

`ParityHolds: true`. The harness reproduces the frozen generation exactly, so run B's numbers are
attributable to the one variable that changed.

## Run B — the RF series loaded (121,297 rows)

**Byte-for-byte identical to run A.** `ParityHolds: true`; promotion shape `moved=0 gained=0 lost=0`;
α\*(H) `0.069474` both sides.

---

## finding 446 — THE HARNESS CANNOT SEE THIS CHANGE, AND ITS ZERO DOES NOT MEAN WHAT IT LOOKS LIKE

Run B reports `PARITY HOLDS: True`. Read at face value that says the RF swap is inert. **It is not.**

`MonitorRecompute`'s own summary line is the explanation: *"Re-derives `overfitting_status` (and the
would-be-retire events) **from the STORED** …"* — its unit of input is `StoredCheck(Signal, Value,
Contribution, ThresholdJson)`. It reads the S2/S3/S6 values **as persisted** and re-applies the threshold
rules to them. It never recomputes a Sharpe or an α from an equity curve. So a change to the *inputs* of
those signals — which is exactly what RF is — **is invisible to this arm by construction**, at any tier.
The run was `DirectRead`, where `BandInputs` is not even built.

**This is a third kind of zero**, distinct from the three this corpus already names: not D148's "code that
never ran", not D156's "the path ran and refused nothing", not D158's "a precondition the data never
satisfied". It is **an instrument that cannot observe the quantity**. The predictions for
`overfitting_checks` and `overfitting_status` were therefore not *falsified* — they were **not tested**,
and reporting their zero as "no impact" would have been the most confident wrong sentence in this
checkpoint.

**Consequences:** names D106 and D117 (the harness and its tiers) and MASTER §25 — **no register row is
amended**, because none of them claims the harness recomputes signal inputs; the defect is in reading a
parity result as broader than its scope. `FX-RecomputeParity`'s guarantee is unchanged and remains
correct for what it covers. **Trigger:** any future change to a monitor signal's INPUTS (rather than its
thresholds) that is scored with this harness — the next one is the gate-side RF change D118 still owes.

---

## What the mechanism actually does — measured directly, on returns

Because the harness could not answer, the shift was measured straight from the equity curves for **400
subjects** over the generation:

| | min | p25 | median | p75 | max |
|---|---|---|---|---|---|
| **Sharpe shift** (annualized) | −0.0866 | −0.0844 | **−0.0840** | −0.0834 | −0.0813 |
| **α shift** (annualized, decimal) | +0.000376 | +0.000827 | **+0.000932** | +0.001055 | +0.001521 |
| **β** | 1.0234 | 1.0503 | **1.0564** | 1.0635 | 1.0910 |

**400 of 400 subjects moved on both.** Nothing here is inert.

**The signs are a check on the mechanism, and they pass.** Sharpe does not difference RF away at all, so
subtracting a positive rate must *lower* it — it does, by ~0.084 of annualized Sharpe. α shifts by
`−(1 − β)·r̄f`; every subject has **β > 1**, so `(1 − β) < 0` and the shift must be **positive** — it is,
median +0.093 %/yr. A sign error anywhere in the excess-at-source wiring would have shown up here.

## Scorecard against the predictions

| Prediction | Result | Verdict |
|---|---|---|
| `go_live_log(Promoted)` → ZERO | 0 of 144 | ✅ **confirmed, and meaningful** — `GateRecompute` *does* re-derive from returns (`CurveMath.AlignedReturns`, raw) through the same `PairedEffect` call the live gate makes. An identical result is real evidence the gate path is RF-free. |
| **α\*(H)** → ZERO | 0.069474 → 0.069474 | ✅ **confirmed** — built from those promotions. **The frozen 6.95 %/yr floor stands; no calibration row is owed by 6.6.** |
| `power_reports` → ZERO | not separately reported | ⚠️ same gate mechanism, but not independently observed |
| `allocation_log` → ZERO | not measured | ⚠️ not recomputed by the harness |
| `overfitting_checks` → MOVE | **not testable** | ❌ finding 446 |
| `overfitting_status` → MOVE | 0 differing, but RF-blind | ❌ finding 446 |
| `replay_regime_outcomes` → MOVE | not measured | ❌ not recomputed by the harness |

**The prediction set earned its keep by failing.** Had commit 5 stated one expected number and seen it,
the run would have read as a clean pass. Seven predictions with mechanisms attached is what exposed that
three of them were addressed to an instrument that cannot answer them.

## Still open

**The monitor-side divergence is UNMEASURED**, and this artefact does not claim otherwise. Measuring it
means re-running `OverfittingMonitor.Run` over the generation with and without RF — a full re-derivation
of 95,600 status rows including the per-family population alphas, not a harness pass. The direct
measurement above bounds the *inputs* (Sharpe −0.084, α +0.00093 on every subject); what it does not say
is how many stored statuses sit close enough to a threshold to flip. **Owed before the monitor's numbers
are trusted under RF; it is not a blocker for the ingest, which is what 6.6 delivers.**
