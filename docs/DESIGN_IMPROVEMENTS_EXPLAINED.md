# Design Improvements — The Plain-Language "Why"

*A companion to `DESIGN_IMPROVEMENTS_v1.9.md`. That document is the engineering spec — the exact formulas, the estimators, the config knobs. This one is for onboarding: it explains **why** each of those choices was made, in plain English, so that months from now (or a new collaborator) can understand the reasoning without re-deriving the mathematics. Where you want the actual formula, the section numbers match the spec (its §3.3 is this doc's §3.3). Research/paper-trading only — not investment advice.*

---

## The one idea this whole document serves

Everything in the spec exists to answer a single question honestly: **"Does this strategy actually have an edge, or does it just look like it does?"**

That sounds simple, but it is the hardest question in investing, because random luck is very good at impersonating skill. Flip a coin for a strategy 100 times and some of them will show a "track record" that looks brilliant — purely by chance. A naive lab would crown those winners and lose money on them forever. This lab is built, top to bottom, to *not fool itself* that way. Almost every piece of machinery in the spec is a specific defense against a specific way you could be fooled.

Keep that frame and the rest of the document stops looking like a wall of statistics and starts looking like a list of traps and the guard built for each one.

---

## §1 — How we measure a strategy (and why "return" isn't the measure)

**The trap:** the obvious way to rank strategies is by how much money they made. That's almost worthless on its own, because a strategy can make money three dishonest ways: by taking more risk, by riding the overall market up (not by being clever), or by getting lucky. High return tells you nothing about which of those happened.

**What we do instead**, and why each piece matters:

- **We measure *alpha*, not return** (§1.1). Alpha is "how much better did you do than you'd expect *given how much you just rode the market*." If the market went up 20% and your strategy went up 22% while swinging just as hard as the market, your real skill contribution is roughly 2%, not 22%. Everything is judged on this beta-adjusted number, because raw return flatters strategies that are secretly just "own the market with extra leverage."

- **We never show a number without a margin of error** (§1.2, the "MDE"). This is the heart of the lab. Any two strategies show *some* gap between them — but is the gap real, or is it noise? The MDE ("minimum detectable effect") is the honest answer to "how big would the gap have to be before we could believe it, given how little data we have so far?" If your strategy beats the benchmark by 1.8% but the margin of error is ±4.6%, the honest verdict is **"too early to tell"** — and the system says exactly that, rather than pretending 1.8% is a win. This is why the interface constantly shows things like "gap +1.8% · MDE ±4.6% — too early to judge."

  - The one refinement worth understanding: an early version of this math assumed each day's result was independent of the last. It isn't — a strategy holds the same stocks day after day, so its good and bad days come in clusters. Ignoring that clustering makes the margin of error look *smaller* than it really is, i.e. it makes the honesty tool itself overclaim. The Newey–West correction (the scary name in §1.2) is just "account for the fact that the days are correlated, so the margin of error tells the truth." The lab refuses to let even its honesty tool cheat.

- **We keep a separate "per-trade" scoreboard for high-frequency strategies** (§1.3). A strategy that makes hundreds of trades can be judged faster on a different question — "does the average trade make money after costs?" — which needs far less time than the alpha question. This is a *falsification accelerator*: it can kill a bad fast-trading strategy quickly, but it's never allowed to *promote* one on its own (making money per trade doesn't prove skill; it needs the full alpha test for that).

- **We run every strategy through a "what are you really made of?" X-ray** (§1.4, factor attribution). This decomposes a strategy's returns into known, well-documented ingredients — market exposure, momentum, company size, etc. The punchy example in the spec: *"your 'clever' strategy = 0.92 market + 0.31 momentum + noise."* Translation: it isn't clever, it's 92% just-owning-the-market plus a known momentum tilt. This is diagnostic only — it never decides anything — but it keeps you honest about what you've actually built.

- **We count how many market "episodes" a claim is based on** (§1.5). If a strategy "does great in bear markets" but there's only been *one* bear market in your data, that's a story, not evidence. The system literally tags such claims with an "anecdote" badge until there are enough episodes to mean something.

**The through-line of §1:** every metric is paired with a statement of *how much you're allowed to trust it yet*. That pairing is the product.

---

## §2 — What edges actually exist, and how much they shrink in real life

**The trap:** the finance literature is full of "anomalies" — momentum, value, low-volatility, quality — that made money in academic studies. It is dangerously easy to assume those premiums will show up, in full, in your account. They won't, and building as if they will guarantees disappointment.

**What this section is:** a reality-check on expectations, so the whole system is calibrated to a *realistic* prize rather than a fantasy one. The key points in plain terms:

- These edges are real but **published in an idealized form** — usually as "long-short" strategies (simultaneously betting for the good stocks and *against* the bad ones), with no trading costs, across thousands of stocks.
- This lab is **long-only** (it can only buy, not short), **large-cap only**, and **pays realistic costs**. Each of those strips away a chunk of the published premium. The short leg is often where a lot of the edge lived; costs eat most of the fast-reversal edge; large-cap-only removes the small-company premium.
- **Edges decay after publication** — once everyone knows about an anomaly, it roughly halves. So the design assumes decay rather than a forever-edge.
- **The realistic prize is 1–3% per year of genuine alpha — and some years it's negative.** This single number is the most important calibration in the whole system. Every margin of error, every "how long until we know" estimate, every screen is tuned to detecting an edge *that small*. That's why patience is built into everything.
- **The real edge is diversification.** Because these strategies zig and zag at different times, a *portfolio* of several mediocre strategies is meaningfully better than any one of them. This is why the "allocator" (§3.5) — which blends strategies — is treated as the main way the lab improves, not the hunt for one killer strategy.

**The through-line of §2:** aim at a small, decaying, realistically-achievable prize — and never let the system's expectations drift back up to the fantasy numbers.

---

## §3 — How much to buy, and the safety rails

This section is the "portfolio construction" plumbing. The formulas are involved, but the *reasons* are simple.

- **Position sizing by steadiness, not conviction** (§3.1–3.2). The default rule is "put less money in the jumpy stocks and more in the steady ones," then scale the whole book so the portfolio's overall bumpiness stays under a target. Why this and not something fancier (like Kelly betting)? Because this rule is *estimable from day one* and *deterministic* — you don't need years of data or a confident forecast to run it. The spec's covariance estimator (the "Ledoit–Wolf" name in §3.1) is just a well-behaved way to measure how stocks move together without the estimate blowing up on limited data.

- **Costs are always on, and always realistic** (§3.3). Every simulated trade pays a commission, a spread, and a "market impact" cost that grows with how much you're trying to trade relative to how much normally trades. And there's a hard cap: you can't pretend to buy more than 2% of a stock's daily volume — attempts beyond that are rejected and logged. **Why this is load-bearing:** the single most common way backtests lie is by ignoring costs. A strategy that's brilliant on paper and worthless after costs is the default outcome, not the exception, especially for fast-trading ideas. Making costs unavoidable is what makes the whole lab trustworthy. (And the cost model stamps its version on every trade, so recalibrating costs later never silently rewrites your history.)

- **Fail closed** (§3.4). Every safety rail — max position size, concentration limits, cooldowns, drawdown circuit-breakers — has the same rule: **if a required input is missing, reject the trade and log why.** Never guess, never default, never silently misprice. A rail that never trips is untested; one that always trips is set wrong; so rejections are surfaced on screen where you can see them.

- **The allocator — how the lab actually "gets better over time"** (§3.5). This is the most important part of §3 to understand, because it's the primary improvement mechanism. Rather than dramatically crowning one winning strategy (which the math says you usually *can't* justify for years), the lab makes **small, continuous, reversible tilts** toward strategies that are doing better — with heavy braking. The braking is the clever part:

  - A strategy with a short track record has a huge margin of error, so the allocator **shrinks it toward the middle** — it gets roughly equal weight, not a big bet, because we don't yet trust its number. (This is the "James–Stein shrinkage" in the spec: borrow strength from the group when the individual estimate is noisy.)
  - A strategy that's flagged as suspicious can only have its weight **decayed down**, never increased.
  - A strategy that's "too early to judge" has a hard cap on how far its weight can move.
  - Weights only move when the change is big enough to matter, and then only partway.
  - And **every single weight is fully reconstructable** — the screen shows the arithmetic behind each allocation, so nothing is a black box.

  The philosophy: under genuine uncertainty, the honest action is a small hedged tilt, not a bold bet. The allocator is that philosophy turned into a mechanism.

**The through-line of §3:** size by what you can actually estimate, make costs unavoidable, fail loudly rather than silently, and improve through small reversible tilts rather than dramatic bets.

---

## §4 — What the AI (Claude) is for, and what it costs

**The goal:** a self-improving system where the AI reads what's in the local store and makes decisions, and those decisions get better over time from the same stored history. The trick is doing that *without* creating an unfalsifiable black box. The design does it by giving the AI three defined seats and making the arena judge each one exactly like any other strategy. (The full, buildable spec is MASTER §23; this is the plain-language version.)

**The three seats:**

- **Researcher (the main one).** The AI reads the lab's own accumulated evidence — which strategies are working, what's been refuted, the factor breakdowns, the regime history — and proposes the next experiments to run. It proposes; *you* approve (pre-register) before anything runs, and every proposal has to cite the evidence it grew from or it's rejected. This is what makes the lab actually improve itself: it turns "what should we try next?" from a manual chore into a loop fed by results. There's a yearly budget on how many new forks it can spawn, so it can't flood the system with half-baked ideas.

- **Contestant (the AI as a trader).** Here the AI *does* score names and trade its own account — the thing the earlier design was afraid to let it do. What makes that safe now is the **twin**: every AI trading account is paired with an identical account that does everything the same way except it makes no AI call. Subtract the twin's results from the AI's, and what's left is the AI's actual contribution, with nothing else contaminating it. If the AI adds nothing, the comparison shows it plainly — and because the two accounts are nearly identical, this is the single fastest, cleanest experiment the whole lab can run (see §6). The AI is allowed to be a stock-picker precisely because its edge is measured in the open, not hidden.

- **Advisor (switched off for now).** The AI could also advise how to split money across strategies, but that job is deferred: the math-based allocator is already good at it, this seat only makes sense once the other two are running, and it's the one closest to real capital — so it stays off until it's proven to beat the allocator in a head-to-head, and it's never allowed to move real weights until then.

**One rule ties it together:** no AI output is ever fed into anything that judges AI output. The scoring and grading machinery stays pure math; the AI competes inside it, it doesn't get to grade itself.

- **The economics are engineered so cost never surprises you** (§4.2). Scheduled reads go through the half-price batch API; the unchanging part of each prompt is cached so you only pay for the day's fresh news; cheap tasks use a cheap model and only the hard tasks use a strong one. The real cost lever is **how much news gets fed in** — so there's a strict budget *before* any tokens are spent: filter to relevant articles, remove duplicates, cap the count, truncate the length. And there are hard daily ceilings with a graceful degradation order, so you can never get a surprise bill.

- **Claude never touches the historical replay** (below). Its outputs are forward-only, by construction.

**The through-line of §4:** use the AI where it's genuinely good (reading, arguing, skepticism), never as a trusted black-box oracle, and cap its cost so it's always priced rather than presumed.

---

## §5 — The "flight simulator" (Arena Replay), and why it never proves a strategy works

**The trap — and this is subtle:** you might think "let me run my strategies over the last 15 years of history and see if they'd have made money." That backtest number is almost worthless as evidence a strategy works, for a deep reason: **the strategies you're testing already exist because they seemed to work.** History has been quietly filtered — the ideas that failed in the past didn't survive to become the strategies you're testing today. So a backtest will always flatter you. This is called survivorship bias, and it's inescapable.

**So what is the replay for?** It's a **flight simulator for the lab itself, not for the strategies.** You don't use a flight simulator to prove the *plane* is airworthy; you use it to check the *instruments and the pilot* behave correctly in known conditions. Same here. The replay runs the entire machine over historical data to answer two questions that have *nothing* to do with whether any real strategy makes money:

1. **"Does the machinery behave correctly on cases where we know the right answer?"** We plant fake strategies with *known* properties — one with a real edge, one with no edge, one that's deliberately *anti*-predictive (worse than random) — and check that the lab does the right thing with each: detects the real edge, quickly flags the anti-predictive one, and correctly labels the no-edge one as "indistinguishable from random" rather than falsely crowning it. If the lab can't get these known cases right, it can't be trusted on real ones. (These planted strategies are carefully made *realistic* — lumpy, regime-dependent, not a naive constant trickle of returns — because a machine that only works on unrealistically clean fakes hasn't been tested on anything like reality.)

2. **"What should the warning thresholds be set to?"** The monitor's alarm levels (how bad is "suspicious", how long to wait before retiring a strategy) can't be sensible guesses — they're *calibrated* against what actually happens in the replay, then **frozen** for real use. Otherwise you'd spend years of real forward operation discovering your alarms were mis-set.

**The quarantine is absolute and worth understanding.** Replay results are tagged, stored separately, never mixed into the real forward views, never allowed to influence a real promotion, never even shown on the same chart as real results. This is enforced in the code and tested. Why so strict? Because a replay result that *looked* like a real track record — even by accident, even for a moment — would reintroduce exactly the survivorship-bias lie the whole system exists to prevent. So the design makes it structurally impossible to confuse the flight simulator with the real flight.

**The through-line of §5:** the replay validates the *lab*, never the strategies; and it's walled off so completely that its results can never masquerade as evidence a strategy works.

---

## §6 — The uncomfortable truth about time (and why that's the design working)

This is the most important section for your own expectations, and the one most likely to make you think the lab is "broken" when it's actually being honest.

**The core fact:** detecting a small real edge takes a *long* time — and how long depends enormously on how cleanly you can compare two strategies. The spec has a table; here's what it means:

- If you compare two very *different* strategies, the noise between them is large, and detecting a 2%-per-year edge could take **decades** — literally ~79 years in the loosest case.
- If you compare two *nearly identical* strategies that differ in just one small way, the noise mostly cancels, and the same 2% edge becomes detectable in **about a year**.

Three consequences the design commits to, so they never have to be re-argued:

1. **"Too early to judge" is the normal state, and it's correct.** The gate refusing to crown winners for months or years isn't a bug or a limitation — it's the system telling the truth about how much evidence exists. A lab that promoted strategies quickly would be *lying* about its statistical power.

2. **The lab's fast, real products aren't "winners" — they're honest negatives.** Two things arrive quickly and are genuinely valuable:
   - **"Indistinguishable from random"** — because every strategy is compared against a matched set of random strategies that pay the same costs, an edgeless strategy will sit right in the middle of that random pack forever. It never gets *falsified* (it's not *worse* than random), but it gets *labeled* "we can't tell this apart from luck" within months. That label is real knowledge, and it's the lab's most common fast output. It's given a first-class name and chip in the interface precisely so it doesn't feel like "the lab has nothing to say."
   - **Fast kills** of strategies that are actively bad — either negative money-per-trade (caught quickly by the per-trade scoreboard) or genuinely anti-predictive (caught by the monitor).

3. **Engineer comparisons to be tight.** Since near-identical comparisons resolve in ~1 year and loose ones take decades, the whole design pushes toward tight comparisons: the AI contestant and its no-LLM twin differ in exactly one ingredient (the LLM decision layer — MASTER §23.3), parameter variants differ in exactly one setting, and the allocator tilts continuously instead of waiting for slow statistical certainty. Pairing tightness is the single biggest lever *you* control over how fast the lab can tell you anything.

**The through-line of §6:** honesty about a small edge means patience is unavoidable — so the design makes the *honest negatives* fast and valuable, and engineers every comparison to shorten the wait for the honest positives.

---

## If you remember only this

The whole system is one stance applied over and over: **assume you're fooling yourself, and build the specific guard that would catch it.**

- Judging on raw return would fool you → judge on alpha, with a margin of error attached to every number.
- Ignoring costs would fool you → costs are always on and unavoidable.
- Trusting a backtest would fool you → the replay validates the *lab*, never the strategies, and is walled off so it can't pretend otherwise.
- Impatience would fool you → "too early to judge" is a first-class verdict, and the honest *negatives* are the fast product.
- An AI black box would fool you → the AI reads and argues; the math judges.

Every formula in the real `DESIGN_IMPROVEMENTS_v1.9.md` is one of those guards, written precisely. This document is just the map of *why each guard is there*.

*Research/paper-trading only — never investment advice.*
