# D41 risk-free swap — PREDICTIONS, recorded before the measurement ran

*Checkpoint 6.6, commit 5. Written and committed **before** the scratch copy was taken, the fetch was
made, or any recompute executed. The results live in a separate artefact so the order is auditable
rather than asserted.*

**Why per-table rather than one number.** A single expected count that comes back matching is a
coincidence indistinguishable from a correct run. Each table below is predicted with the mechanism that
produces it, so a result is evidence only if **every** row lands where its reason says it should — and a
row landing in the wrong place names which mechanism was wrong.

---

## The correction this prediction set rests on

An earlier statement in this checkpoint's working notes said the gate is unaffected **because RF cancels
in the paired difference**. That reasoning is **wrong for the arm the gate actually uses**, and the
prediction below does not rely on it.

- The **raw-gap** arm (`PairedEffect.cs:66-74`) forms `d_t = r_s − r_b`. RF genuinely cancels here:
  `(r_s − rf) − (r_b − rf) = r_s − r_b`, exactly, for any rf.
- The **Jensen** arm (`PairedEffect.cs:88`) is `NeweyWest.Ols(stratReturns, benchReturns, lag)` — an OLS
  intercept, not a difference. **RF does not cancel in it.** Subtracting rf from both sides shifts α by
  `−(1 − β)·r̄f`, precisely as it does for the monitor's α.
- **D118 moved the gate onto the Jensen arm.** So the gate's effect is RF-SENSITIVE, and the reason the
  numbers below are predicted at zero is **not** invariance — it is that **the gate path was deliberately
  not changed by commit 5**: `EvaluationStep.cs:131` still calls `CurveMath.AlignedReturns` (raw), and
  neither `PairedEffect` nor `MdeCalculator` references `RiskFreeSeries` or `Excess`.

The distinction matters for what a non-zero would MEAN, which is the point of predicting at all.

---

## Predictions

| Artefact | Predicted | Mechanism |
|---|---|---|
| `go_live_log(Promoted)` | **ZERO** | The gate path is unchanged code on unchanged inputs. `EvaluationStep` aligns raw returns; `PairedEffect`/`MdeCalculator` never see an excess series. |
| `power_reports` | **ZERO** | Written by `EvaluationStep.cs:153` and `ReplayVerification.cs:678` — both gate-path, both unchanged. `observed_gap_ann`, `mde_ann` and `sigma_lr` all come from the one `PairedEffect` fit. |
| `allocation_log` | **ZERO** | Downstream of the gate's outputs and the D51 allocator's inputs. Moves only if the two rows above move. |
| **α\*(H)** | **ZERO** | `RecomputeHarness.cs:116` builds it from `recomputedPromotions`. Unchanged promotions ⇒ unchanged detection curve ⇒ the frozen 6.95 %/yr floor stands. |
| `overfitting_checks` | **EXPECTED TO MOVE** | S2 is Sharpe, which does **not** difference RF away at all — an absolute excess-return statistic, biased by the whole level of rates. S3 ranks α inside the population's α distribution and RF shifts α by `−(1 − β)·r̄f` with per-member β, so members move by different amounts and the ranking is not preserved. S6's rolling α likewise. |
| `overfitting_status` | **EXPECTED TO MOVE** | The aggregate over the moved signals. Whether it moves *materially* depends on how many rows sit near a threshold, which is not predictable from the mechanism — only the direction is. |
| `replay_regime_outcomes` | **EXPECTED TO MOVE** | `ReplayRegimeOutcomesWriter` now computes each episode's `edgeAnn` on excess returns (changed by commit 5). Per-episode Jensen α shifts by `−(1 − β)·r̄f` over that episode's span. |

### What a violated prediction would mean

- **A non-zero in any of the first four falsifies the "gate path untouched" claim** and is a larger finding
  than a moved floor: it would mean the RF change leaked into the promotion path by a route not in the
  four files commit 5 edited. That is the result to hope against, and the one worth the run.
- **A zero in `overfitting_checks`** would mean either the RF series never reached the monitor (a wiring
  failure, checkable against the reported coverage) or the rates are too small to move any stored value at
  the persisted precision. Those are different and the coverage figure separates them.
- **A zero in `replay_regime_outcomes` while `overfitting_checks` moved** would mean the episode writer's
  excess path is not actually wired, since both changed in the same commit for the same reason.

### Coverage is reported beside the divergence, not after it

`RiskFreeWindow.FullyCovered` is what decides whether an absolute figure may be presented without
`MetricCell.ReasonRfPlaceholder`. If the French series does not cover the whole frozen window, the
divergence is measured over a MIXED population — partly RF-adjusted, partly not — and means less than it
appears to. The results artefact states covered days, uncovered days, and **which end of the window** any
gap falls on: a gap at the recent end is the publication lag (expected, D41 says weeks); a gap at the old
end would mean something else entirely.

### Method constraints this run is held to

1. The fetch goes through `FrenchFactorProvider` and the sanctioned `IResilientBinaryFetcher` — the first
   real execution of the code this checkpoint built. A direct download would obtain the data and prove
   nothing about the ingest.
2. **Scratch copy only.** Production is byte-hashed before, during and after; the copy is destroyed; no
   scratch path or id appears in any committed artefact.
3. Two recomputes, one variable: **A** with `factor_returns` empty (must reproduce the stored generation
   exactly — the parity baseline) and **B** with the series loaded. The divergence reported is **B vs A**,
   so it is the RF effect alone and not the harness's own deviation. A ≠ stored invalidates the run before
   B is even read.
