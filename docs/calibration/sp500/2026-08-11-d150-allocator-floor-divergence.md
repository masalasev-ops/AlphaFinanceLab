# D150 — what the floor-aware renormalization does to the frozen generation

*Generated 2026-08-11 for Phase 6 checkpoint 6.5a, review remediation PR 9 (finding 416). Store:
`E:/AlphaLabDatabase/sp500/alphalab.db`, opened `Mode=ReadOnly`. Subject: generation 2, `run_kind='replay'`,
239 `allocation_log` rows / 95,768 persisted per-strategy entries, roster 400–401.*

This artefact exists because the review's brief for F9 asserted that a literal fix "collapses the blend
toward its own comparator" and would take the published D51 allocator value-add KPI to ~0 by construction.
That is a claim about a number, and the standing rule since finding 414 is to **bound a divergence rather
than declare it**. It was measured instead, and the claim does not survive the measurement.

## Method (preserved, not only its result)

`allocation_log.weights_json` persists the full reconstructible input vector per strategy (NFR-2), so the
whole 239-evaluation sequence can be re-run offline:

1. read `alpha_hat_pct` and `se_pct` per (as_of, strategy) straight out of the persisted vector;
2. read `TooEarly` from `power_reports.verdict` and `Suspect` from `overfitting_status.status`, both
   `run_kind='replay'`;
3. chain `PriorWeight` from the **previous simulated** evaluation, exactly as `AllocationStep.PriorWeights`
   does (it reads `Weight`, not `Applied`);
4. run the sequence twice — once through a verbatim replica of the **pre-D150** allocator, once through the
   repo's own `EnsembleAllocator` as shipped.

**The reconstruction is validated before anything is concluded from it.** The pre-D150 replica reproduces the
store at `max |recomputed − persisted| = 0.000E+000` across all **95,768** entries. Not "close" — exact. That
is what makes every number below comparable to the store rather than to a model of it.

## Result

| | pre-D150 (= the store) | D150 as shipped |
|---|---|---|
| D51 value-add `observed_gap_ann` | **0.1781382919832431** | **0.173217202324534** |
| `d.Count` | 5,010 | 5,010 |
| verdict (MDE 0.01765/yr) | `Promoted` | `Promoted` |
| final book (2025-12-12) max weight | 0.151253 | 0.130088 |
| final book top-5 share | 40.91 % | 40.01 % |
| mean weight on the 250 edge plants | 0.396016 % | 0.394703 % |
| mean weight on the 50 anti plants | 0.006029 % | 0.007722 % |
| edge−anti separation | 0.389987 pp | 0.386981 pp |

**The KPI moves −0.49 pp on a 17.81 pp gap — 2.8 % relative — against an MDE of 1.76 %/yr.** The verdict is
unchanged and is not close to changing. The persisted number was reproduced exactly first
(`0.1781382919832431`, `d.Count = 5010` matching the stored `t_days = 5010`), so the two figures are the same
computation over two weight sequences and nothing else.

**The brief's collapse claim is refuted.** The reasoning behind it was that at `n ≥ ⌊100/WeightFloorPct⌋` the
effective floor is exactly `1/n`, so `{w : wᵢ ≥ 1/n, Σw = 1}` is the single point of equal weight. The
arithmetic is right; the inference is not. It only bites if **every** row is floor-clamped, and in this
generation only **7,945 of 95,768** rows (≈ 33 per evaluation of ~400) end with `applied == effectiveFloor`.
Flattening is a startup transient, not the steady state — evaluation 0's max weight goes 0.0886 → 0.0047,
but by evaluation 40 the two sequences agree to 0.318 vs 0.322.

## The invariant, checked directly

- Rows the floor clamp produced (`applied == effectiveFloor`): **398**, all on `2006-02-01`, the only
  evaluation with no priors.
- Of those, weight below the floor **after** D150: **0**. That is the clause MASTER §20.2 states.
- `max |Σweight − 1|` over all 239 evaluations: **7.105E-015**.

## What D150 does NOT fix, sized so it is not mistaken for done

Rows carrying the `floor` token whose final weight is below the floor: **86,513 → 80,555**. The residue is
not renormalization at all — it is the **suspect decay compounding without a lower bound**: `w = prior ×
0.75` every evaluation, driving a plant from 2.49E-03 to **3.96E-32** over 239 evaluations while
`clamps_bound` reads `["floor","suspect_decay"]` the entire way. On the last evaluation **354 of 400**
derivation rows would render a UX-9 `floor` chip beside weights as small as 4.7E-33.

That value is **conformant** — DESIGN_IMPROVEMENTS §3.5 step 3 clause 3 says "decay only, never a new tilt",
and D150 deliberately does not lift those rows (`FR27_D150_ASuspectDecayedRowIsNotLiftedBackToTheFloor` pins
it). The defect is the **token**, which MASTER §592 and UX-9 both describe as "the clamp that **bound**".
Filed as **finding 417**, not fixed here: it is a read-model/vocabulary question, not an arithmetic one.

## Consequence for the frozen generation

Generation 2's `allocation_log` was produced by pre-D150 arithmetic and this changes it — final-book L1
divergence **0.246**, peak **0.555** at 2006-05-03, 200 of 400 rows moving by more than 1e-9. It is **not** a
`LedgerArithmetic` (D144) bump: `allocation_log` has exactly three readers — `AllocationStep.PriorWeights`,
`AllocatorValueAddKpi`, and `AllocationReadModelBuilder` — and none of them writes `trades`, `positions`,
`cash_events` or `equity_curve`, which is precisely the boundary D144 draws. The generation's ledger is
untouched; only a re-derivable evaluation artefact would differ, and `ReplayRunner` already deletes and
rewrites `allocation_log` wholesale on any replay.

**The numbers above are the expected divergence if generation 2 is ever regenerated**, stated here so that a
future difference can be checked against a figure rather than argued about.
