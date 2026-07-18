# Design Improvements & Factor Research — v1.9

*Companion to MASTER_DESIGN_v1.9 (§9, §10, §12) and OVERFITTING_MONITOR_v1.9. This document holds the deep-dive material: (1) the metrics and evaluation machinery in full, (2) what the factor-research literature actually supports and what a long-only large-cap implementation keeps, (3) the sizing/guardrail mathematics, (4) the LLM value model, and (5) the Arena Replay design. BUILD_AND_PROMPTS_v1.9 sequences all of it.*

> Research/paper-trading only. Not investment advice.

---

## 0. What changed in v6

1. **§1 metrics:** MDE is now Newey–West-corrected (D48); the trade-level expectancy track is specified (D44); factor attribution has a named data source and refresh job (D41 — Ken French daily factors + RF); regime metrics carry episode counters (D45).
2. **§3 sizing/guardrails:** the covariance estimator is specified (Ledoit–Wolf, D42) and the slippage/impact model is fully parameterized (D43).
3. **§4 AI seats:** the LLM layer is now the three-seat design (D79-D82; researcher, contestant with mandatory no-LLM twin, deferred advisor) — full spec in MASTER §23; the economics (batching, caching, tiering, news budget, D46) and the context-pack cost model carry over here.
4. **§5 (new): Arena Replay** (D37) — design, quarantine rules, and the two jobs it exists for (machinery validation, threshold calibration).
5. **§6 (new): the power reality in full** — the arithmetic behind MASTER_DESIGN_v1.9 §1.1, with the pairing-tightness sensitivity table, and what it implies for how the system should be *used*.
6. **v1.8 revision:** §1.5 gains the concrete PIT regime-label computation (D50); §3.5 is replaced by the **full allocator specification (D51)** — the primary improvement mechanism is no longer under-specified.
7. **v1.9 revision (D63/D64):** §5's machinery-validation job and §6's "fast product" consequence are corrected — the cost-matched population channel yields **`IndistinguishableFromRandom`** statements, not fast falsification (that belongs to the trade-level track and to anti-predictive breaches); and §5's plants are now fully specified in MASTER §20.9 (regime-conditional, autocorrelated, multi-seed, sensitivity-checked).

---

## 1. Metrics & evaluation machinery (full spec)

### 1.1 Per-strategy metrics (forward, net of D43 costs, computed locally)

| Metric | Definition | Notes |
|---|---|---|
| **β-adjusted alpha (Jensen's)** | intercept of `r_s − r_f = α + β(r_b − r_f) + ε` on daily data | benchmark = cap-weight Buy&Hold account; **r_f from the French RF series (D41)**; **Newey–West standard errors** (lag = min(2×max holding horizon, 21)); reported annualized with t-stat |
| **Information Ratio** | mean active return ÷ tracking error (annualized) | rendered beside alpha; both required on any comparison screen (D26) |
| **Expectancy** | mean net P&L per trade | the per-trade twin of alpha |
| **Profit factor** | gross wins ÷ gross losses | pairs with expectancy |
| **Sharpe / Sortino** | excess-return mean ÷ (total / downside) vol | excess over French RF |
| **Deflated Sharpe** | Sharpe corrected for the trials count | trials from `trials_registry` — the honest count, incl. retrains and forks |
| **Max drawdown / Calmar** | worst peak-to-trough / annual return ÷ MDD | rendered on every equity chart |
| **Turnover** | annualized % of book traded | the cost model's lever; watched per strategy |
| **Win rate** | only ever displayed with avg-win/avg-loss beside it | D10 |
| **Population percentile (D36)** | rank of forward net β-adj alpha within the matched random population | the S3 input; "97th pct of 200 matched randoms" |
| **Capacity pressure (D43)** | % of intended quantity rejected by the participation cap | a live capacity-awareness readout |

### 1.2 The MDE — Newey–West corrected (D48)

For any head-to-head, form the **daily active-return difference series** `d_t = a_t^{(A)} − a_t^{(B)}` (paired testing, D31). The v5 MDE assumed i.i.d. `d_t`; active-return differences are autocorrelated (shared holdings persist across days; overlapping horizons), which understates the effective variance and makes the honesty metric itself overclaim. v6:

```
σ²_LR = γ₀ + 2·Σ_{k=1..L} w_k·γ_k        # Newey–West long-run variance of d_t
        w_k = 1 − k/(L+1)                 # Bartlett weights
        L   = min(2 × max(horizon_A, horizon_B), 21)

MDE_ann = (z_{1−α/2} + z_{power}) · σ_LR · 252 / √T
        = 2.8 · σ_LR · 252 / √T           # at 95% / 80% defaults
```

Rendered beside every comparison, recomputed at every evaluation, persisted to `power_reports`. **The promotion gate never acts on a gap smaller than the pair's current MDE — the verdict is `TooEarly`.** The same long-run-variance idea governs the trade track's block bootstrap (§1.3).

### 1.3 The trade-level evidence track (D44)

For high-trade-count strategies (fast mean reversion, breakout): mean net P&L per trade with a **moving-block bootstrap** CI (block length ≥ max holding period — trades cluster in time and regime), plus a **trade-track MDE**: the smallest expectancy distinguishable from zero at the configured confidence/power given the current trade count and bootstrap variance. Persisted to `trade_evidence`. Role: **falsification accelerator and evidence supplement — never a standalone promotion basis**; a sharp contradiction between the daily-alpha and trade tracks feeds Monitor S8.

### 1.4 Factor attribution (D41)

Monthly job pulls the **Ken French Data Library daily factor files** (Mkt−RF, SMB, HML, UMD, RMW; optional CMA; plus RF), with checksum and date-continuity validation, into `factor_returns`. Per strategy (≥ ~1y of track):

```
r_s − r_f = α + β_mkt(Mkt−RF) + β_smb·SMB + β_hml·HML + β_umd·UMD + β_rmw·RMW + ε
```

Newey–West errors; rendered as a bar decomposition ("your 'clever' strategy = 0.92 market + 0.31 momentum + noise"). **Diagnostic-only:** never a funnel input, never a gate input — which is exactly why the library's publication lag (weeks; the panel states "factor data through <date>") is acceptable rather than a hidden defect. SMB in the regression also catches the equal-weight/size residual that motivated D27.

### 1.5 Regime metrics (D34 + D45)

All regime tags are **point-in-time labels** (computable from data ≤ that day; covered by the leakage suite). v6 adds the **episode counter**: an episode is a maximal contiguous run of one label, persisted to `regime_episodes`; every regime-conditional statistic renders `n = episodes`, and **n < 3 draws an "anecdote" badge**. A few forward years contain perhaps one bear market — the dashboard must say that its bear-market rows are stories until they aren't.

**The label itself (v1.8, D50):** trend × volatility on the cap-weight proxy, PIT at the run's watermark. Trend = `bull`/`bear` by the 200-day SMA with hysteresis (flip requires ≥ 1% beyond the SMA for 5 consecutive sessions — config `Regime.*`); volatility = `high_vol` when 21-day realized vol ≥ its trailing 3-year 80th percentile, same confirmation. Episodes count on the trend component. Full spec: MASTER §20.1. Ships with F-LEAK and the `FX-RegimeHysteresis` fixture (oscillation around the SMA must produce zero flips).

### 1.6 Statistical honesty stack (summary)

Paired tests on `d_t` (D31) → NW-MDE gate (D48) → deflated Sharpe over honest trials (D23) → **population percentile as the empirical null (D36)** → the monitor's eight signals with Suspect-veto and auto-retire → the trials registry counting *every* attempt (forks, retrains, parameter siblings). Each layer catches a different self-deception; none is decorative.

---

## 2. Factor research — what the literature supports, and the long-only haircut

*(Carried from v5; unchanged in substance, restated compactly because it calibrates every expectation in the system.)*

- **Momentum:** the most robust cross-sectional anomaly (Jegadeesh–Titman 1993 and hundreds of out-of-sample confirmations); published as long-short deciles; **skip-month is mandatory**; crash risk concentrates in the short leg; vol-scaling literature (Barroso–Santa-Clara, Daniel–Moskowitz) mostly rescues the short side. Long-only large-cap tilt: expect a **fraction** of the premium, dominated by beta.
- **Short-term reversal / mean reversion:** strong at 1-week–1-month horizons pre-cost; **costs eat most of it** — which is precisely why the D43 model is load-bearing and why the fast variant lives on the trade track: the question is not "does reversal exist" but "does it survive *your* cost model," and that is answerable quickly.
- **Low-volatility:** the anomaly is real and persistent (Ang et al.; Baker–Bradley–Wurgler); it is a *risk-adjusted* result — β≈0.6–0.8 long-only implementations lose raw-return races structurally (hence D26).
- **Value / Quality:** decades of evidence (Fama–French; Novy-Marx profitability; Asness QMJ), long cyclical droughts (value 2010–2020), and a hard PIT-data dependency (hence D33's gate and §7.0's protocol in the catalog).
- **Decay:** post-publication premia shrink roughly by half (McLean–Pontiff); build for decay-awareness, not for a found-forever edge.
- **Diversification across factors is the real edge:** low pairwise correlation between momentum/value/low-vol/quality means the *portfolio of strategies* has a better IR than any member — the ensemble allocator is the mechanism that harvests this.
- **The long-only haircut (the number that sets §6's power math):** transaction costs, the missing short leg, large-cap-only universe, and capacity all compound; a realistic expectation for a well-implemented long-only S&P 500 factor tilt is **1–3% annualized β-adjusted alpha** — some years negative. Every screen, gate, and expectation in this system is calibrated to that prize.

---

## 3. Sizing, guardrails & portfolio construction (mathematics)

### 3.1 Covariance (D42)
**Ledoit–Wolf shrinkage** toward the constant-correlation target, estimated on 252 trading days of daily returns over the active name set (held + wish-listed across accounts). Fallback on numerical failure: EWMA (λ=0.97) single-index model. Estimator, window, and shrink intensity logged per run. Consumers: inverse-vol weights' vol inputs, the **correlation-aware heat guardrail** (cap on √(wᵀΣw), annualized, per account), momentum's vol-targeting overlay, and **Betting-Against-Beta's beta derivation (catalog §6.7, v1.9.23)** — `cov(s, mkt)/var(mkt)` off the same shrunk solve, which requires the market proxy's return series in the estimation set.

### 3.2 Inverse-vol sizing (default)
`w_i ∝ 1/σ_i`, normalized, then scaled so predicted portfolio vol (from Σ) ≤ the account's target (default 12% ann.); position cap and guardrails applied after. Deterministic, estimable from day one, correlation-aware at the portfolio step — everything Kelly-at-Phase-3 is not (D32).

### 3.3 Cost & fill model (D43)
Per fill: `cost = commission (default $0) + half_spread(bucket) + impact`, with
`impact = k · σ_daily · √(Q / ADV₂₁)`, defaults `k = 0.1`, half-spread buckets {mega: 1bp, large: 2.5bp, other: 5bp} by ADV-proxy, **participation cap Q ≤ 2% of ADV₂₁** (excess rejected + logged to the capacity readout). All coefficients config; the **cost-model version stamps every trade row**, so a later recalibration never silently rewrites history. Fills occur at next open on the raw price; the model's costs are added on top (D30).

### 3.4 Guardrails (fail closed)
Min score · max position weight · correlation-aware heat (Σ-based) · max concurrent positions · re-entry cooldown · PIT regime halts · participation cap · drawdown circuit-breaker (account-level). Any missing input ⇒ reject the order, log the reason. Guardrail rejections are surfaced on the Risk screen — a guardrail that never fires is untested, one that always fires is mis-set.

### 3.5 Ensemble allocator — full specification (v1.8, D51)

This — not promotion — is the primary "improves over time" mechanism, because small reversible tilts are the honest action under §6's power reality. The v6 one-paragraph sketch is replaced by the following complete spec; every symbol is a quantity the gate already computes.

**Inputs** per promotable strategy *i* at each evaluation: forward net β-adjusted alpha `α̂_i` and its Newey–West standard error `se_i` (both from the §1.1–1.2 machinery), current monitor status, current gate verdict vs Live.

**Step 1 — shrinkage (James–Stein in spirit):**
```
ᾱ    = cross-sectional mean of α̂ over the promotable roster
τ    = max(cross-sectional stdev of α̂, Allocator.TauMinPctAlpha)
w_i  = τ² / (τ² + se_i²)                # 0..1: confidence in this strategy's own estimate
α̃_i = w_i·α̂_i + (1−w_i)·ᾱ            # short/noisy track ⇒ w→0 ⇒ shrink to the roster mean
```
A strategy with three months of track has a huge `se_i` and lands at ~equal weight — which is exactly the honest action under §6.

**Step 2 — weight map:** `t_i = softmax(α̃_i / λ)`, temperature `λ = Allocator.TemperaturePctAlpha` (default 2.0%/yr — a 2%/yr shrunk-alpha gap moves relative weight by ~e-fold before clamps).

**Step 3 — clamps, applied in this order:**
1. floor/ceiling: `WeightFloorPct ≤ t_i ≤ WeightCeilingPct` (defaults 5% / 60%);
2. `TooEarly` vs Live ⇒ `|t_i − current_i| ≤ TooEarlyTiltCapPts`;
3. Suspect ⇒ `t_i = current_i × (1 − SuspectDecayPctPerEval)` — decay only, never a new tilt;
4. banded movement: only move if `|t_i − current_i| > BandPts`, and then **to the band edge**, not to `t_i`;
5. renormalize. Baselines and control populations never receive weight.

**Persistence (NFR-2):** every evaluation writes the full input vector `{α̂, se, α̃, w, target, applied, clamps_bound}` per strategy to `allocation_log` — every weight on screen (UX-9) reconstructs from the log.

**Tests (FR-27):** short-track ⇒ ~equal weight; Suspect decays; TooEarly cap binds; sub-band targets don't move; reconstruction from the log.

---

## 4. The AI seats — the three-seat design (D79-D82; full spec in MASTER §23)

> This section is the economics- and value-model view. The **authoritative, buildable spec for the AI seats is MASTER_DESIGN_v1.9 §23** — build against that; the summary here exists so the improvement-and-economics story is complete in one place. The earlier "assistant-only" LLM framing is superseded by the seats below.

### 4.1 The three seats (D79)
The AI occupies exactly three seats, and the arena prices each one the same way it prices any strategy (golden rule 32: no AI output is ever an input to a component that judges AI outputs).

1. **Researcher (primary).** Reads the locally stored, D80-compressed evidence — verdicts, separation states, factor attribution, monitor statuses, regime episodes, closed journal outcomes, the trials ledger — and proposes the next pre-registered hypotheses and forks (MASTER §23.4). This is the generative step that turns the improvement loop into an actual loop: the AI proposes, the **operator** pre-registers (rule 30), and every proposal must cite parent evidence or it is refused. Ranged by a fork budget (`Research.ForkBudgetPerYear`, default 6) so self-improvement rations its own significance spend.
2. **Contestant.** An LLM decision layer as a first-class `IModel`: a deterministic local pre-filter hands it a ≤25-name shortlist, it returns scores, and it trades its own account under every existing rail (costs, guardrails, populations, gate, monitor). It never runs without a **mechanics-identical no-LLM twin** — same pre-filter, breadth, sizing, exits, costs, seed. The paired daily difference against that twin is the headline number and the fastest honest "does the AI add alpha?" verdict the lab can produce (M.1 pairing). This is the seat that makes the LLM a stock-scorer — and it is only allowed to be one because the twin makes its edge falsifiable rather than hidden.
3. **Advisor (deferred, opt-in).** LLM allocation advice, evaluated as a paired A/B against the D51 allocator, never wired to applied weights until it has priced positive. Deferred because it is the weakest bet (the statistical allocator is a strong incumbent), it depends on the other two existing to be measurable, and it is the seat closest to capital, so it carries the most risk if wired in early. Nothing else in the design depends on it.

The seats are separable: the researcher improves the lab even if the contestant prices at zero. The daily D46 market-level news read and its with/without A/B continue unchanged and are separate from the seats.

### 4.2 Economics
- **Message Batches API** for all scheduled reads (½ price; the job is asynchronous by nature).
- **Prompt caching** on the static instruction block; only the day's news is fresh tokens.
- **Per-task model tiering (config):** extraction/classification → cheap fast model; briefs/skeptic/regime narrative → stronger model.
- **News ingestion budget — the real token lever:** `INewsProvider` (EODHD news) enforces, before any token is spent: relevance filter (universe symbols + macro tags) → title-hash dedupe → cap 25 articles/read → truncate each to 2,000 chars post-extraction. The budget, not the call count, bounds cost.
- **Hard caps (D24) unchanged:** daily token/call/cost ceilings; cache hits free; degradation order (held names first → cached → neutral fallback); never a blackout.
- **Forward-only (D16):** no LLM output ever enters a backtest or replay; `analysis_cache` rows are keyed `(prompt_hash, model, date)` and replay simply has none.

---

## 5. Arena Replay (D37) — judging the machinery, never the strategies

**What it is:** the entire pipeline — funnel, ledger (full corporate-action semantics), populations, gate, monitor, allocator — executed over a historical window on a simulated clock, reading versioned bars at historically-plausible watermarks and **as-of membership** reconstructed from EODHD historical constituents.

**Quarantine rules (absolute):**
- every artifact stamped `run_kind='replay'`; stored in replay scope; **never joined into forward views** (query-layer enforcement + a test);
- **never a promotion input, never co-plotted with forward numbers** (distinct badge if ever shown);
- no LLM calls inside replay (D16) — the analysis feature is simply absent;
- replay trials counted in a **separate** registry column (they deflate nothing forward).

**Its two jobs:**
1. **Machinery validation:** over ≥ 15 simulated years — random populations promoted ≤ chance; gross population alpha bands centered on zero; the three **D64 plants** (MASTER §20.9: edge, no-edge, anti-predictive — regime-conditional, autocorrelated, ≥ 50 seeds each) behave as designed: the edge plant is detected, the **anti-predictive** plant reaches Suspect/auto-retire fast (the anti-predictive detection-speed KPI, D63), and the **no-edge** plant stays mid-band and earns its `IndistinguishableFromRandom` state at the honest cadence (days-to-statement KPI, D63 — a no-edge plant going Suspect beyond the false-alarm rate is a calibration bug); leakage suite green under replay. The mandatory **plant-sensitivity check** (naive constant-drift vs realistic plant) is part of this job's output.
2. **Threshold calibration:** the monitor's config defaults (`suspect_below`, auto-retire patience, PSI cut-offs, S3 percentile bands) are tuned against replay distributions and the calibration report is archived — because otherwise those thresholds are guesses that forward operation would take years to falsify. Calibrated thresholds are then **frozen** for forward use (changing them later is itself a logged, versioned event).

**What it is not:** evidence that any strategy works. Pre-launch data carries residual survivorship bias (§13.4 of the master doc) and replay Sharpe is expected to flatter — which is fine, because replay's verdicts are about the *lab's* behavior, not the strategies' merit. The v5 walk-forward backtest engine (strategy seeding, S1's backtest reference) is the restricted special case of this machinery.

---

## 6. The power reality, in full

The paired-test MDE (§1.2) as a function of track length and pairing tightness, at 95%/80%:

| σ_LR (daily, paired) | MDE @ 6m | MDE @ 1y | MDE @ 3y | Years to detect 2%/yr |
|---:|---:|---:|---:|---:|
| 0.40% (loose pairing) | 25.1% | 17.8% | 10.3% | ~79 |
| 0.20% (good pairing) | 12.6% | 8.9% | 5.1% | ~20 |
| 0.10% (tight pairing — one-component difference) | 6.3% | 4.4% | 2.6% | ~5 |
| 0.05% (near-twin accounts, e.g. the AI contestant vs its no-LLM twin — MASTER §23.3) | 3.1% | 2.2% | 1.3% | ~1.2 |

Three design consequences, restated once, here, so they are never re-litigated screen by screen:
1. **`TooEarly` dominates by design.** The gate refusing to crown winners for years is the system working.
2. **Name the fast products correctly (D63).** The population channel's fast product is the **`IndistinguishableFromRandom`** statement: an edgeless strategy pays the same costs as its turnover-matched controls and sits at its band's **median** indefinitely, so it is never *falsified* there — it is declared inseparable from luck, which arrives in months and is real knowledge. Genuine fast kills come from exactly two channels: the **trade-level expectancy track** (tests against zero — a fast strategy with negative net expectancy is dead in a few hundred trades) and **anti-predictive** S3/S6 breaches (performing worse than random). Grade the lab on the §1.2 KPIs as re-split by D63.
3. **Comparisons should be engineered for tightness.** The table's bottom rows are where verdicts live on human timescales — which is why the AI contestant and its no-LLM twin share everything but the LLM decision layer (MASTER §23.3), why parameter siblings differ in one parameter, and why the allocator tilts continuously instead of waiting for significance.

---

*Design Improvements v1.9. The metrics are honest about what they can detect, the costs are parameterized enough to be falsifiable, the factor expectations are haircut to long-only reality, the LLM is priced rather than presumed, and the machinery itself is validated in a quarantined replay before it is trusted with years of your forward data. Research/paper-trading only — not investment advice.*
