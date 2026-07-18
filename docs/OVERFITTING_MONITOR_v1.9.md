# Overfitting Monitor — v1.9 Specification

*Companion to MASTER_DESIGN_v1.9 (§11) and DESIGN_IMPROVEMENTS_v1.9. The monitor is the system's institutionalized skepticism: eight signals, three statuses, and hard wiring into the promotion gate and allocator so that a suspicious strategy cannot be promoted no matter how good its P&L looks.*

> Research/paper-trading only. Not investment advice.

---

## 0. What changed in v6

1. **S3 rewritten as a population percentile-rank test (D36):** the strategy is ranked within its matched random **population** (M≈200) instead of two-sample-tested against a single twin whose own luck contaminated the verdict.
2. **The MDE is Newey–West-corrected (D48):** Appendix C now uses the long-run variance of the paired difference series; the i.i.d. formula is retained only as the illustrative upper-bound-of-optimism.
3. **Thresholds are calibrated, not guessed (D37):** every numeric default below is a *starting* value to be calibrated in Arena Replay before forward trust; the calibration report is part of Phase 4's Definition of Done, and calibrated values are then frozen (changes are logged, versioned events).
4. **S1's backtest reference comes from Arena Replay's seeding mode**, with the survivorship caveat stated on the panel.
5. **S8 gains the trade-track input (D44):** a sharp contradiction between the daily-alpha track and the per-trade expectancy track is cross-metric divergence.
6. **Status displays reference regime episode counts (D45)** wherever a signal is regime-conditioned.
7. **v1.8 (D56): S3 thresholds become track-length-aware trajectories.** Flat percentile cuts contradicted the master doc's own power math — a genuine 1–3%/yr edge sits in the 60th–90th percentile of its noise band for years, so flat 95/80 cuts would have made permanent Warning/Suspect the steady state for every honest strategy. Phase-4 calibration now produces `P_noise(t)` / `P_edge(t)` curves and S3 judges against them (see S3 below and MASTER §20.7). The retained *pre-calibration* anchors are correspondingly Healthy ≥ 95th / **Suspect < 25th** — the Suspect anchor is the anti-predictive tail (D63), so a merely edgeless strategy sitting near its band's median never trips it (Appendix A).
8. **v1.9 (D63/D64): the plants under the curves are specified, and the verdict economics are corrected.** Every calibrated curve now derives from the **D64 plants** (MASTER §20.9): regime-conditional, autocorrelated edge injection (never constant drift), an explicit **anti-predictive plant**, ≥50 seeds per plant with archived bands, and a mandatory naive-vs-realistic **plant-sensitivity check** in the calibration report. And per **D63**: the population channel cannot falsify a merely edgeless strategy (it sits at the median of its cost-matched band) — non-separation is surfaced by the `IndistinguishableFromRandom` separation state, computed in the D58 read-models (MASTER §20.8), **not** by a monitor status; fast kills belong to the trade-level track and to anti-predictive breaches only.
8½. **v1.9.7 (findings 113–114): the calibration report gains two system-level outputs.** The **edge-plant survival** fraction at 5y/10y of simulated track (Phase-4 DoD floor `Replay.EdgePlantSurvivalFloor5y`, default 0.90; every edge-plant auto-retire logged with its triggering signal; a floor failure recalibrates S6's *patience*, never the plant — the lab must not auto-retire its own honest small winners), and the **joint any-signal false-alarm** fraction for no-edge plants (bound `Replay.JointFalseAlarmMaxFrac`, per-signal contribution archived) — turning "thresholds are calibrated" from a per-signal claim into a system-level one. *(Numbered 8½ so the existing item numbering below is undisturbed.)* **v1.9.8 addendum (C-2):** because S3's Healthy ≥ 95th / Suspect < 25th anchors ride a 200-member empirical distribution, the 95th-percentile cut itself carries binomial sampling noise (~±1.5 members at M=200). No design change is needed — the joint any-signal bound (114) already covers the family-wise consequence — but the calibration report **archives the percentile-threshold's sampling band** alongside the curves, so a future "should M be 500?" question has its evidence waiting rather than requiring a re-run.

---

## 1. Why a monitor at all

Forward paper trading removes *backtest* overfitting but not *selection* overfitting: run enough strategies (or fork enough variants) and one looks great by chance; the researcher then "believes" it. The monitor exists to make that failure mode mechanically hard: it watches every strategy continuously, scores eight orthogonal symptoms of self-deception, and holds veto power over promotion. It is wired to consequences, not dashboards.

**Meta-guard (the monitor's own Goodhart defense):** live parameters — including exits, sizing mode, and selection params — are **frozen** (D17). Any change forks a new strategy and increments `trials_registry`. `TooEarly` is not an invitation to re-run the gate tomorrow: evaluations happen on the configured cadence (default 21 days), full stop.

---

## 2. Statuses and wiring

| Status | Meaning | Hard consequences |
|---|---|---|
| **Healthy** | no signal elevated | eligible for promotion & full allocator tilt |
| **Warning** | ≥1 signal elevated | promotion allowed only with explicit operator acknowledgment (logged); allocator tilt capped |
| **Suspect** | ≥1 signal critical, or ≥3 elevated | **promotion vetoed regardless of P&L**; allocator freezes/decays weight; strategy flagged on every screen |

Additional wiring:
- A head-to-head gap **inside the pair's NW-MDE ⇒ verdict `TooEarly`** (not a status — a gate outcome; the common case per DESIGN_IMPROVEMENTS_v1.9 §6).
- **Auto-retire:** Suspect on S6 (edge decay) at 4 consecutive evaluations ⇒ the strategy is retired to observation-only (its account keeps running for the record; it leaves the promotable pool). Patience value calibrated in replay — **against the v1.9.7 edge-plant survival floor (finding 113): patience is set so ≥ `Replay.EdgePlantSurvivalFloor5y` of D64 edge plants survive at 5y; a lumpy 2%/yr edge spends long stretches mid-band by design, and auto-retiring it would be the lab killing its own winners.** **v1.9.23 addendum:** the two new low-turnover strategies (Betting-Against-Beta — Low-Vol's sibling — and Time-Series Momentum, both monthly cadence) spend even longer mid-band **by construction** — precisely the honest-small-winner case the survival floor protects — so the floor calibration must include a **low-turnover plant**, not only the default cadence.
- All transitions persisted to `overfitting_status` with the triggering signal snapshot; the GUI badge links to the evidence.

---

## 3. The eight signals

Defaults marked ⚙ are replay-calibrated before forward trust (v6 §0.3).

### S1 — Backtest-vs-forward degradation
Compare the strategy's seeding-replay Sharpe/alpha (from Arena Replay's walk-forward seeding mode) with its forward values over the same horizon length. Expected: forward ≈ 50–70% of replay (decay + residual pre-launch survivorship inflates the replay side — stated on the panel; the signal therefore reads *stricter than truth*, which is the conservative direction). ⚙ Elevated: forward < 40% of replay; critical: forward ≤ 0 while replay was strongly positive, sustained one full evaluation window. Strategies without a seeding replay (e.g. randoms) skip S1 — **and the AI contestant skips it by the same mechanism (v1.9.23, §3½): D81 makes it forward-only and the replay engine refuses it by construction (`FX-ContestantReplayRefused`), so it has no replay side to degrade against.**

### S2 — Deflated Sharpe
Sharpe deflated by the honest trials count (`trials_registry`, incl. forks, retrains, siblings; replay trials excluded — separate column). ⚙ Elevated: deflated Sharpe < 0 while raw Sharpe > 0.5 (the gap is pure selection); critical: the strategy's *rank among candidates* inverts under deflation (its standing is a trials artifact).

### S3 — Separation from the random population (v6, D36)
**Percentile rank of forward net β-adjusted alpha (and of the paired-difference statistic vs the population median) within the matched population's distribution**, recomputed each evaluation.
⚙ **v1.8 (D56) — trajectory thresholds:** Phase-4 replay calibration produces two percentile-vs-track-length curves — `P_noise(t)`, the envelope below which a planted **no-edge** strategy falls at the configured false-alarm rate, and `P_edge(t)`, the **median trajectory of a planted 2%/yr edge** — all plants per **D64** (MASTER §20.9): regime-conditional, autocorrelated, ≥50 seeds each, with the plant-sensitivity check archived. At track length t: **Suspect** below `P_noise(t)` sustained (anti-predictive detection stays fast at every horizon) · **Healthy** above `P_edge(t)` sustained · **Warning** between. The flat anchors (Healthy ≥ 95 / Suspect < 25 — the anti-predictive tail, D63) apply only until calibration; calibrated curves land as versioned config rows with the archived report. The panel plots the strategy's percentile *path* against both curves, so "too early for this band to mean anything" is visible rather than punished.
Panel shows the strategy's dot on the population's histogram plus the 5–95% band on the equity chart. The **cost-free** population never serves as an S3 comparator (display-only). Acceptance property (permanent, replay-exercised): across the population itself, promotions occur ≤ chance and the percentile of a no-edge synthetic strategy is uniform over time.
**What S3 cannot do (D63):** a merely edgeless strategy pays the same costs as its turnover-matched controls and hovers at its band's **median** — S3 never flags it, and under the null it breaches `P_noise(t)` only at the false-alarm rate. Non-separation is therefore *not* a monitor status: it is surfaced by the D63 **separation state** (`IndistinguishableFromRandom` chip, MASTER §20.8), computed in the read-models from the same percentile rows S3 produces. The Suspect fixture in tests must be the **anti-predictive plant**, never the no-edge plant.

### S4 — Parameter robustness
Local perturbation scan around the frozen parameters (±1–2 steps per axis, **including exit params** — `exitRank`, `maxHoldDays`, `exitChannel`), run in replay's seeding mode, stored in `parameter_scans`. A real edge is a plateau; a spike is noise. ⚙ Elevated: > 40% of neighbors lose > half the alpha; critical: the frozen point is a strict local maximum with cliff edges. (Scan is diagnostic; it never tunes — D17.) **N/A for the AI contestant (v1.9.23, §3½):** its frozen policy is prompt text, model id, pack recipe, and shortlist size (D80) — none has a ±1–2-step numeric neighbourhood to scan; the falsification role S4 plays for mechanical strategies is carried for the contestant by the twin A/B instead. Marked N/A so no implementer invents a perturbation.

### S5 — Feature & regime PSI (population stability)
PSI of each input feature's live distribution vs its `feature_baselines` snapshot, and of the regime-label mix. ⚙ Elevated: PSI > 0.10 on a core feature; critical: > 0.25 (the world the strategy was built for has moved). Regime-mix rows display episode counts (D45).

### S6 — Rolling edge decay
Rolling-window (63d) net alpha trend vs the strategy's own history **and vs its population band**: decaying toward/into the band is the signature of a dead edge. ⚙ Elevated: two consecutive windows inside the band's central 50%; critical: three, or a negative rolling alpha t < −1 sustained. Four consecutive Suspect evaluations ⇒ auto-retire (§2). **Scope note (D63):** S6 catches decay *from* an apparent edge and anti-predictive drift; a strategy that has simply never separated is the separation state's job (MASTER §20.8), not S6's — do not tune S6 to "catch" mid-band lifers.

### S7 — Calibration drift
For strategies exposing probabilities (Kelly variants, learned blends): Brier score / reliability-curve drift vs the calibration baseline over the declared horizon. ⚙ Elevated: Brier degradation > 20%; critical: reliability slope sign-flip. Skipped for strategies without probability semantics.

### S8 — Cross-metric divergence
Metrics that should co-move but don't: net alpha up while expectancy down (cost story changed?); Sharpe up while population percentile down (luck vs the null); **daily-alpha track and trade-level track in sharp contradiction (v6, D44)**; equity up while max-drawdown-adjusted rank collapses; **the contestant-vs-twin paired difference contradicting the standalone percentile path (v1.9.23, §3½)**. ⚙ Elevated: any one divergence sustained an evaluation window; critical: two simultaneously. S8 is the tripwire for "the number you optimized got better while the thing it measured got worse."

### 3½ — AI-seat handling (v1.9.23; the contestant under the eight signals)

The AI pass (D79–D82) made the contestant a first-class `IModel` the monitor must score, but three signals assume machinery the seat does not have. Collected here so the treatment is stated once (cross-refs: MASTER §23.3 — the twin; D81 — forward-only; D83 — the factor series' dual role):

- **S1 — skipped.** The contestant is forward-only (D81) and the replay engine refuses it by construction (`FX-ContestantReplayRefused`), so it has no seeding-replay side to degrade against. Same skip mechanism S1 already applies to strategies without a seeding replay.
- **S4 — N/A.** S4 perturbs frozen *numeric* parameters; the contestant's frozen policy (prompt text, model id, pack recipe, shortlist size — D80) has no ±1–2-step neighbourhood. The twin A/B carries the falsification role instead.
- **S8 — gains the twin input.** The contestant-vs-twin paired difference (M.1) is an S8 divergence input: a paired difference that contradicts the standalone percentile path is exactly the "optimized number up, measured thing down" tripwire. The twin therefore does double duty — the promotion signal *and* a monitor input.

All other signals (S2 deflated Sharpe, S3 population percentile, S5 PSI, S6 edge decay, S7 calibration drift where probabilities exist) apply to the contestant unchanged — the seat is priced by the same arena as every other strategy (golden rule 32 governs what may *judge* it, not what it is judged *by*).

---

## 4. Evaluation cadence & data flow

Runs at the configured cadence (default 21 days) inside the daily orchestrator's post-close sequence, and on demand (read-only) from the GUI. Inputs: `equity_curve`, `trades`, `trade_evidence`, `control_equity` (population), `factor_returns` — **dual-role since D83 (v1.9.23): attribution diagnostic *and* residual-momentum signal input, so S5's PSI baseline and S8's cost-story reasoning must read it as a signal feed, not only a diagnostic one** — `feature_baselines`, `parameter_scans`, `trials_registry`, `regime_episodes`. Outputs: one `overfitting_checks` row per signal per strategy per evaluation (value, threshold snapshot, status contribution) and an `overfitting_status` transition row when the aggregate changes. Everything the badge claims is reconstructible from those rows (audit rule).

---

## 5. Interpretation discipline (what the monitor is *not*)

- Not a strategy-improvement tool: a Suspect flag is information for the *operator's beliefs*, never a prompt to tune the live strategy until green (that forks a new candidate, by rule).
- Not a substitute for the MDE: a Healthy strategy inside `TooEarly` is still unjudged.
- Not regime-blind: several signals condition on regime labels; those rows inherit D45's episode-count honesty ("S5 regime-mix shift — observed over n=1 bear episode").
- Not self-exempt: the monitor's own thresholds are versioned config; every change is a logged event with a reason, and recalibration re-runs the replay validation suite.

---

## Appendix A — Threshold config (starting values, ⚙ replay-calibrated)

*Where a key here also exists in CONFIG_REFERENCE_v1.9 (gate/verdicts/calibration blocks), CONFIG_REFERENCE is authoritative — this appendix mirrors values for reading convenience.*

```yaml
monitor:
  evaluation_cadence_days: 21
  s1: { elevated_forward_fraction: 0.40, critical_sustain_windows: 1 }
  s2: { elevated_gap_raw_sharpe: 0.5 }
  s3: { # flat anchors used ONLY until Phase-4 calibration (D56)
        healthy_percentile_anchor: 95,
        suspect_below_anchor: 25,   # anti-predictive tail (D63): a no-edge strategy sits ~50th pct and must NOT trip Suspect; v5's original 0.25 restored

        # post-calibration: piecewise-linear curves over track-length days,
        # written as versioned config rows by the Phase-4 calibration job
        p_edge_curve: "calibrated:phase4", p_noise_curve: "calibrated:phase4",
        false_alarm_rate: 0.05,
        population_size: 200, cost_free_population_size: 50 }
  s4: { elevated_neighbor_loss_frac: 0.40 }
  s5: { psi_elevated: 0.10, psi_critical: 0.25 }
  s6: { window_days: 63, band_central_frac: 0.50,
        elevated_windows: 2, critical_windows: 3, auto_retire_evals: 4 }
  s7: { brier_degradation_elevated: 0.20 }
  s8: { elevated_sustain_windows: 1, critical_simultaneous: 2 }
  mde: { confidence: 0.95, power: 0.80, nw_lag_cap_days: 21 }
  calibration:                      # D64 — the plants under P_noise(t)/P_edge(t)
    plant:
      alpha_annual_pct: 2.0         # edge plant target (§1.1's realistic prize)
      anti_alpha_annual_pct: -2.0   # anti-predictive plant (the Suspect fixture)
      active_day_frac: 0.25         # lumpy delivery — edge arrives in streaks
      persistence_phi: 0.9          # AR(1)-style run persistence, scaled to horizon
      regime_multipliers: { bull: 1.25, bear: 0.5 }   # renormalized to target
      seeds_per_plant: 50           # curves are multi-seed medians with 25–75% bands
      sensitivity_max_gap_pts: 10   # naive-vs-realistic P_edge divergence trigger
  verdicts:                         # D63 — separation state (computed in read-models)
    separation_min_track_days: 252
    separation_band_central_frac: 0.50
```

## Appendix B — Trials registry rules

Every counted trial: a new strategy id (any fork, any parameter sibling, any retrain of a learned blend, any sizing-mode variant). Not counted forward: replay trials (separate column), the permanent baselines, and the random populations themselves. The registry is append-only and its count feeds S2's deflation for *every* strategy — one researcher's trial spends everyone's significance, which is the point.

## Appendix C — MDE derivation (Newey–West corrected, D48)

Paired daily active-return difference `d_t = a_t^{(A)} − a_t^{(B)}`, T observations.

```
γ_k    = autocovariance of d_t at lag k
σ²_LR  = γ₀ + 2·Σ_{k=1..L} (1 − k/(L+1))·γ_k      # Bartlett kernel
L      = min(2 × max(horizon_A, horizon_B), 21)

MDE_ann = (z_{1−α/2} + z_pow) · σ_LR · 252 / √T   # 2.8·σ_LR·252/√T at defaults
```

Worked example (illustrative): T = 84 days, σ_LR = 0.7%/day ⇒ MDE ≈ 2.8·0.007·252/√84 ≈ **54% annualized** — nothing is judgeable at 84 days between loosely-paired accounts, and the panel must say so. With tight pairing σ_LR = 0.1%: MDE(1y) ≈ 4.4%, MDE(3y) ≈ 2.6% — the regime in which real verdicts eventually live (full sensitivity table: DESIGN_IMPROVEMENTS_v1.9 §6). The uncorrected i.i.d. formula (v5) is retained in comments as the lower bound; using it operationally is a bug class the leakage/honesty suite now tests for (a synthetic AR(1) difference series must yield a larger MDE than its i.i.d. σ would imply).

---

*Overfitting Monitor v1.9. Eight orthogonal symptoms of self-deception, thresholds calibrated in quarantined replay before they are trusted, a population of matched randoms as the empirical null, and veto power that no P&L can override. Research/paper-trading only — not investment advice.*
