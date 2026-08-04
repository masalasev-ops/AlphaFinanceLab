# Phase 5 — the per-checkpoint build prompts (5.0 … 5.8) — **PHASE COMPLETE**

> **Status: every checkpoint below shipped (v1.9.60 → v1.9.68), plus the live smoke test on both pinned
> tiers (v1.9.69) and the post-close reconciliation that caught the drift 5.8 missed (v1.9.70).** These
> files are kept as the built record — what was asked for, and the rails it was built under. They are no
> longer instructions. The DoD evidence lives in PROGRESS's Phase-5 gate box; the narrative in
> CHANGELOG v1.9.60–v1.9.70.

*One file per checkpoint. Each is a **ready-to-paste Claude Code prompt**: what to build, the rails it must
not cross, the fixtures that gate it, and the traps that are already known. This folder is the executable
form of the Phase-5 prompt in [`BUILD_AND_PROMPTS_v1.9.md`](../BUILD_AND_PROMPTS_v1.9.md) §4 — that
paragraph stays the authority on scope; these files expand it into build order without adding scope.*

## Why this folder exists

The Phase-5 prompt is a single ~9 KB paragraph carrying FR-21..23, D46, D79–D82, D104/D105, D110 and now
D112/D113. It is *complete*, which is why it is authoritative — and unusable as a working instruction in
one sitting, which is why it is expanded here. Phase 0 and Phase 4.5 both solved this inside the prompt
(checkpoints 0.1–0.6 and 4.5.1–4.5.5); Phase 5 is large enough that the expansion earns its own files.

**Scope is never added here.** If one of these files disagrees with the BUILD prompt or a MASTER §2 row,
the prompt and the register win, and the disagreement is a finding — that is the rule-25 discipline applied
to this folder rather than an exception to it.

## The checkpoints

| # | File | What lands | Gate |
|---|---|---|---|
| 5.0 | [5.0-prep.md](5.0-prep.md) | **DONE (v1.9.60)** — D112, D113, the model tier, the decomposition, findings 314–322 | `check-register` clean; no `src/` |
| 5.1 | [5.1-provider-seam.md](5.1-provider-seam.md) | **DONE (v1.9.61)** — `IAnalysisProvider`, the Anthropic Batches client, the D24 budget, M7 | `FR21_CacheHit_CostsZero` |
| 5.2 | [5.2-news-budget.md](5.2-news-budget.md) | **DONE (v1.9.62)** - the D46 news budget, enforced pre-token | `FR22_NewsBudget_CapsAndDedupes`, `FR22_Budget_DegradesInOrder` |
| 5.3 | [5.3-pipeline-stage3.md](5.3-pipeline-stage3.md) | **DONE (v1.9.63)** - Stage 3 of the D53 pipeline + the daily regime brief | `FR21_Replay_HasNoAnalysisPath`, the FR-29 post-commit test |
| 5.4 | [5.4-context-packs.md](5.4-context-packs.md) | **DONE (v1.9.64)** - `ContextPackBuilder`, `ai_context_packs`, the digest seam, the common floor field | `FX-PackWatermark`, `FX-PackNoLeak` |
| 5.5 | [5.5-ai-decisions.md](5.5-ai-decisions.md) | **DONE (v1.9.65)** - `ai_decisions` persist-before-use, per-seat budgets, reproduce-day | `FX-AiDecisionIsTheRow`, `FX-BudgetAbstain`, `FX-ReproduceDay-AiSession` |
| 5.6 | [5.6-researcher-seat.md](5.6-researcher-seat.md) | **DONE (v1.9.66)** - the hypotheses/brief/skeptic endpoints + the D112 evidence diet | `FR23_Hypotheses_RequireParentEvidence`, `FX-EvidenceDietRefusal` |
| 5.7 | [5.7-proposal-inputs.md](5.7-proposal-inputs.md) | **DONE (v1.9.67)** - the D110 **inputs** + the D113 paper control | `FX-ProposalPriorRequired`, `FX-ProposalScorePinBeforeProposal`, `FX-ProposalScoreIsMechanical` |
| 5.8 | [5.8-reconciliation.md](5.8-reconciliation.md) | **DONE (v1.9.68)** - measured numbers, gate boxes, corpus reconciliation | The phase DoD |

## Rails that bind every checkpoint

These are not restated in each file; they hold throughout.

1. **Forward-only (D16, rule 13).** The replay composition root registers no `IAnalysisProvider`.
   Compile-time absence is preferred to a runtime guard.
2. **Budget before tokens (D24, rule 13).** Never reconciled after the fact.
3. **Rule 32.** No AI output is an input to anything that judges AI output — no monitor signal, gate input,
   allocator term, population comparison, **or context-pack field** reads `ai_context_packs` /
   `ai_decisions`.
4. **The reference graph** (`ci.ps1`). `AlphaLab.Llm = (Core)`; `AlphaLab.Api = (Core, Data, Evaluation)`.
   The Api never runs long work on a request thread — it enqueues and returns 202 + job_id.
5. **Rule 14.** Every migration is snapshot-first, integer PKs are plain rowids (delete the generated
   `AUTOINCREMENT` annotation), and SCHEMA is updated in the same PR.
6. **Rule 25.** A register row is changed only by another register row.
7. **Branch + PR always.** Never a commit to `main`.

## How to use a file

Read the checkpoint's file, then the sections of the corpus it names (its own "Read first" list — that is
the phase diet applied per checkpoint). Build. Run `tools/ci.ps1`. Open the PR.
