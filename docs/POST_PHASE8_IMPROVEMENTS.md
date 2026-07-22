# Post-Phase-8 Improvements: What and Why

Purpose: a standing record of the lab improvements agreed for later, explaining what each one is and why it earns its place, so the reasoning is not lost while the core build (Phases 0 to 8) proceeds. This is the companion to POST_PHASE8_PLAN.md, which carries the build order, the prerequisites, and the definition of done. This doc answers what and why. That doc answers when and how.

As of this writing the build is at Phase 3 (the honest arena). Nothing here is built during Phases 0 to 8 except the named near-term hookups, which are small seams placed in the upcoming phases so the post-8 work is cheap to finish.

## Governing principle

The lab is already well built for its two immediate jobs: pruning dead weight quickly and falsifying honestly. The largest remaining risk is not that it fails to find a winner. It is that it crowns a paper winner that is not a real winner. Everything paper cannot see (market impact at size, borrow costs, and regimes the strategy never lived through) sits in the gap between a clean equity curve and a deployable edge. So this work is ordered to make a verdict more true before making the search wider. Validity first, then power.

## Two things that are easy to confuse

Fundamentals (Phase 8, part of the current design) is a bigger search space. It lets the lab look for value, quality, and growth strategies it currently cannot express with price data alone. It is not an improvement mechanism, it is a wider field.

The improvements below are about getting more truth and more reach out of whatever space the lab searches. Most of them feed the machinery you already have. One of them, the Learning Researcher, is the single meta-level self-improvement that is legitimate in a market, because it improves the research process rather than trying to build a trade algorithm that beats a moving target.

## Considered and declined

Real-money confirmation sleeve. A tiny real allocation to a crowned strategy, to measure live-minus-paper degradation. Declined on 2026-07-20: it requires trade execution through a broker, which brings execution risk, tax complexity, and ongoing cost that are not acceptable. It is recorded here so the decision is deliberate rather than forgotten. If that judgement ever changes, this is the single most direct validation of the whole lab, because it is the only thing paper cannot measure.

## The near-term hookups (built earlier, not post-8)

Three small provisions go into the upcoming phases so the later work is cheap. They are seams, not features. They persist data or expose a switch, and none of them does anything on its own. The plan doc details them. In brief: Phase 4 persists replay results broken down by regime episode and gains the ability to split replay history into named learn and validate periods, and Phase 5 makes the researcher evidence prior a swappable input that can be disabled or fed a placebo. The one real build that travels with them is the detectability-at-admission gate in Phase 4, which is a feature in its own right, not a seam. The reason to place all of this early is that a later read-model or a later validation is cheap only when the rows and switches it needs already exist. This is the same lesson as the cohort maturation curve, which is cheap only because the S3 percentile rows were persisted. Separate from these seams, the Phase 4.5 Signal Library (D91) is a full phase, not a hookup; its per-signal digest is the scheduled first occupant of the Phase 5 seam.

## The improvements

### 1. Capacity and market-impact model

What: replace the flat per-name cost buckets with a cost that scales with participation rate (order size as a fraction of a name's average daily volume). Each strategy then carries an implied capacity ceiling, the AUM at which its net edge crosses zero.

Why: paper trading assumes fills. A strategy that looks strong at 100k can have zero net edge at 10M once impact is paid. Without this, the lab cannot tell a scalable edge from a paper curiosity, and it would manufacture false winners in less-liquid names.

When: bundled with the SP1500 widening as its hard prerequisite, whenever that widening clears its membership-source gate (D87). It is only load-bearing when trading less-liquid names, so at SP500 it is informational rather than protective. It can be pulled earlier purely to attach capacity ceilings to large-cap winners, but its protective value starts at SP1500.

Depends on: the existing D43 cost-model seam (it drops in as a layer) and the volume data already ingested in bars.

### 3. Multi-regime survival requirement

What: a crowning condition that a winner has shown non-negative edge across at least two distinct regime types, plus a regime-conditional verdict display so it is visible where the edge actually lives.

Why: a strategy certified over a stretch that was mostly one regime has not been tested against regime change. Requiring regime coverage guards against crowning something that only works in the conditions it happened to be born into. It is the same principle as the existing edge-plant survival floor: do not kill honest lumpy winners, but do require robustness before promotion.

When: first post-Phase-8 pass, paired with improvement 4. The descriptive display is a read-model addition. The crowning threshold calibrates from the Phase-4 per-regime breakdown stored via the near-term hookup, with no replay re-run.

Depends on: regime_episodes and the regime labeler (already built), the promotion gate (Phase 3), and the Phase-4 per-regime replay persistence hookup.

### 4. Lab-level power accounting (exhaustion readout)

What: a descriptive KPI that summarizes, given all trials spent and the MDEs achieved across candidates, the probability that a tradeable edge above a stated size exists in this universe and was missed.

Why: it turns "no winner after three years" from a shrug into a conclusion. There is a real difference between failing to find an edge and being able to say, with quantified confidence, that there is not one above a given size. This is the capstone of the lab's falsification mission, and it is the aggregate cousin of the detectability-at-admission gate.

When: first post-Phase-8 pass, alongside improvement 3. It is descriptive and reuses data already persisted (trials_registry, power_reports), so it needs no new infrastructure. It only becomes meaningful once many candidates have accumulated, which is naturally late.

Depends on: trials_registry and power_reports (Phase 3), and the detectability-at-admission gate (Phase 4) as its admission-time precursor.

### 5. Independent breadth via a cross-asset sleeve

What: add a sleeve of other asset classes (rates, commodities, FX, credit, via liquid ETFs) whose signals are close to independent of the equity cross-section, rather than only adding more correlated US equity names.

Why: the breadth benefit to finding and compounding edge (the Grinold root-N argument) is real only when the added bets are independent. 1500 US equities are highly correlated, so widening within equities buys far less effective breadth than it appears. Genuinely uncorrelated sources multiply effective breadth much more.

When: last, and on its own phase, after the SP1500 equity widening has proven the widening machinery. It is the largest lift of the five, since it brings new data, new cost models per asset class, and new compute scale.

Depends on: the equity-widening infrastructure (improvement 1 plus SP1500) being solid first.

### 6. The Learning Researcher (guarded)

What: give the researcher seat an evidence prior, an auditable, human-readable digest of what the lab has actually learned from its own math verdicts (which hypothesis families cleared their MDE, at what track lengths, in which regimes, and which died), fed into the researcher's context pack so it proposes better over time. The defining feature is not the learning, it is the guarding, because a learning researcher that is not guarded fits the lab's own noise and only appears to improve.

Why: this is the only compounding form of self-improvement that survives contact with a market. It improves the research process rather than a trade algorithm, so it does not fight the non-stationarity that dooms self-improving trade algorithms. The design already has the bones: the researcher must cite evidence to propose (D82), pre-registration captures each hypothesis (D52), the outcome record is persisted (journal_entries, trials_registry, go_live_log, overfitting_status), and the cohort maturation curve plus researcher yield already exist as the yardstick for whether the researcher is improving. What is missing is the digest that turns the record into a usable prior, and the guards that keep that learning honest. The signal-level half of that record now has a scheduled source: the Phase 4.5 Signal Library (D91) produces a per-signal digest line (1y rank-IC, 5y rank-IC, trend flag) that enters the evidence prior through the Phase-5 seam.

When: post-Phase-8, in the second post-8 pass. It needs an accumulated body of outcomes to learn from, the same lateness constraint as the cohort curve, and it pairs naturally with the Phase-8 fundamentals expansion, since a smarter searcher pays off most when the space just got bigger. Building it earlier would give it almost nothing real to learn from, so it would fit noise, which is the exact failure the guards exist to prevent. The signal record is the one exception to that lateness: the Phase 4.5 backfill (D91) gives the digest two decades of rank-IC history on day one, so the prior's signal lines are populated long before the lab's own outcome record matures.

Depends on: the Phase-5 swappable evidence prior hookup, the Phase-4 learn-validate partitioning hookup, the Phase 4.5 Signal Library digest (D91), accumulated outcomes, and the Phase-3 deflated-Sharpe trials accounting.

The five guards, each mirroring a strategy-level discipline the lab already uses:

1. Control researcher (the matched-null idea, one level up). Run a baseline researcher with no learned prior, or a shuffled placebo prior, alongside the learned one. The learned researcher's cohort curve must beat the control's by more than the MDE, or the learning is noise and does not ship. A rising cohort curve on its own proves nothing, since drift or luck can lift it. A rising curve relative to a no-learning control is the real signal. This is the strongest guard.

2. Out-of-sample validation (forward-only certification, applied to the prior). Build the prior on one slice of history and measure its benefit on a slice it never saw. This lives in replay, the only place with enough partitioned history to learn on period A and validate on period B without waiting years. If the prior helps in-sample but not out-of-sample, it memorized noise.

3. Frozen, pre-registered learning rule (D52, applied to the researcher). Fix in advance how the researcher digests the record into a prior, and freeze that rule the way strategy recipes are frozen. Being able to tweak the rule after seeing whether cohorts rose is an enormous invisible degree of freedom. Changing the rule is a new researcher generation, not an in-place edit.

4. Inflated trials accounting (the deflated-Sharpe budget, applied to implicit search). A learning researcher runs far more implicit tests than its explicit fork count shows, because every lesson is a search over the record. If the budget counts only explicit forks it understates the multiple-testing burden, so a learned proposal carries a higher significance bar. Even a conservative inflation is protective.

5. Coarse, auditable digest (prefer-simple, applied to the prior). The finer and more expressive the prior, the more noise it can store. A human-readable digest with few degrees of freedom cannot memorize much. This is the case where auditability and overfitting resistance point the same way.

One honest residual: there is one finite market history, and every learn-validate split reuses the same underlying data, so the out-of-sample check is never as clean as a domain with a fresh stream. The guards force meta-overfitting to clear a real bar and make it much harder to hide, but they cannot make it impossible. The only fully trustworthy evidence that the researcher improved is fresh forward outcomes accrued after the learning was frozen, and those come in slowly. So even a well-guarded Learning Researcher is adopted cautiously and kept under confirmation, not declared proven the moment replay validation passes.

## Sequence at a glance

1. Upcoming phases (near-term hookups): Phase 4 builds the detectability-at-admission gate and adds the per-regime replay persistence and learn-validate partitioning seams; Phase 4.5 builds the Signal Library (D91), whose per-signal digest is the seam's first scheduled occupant; Phase 5 adds the swappable researcher prior seam.
2. First post-Phase-8 pass: improvements 3 and 4 together. Both are cheap, reuse existing data, and make verdicts more honest rather than widening the search.
3. Second post-Phase-8 pass: improvement 6, the Learning Researcher, built with all five guards from the start.
4. With the SP1500 widening: improvement 1 (capacity and impact model) as its prerequisite.
5. Last, on its own phase: improvement 5 (cross-asset sleeve).

## Numbering note

The item numbers (1, 3, 4, 5, 6) are kept from the original shortlist discussion so they map cleanly to earlier notes. Number 2 was the real-money confirmation sleeve, recorded above under "Considered and declined."
