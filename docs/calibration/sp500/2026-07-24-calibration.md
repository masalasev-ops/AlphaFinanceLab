# Threshold-calibration report — arena sp500

> ## ⚠ READ THIS FIRST — THIS IS A 40-SESSION MECHANICS RUN, **NOT** THE STAGE-2 SMOKE EVIDENCE
>
> *Editorial banner added by hand on 2026-07-25 (finding 279); everything below the line is generated
> output and is unmodified.*
>
> This file covers **`Replay run span: run 1..40`** — a 40-session window (2006-01-01 .. 2006-03-01).
> It is **NOT** the Stage-2 smoke run, which was **251/251 sessions** and whose evidence lives in
> `PROGRESS.md` ("Last session", 2026-07-24). The two are routinely confused, and the confusion is
> load-bearing:
>
> - **This file reports `joint_false_alarm` as `Pass` — 0/50 no-edge plants ever Suspect.** That is an
>   artefact of the window length, not a result. The first no-edge plant trips "ever Suspect" at
>   **~session 62**, so a 40-session run ends *before* the metric can fire.
> - **The Stage-2 smoke (251 sessions) recorded `joint_false_alarm` at 100 % — a Fail** — classified as
>   expected under the amendment-C1 comparability caveat, with a nonzero exit.
> - Reading this file's `Pass` as smoke evidence produces exactly the wrong conclusion about whether the
>   freeze gate was ever going to clear. That mistake is recorded as **finding 279**.
>
> It is committed as **evidence of a generation that no longer exists** — the subsequent full-scale run
> launched with `--reset`, which deletes the sessions this report was computed from (RUNBOOK §8 step
> 4(a): "commit that report as evidence FIRST"). It is **not** a Phase-4 sign-off artefact and must
> never be cited as one.

---

- Window: **2006-01-01 .. 2006-03-01**  (learn through: 2006-02-15)
- Frozen replay watermark (D95): `2026-07-24T22:00:00Z`
- Replay run span: run 1..40
- Seeds per plant: 50 · population M: 200
- Generated: 2026-07-24T23:29:24Z
- Build configuration: **Release** (finding 278: the sign-off artifact records which build produced these numbers)
- Config rows frozen this run: Monitor.S3.PNoiseCurve.daily, Monitor.S3.PEdgeCurve.daily, Calibration.DetectionPower, Monitor.S6.AutoRetireEvals, Calibration.ReportRef

## D56 trajectory curves (S3)

### P_edge(t) — realistic plant (the calibration plant, D64)

| t (sessions) | percentile | 25–75% band |
|---|---|---|
| 21 | 59.2 | 43.0–82.4 |

### P_noise(t) — false-alarm envelope of the no-edge plants

| t (sessions) | percentile | 25–75% band |
|---|---|---|
| 21 | 9.9 | 44.1–80.6 |

C-2 sampling band: the anchors ride an M=200 empirical distribution — ±3.08 members (edge) / ±3.08 members (noise) of binomial noise at the defining quantiles. Archived so a future "should M be 500?" has its evidence.

## Plant sensitivity — naive vs realistic (PERMANENT section, D64/FX-PlantRealism)

### P_edge(t) — NAIVE constant-drift comparator (prohibited as the calibration plant)

| t (sessions) | percentile | 25–75% band |
|---|---|---|
| 21 | 59.5 | 29.8–86.2 |

No knots at t ≥ 126d in this window — divergence not evaluable at this horizon (recorded, not skipped).

## C-1 detection power — empirical P(promoted by t | α)

The FR-40 detectability-at-admission gate's empirical floor (D89): swept across the edge-plant
alpha levels on the same seeds, validating the analytic NW-MDE end-to-end against the machinery.

### α = 2%/yr (50 seeds)

| t (sessions) | P(promoted by t) |
|---|---|
| 21 | 0.00 |

Median sessions to promotion: (none promoted)

### α = 4%/yr (50 seeds)

| t (sessions) | P(promoted by t) |
|---|---|
| 21 | 0.00 |

Median sessions to promotion: (none promoted)

### α = 8%/yr (50 seeds)

| t (sessions) | P(promoted by t) |
|---|---|
| 21 | 0.00 |

Median sessions to promotion: (none promoted)

### α = 16%/yr (50 seeds)

| t (sessions) | P(promoted by t) |
|---|---|
| 21 | 0.00 |

Median sessions to promotion: (none promoted)

## Machinery verification + KPIs (FX-Replay15y)

| Check | Outcome | Detail |
|---|---|---|
| promotions_le_chance | **Pass** | 0/50 no-edge plants promoted; chance bound 4 at p=0.0250 |
| edge_plant_detected | **Insufficient** | 0/50 PRIMARY edge plants promoted (window 40 sessions); detection-power by rung — daily@2%:0/50, monthly@2%:0/50, monthly@4%:0/50, monthly@8%:0/50, monthly@16%:0/50 |
| joint_false_alarm | **Pass** | 0/50 no-edge plants ever Suspect (bound 10 %) |
| anti_detection_speed | **Insufficient** | no anti plant reached Suspect (short window?) |
| days_to_indistinguishability | **Insufficient** | window 40 < SeparationMinTrackDays 252 |
| noedge_pnoise_breach_validate | **Insufficient** | no validate-period S3 points |
| noedge_curve_breach_validate | **Pass** | 0/50 no-edge plants sustain-breach P_noise on validate (bound 10 %) |
| curve_based_edge_survival | **Pass** | 100/100 floor-edge plants do not sustain-breach P_noise on validate (floor 90 %); 0/100 sustain-clear P_edge (distinguishable) |
| would_be_edge_survival_5y | **Insufficient** | window 40 < 5y |
| edge_retires_logged | **Pass** | no edge plant would auto-retire (nothing to log) |
| allocator_value_add | **Insufficient** | blend−EW gap 0.90 %/yr (MDE 1.89 %, TooEarly, T=19); mean weight edge 0.3% vs anti 0.2% |
| cohort_s3_paths_present | **Pass** | persisted replay S3 percentile paths exist for cohort reconstruction |

- Anti-predictive detection speed (D63): (not evaluable at this scale) sessions (median)
- Days to IndistinguishableFromRandom (D63): (not evaluable at this scale)
- Would-be edge-plant survival (from the retire log): 5y (not evaluable at this scale) · 10y (not evaluable at this scale)
- Joint any-signal false alarm (monitor flagging — see comparability note): 0.0 %
- No-edge P_noise breach rate, point-level (validate segment): (not evaluable at this scale)
- No-edge curve breach, per-plant sustained (validate — INDEPENDENT of monitor flagging): 0.0 %
- Curve-based edge survival (validate): 100 %
- Allocator value-add (§1.2): gap 0.90 %/yr, MDE 1.89 %, TooEarly, T=19; mean weight edge 0.3% vs anti 0.2%

### Per-signal false-alarm contribution (finding 114)

(no no-edge plant ever reached Suspect)

## Data vintage (D64 stamp)

- Membership source: fja05680 community CSV (Backfill.HistoricalGateSweep: {"universe":"sp500","from":"2006-01-01","to":"2026-01-01","artifact_sha256":"99453b3fdde19542badd64a9e582f58810ef3e57d7609ea592969971652ff373","excluded":["CBH"…)
- Survivorship caveat: pre-launch data carries residual survivorship bias (MASTER §13.4) — replay Sharpe is expected to flatter; the curves are relative separations, which is what the monitor consumes.
- Slice caveat: curves are calibrated on S&P 500 as-of membership (D70); the FORWARD universe remains the S&P 100 slice until the post-sign-off widen (rule 22).
- Replay is slightly LESS informed than a true historical observer (a declared-but-not-yet-effective action is invisible under the D95 date ceiling) — the conservative direction.
