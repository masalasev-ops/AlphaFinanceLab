# Construction study — sp500, 2026-08-04 (D123, Phase 5.5)

**The question: can this arena adjudicate a realistic edge, or only an implausible one?**

The detectability floor is `ZSum x TE / sqrt(H)`, so TRACKING ERROR decides what is adjudicable at all. This measures TE under two constructions of the same signal, over the same universe, the same rebalance and the same costs — the only difference is the construction. ZSum = 2.8016 (Gate.Confidence/Gate.Power), H = 10 year(s) (Gate.DetectabilityHorizonYears).

*Window 2001-07-24..2026-07-24 (6287 sessions) at watermark `2026-07-24T22:00:00Z`. Tail fraction 10 %; monthly rebalance (first session of each calendar month); book $100,000 USD; NW lag from a 21-session holding period.*

## What this report is NOT

- **It is not a backtest and no strategy is being proposed.** It measures a PROPERTY OF A CONSTRUCTION — how noisy the active series is — not whether any signal makes money.
- **Nothing here may set a pre-registered `expected_effect_ann` (rule 16 / D52).** Choosing the number you then pre-register by looking at measured data is exactly what pre-registration exists to prevent. This answers "which construction?", never "what should I claim?".
- **The Signal Library stays descriptive-only (D91).** Informing a BUILD decision is not the allocator, a gate, sizing or eligibility. No output of this study is read at runtime.
- **Borrow cost is an ASSUMPTION, not a measurement.** The D43 model has no borrow term and this arena buys no borrow data. The 0 bp column is the optimistic bound: a construction that fails to lower the floor even when borrowing is free fails for a reason no borrow data would rescue.

## Control — what the arena measures for itself today

| source | n | sigma_LR (daily) | TE (ann) | floor at H=10y |
|---|---:|---:|---:|---:|
| replay generation (power_reports run_kind='replay') — the calibration-vintage estimate | 50 | 0.005263 | 8.35 % | 7.40 % |

*Read from the same `power_reports` sigma the admission gate's analytic floor uses, so the comparison below is against a measured number rather than one quoted from a document.*

## The measurement

| signal | family | rebal | tail | scored | construction | TE (ann) | floor | gross eff | cost drag | net eff |
|---|---|---:|---:|---:|---|---:|---:|---:|---:|---|
| `mom:L252s21` | momentum | 233 | 44.3 | 447.7 | long_only | 14.01 % | 12.41 % | -0.18 % | 0.19 % | -0.37 % @ 0bp |
| `mom:L252s21` | momentum | 233 | 44.3 | 447.7 | long_short | 29.67 % | 26.28 % | -0.55 % | 0.39 % | -0.94 % @ 0bp<br>-1.34 % @ 40bp |
| `mom:L126` | momentum | 240 | 44.3 | 447.5 | long_only | 12.59 % | 11.15 % | 1.29 % | 0.26 % | 1.03 % @ 0bp |
| `mom:L126` | momentum | 240 | 44.3 | 447.5 | long_short | 27.51 % | 24.37 % | -2.26 % | 0.53 % | -2.79 % @ 0bp<br>-3.19 % @ 40bp |
| `rev:L21` | reversal | 245 | 44.2 | 447.5 | long_only | 14.48 % | 12.82 % | 0.47 % | 0.60 % | -0.12 % @ 0bp |
| `rev:L21` | reversal | 245 | 44.2 | 447.5 | long_short | 22.38 % | 19.83 % | 0.99 % | 1.18 % | -0.19 % @ 0bp<br>-0.59 % @ 40bp |
| `lowvol:L252` | lowvol | 234 | 44.3 | 447.6 | long_only | 13.08 % | 11.59 % | -2.26 % | 0.07 % | -2.33 % @ 0bp |
| `lowvol:L252` | lowvol | 234 | 44.3 | 447.6 | long_short | 30.73 % | 27.23 % | -9.24 % | 0.14 % | -9.38 % @ 0bp<br>-9.78 % @ 40bp |
| `brk:L252` | breakout | 235 | 44.3 | 447.4 | long_only | 12.12 % | 10.74 % | -2.92 % | 0.47 % | -3.38 % @ 0bp |
| `brk:L252` | breakout | 235 | 44.3 | 447.4 | long_short | 31.21 % | 27.65 % | -5.83 % | 0.65 % | -6.48 % @ 0bp<br>-6.88 % @ 40bp |
| `resmom:L252` | resmom | 234 | 44.3 | 447.6 | long_only | 12.84 % | 11.37 % | 1.53 % | 0.19 % | 1.34 % @ 0bp |
| `resmom:L252` | resmom | 234 | 44.3 | 447.6 | long_short | 26.54 % | 23.51 % | 1.57 % | 0.39 % | 1.19 % @ 0bp<br>0.79 % @ 40bp |
| `bab:L252` | bab | 234 | 44.3 | 447.6 | long_only | 14.38 % | 12.74 % | -3.44 % | 0.08 % | -3.52 % @ 0bp |
| `bab:L252` | bab | 234 | 44.3 | 447.6 | long_short | 31.54 % | 27.95 % | -12.19 % | 0.16 % | -12.35 % @ 0bp<br>-12.75 % @ 40bp |

## The decision number — information ratio and years-to-detect

**Do NOT compare the two floors above.** A long-short book is roughly 2x leverage on the same cross-sectional bet: it scales the tracking error AND the effect together. The floor rises with TE, but so does the effect that has to clear it, so the comparison says nothing. The t-statistic is `IR x sqrt(T)`, so detectability depends on the INFORMATION RATIO alone — and `years-to-detect = (ZSum / IR)^2` is the quantity that IS comparable across constructions.

**The bar: at H = 10 years this arena can only adjudicate a strategy whose active-return information ratio is at least `ZSum/sqrt(H)` = 0.886, sustained.** That follows from the horizon and the confidence/power pair alone — no measurement enters it. Read every row below against it.

| signal | IR long-only | IR long-short | IR gain | yrs to detect (LO) | yrs (LS) | reading |
|---|---:|---:|---:|---:|---:|---|
| `mom:L252s21` | 0.026 | 0.032 | 1.21x | 11519.1 | 7813.3 | no material gain — leverage, not information |
| `mom:L126` | 0.082 | 0.101 | 1.24x | 1166.2 | 764.6 | no material gain — leverage, not information |
| `rev:L21` | 0.009 | 0.008 | 0.99x | 107315.7 | 109481.9 | long-short is WORSE here |
| `lowvol:L252` | 0.178 | 0.305 | 1.71x | 247.5 | 84.3 | materially faster, still beyond the horizon |
| `brk:L252` | 0.279 | 0.208 | 0.74x | 100.7 | 181.9 | long-short is WORSE here |
| `resmom:L252` | 0.104 | 0.045 | 0.43x | 718.8 | 3927.4 | long-short is WORSE here |
| `bab:L252` | 0.245 | 0.392 | 1.60x | 131.1 | 51.2 | materially faster, still beyond the horizon |

*An IR gain near 1.00x means the construction bought LEVERAGE, not information — the same bet twice the size, detectable no sooner. A gain meaningfully above 1.00x is the only thing that would justify building shorting. Compare years-to-detect against the gate horizon of 10 year(s).*

*The banding is a READING AID, not a threshold the code enforces: no verdict, gate or config anywhere consumes it. The decision is the operator's, made on the numbers and the rule text — never on which answer is more convenient.*

### Borrow sensitivity (long-short only)

| signal | IR @ 0bp | IR @ 40bp | verdict flips? |
|---|---:|---:|---|
| `mom:L252s21` | 0.032 | 0.045 | no |
| `mom:L126` | 0.101 | 0.116 | no |
| `rev:L21` | 0.008 | 0.026 | **YES — the answer depends on borrow data this arena does not have** |
| `lowvol:L252` | 0.305 | 0.318 | no |
| `brk:L252` | 0.208 | 0.221 | no |
| `resmom:L252` | 0.045 | 0.030 | no |
| `bab:L252` | 0.392 | 0.404 | no |

## Caveats a reader must carry

- **Tracking error is NW-corrected** (sigma_LR, not the naive standard deviation). An autocorrelated active series gives sigma_LR > sigma_naive, so these floors are LARGER than a naive TE would imply. A study arguing for a lower floor must not pick the estimator that flatters it.
- **Cost is a drag on the mean, never folded into the series TE is measured from.** A monthly cost lands as a lump on twelve days a year; charging it to the series would add variance that is an artefact of the rebalance calendar rather than a property of the construction.
- **The floor here carries no Bonferroni trials haircut**, deliberately — that haircut is a property of how many candidates the arena has registered, and including it would make the two constructions differ by the trials count as well as by their tracking error.
- **The benchmark is the SCORED set, not the eligible pool** (finding 294's rule). A benchmark holding names the signal could not score would fold "thin-history names behaved differently" into what this calls the signal's active return.
- **Survivorship and the stored corpus.** The universe resolves through the D97/D109 exclusion-scoped as-of membership, so the D120 sweep's exclusions apply; the D49 community-CSV survivorship caveat still rides on every historical statement this arena makes.
- **A signal showing zero rebalances is a DATA GAP, not a result** — most likely a missing market proxy, which leaves `resmom:L252` and `bab:L252` scoring nothing at all.
