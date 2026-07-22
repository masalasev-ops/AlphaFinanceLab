# Strategy Catalog — v1.9 `IModel` Specifications

*Companion to MASTER_DESIGN_v1.9 (§6, §9) and BUILD_AND_PROMPTS_v1.9 (Phases 2, 3, 6, 8). Defines the concrete strategies that plug into the `IModel` socket. Each strategy is one "brain"; the Live-vs-Candidate loop treats each as a black box and judges it on forward paper P&L (beta-adjusted alpha, net of costs, ranked against its random control population).*

> **Paper-trading / research only. Not investment advice.** These are well-studied *factor* families chosen to be diverse and correctly implementable — **not** predictions that any will be profitable. Expect many to lose to buy-and-hold after costs; that is the base rate and the system is designed to reveal it, not hide it. Per MASTER_DESIGN_v1.9 §1.1, also expect the honest verdict on most head-to-heads to be **`TooEarly`** for a long time — the fast, reliable products of this catalog are **`IndistinguishableFromRandom`** statements (D63: an edgeless strategy sits at the median of its cost-matched population band, so it is declared inseparable from luck rather than falsified), fast trade-level kills of negative-expectancy strategies (D44), and anti-predictive retirements — not crowned winners.

---

## 0. What changed in v6

- **Random controls become populations (D36):** `RandomModel` is now instantiated as **M seeded members per cadence family** (default M=200), producing an empirical null *distribution* per family; Monitor S3 is a percentile-rank test and every chart carries the population's 5–95% band (§5.2).
- **All identity is `security_id` (D39):** `ScoreUniverseAsync` receives and returns security ids; tickers are display aliases. Every strategy inherits the shared corporate-action semantics (ticker changes are non-events; mergers/spin-offs are forced ledger events — see §11 and MASTER_DESIGN_v1.9 §13.6). A spin-off receipt lands in the owning account and is managed by that strategy's `ExitPolicy` or the configured liquidation rule — never silently dropped or mispriced.
- **Sector data source named (D35):** `LowVolModel`'s sector cap (and the Risk screen's concentration panel) consume **EODHD sector/industry classification**; the cap is no longer specified against an unsourced field.
- **Trade-level evidence track (D44):** the fast mean-reversion sibling (and breakout) accrue a per-trade expectancy test with a moving-block bootstrap and its own MDE, in parallel with the daily-alpha track — matching verdict speed to each family's natural sample rate (§6.2, §9.1).
- **Fundamentals gate operationalized (D33 + D35):** Phase 8 remains contingent, but **EODHD Fundamentals is now the named first candidate**, with the concrete **PIT validation protocol** it must pass written into §7.0. The gate is unchanged: no pass, no Phase 8.
- **Cost realism sharpened (D43):** every strategy's "gotchas" on turnover now reference the parameterized spread + √impact model and the 2%-ADV participation cap; capacity rejections are logged per strategy.
- Everything else — horizons/exits on the contract, the zero-score invariant, momentum's hysteresis, MR's explicit exits, low-vol's 252-day window — carries forward from v5 unchanged.
- **v1.8 revision:** the equal-weight benchmark convention is pinned (§5.1), and the control populations carry an explicit **compute-vectorization requirement** (§5.2) — "no API calls" was never "no compute."
- **v1.8 (D52):** every strategy created through CandidateFactory is **pre-registered** — a linked, immutable hypothesis (claim + confirm/refute metric + evidence window) or an explicit `unregistered` flag rendered permanently on its card (MASTER §20.3, Golden Rule 30).

---

## 1. Purpose & how this fits

The daily funnel (MASTER_DESIGN_v1.9 §6) is: **Stage 1 eligibility (shared)** → **Stage 2 scoring (per-strategy)** → Stage 3 selection (shared code, per-strategy params, score>0 invariant) → **Stage 4 portfolio (shared mechanics, per-strategy `ExitPolicy` + forced corporate-action events)** → Stage 5 sizing/safety → Stage 6 orders. This catalog specifies **Stage 2 and each strategy's exit/horizon declaration**. Everything else is shared plumbing.

Each strategy diversifies on *theory*, so candidates don't collapse into clones. The starting roster deliberately pairs **opposites** (momentum vs mean-reversion) so that whatever the regime, something in the arena tends to fit.

---

## 2. The `IModel` contract

```csharp
public interface IModel
{
    string Id { get; }                 // stable identity, e.g. "momentum:L126:K21:N40"
    StrategyConfig Config { get; }      // serialized params + seed (persisted in `strategies`)

    /// Every strategy declares how long it intends to hold and when it exits.
    /// The calibration target P(up over horizon) and any Kelly payoff estimate `b`
    /// are defined over HoldingHorizon; Stage 4 consults ExitPolicy for closes.
    HoldingHorizon Horizon { get; }     // e.g. Days(10), ToRankExit, ToNextRebalance
    ExitPolicy Exits { get; }           // declarative: rank-buffer / target-or-time-stop / scheduled / channel

    /// Score the eligible universe for one decision date.
    /// CONTRACT:
    ///  - MUST use only data with timestamp <= asOf, resolved at the run's data
    ///    watermark (point-in-time; no look-ahead; D40).
    ///  - Keys are SECURITY IDS (D39), never raw tickers.
    ///  - Returns a signal in [0,1] per security: higher = more bullish.
    ///  - Omit a security (or return null) if it lacks enough history/data.
    ///  - MUST be deterministic given (inputs, watermark, Config.Seed).
    Task<IReadOnlyDictionary<SecurityId, double>> ScoreUniverseAsync(
        IReadOnlyList<SecurityId> eligible,
        DateOnly asOf,
        IFeatureView features);        // read-only, asOf- and watermark-bounded accessor
}
```

**Why score the whole universe at once:** cross-sectional strategies rank names *against each other*, which needs the full set.

**Score semantics — `[0,1]` signal, not yet a probability.** Sizing that needs a probability (the Kelly variant) gets it through a per-strategy calibration map (§9). The default inverse-vol sizer needs no probability.

**Point-in-time is enforced by `IFeatureView`.** It only exposes data `<= asOf` at the run's watermark. Strategies never touch raw stores directly.

**Scoring parity with the Signal Library (D91, Phase 4.5).** Cross-sectional IModels wrap the same `ISignal` implementations the Signal Library grades, so the library's rank-IC record measures the exact deployed formula, not a lookalike. Parity is pinned by `FX-SignalParity` (lands with Phase 6): scorer-output equality between the library path and the strategy path on the same day and pool. The library is descriptive only; nothing in it feeds Stage 2 or any other funnel stage (MASTER §24).

**`ExitPolicy` shapes.** Declarative, serialized in `StrategyConfig`, executed by shared Stage 4 code:
- `RankBuffer(exitRank)` — exit when cross-sectional rank falls below `exitRank` (momentum).
- `TargetOrTimeStop(exitCondition, maxHoldDays)` — exit on the reversion condition or the time stop (mean reversion).
- `ScheduledRebalance(everyNDays)` — hold to the next rebalance (low-vol; Value/Quality).
- `ChannelExit(exitChannel)` — exit on a close below the N-day low channel (breakout).
- `SignFlip(lookback)` — exit when the position's own trailing excess return over `lookback` turns negative (time-series momentum's trend-flip sibling, §6.6).
- `Never` — buy-and-hold.

*Phasing note (v1.9.23):* `TargetOrTimeStop`, `ChannelExit`, and `SignFlip` are declared here but not executable before Phase 6 — the Stage-4 executor **refuses each by name**, citing its Phase-6 owner (the `ExitPolicyExecutor` precedent already in place for the first two; `SignFlip` joins that refusal set when its record lands in code — this pass declares the shape only).

Stage 4 semantics: **the wish list opens/adds; only the `ExitPolicy` closes** — plus forced events: guardrail circuit-breakers, and the corporate-action semantics of MASTER_DESIGN_v1.9 §13.6 (delist force-exit, merger cash-out/conversion, spin-off receipt).

---

## 3. Shared selection stage (Stage 3) — same code for every strategy

- **Invariant: a name with `score == 0` or `score < minScore` is never selectable.** Sparse days ⇒ short wish lists, more cash. No padding, ever. (Unit-tested.)
- **Top-N:** keep the N highest-scored names passing the invariant (momentum default N = 40).
- **Threshold:** keep names with score ≥ `minScore` (default 0.60), capped at `maxConcurrent`. Preferred for sparse-signal strategies.
- **AllPositive (v1.9.23):** keep **every** name passing the invariant, capped at `maxConcurrent` — for absolute-signal strategies where a cross-sectional rank is meaningless (time-series momentum, §6.6).
- **Rank hysteresis:** for Top-N strategies, entries at rank ≤ N; exits governed by `RankBuffer(exitRank)` (§6.1).
- **Deterministic tie-break (D40; v1.9.23):** equal scores at a selection boundary — routine for binary scorers (§6.4 Breakout, §6.6 in sign mode) and for any capped mode — break by a **seeded stable order derived from (`Config.Seed`, `security_id`)**, never by ingestion order, ticker sort, or dictionary/hash order. Same inputs + watermark + `Config.Seed` ⇒ the identical wish list; the rule is asserted in each affected strategy's acceptance tests.
- **Mode** and parameters live in `StrategyConfig` — the selection rule is itself a dimension candidates can differ on.

---

## 4. Roster at a glance

| Strategy | Family | Data needed | Turnover | Build phase | Role |
|----------|--------|-------------|:--------:|:-----------:|------|
| Buy & Hold — cap-weight | passive | price (index proxy) | ~none | 1–2 | benchmark & alpha regression reference (permanent) |
| Buy & Hold — equal-weight | passive | price (EW proxy) | low | 1–2 | construction-matched benchmark (permanent) |
| Random — matched **populations** ×3 cadences | control | none | matches family | 2–3 | **empirical null distribution per family** (permanent, D36) |
| Random — cost-free **population** | control | none | high | 2–3 | pure-noise band (permanent, display-only) |
| Momentum (cross-sectional) | trend | daily bars | med-high (banded) | 6 | core |
| Mean-Reversion (std + fast variant) | reversal | daily bars | high | 6 | core (momentum's opposite; trade-track flagship) |
| Low-Volatility | risk | daily bars + **EODHD sectors** | low (monthly) | 6 | diversifier |
| Breakout (Donchian) | trend | daily bars | medium | 6 (optional) | optional |
| Residual Momentum | trend | daily bars + factor returns | med-high (banded) | 6 | core (cleaner momentum; lower crash risk) |
| Time-Series Momentum | trend | daily bars | medium | 6 | diversifier (absolute, not cross-sectional) |
| Betting-Against-Beta | risk | daily bars (+ index proxy) | low (monthly) | 6 | diversifier (beta-ranked; low-vol's sibling) |
| Value | value | fundamentals (PIT-validated) | low | **8 (contingent)** | fundamental |
| Quality | quality | fundamentals (PIT-validated) | low | **8 (contingent)** | fundamental |
| Blended / Meta | fusion | mixed signals | varies | 6+ | fusion (logistic → LightGBM) |

---

## 5. Baseline strategies (build these first — they are the honesty bar)

### 5.1 `BuyAndHoldModel` — two permanent benchmarks (cap-weight and equal-weight)

- **Role:** permanent, non-promotable benchmarks. The **cap-weight account is the default regression benchmark for Jensen's alpha (D26)**; the **equal-weight account is displayed beside it** (D27) and the attribution regression includes size to catch the residual.
- **Data:** cap-weight: **the traded universe's cap-weight ETF proxy, following `Universe.Bootstrap.MembershipPrimary` (config-driven, not hardcoded)** — `OEF.US` (iShares S&P 100) on the D70 slice, flipping to `IVV.US` (iShares Core S&P 500) when the universe widens, with no code change (finding I / v1.9.19; resolved by `CapWeightProxy`) — under **D87** the widen target is the S&P 1500 (contingent), which needs a new sp1500 proxy-sleeve mapping; else it stays `IVV.US`. Benchmarking against the universe the strategies actually trade — *not* a fixed S&P 500 proxy while they trade the S&P 100 — is the point. **Expense-ratio bias (disclosed, not corrected):** the proxy is a real ETF, so its own fee drag (~0.20%/yr OEF, ~0.03%/yr IVV) sits inside the benchmark, measuring every strategy against a slightly-handicapped baseline — a known, bounded, one-directional bias, named here rather than silently swapped for a synthetic index. Equal-weight: **self-built equal weight of the eligible universe, monthly rebalance — pinned as D68** — it embeds the D43 cost model and matches the construction the random populations and low-vol are judged against; an EW ETF proxy is not used (it would embed the fund's own rebalance timing and expense drag — the CW bias the self-build avoids).
- **Logic:** allocate ~100% on first run; hold; never re-score. `ExitPolicy = Never`.
- **Gotchas:** pays one entry cost. **Ledger conventions apply (D30/D39):** real share counts at raw prices, dividends credited on ex-date, and — v6 — the proxy's own corporate actions (splits, any ticker change) flow through the §13.6 semantics like any holding.
- **Acceptance:** enters once, never churns; total return over any window equals the proxy's **total return** (price + dividends) minus one entry cost (fixture span includes a dividend event).

### 5.2 `RandomModel` — matched control **populations** + a cost-free population (v6, D36)

- **Role:** the empirical null. **v6 correction of the v5 correction:** a single turnover-matched twin fixed the *fairness* problem (D28) but left a *stability* problem — one seeded twin is one noise path, so the two-sample S3 test against it inherited that path's luck. The fix: instantiate a **population of M seeded members per cadence family** (default **M = 200**, config `Populations.Size`), and rank the strategy within the population's distribution.
- **Populations:**
  - `RandomPop-Daily[0..M)` — re-draws daily (null for mean reversion / daily-churn families),
  - `RandomPop-Banded[0..M)` — re-draws with momentum's rank-buffer band cadence,
  - `RandomPop-Monthly[0..M)` — re-draws at monthly rebalance (null for low-vol; a quarterly population is spawned when Phase 8 strategies arrive),
  - `RandomPop-Event[0..M)` — **(named v1.9.23; spawned on demand, never speculatively)** re-draws/exits on the event-driven cadence, mirroring `ChannelExit`/`SignFlip` mechanics (§6.4, §6.6's trend-flip sibling). Per §10's rule — a new cadence spawns a new population — the factory creates it **when the first event-driven strategy enters the arena**, not in Phase 3; until it exists, S3 is undefined for those strategies and the `FX-TurnoverMatch` cost-match caveat renders.
  Each member uses the **same selection breadth (N), same sizing mode, same `ExitPolicy` shape, and same cost model** as the family it is the null for. Members are **lightweight ledger-only accounts** (`control_equity` rows; no GUI account cards) — they need no market-data calls and trivial compute, so M=200 is cheap.
- **Cost-free population:** a smaller population (M = 50) with costs off — the **pure-noise band**, display-only, never an S3 comparator, never promotable.
- **Data:** none (only the eligible list).
- **Logic:** member *i* uses a **seeded** RNG whose daily draw derives deterministically from `(familySeed, memberIndex, date)`; on re-draw days it assigns uniform scores in `[0,1]`; between re-draws it returns current holdings at prior scores (genuinely matching the family's turnover). Selection picks top-N as usual; `ExitPolicy` mirrors the matched family's.
- **Params:** `familySeed` (required), `cadence`, `n`, `costsOn`, `populationSize`.
- **Gotchas:** determinism is a hard requirement — never reseed per day; derive from `(familySeed, memberIndex, date)`. Storage: one compact equity row per member per day (600–800 rows/day total) — trivial for SQLite, but keep the table lean (no per-member trade logs by default; a config flag can enable full ledgers for a sampled subset for auditing). **v1.8 compute requirement:** populations need no API calls but ~650 members run fills, inverse-vol sizing, and D43 costs daily — batch the pipeline: **one covariance solve per family per day shared by all members**, vectorized scoring/fill application over the member axis, bulk-insert `control_equity` (no per-member EF round-trips). Phase-3 DoD: full daily run incl. populations < 60s on the dev machine; a 15-year replay's population step projects to < 4h.
- **What the population gives you:**
  - **S3 (monitor):** the strategy's percentile rank of forward net β-adjusted alpha (or the paired-difference statistic) within its matched population. Thresholds in OVERFITTING_MONITOR_v1.9 §3/S3.
  - **Charts:** the 5–95% (and 25–75%) band of the population's equity/alpha shaded behind every strategy chart — you *see* where noise lives.
  - **Gate sanity:** across the population, promotions must occur ≤ chance (a permanent acceptance property, exercised in Arena Replay).
- **Acceptance (v6):** identical member trades across two runs with the same seeds and watermark; the population's **gross** alpha distribution centers on zero; its **net** distribution is offset below zero by ≈ the modeled cost drag (assert both); band percentiles are deterministic; a known-edge synthetic strategy lands above the 95th percentile of its matched population while a no-edge synthetic lands inside the band (this pair of tests is what makes S3 meaningful).

---

## 6. Price-only strategies (Phase 6 — need only daily bars)

### 6.1 `MomentumModel` — cross-sectional momentum

- **Theory:** recent winners tend to keep winning over the medium term.
- **Honest expectations:** the published premium is a long-short decile result; a long-only S&P 500 tilt keeps a fraction and is dominated by market beta. The crash/vol-scaling literature is a short-leg story; expect a **modest** long-only benefit from vol targeting. Judge on β-adjusted alpha with realistic margins — and expect the head-to-head verdict to read `TooEarly` for a long time (MASTER_DESIGN_v1.9 §1.1). The *fast* verdicts available here are negative ones: if banded momentum can't stay above its matched population band net of the D43 cost model, that is a real finding, quickly.
- **Data:** `adj_close` daily bars. Warm-up: `lookback + skip`.
- **Parameters (defaults):** `lookback = 126` (sibling: 252 for canonical 12-1) · `skip = 21` (the skip-month — not optional; avoids 1-month-reversal contamination) · `selection = TopN(40)` (range 30–50; decile-like breadth so the factor is measurable over idiosyncratic noise) · `exitRank = 80` (rank hysteresis: enter ≤ 40, exit < ~2N — kills boundary-churn cost bleed) · optional `absoluteFilter = true`.
- **Horizon / exits:** `Horizon = ToRankExit`; `Exits = RankBuffer(exitRank)`.
- **Volatility scaling:** a **sizing overlay**, not a score change — Stage-5 weights scaled by `targetVol / realizedPortfolioVol` (clamped [0.5, 1.5]), point-in-time, with realized portfolio vol from the **Ledoit–Wolf covariance (D42)**. The signal stays pure momentum so attribution stays clean.
- **Scoring logic:**
  ```
  for each security s in eligible:
      p_now  = adj_close[s] at asOf - skip trading days
      p_then = adj_close[s] at asOf - skip - lookback trading days
      if either missing -> omit s
      ret[s] = p_now / p_then - 1
  score[s] = percentile_rank(ret[s])            # cross-sectional
  if absoluteFilter and ret[s] <= 0: score[s] = 0
  ```
- **Gotchas:** high turnover even banded → the **D43 cost model is load-bearing**; watch the per-strategy capacity-rejection log (participation cap) — if it fires at paper notional, the strategy is already capacity-constrained. Never run without the rank buffer (ships in the same phase). Point-in-time `adj_close` only. Ticker changes among winners are non-events under the security master (D39) — no phantom churn.
- **Acceptance:** monotone-up fixture beats monotone-down; lookback changes ranking; hysteresis test (oscillation around rank N does not churn); vol overlay is point-in-time; leakage test (scores at `asOf` unchanged whether future bars exist at any watermark).

### 6.2 `MeanReversionModel` — buy the oversold (momentum's opposite)

- **Theory:** short-term overreactions revert. Structural opposite of momentum — the pairing gives the arena regime diversity.
- **Signal frequency:** RSI(14) < 30 while above the 200-day SMA is rare among S&P 500 members — expect sparse days; log the daily candidate count. Prefer the fast variant if the standard one can't accrue trades.
- **Data:** `adj_close` daily bars. Warm-up: `max(rsiPeriod, smaTrend) + buffer`.
- **Parameters (standard):** `rsiPeriod = 14`, `oversold = 30`, `trendFilter = true`, `trendSma = 200`, `selection = Threshold(minScore 0.60, maxConcurrent 15)`.
- **Fast sibling:** `rsiPeriod ∈ {2,3,4}`, `oversold ∈ {10,25}`, same trend filter — Connors-style short-horizon reversal; trades far more often.
- **Horizon / exits:** `Horizon = Days(maxHold)`; `Exits = TargetOrTimeStop(RSI(rsiPeriod) > 50, maxHoldDays: 10)` (fast: 5). The trade has a defined shape: enter oversold, exit on reversion to neutral or the time stop.
- **Trade-level evidence track (v6, D44):** the fast sibling is the flagship of the per-trade expectancy channel — hundreds of completed trades per year vs dozens of independent daily-return observations' worth of information. Its evidence panel shows: mean net P&L/trade, moving-block-bootstrap CI (blocks span ≥ the holding period, because trades cluster in time and regime), the **trade-track MDE**, and the same `TooEarly` discipline. The trade track is a *falsification accelerator and evidence supplement* — never a promotion basis on its own (§9.1).
- **Scoring logic:**
  ```
  for each security s in eligible:
      rsi = Wilder_RSI(adj_close[s], rsiPeriod) as of asOf
      if trendFilter and close[s] <= SMA(adj_close[s], trendSma): score[s] = 0; continue
      score[s] = clamp((oversold - rsi) / oversold, 0, 1)
  ```
- **Gotchas:** the trend filter separates "buy the dip" from "catch a falling knife" — on by default. High turnover ⇒ D43 model decisive. Wilder RSI warm-up tested against an independent reference. Zero-score names unselectable.
- **Acceptance:** oversold-uptrend fixture scores high; oversold-downtrend scores 0; RSI matches reference; exit fires on reversion or time stop — not wish-list exit; sparse-day test; trade-track CI computation verified on synthetic clustered trades; leakage test.

### 6.3 `LowVolModel` — the low-volatility diversifier

- **Theory:** low-vol stocks deliver strong *risk-adjusted* returns (the low-risk anomaly). Low turnover; diversifies trend/reversal. **Sibling:** §6.7 (Betting-Against-Beta) targets the same anomaly but ranks on **beta** rather than realized volatility; run both to test which expression the arena rewards (they are close cousins — see the anti-clone note in §10).
- **The evaluative point that decides its fate:** low-vol runs β ≈ 0.6–0.8 — on raw return vs cap-weight it loses every bull market *while working as designed*. It is judged **only** on β-adjusted alpha / IR (D26).
- **Data:** `adj_close` daily bars + **sector classification from EODHD (D35)**.
- **Parameters (defaults):** `volWindow = 252` · `selection = TopN(30)` · `rebalanceDays = 21` · `sectorCap = 25%` (long-only low-vol otherwise converges on a utilities/staples fund; the cap consumes the EODHD sector field and surfaces concentration on the Risk screen from day one).
- **Horizon / exits:** `Horizon = ToNextRebalance`; `Exits = ScheduledRebalance(rebalanceDays)`.
- **Scoring logic:**
  ```
  vol[s] = stddev(daily_returns(adj_close[s], volWindow)) as of asOf
  score[s] = 1 - percentile_rank(vol[s])
  ```
- **Gotchas:** re-score only at rebalance dates. Compared against `RandomPop-Monthly` (its matched population). A sector reclassification (EODHD change-log) takes effect at the next rebalance — never intra-period churn.
- **Acceptance:** low-vol fixture outranks high-vol; churn test (changes only at rebalance); sector-cap test (fixture with 20 utilities in the top 30 gets capped); classification-change test (reclass applies at next rebalance only); leakage test.

### 6.4 `BreakoutModel` — Donchian channel breakout (optional)

- **Theory:** a close at a new N-day high signals a trend beginning. Event-driven trend cousin of momentum.
- **Data:** daily high/low/close. Warm-up: `channel`.
- **Parameters:** `channel = 55` (entry), `exitChannel = 20`.
- **Horizon / exits:** `Horizon = ToChannelExit`; `Exits = ChannelExit(exitChannel)`.
- **Scoring logic:**
  ```
  hi = max(high[s] over last `channel` bars ending asOf-1)   # exclude today's bar
  score[s] = (close[s] at asOf) >= hi ? 1.0 : 0.0
  ```
- **Gotchas:** exclude the current bar from the channel max. Binary scores ⇒ threshold selection and a degenerate calibration map (a Kelly variant needs distance-above-channel scaling). Participates in the trade-level track (D44). Matched population (v1.9.23): `RandomPop-Event` (§5.2) — spawned when the first event-driven strategy enters the arena, never speculatively; until it exists, S3 is undefined for this strategy and the `FX-TurnoverMatch` caveat renders.
- **Acceptance:** new-high fixture triggers 1.0; current-bar exclusion verified; channel exit closes positions; boundary ties among equal binary scores break by the §3 seeded stable order (deterministic wish list); leakage test.

### 6.5 `ResidualMomentumModel` — momentum on factor-residual returns

- **Theory:** conventional momentum's returns are contaminated by time-varying factor exposures (it loads heavily on market/sector, which is why §6.1's own note calls the long-only version "dominated by market beta"). Ranking on the *residual* return, the part of each stock's return not explained by common factors, isolates the stock-specific continuation and strips the factor bet. Blitz, Huij & Martens (2011) show residual momentum earns risk-adjusted profits roughly **twice** those of total-return momentum, is more consistent over time, and is far less exposed to the momentum-crash tail. For a long-only S&P 500 lab where raw momentum is dominated by beta, this is the higher-quality expression of the same idea, and the direct answer to §6.1's honest limitation.
- **Honest expectations:** the "twice the risk-adjusted profit" figure is, like all of §6, a **long-short** result; a long-only tilt keeps a fraction. What residual momentum should deliver *here* versus plain momentum is **not** a higher raw return, it is a cleaner β-adjusted alpha and a materially smaller drawdown in momentum-crash regimes (2009-style sharp reversals). Judge it head-to-head against `momentum:v1` on the paired-difference statistic (both are trend, so they share most market exposure, tightening the MDE), not against the benchmark. Expect `TooEarly` for a long time on the absolute verdict; the *fast* result is the paired one: if residual momentum does **not** reduce crash-regime drawdown versus total-return momentum, that specific claim is falsified quickly.
- **Data:** `adj_close` daily bars **plus a factor-return series** for the residualizing regression. Minimum viable: the market excess return (single-factor residual, i.e. residual = stock return minus its beta-times-market). Preferred: the Fama-French 3 factors (MKT, SMB, HML), which the design already ingests for attribution (INTEGRATIONS_v1.9; Ken French data library, already a dependency of the D41 attribution regression, **diagnostic-only there, a signal input here**). Warm-up: `regWindow + lookback + skip`.
- **Parameters (defaults):** `regWindow = 756` (36 months of daily obs for the rolling factor regression, per Blitz-Huij-Martens) · `lookback = 252` · `skip = 21` (same skip-month discipline as §6.1) · `selection = TopN(40)` · `exitRank = 80` (same rank hysteresis) · `factors = FF3` (fallback `MKT` if only the market proxy is available).
- **Horizon / exits:** `Horizon = ToRankExit`; `Exits = RankBuffer(exitRank)` — identical machinery to §6.1, so this reuses the momentum plumbing wholesale.
- **Scoring logic:**
  ```
  # 1. rolling factor regression per security over regWindow, ending asOf - skip - lookback
  for each security s in eligible:
      if history(s) < regWindow + lookback + skip -> omit s     # depth guard, like Quality
      estimate (alpha_s, beta_s[]) from daily excess returns of s on `factors`
          over the window [asOf - skip - lookback - regWindow, asOf - skip - lookback]
      # 2. residual return over the formation window
      resid_ret[s] = sum over t in (asOf-skip-lookback , asOf-skip] of
                     ( ret_s[t] - beta_s · factor_ret[t] )      # factor-neutral cumulative residual
  score[s] = percentile_rank(resid_ret[s])                      # cross-sectional, standardized
  ```
  (Standardizing the residual by its regression standard error before ranking, the Blitz-Huij-Martens t-stat form, is a tested sibling `standardize = true`; the plain cumulative-residual form is the default for auditability.)
- **Gotchas:** the regression is **point-in-time** — betas are estimated only on data available at `asOf`, and the leakage test must confirm a residual score at `asOf` is unchanged whether later bars exist (this is the same leakage discipline as §6.1 but with a regression in the middle, so it needs its own fixture). Factor returns join **availability-lagged** exactly like fundamentals (French factors publish with a lag; §7.0's leakage warning applies). Turnover is similar to §6.1, so the **D43 cost model is load-bearing** and the participation-cap log matters. Do **not** let the residualization silently drop names with short history, omit them (depth guard) rather than zero-fill, or the cross-section is biased toward long-listed stocks. Matched population: `RandomPop-Banded` (same cadence as momentum).
- **Acceptance:** a fixture where a stock's raw return is high **only** because of market beta scores **lower** than a stock with the same raw return but positive residual (this is the whole point, and the test that distinguishes it from §6.1); monotone residual fixture ranks monotonically; regression is PIT (betas at `asOf` unaffected by future bars); factor-return availability lag enforced; depth guard omits short-history names; standardize-variant sibling reproduces the t-stat ordering; leakage test through the regression.

### 6.6 `TimeSeriesMomentumModel` — absolute (own-past) trend

- **Theory:** distinct from cross-sectional momentum (§6.1 ranks stocks *against each other*); time-series momentum asks whether each asset's **own** past return predicts its **own** future return. Moskowitz, Ooi & Pedersen (2012) documented that the past 12-month excess return positively predicts the next 1-12 month return, an effect that persists for about a year then partially reverses. It is the academic basis of trend-following. Because it is an absolute signal (a stock can be "in an uptrend" regardless of how it ranks), it goes to cash-equivalent (simply holds fewer names) when the whole market trends down, giving the roster a **defensive** behavior that cross-sectional momentum, always fully invested in the relative winners, does not have. That is the diversification it adds.
- **Honest expectations — and the key caveat:** MOP (2012) tested TSMOM on **58 futures and forward contracts** (equity indices, currencies, commodities, bonds), **not** individual stocks. Applying it to single-name equities is a documented *adaptation*, not the original result; the single-stock trend literature is weaker and noisier than the futures result, and long-only single-stock trend is closest in spirit to Hurst, Ooi & Pedersen (2017), "A Century of Evidence on Trend-Following Investing," than to the original TSMOM paper. So the honest prior here is **modest**: this is a diversifier expected to earn its keep through *drawdown reduction and low correlation to the cross-sectional strategies* in falling markets, not through a high standalone alpha. State that in the verdict framing; do not sell it as the futures Sharpe. Judge β-adjusted, and expect its value to show up most in the paired difference during bear regimes.
- **Data:** `adj_close` daily bars. Warm-up: `lookback`.
- **Parameters (defaults):** `lookback = 252` (the canonical 12-month window) · `skip = 21` (the skip-month, declared v1.9.23 to match the pseudocode below; same 1-month-reversal-contamination rationale as §6.1 — not optional) · `signal = sign` (hold if trailing 12-month excess return > 0; sibling `signal = volScaled` sizes by the return divided by ex-ante vol, the MOP construction) · `selection = AllPositive(maxConcurrent 50)` (hold every name whose own trend is positive, capped for capacity; this is what makes it go partially to cash in a downturn) · `rebalanceDays = 21`.
- **Horizon / exits:** `Horizon = ToNextRebalance`; `Exits = ScheduledRebalance(rebalanceDays)` **or** trend-flip (`Exits = SignFlip(lookback)`), whichever the sibling declares.
- **Scoring logic:**
  ```
  for each security s in eligible:
      r12[s] = adj_close[s] at (asOf - skip) / adj_close[s] at (asOf - skip - lookback) - 1
      excess = r12[s] - riskFreeReturn(lookback)          # own excess return
      if signal == "sign":     score[s] = excess > 0 ? 1.0 : 0.0
      if signal == "volScaled": score[s] = excess > 0 ? clamp(excess / exAnteVol[s], 0, 1) : 0.0
  ```
- **Gotchas:** binary `sign` scoring ⇒ threshold selection and a degenerate calibration map (the `volScaled` sibling gives a continuous score for the Kelly path, same issue flagged for §6.4 Breakout). The "go to cash" behavior means some days select very few names, its equity curve will look nothing like a fully-invested strategy, and that is correct, not a bug; the Risk screen should show the cash weight rising in downturns. Overlaps conceptually with §6.4 Breakout (both are absolute trend), so the CandidateFactory anti-clone check (§10) must keep them from being near-duplicates, prefer running one of {Breakout, TSMOM} unless their signals demonstrably diverge. Matched population: `RandomPop-Monthly` (rebalance cadence) for the `ScheduledRebalance` sibling; the `SignFlip` sibling is **event-driven** and matches `RandomPop-Event` (§5.2, v1.9.23) — until that population is spawned, S3 is undefined for it and the `FX-TurnoverMatch` caveat renders. Point-in-time; risk-free series joins availability-safe.
- **Acceptance:** a name in a sustained uptrend scores 1.0 (or high under `volScaled`); a name in a downtrend scores 0 and is **not** held (verifying the defensive drop-to-cash); a broadly falling fixture reduces the invested count (cash-weight rises); `volScaled` sibling produces a continuous score; sign-flip exit closes a position when the 12-month trend turns negative; sign-mode boundary ties break by the §3 seeded stable order (deterministic wish list); leakage test.

### 6.7 `BettingAgainstBetaModel` — the low-beta tilt

- **Theory:** the low-risk anomaly's other formulation. Where §6.3 ranks on realized volatility, Betting-Against-Beta ranks on **market beta**: leverage-constrained investors overweight high-beta stocks (reaching for return without borrowing), bidding them up so they earn *lower* risk-adjusted returns, while low-beta stocks are underpriced and earn higher CAPM alpha. Frazzini & Pedersen (2014) built the canonical BAB factor and found it delivered a Sharpe ratio of ~0.78 from 1926 to 2012, about twice the value effect's and higher than momentum's over the same period, with significant alpha after controlling for market, size, value, momentum, and liquidity. It is one of the most-cited anomalies in asset pricing.
- **Why it earns a slot next to §6.3:** low-vol and low-beta are close cousins but **not** identical, a stock can have low standalone volatility yet high beta (moves little but in lockstep with the market), or high volatility yet low beta (moves a lot but idiosyncratically). Which one is the "true" low-risk signal in a long-only S&P 500 arena is an empirical question this lab is built to answer. Because beta falls out of the **Ledoit-Wolf covariance you already compute (D42)** for sizing, adding BAB is nearly free on the compute side, it needs no new data pipeline, only a second ranking off a matrix already in memory.
- **Honest expectations:** the ~0.78 Sharpe is, like every §6 figure, a **long-short, leverage-adjusted** result (the classic BAB factor *leverages* the low-beta leg up to β=1 and *shorts* the high-beta leg). This lab is **long-only and unlevered**, so it captures only the long-leg tilt toward low-beta names, a fraction of the published premium, and it will look like a low-β portfolio that lags in bull markets exactly as §6.3 does. Judge it **only** on β-adjusted alpha / IR (D26), never raw return. Its most useful comparison is the **paired difference against `lowvol:v1`**: since both are long-only low-risk tilts sharing most market exposure, that pairing has a tight MDE and answers the real question, does ranking on beta beat ranking on vol here? A near-zero paired difference is itself a clean finding (the two signals are redundant in this universe); a persistent one tells you which to keep.
- **Data:** `adj_close` daily bars **plus the market index proxy** for beta estimation (the same `CapWeightProxy` the benchmark uses, `OEF.US`/`IVV.US` per the universe). Warm-up: `betaWindow`.
- **Parameters (defaults):** `betaWindow = 252` (1y of daily returns for the beta estimate) · `betaSource = LedoitWolf` (derive each name's beta from the D42 shrunk covariance with the market proxy — `cov(s, mkt) / var(mkt)`; sibling `betaSource = rolling_ols` for a plain 1y regression beta as a cross-check) · `selection = TopN(30)` · `rebalanceDays = 21` · `sectorCap = 25%` (same rationale as §6.3, a long-only low-beta book otherwise concentrates in utilities/staples; consumes the EODHD sector field, surfaces on the Risk screen). **Implementation seam (v1.9.23):** for `betaSource = LedoitWolf` to work, **the market proxy's return series must be included in the D42 covariance estimation set** — the estimator normally runs over held + wish-listed names; BAB adds the proxy row so `cov(s, mkt)` and `var(mkt)` fall out of the same shrunk solve rather than a second estimator.
- **Horizon / exits:** `Horizon = ToNextRebalance`; `Exits = ScheduledRebalance(rebalanceDays)` — identical cadence to §6.3, so it reuses the low-vol plumbing.
- **Scoring logic:**
  ```
  # beta of each eligible name vs the market proxy, point-in-time over betaWindow
  for each security s in eligible:
      if history(s) < betaWindow -> omit s                      # depth guard
      beta[s] = cov(returns(s), returns(mkt), betaWindow) / var(returns(mkt), betaWindow)
                # LedoitWolf source reads the shrunk covariance already solved for D42 sizing
  score[s] = 1 - percentile_rank(beta[s])                       # low beta -> high score
  ```
- **Gotchas:** re-score only at rebalance dates (like §6.3), matched against `RandomPop-Monthly`. Beta is **point-in-time** — estimated only on data available at `asOf`; the leakage test confirms a name's beta at `asOf` is unchanged whether later bars exist. The market-proxy return series must be availability-safe and follow the same universe-driven proxy as the benchmark (do **not** hardcode a single index, finding I / `CapWeightProxy`). Depth guard omits short-history names rather than assigning a noisy beta. **Anti-clone discipline (§10):** BAB and `lowvol:v1` will often overlap heavily, the factory must treat them as a near-clone pair and only keep both live while their holdings/returns demonstrably diverge; if the paired difference sits inside its MDE indefinitely, retire one.
- **Acceptance:** a low-beta fixture outranks a high-beta one; a **high-volatility-but-low-beta** name scores **higher** than a **low-volatility-but-high-beta** name (the test that distinguishes BAB from §6.3 low-vol — this is the whole reason it exists); beta from the Ledoit-Wolf source matches the `rolling_ols` sibling within a **shrinkage-aware tolerance** on a fixture — LW pulls betas toward the shrinkage target **by construction**, so an exact-match test fails by design; the tolerance is a stated function of the fixture's shrinkage intensity, **asserted in the test**, not hand-waved (v1.9.23); beta is PIT (unaffected by future bars); re-scores only at rebalance; sector cap enforced; depth guard omits short-history names; leakage test.

---

## 7. Fundamental strategies (**Phase 8 — contingent on a PIT-validated source, D33/D35**)

### 7.0 The gate, operationalized (v6)

> **Gate:** Phase 8 does not start until a fundamentals source **passes the PIT validation protocol** below. **EODHD Fundamentals is the named first candidate** (D35 — it ships with the same subscription); SEC EDGAR/XBRL ingestion (free, heavy engineering) and a verified paid PIT feed remain the alternatives if it fails.

**PIT validation protocol (run on a ~30-name, multi-year sample before any Phase 8 code):**
1. **As-reported check:** for sampled quarters with known later restatements, does the source return the *originally filed* value or the restated one? (Restated-only ⇒ fail for signal use.)
2. **Availability-date check:** does each quarterly record carry a usable *as-of availability date* (filing/acceptance date), not just the fiscal period end? Cross-check a sample of dates against SEC EDGAR acceptance timestamps.
3. **Lag realism:** distribution of (availability − period-end) lags is plausible (weeks, not zero).
4. **Depth check:** ≥ 3 years of quarterly history per name for Quality's earnings-stability term; measure coverage across the universe.
5. **Survivor check:** do delisted names retain their historical fundamentals? (Vanishing history ⇒ survivorship contamination.)
Record the protocol's results in `PROGRESS.md`; a pass names the source in the decision log (a **new** decision appended to MASTER §2 under the next free D-number at that time — D49 itself is the budget-tier launch configuration and is not reused); any fail keeps Phase 8 closed.

> **Leakage warning (all of §7):** fundamentals join on `report_available_date <= asOf`, **as-reported**, availability-lagged. `IFeatureView` exposes fundamentals only in that form. Getting this wrong silently inflates every backtest.

### 7.1 `ValueModel` — buy cheap relative to fundamentals

- **Parameters:** `metric = EarningsYield` (E/P) or `BookToPrice`; `winsorize = 0.02`; optional `sectorNeutral = true` (sector field from EODHD, D35).
- **Horizon / exits:** `Horizon = ToNextRebalance`; `Exits = ScheduledRebalance(63)` (quarterly). Matched population: a quarterly-cadence random population spawned with the phase.
- **Scoring:** PIT fundamental → cheapness → winsorize → optional sector demean → percentile rank.
- **Gotchas:** negative earnings ⇒ E/P undefined (omit or B/P). Value can underperform for *years* — the MDE display will say, correctly, that short windows cannot judge it; that is the tool being honest.
- **Acceptance:** low-P/E fixture outranks high-P/E; **PIT test: a filing dated before but available after `asOf` is excluded**; winsorization caps outliers.

### 7.2 `QualityModel` — buy financially strong firms

- **Parameters:** composite z-scores: `ROE` (higher better) − `debtToEquity` (lower better) − `earningsVariance` (lower better); PIT inputs, ≥ 3y quarterly depth.
- **Horizon / exits:** `Horizon = ToNextRebalance`; `Exits = ScheduledRebalance(63)`.
- **Gotchas:** z-scores cross-sectional at `asOf`; tilts defensive — judge β-adjusted.
- **Acceptance:** high-ROE/low-debt fixture outranks weak; PIT test; depth check (a name with < 3y of quarters is omitted, not zero-filled).

---

## 8. Blended / Meta strategy (Phase 6+ — where the meta-classifier lives)

### 8.1 `BlendedModel` — fuse several signals into one probability

- **Composition:** sub-strategies (e.g. `[Momentum, LowVol, ResidualMomentum]`) combined per security. The blend fuses **mechanical** signals only; the LLM's role in the lab is the three AI seats (MASTER §23), not a sub-signal spliced into this classifier (see the note below).
- **Fusion modes (ordering):** **Weighted** (deterministic, start here) → **Logistic regression** (first learned mode; out-of-fold training **mandatory** — stacked generalization — or the meta learns leaked in-sample predictions) → **LightGBM** (a later candidate that must beat the logistic blend forward).
- **Retrain discipline:** ML.NET LightGBM has no warm-start; **every retrain enters as a fresh Candidate and increments `trials_registry`** — a monthly cadence adds ~12 trials/year and deflates everyone's Sharpe. Quarterly is the sane default; write it in `Config`.
- **Horizon / exits:** declared explicitly in `Config` (typically the dominant sub-signal's).
- **Where the LLM lives (superseded framing):** earlier revisions folded an experimental `ClaudeSentiment` score into this blend as a sub-signal. That is **superseded by the three-seat AI design (D79-D82; MASTER §23)**: the LLM is now a first-class **contestant seat** that scores names and trades its own account against a mechanically-identical no-LLM twin (the paired A/B that prices its contribution), a **researcher seat** that proposes hypotheses, and a deferred **advisor seat** — *not* a hidden numeric input to a classifier that also judges it (golden rule 32). The paired-A/B intuition that made the old sub-signal attractive is preserved and strengthened by the contestant's twin. This `BlendedModel` therefore blends mechanical signals only; do not re-introduce an LLM sub-signal here.
- **Gotchas:** out-of-fold training mandatory (or the meta learns leaked in-sample predictions). No LLM sub-signal (the LLM is the contestant seat, MASTER §23, not a blend input).
- **Acceptance:** weighted mode reproduces `Σ wᵢ·scoreᵢ` exactly; learned mode provably out-of-fold (provenance test); a retrain creates a new strategy id + trial row; **no test asserts an LLM sub-signal (that path is retired; the contestant seat's A/B is specified in MASTER §23.3).**

---

## 9. Score → probability → sizing (calibration, horizons, Kelly)

- **Default sizer: inverse-volatility under a portfolio vol target**, covariance from **Ledoit–Wolf (D42)**. Equal-weight acceptable for dummies. Heat guardrail caps predicted portfolio vol, never summed notional.
- **New opens are sized against available cash, never total equity (D84):** the day's opens scale to fit the cash on hand, so an account never spends cash it does not hold and no held position is sold to fund an open (rule 7 — only the `ExitPolicy` closes). A whole-book rebalance re-weights against equity (it self-funds). `Sizing.PositionCapPct` still binds per name. Resolves finding 190.
- **Kelly variant (Phase 6+, opt-in per strategy):** calibrated `p` (isotonic/Platt over the declared horizon; **overlapping-label caution** — block bootstrap / non-overlapping windows for confidence); `b` from realized trades with a shrinkage prior toward 1.0 and a 30-trade minimum; `f* = (p·b − q)/b` clamped (cap 0.25); shrink-to-zero on unknown/failing calibration. A Kelly-sized strategy is a separate candidate vs its inverse-vol twin.
- **The AI contestant's paired control scores by a frozen equal-weight z-score blend (D85):** where the contestant (MASTER §23) asks the LLM for a per-name Stage-2 score, its mandatory no-LLM twin (`status='control'`) instead standardizes each pack feature cross-sectionally across the day's shortlist (at the watermark) and averages them with **equal weights** — no tuning, no free params, sign conventions fixed in the recipe (§23.3). The pair differs only at Stage 2, so the paired contestant-minus-twin difference (M.1) isolates the LLM's *weighting skill* over the naive combination of the same facts. Degenerate days drop zero-variance features, then fall back to equal scores across the shortlist with a `degenerate_blend` flag — never a divide-by-zero or NaN.

### 9.1 The trade-level evidence track (v6, D44)
For strategies whose `Horizon` is short and trade count high (fast MR, breakout):
- **Metric:** mean net P&L per trade (expectancy) with a **moving-block bootstrap** CI; block length ≥ max holding period (trades cluster in time and regime — naive i.i.d. CIs overclaim).
- **Its own MDE:** the smallest expectancy the current trade count can distinguish from zero at the configured confidence/power, rendered beside the estimate.
- **Role:** a **falsification accelerator** (a fast strategy with negative expectancy after 300 trades is dead long before its daily-alpha regression could say so) and an evidence supplement. It is **never a promotion basis alone** — promotion still runs through the paired daily-alpha gate — but a `Suspect`-level contradiction between the tracks feeds Monitor S8 (cross-metric divergence).
- **Storage:** `trade_evidence(strategy_id, as_of, n_trades, expectancy, ci_lo, ci_hi, mde, block_len)`.

---

## 10. CandidateFactory — spawning variants without breeding clones

- **Family diversity first:** always keep momentum **and** mean-reversion (ideally + low-vol) live.
- **Parameter variants second:** perturbations only once a family shows promise; every spawn increments the trials registry.
- **Anti-clone rule:** no dozens of near-identical arms — clones overlap trades and inflate false discovery. Pool stays small (1 Live + 2–3 Candidates to start). **Known near-clone pairs to watch:** Betting-Against-Beta (§6.7) vs Low-Vol (§6.3) — both long-only low-risk tilts; and Time-Series Momentum (§6.6) vs Breakout (§6.4) — both absolute trend. Keep both members of a pair live **only** while their holdings/returns demonstrably diverge (paired difference outside its MDE); otherwise retire one. The **§12 trials arithmetic** (v1.9.23) states how the ~8 implemented Phase-6 strategies square with this small pool: shelf capacity, entering only as registered trials.
- **Retrains as candidates:** a retrained model is a fresh Candidate and a fresh trial.
- **Population hookup (v6):** the factory wires every new candidate to its matched random population by cadence family; a new cadence (e.g. quarterly at Phase 8) spawns a new population.

---

## 11. Cross-cutting correctness & cost notes (apply to every strategy)

- **Point-in-time everything** via `IFeatureView(asOf, watermark)`; the leakage suite is a permanent CI gate; regime labels included (D34).
- **Identity is `security_id` (D39):** ticker changes are non-events; merger conversions carry positions across ids; spin-off receipts land as forced opens managed by the owner's `ExitPolicy` / configured liquidation rule; unmapped events freeze + alert (fail closed).
- **Warm-up:** no score for a security lacking history; the account holds cash/existing positions until warmed.
- **Costs always on (D43):** commission + half-spread by liquidity bucket + √impact, participation-capped; the cost-model version stamps every fill; capacity rejections logged per strategy.
- **Ledger conventions (D30):** raw-price trading/valuation, ex-date dividends, split share-adjustments, §13.6 corporate-action semantics; signals on `adj_close`.
- **Determinism:** same inputs + watermark + `Config.Seed` → identical scores (D40).
- **Decide at close, fill next open.**
- **Fair comparisons:** each strategy's S3 rank is against its **matched population (D36)**; alpha is β-adjusted vs cap-weight with the equal-weight account beside it.

---

## 12. Phase mapping

| Build phase | Strategies to implement |
|:-----------:|-------------------------|
| 1–2 | `BuyAndHoldModel` ×2 (CW + EW), `RandomModel` population machinery + a trivial `ThresholdModel` — exercising ledger conventions, corporate-action semantics, funnel, exit plumbing |
| 3 | Full baseline arena proving the loop: populations live; promotions ≤ chance; population bands rendered |
| 6 | `MomentumModel` (banded, vol overlay), `MeanReversionModel` (std + fast, explicit exits, trade track), `LowVolModel` (252d, monthly, EODHD sector cap), `ResidualMomentumModel` (factor-residual, reuses momentum plumbing), `TimeSeriesMomentumModel` (absolute trend, defensive), `BettingAgainstBetaModel` (beta-ranked, reuses low-vol plumbing + D42 covariance), optional `BreakoutModel`; `BlendedModel` (weighted → logistic; LightGBM later, mechanical signals only). The **AI contestant seat** and its paired no-LLM twin are specified separately in MASTER §23, not here. |
| **8 (contingent)** | `ValueModel` / `QualityModel` once a source **passes the §7.0 PIT protocol** (EODHD Fundamentals is the first candidate) |

**Recommended day-one live arena (end of Phase 3):** Buy & Hold CW + EW · random populations (3 matched cadences + cost-free) · then Momentum and Mean-Reversion entering in Phase 6.

**Trials arithmetic (v1.9.23 — how ~8 implemented strategies square with a 3-candidate pool):** §6.5–6.7 (and optional §6.4 Breakout) are **implemented shelf capacity** — built and acceptance-tested in Phase 6, entering the live arena only through researcher-proposed, operator-registered forks within `Research.ForkBudgetPerYear` and `Research.MaxConcurrentCandidates` (MASTER §23.4). Day one of Phase 6 admits **Momentum and MeanReversion only** (unchanged, above). Every candidate that enters increments `trials_registry` (unchanged); the fork budget governs **researcher-originated** proposals — the Phase-6 day-one entrants are operator-initiated and spend trials, not fork budget. The contestant's no-LLM twin is a **control** (`strategies.status='control'`, the population precedent): it registers no trial and is never promotable alone (MASTER §23.3).

---

## 12.5 References — the literature each strategy rests on

Every family in this catalog is a documented, peer-reviewed anomaly. The published results are almost always **long-short** and pre-cost; this lab's long-only, cost-and-capacity-realistic, forward-only paper trading is expected to keep a fraction of any of them, which is exactly what the MDE display and the `TooEarly` discipline exist to state honestly.

- **Cross-sectional momentum (§6.1):** Jegadeesh, N. & Titman, S. (1993). "Returns to Buying Winners and Selling Losers: Implications for Stock Market Efficiency." *Journal of Finance* 48(1), 65-91.
- **Short-term reversal / mean-reversion (§6.2):** De Bondt, W. & Thaler, R. (1985). "Does the Stock Market Overreact?" *Journal of Finance* 40(3), 793-805. Also Jegadeesh, N. (1990), "Evidence of Predictable Behavior of Security Returns," *Journal of Finance* 45(3). The fast RSI(2-4) variant is practitioner: Connors, L. & Alvarez, C. (2008), *Short Term Trading Strategies That Work*.
- **Low-volatility / low-beta (§6.3 and §6.7):** Frazzini, A. & Pedersen, L.H. (2014). "Betting Against Beta." *Journal of Financial Economics* 111(1), 1-25 — the canonical BAB factor (§6.7 ranks on beta; §6.3 ranks on volatility, the two long-only expressions of the same low-risk anomaly). Also Ang, Hodrick, Xing & Zhang (2006), "The Cross-Section of Volatility and Expected Returns," *Journal of Finance* 61(1); Baker, Bradley & Wurgler (2011), *Financial Analysts Journal* 67(1).
- **Breakout / Donchian trend (§6.4):** practitioner lineage (Donchian channels; the Turtle Traders). For the academic trend basis, see the time-series-momentum references below.
- **Residual / idiosyncratic momentum (§6.5):** Blitz, D., Huij, J. & Martens, M. (2011). "Residual Momentum." *Journal of Empirical Finance* 18(3), 506-521. Follow-up: Blitz, Hanauer & Vidojevic (2020), "The Idiosyncratic Momentum Anomaly," *International Review of Economics & Finance*.
- **Time-series (absolute) momentum (§6.6):** Moskowitz, T.J., Ooi, Y.H. & Pedersen, L.H. (2012). "Time Series Momentum." *Journal of Financial Economics* 104(2), 228-250. **Note:** the original result is on 58 futures/forward contracts, not single stocks; for the long-only equity-trend adaptation see Hurst, Ooi & Pedersen (2017), "A Century of Evidence on Trend-Following Investing," *Journal of Portfolio Management* 44(1), 15-29.
- **Value (§7.1):** Fama, E. & French, K. (1992), "The Cross-Section of Expected Stock Returns," *Journal of Finance* 47(2); Fama & French (1993), *Journal of Financial Economics* 33(1). Earnings-yield origin: Basu, S. (1977), *Journal of Finance* 32(3).
- **Quality (§7.2):** Novy-Marx, R. (2013), "The Other Side of Value: The Gross Profitability Premium," *Journal of Financial Economics* 108(1); Asness, C., Frazzini, A. & Pedersen, L.H. (2019), "Quality Minus Junk," *Review of Accounting Studies* 24(1).
- **Factor model used for §6.5 residualization and D41 attribution:** Fama, E. & French (1993), three-factor model; factor-return series from the Kenneth R. French data library.

*Citations are to the seminal or canonical paper for each anomaly; each is widely replicated (and, in several cases, contested) in later literature. Inclusion here is a statement that the signal is well-studied, not a claim that its premium will survive this lab's forward test, that is what the arena decides.*

---

## 13. Master acceptance checklist

- [ ] Every `IModel` uses only `asOf`-bounded, watermark-resolved data (leakage test per strategy).
- [ ] Every `IModel` declares `Horizon` + `ExitPolicy`; Stage 4 closes **only** via `ExitPolicy` / forced events.
- [ ] Stage 3 never selects a zero-score / sub-minScore name.
- [ ] All strategy code is keyed by `security_id`; a fixture ticker change causes zero portfolio churn; a fixture spin-off creates a correctly-based new position; an unmapped terminal event freezes + alerts (D39).
- [ ] `BuyAndHoldModel` (both): enters once; total return = proxy total return (incl. a dividend event) − one entry cost.
- [ ] Random **populations**: reproducible under seeds; gross alpha distribution centered ~0; net offset ≈ −cost drag; synthetic-edge strategy > 95th pct, no-edge inside band; cost-free population display-only (D36).
- [ ] `MomentumModel`: skip-month; hysteresis prevents boundary churn; vol overlay PIT with LW covariance; capacity-rejection logging.
- [ ] `MeanReversionModel`: trend filter; reference-matched RSI; exits via `TargetOrTimeStop` only; sparse-day behavior; **trade-track CI + MDE verified on synthetic clustered trades (D44)**.
- [ ] `LowVolModel`: rebalance-only changes; EODHD-fed sector cap enforced; reclass applies at next rebalance.
- [ ] `ResidualMomentumModel`: beta-driven raw return scores **below** equal-raw-return-with-positive-residual (the test that separates it from plain momentum); rolling regression is PIT; factor-return availability lag enforced; depth guard omits short-history names; leakage test through the regression.
- [ ] `TimeSeriesMomentumModel`: downtrend name scores 0 and is not held (defensive drop-to-cash verified); falling fixture reduces invested count; `volScaled` sibling continuous; sign-flip exit fires on trend reversal; sign-mode ties break by the §3 seeded order; anti-clone check vs Breakout; leakage test.
- [ ] `BettingAgainstBetaModel`: low-beta outranks high-beta; **high-vol-low-beta name outranks low-vol-high-beta name** (separates it from low-vol); LedoitWolf beta matches OLS sibling within the §6.7 shrinkage-aware tolerance on a fixture; beta PIT; rebalance-only re-score; sector cap; depth guard; anti-clone check vs `lowvol:v1`; leakage test.
- [ ] `BreakoutModel`: current-bar exclusion; channel exit; binary-score ties break by the §3 seeded order; trade track wired.
- [ ] Fundamental models (Phase 8): **§7.0 PIT protocol passed and logged before any code**; as-reported availability-lagged join; depth test.
- [ ] `BlendedModel`: weighted exact; out-of-fold provenance; logistic before LightGBM; retrain ⇒ new id + trial; **mechanical signals only (the LLM is the contestant seat in MASTER §23, not a blend sub-signal).**
- [ ] Calibration over declared horizons with overlapping-label correction; Kelly `b` shrunk with min-sample; inverse-vol default (LW covariance).
- [ ] CandidateFactory: family diversity; anti-clone; population hookup per cadence.
- [ ] D43 costs on every strategy; net ≤ gross; cost-model version stamped; ledger/corporate-action conventions tested.

---

*Strategy Catalog v1.9. Diverse, well-studied factor families implemented point-in-time on a security-master identity spine, each with a declared horizon and exit, judged only by forward paper P&L — beta-adjusted, net of a parameterized cost model, ranked against a population of turnover-matched random controls. Paper-trading / research only — not investment advice.*
