# Phase 5.5 — the construction question (5.5.1 … 5.5.4) — **PHASE COMPLETE**

> **Can this arena adjudicate a realistic edge, or only an implausible one?**
> One measurement, one decision, a hard stop either way.

> **Status: shipped v1.9.88, and the answer is NO.** Over the full stored history (6,287 sessions, 912
> securities, 233–245 monthly rebalances per signal), two of seven signals gain materially from a
> long-short construction (`bab:L252` 1.60×, `lowvol:L252` 1.71×), **three are worse**, and the best
> result anywhere — `bab:L252` at **51 years to detect** — is still five times the ten-year gate horizon.
> Under the rule in [5.5.3](5.5.3-the-decision.md), written before the numbers were read, that is *"real
> but insufficient"*: the long-short **build is not started**, and the horizon is a separate decision
> that is **not** reopened to rescue this one.
>
> **The larger result, which outlives the long-short question:** at H = 10 years this arena can only
> adjudicate a strategy whose active-return information ratio is at least `ZSum/√H` = **0.886**,
> sustained. That follows from the horizon and the confidence/power pair alone. The best of the fourteen
> measured (signal × construction) pairs is **0.392** — a factor of 2.3 short, and a gap that belongs to
> the **signals and the horizon**, not the construction, which is why changing the construction could not
> close it. Phase 6 should carry that number.
>
> Report: `docs/calibration/sp500/2026-08-04-construction-study.md` (committed). These files are kept as
> the built record — what was asked for, and the rails it was built under. They are no longer
> instructions.

> ⚠️ **Do not confuse this folder with `docs/phase5/5.5-ai-decisions.md`.** That file is Phase 5's
> *checkpoint 5.5* (`ai_decisions`, shipped v1.9.65). This folder is **Phase 5.5**, a separate phase
> whose checkpoints are numbered 5.5.1–5.5.4. The names sit one character apart and the collision is
> real; it is recorded here rather than left for a reader to trip over. When citing, always write the
> full path.

*One file per checkpoint, each a ready-to-paste build prompt: what to build, the rails it must not cross,
the fixtures that gate it, and the traps already known. The authority on scope is
[`BUILD_AND_PROMPTS_v1.9.md`](../BUILD_AND_PROMPTS_v1.9.md) §4.5 and the MASTER §2 rows (D123); these
files expand that into build order without adding scope.*

## Why this phase exists

The detectability floor is `ZSum · TE / √H`, so **tracking error decides what this arena can adjudicate
at all**. Generation 2 froze the admissible band at roughly **[6.95 %, 32 %]/yr** — measured under the
only construction the lab has ever run: long-only, judged against a broad benchmark. Realistic factor
tilts are a few percent a year. If that gap is structural, the arena can only ever adjudicate claims too
large to be true, and every honest researcher proposal would be refused `below_floor`.

The operator opened the long-short option explicitly ("*I am not opposed to it… if some of these
strategies require buying the winners and short-selling the losers I am all for it*"), and D122 had
already made the expectation a **measured property of the construction and the arena** rather than an
asserted constant. Phase 5.5 is the measurement D122 implies: **measure the construction before
building for it.**

**It is bounded by construction.** Phase 5.5 ends at a decision. If the answer is yes, the long-short
*build* is its own phase — ledger, `TradeSide.Short`, borrow cost, corporate-action inversion, margin,
new control populations, a new calibration generation. None of that is in scope here, and letting it
leak in is the failure mode this boundary exists to prevent.

## The checkpoints

| # | File | What lands | Gate |
|---|---|---|---|
| 5.5.1 | [5.5.1-measurement-harness.md](5.5.1-measurement-harness.md) | The `construction-study` verb, the engine, the fixtures. Report-only. | `FR47_ConstructionStudy_*` (10 fixtures), `ci.ps1` |
| 5.5.2 | [5.5.2-the-run.md](5.5.2-the-run.md) | The full-history run on the live store; the archived, **committed** report | The artefact tracked, not regenerable away |
| 5.5.3 | [5.5.3-the-decision.md](5.5.3-the-decision.md) | The operator's decision, on the numbers and the rule text | A recorded decision either way |
| 5.5.4 | [5.5.4-records.md](5.5.4-records.md) | MASTER §2, CHANGELOG, PROGRESS, TEST_PLAN, MANIFEST | `check-register` clean |

## The decision number — and the trap inside it

**Do not compare the two constructions' floors.** A long-short book is roughly **2× leverage on the same
cross-sectional bet**: it scales tracking error *and* effect together, so the floor rises exactly as fast
as the effect that must clear it. The t-statistic is `IR·√T`, so detectability depends on the
**information ratio** alone.

This is not a hypothetical. The first version of the report led with the floor ratio, and the live smoke
run refuted it immediately — `resmom:L252` came back **TE ×3.01, effect ×3.01, IR 0.374 → 0.373**. The
construction bought leverage and nothing else. `FX-ConstructionScaleFree` now locks the arithmetic so
the mistake cannot return.

The comparable quantity is **years-to-detect = `(ZSum / IR)²`**, and the decision reads:

| result | conclusion |
|---|---|
| IR gain **materially > 1** and years-to-detect falls inside the gate horizon | Long-short is justified. Proceed to the build phase. |
| IR gain **≈ 1.0×** | The construction bought leverage, not information. **Long-only stands; weeks saved.** |
| IR gain > 1 but years-to-detect still beyond the horizon | Real but insufficient — recorded, and the horizon question is a separate decision. |

## Rails that bind every checkpoint

1. **Report-only (D117 clause 1).** The verb writes one markdown artefact under `docs/calibration` and
   never a row, a flag or a config value. No `SoleWriterGate`, no transaction.
2. **Point-in-time (rule 4).** The scoring path uses the real `BarFeatureView` — the one class that owns
   the watermark rule. The study deliberately implements **no second point-in-time view**; the
   realisation panel is backward-looking by definition and is the `BandInputs` precedent.
3. **D91 descriptive-only.** Informing a *build* decision is not the allocator, a gate, sizing or
   eligibility. `AlphaLab.Evaluation/Construction` is deliberately **not** among the consumer directories
   `ci.ps1` scans, and must never be added to them.
4. **The output may never set a pre-registered `expected_effect_ann` (rule 16 / D52).** Choosing the
   number you then pre-register by looking at measured data is exactly what pre-registration prevents.
   The study answers *"which construction?"*, never *"what should I claim?"*.
5. **Borrow is an assumption, not a measurement.** D43 has no borrow term and this arena buys no borrow
   data. Both assumptions are reported; the 0 bp column is the optimistic bound that can settle a
   negative answer outright.
6. **Rule 25.** A register row is changed only by another register row.
7. **Branch + PR always.** Never a commit to `main`.

## How to use a file

Read the checkpoint's file, then the corpus sections it names. Build. Run `tools/ci.ps1`. Open the PR.
