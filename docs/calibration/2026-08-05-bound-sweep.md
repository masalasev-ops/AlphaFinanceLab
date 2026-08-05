# The 3-E bound sweep — every spend, token, article and scope bound, classified — 2026-08-05, v1.9.92

*Pre-edit sweep artifact for the v1.9.92 annual-budget pass (D130), recorded from the tree BEFORE the pass's edits. Classes: **AUTHORED** (a hand-picked number with no arithmetic behind it), **DERIVED** (computed from a stated formula), **STRUCTURAL** (a statistical-honesty or scope rail, not a spend bound), and — per the R-7 ruling, exactly one member — **PRE-REGISTERED ESTIMATE** (neither authored nor derived: a MODELLED seed carrying its provenance and a derived recalibration trigger). D130's DoD: every AUTHORED spend bound is either derived by this pass or carries its recorded reason for staying authored.*

## Spend (USD)

| Bound | Was (pre-pass) | Class after D130 | Disposition |
|---|---|---|---|
| `Llm.AnnualBudgetUsd` | *(did not exist)* | **AUTHORED — the only one** | The one authored spend number (100). |
| `Llm.DailyBudget.MaxCostUsd` (`LlmOptions.cs`; CONFIG; Worker+Api appsettings) | AUTHORED 1.00 | **DERIVED** | round(committed/252 × 1.15, 2) = 0.39. |
| `Ai.Contestant.DailyBudgetUsd` (`AiOptions.cs`; CONFIG; Worker) | AUTHORED 0.05 | **DERIVED** | round(committed × 0.60/252, 2) = 0.20. |
| `Ai.Researcher.MonthlyBudgetUsd` (`AiOptions.cs`; CONFIG; Worker) | AUTHORED 5.0 | **DERIVED** | round(committed × 0.40/12, 2) = 2.83. |
| `ResearchJobExecutor.EstimatedArmCostUsd` (`src/AlphaLab.Worker/Ops/ResearchJobExecutor.cs:60`) | AUTHORED 0.25m, **in code, outside CONFIG_REFERENCE** | AUTHORED — **stays, finding 381** | An authored dollar figure gating the researcher's pair-headroom check; a calibration input to D130's caps. Phase 6: config-ify with a derivation or a recorded reason. Not fixed in this pass (src/ behaviour change beyond the sanctioned estimator fix). |
| `Accounts.StartingCash` (CONFIG:77) | AUTHORED 100000 | AUTHORED — stays | Paper-account convention, not a spend bound: it prices nothing and pays nobody. |
| `Llm.Pricing.*`, `BatchDiscountMultiplier`, `CacheRead/WriteMultiplier` | vendor facts | vendor facts | Dated provider facts (INTEGRATIONS discipline), not authored choices. |

## Tokens

| Bound | Was | Class after D130 | Disposition |
|---|---|---|---|
| `Llm.DailyBudget.MaxTokens` | AUTHORED 0 (**disabled** — finding 320's knob with no value) | **DERIVED** | floor(MaxCostUsd / (mean uncached input rate / 1e6)) = 130,000; now ENFORCED. Overshoot defect recorded as **finding 382** (guard is `state ≥ cap`, admits one call past the limit; Phase 6 aligns it with the cost guard's pre-flight shape). |
| `Llm.DailyBudget.MaxCalls` | AUTHORED 10 | AUTHORED — stays | A call-count rail, not a spend bound: cost and tokens are the money dimensions; calls cap runaway loops. Reason recorded here. |
| `Llm.Tasks.*.ExpectedOutputTokens` | *(did not exist)* | **PRE-REGISTERED ESTIMATE** (the class's one member) | MODELLED seeds (700 compact / 1,500 long-form; provenance: the v1.9.91 design conversation), feeding the pre-flight estimate instead of the ceiling (**finding 380**, the lockout). Recalibration trigger derived: p90 of `analysis_cache` actuals after N = MaxCalls × the 21-session window. |
| `AnthropicProviderOptions.MaxOutputTokens` (`AnthropicAnalysisProvider.cs:14`) | AUTHORED 8192, never bound, undocumented | AUTHORED — stays, **re-scoped** | Demoted to the API hard cap ONLY (thinking + response headroom); no longer the estimate's output term. Its config-ification is not needed while it is a pure wire ceiling. |

## Articles / text

| Bound | Value | Class | Disposition |
|---|---|---|---|
| `Llm.NewsBudget.MaxArticlesPerRead` | 25 | AUTHORED — stays | The real token lever (D46; MASTER §7: "the admitted text is the sink"). Reason for staying authored: it bounds CONTENT quality/recall, not spend — deriving it from dollars would let a price cut silently widen what the lab reads, which is an editorial decision, not a budget one. Its spend consequence is already captured by the derived MaxCostUsd/MaxTokens ceilings downstream. |
| `Llm.NewsBudget.MaxCharsPerArticle` | 2000 | AUTHORED — stays | Same reason; a truncation shape, not a spend cap. |

## Scope / structural (NOT spend caps — out of D130's derivation by classification)

| Bound | Value | Class | Note |
|---|---|---|---|
| `Ai.Contestant.ShortlistSize` | 25 | **STRUCTURAL** (D24 scope rail) + a D130 derivation note | The budget-affordable N at the derived daily budget is ~416 (MODELLED 72 in / 24 out per name), so the SCOPE cap binds, not the budget (it would bind only below ~$6/yr); the value is FROZEN per generation into `config_json` (3-C). `FX_ShortlistDerivation` pins the non-inversion. |
| `Research.MaxConcurrentCandidates` | 3 | STRUCTURAL | Explicitly OUT of scope (the 3-E instruction): a statistical-honesty rail (§8 roster shape; the D112 diet bound), not a spend cap. |
| `Research.ForkBudgetPerYear` | 6 | AUTHORED — **stays, by recorded resolution** | Finding 309's resolution binds: the value cannot be fixed by a config edit or by this derivation — a change needs a decision amending D82, and the recorded relief is the PER-ARENA tax (D109/D110). The one authored bound D130 deliberately does not touch. |
| `Llm.ScopeLevel` | 1 | STRUCTURAL | The D24 scope ladder position, not a spend bound. |
| `Guardrails.MaxConcurrentPositions` | 60 | STRUCTURAL | Exposure rail. |
| `Guardrails.DrawdownCircuitBreakerPct` | 25.0 | STRUCTURAL (risk rail; unread until the Phase-6 pull-forward, finding 376) | Not a spend bound. |
| `Populations.Size` / `CostFreeSize` | 200 / 50 | STRUCTURAL | The D36 null machinery. |
| `Gate.DetectabilityHorizonYears` | 10 | AUTHORED by decision (D121) | A patience statement, not a spend bound; changeable only by a row. |
| Gate ceiling 32%/yr | derived (D116) | DERIVED | The precedent this pass's derivation-is-the-content shape imitates. |
| `Backfill.ApiPlanLimit` | 100000 | vendor fact | EODHD plan limit. |

## The three findings this sweep raises (recorded in CHANGELOG v1.9.92, each with its Consequences field)

- **finding 380 — the MaxOutputTokens LOCKOUT** (fixed IN this pass): the pre-flight estimate passed the 8192 ceiling as its output term (`BudgetedAnalysisProvider.cs`, both call sites), and output dominates cost — against the derived 0.20/day contestant cap an Opus call estimated ~0.28 (0.07 input + ~0.20 assumed output, batched arithmetic scaled accordingly) vs ~0.09 actual at the MODELLED real output, so the guard refused BEFORE spending: the seat would abstain daily and the ledger would show zero spend while the budget sat unused. Sonnet-tier arithmetic passes with no headroom for a second call. Fixed by the pre-registered `ExpectedOutputTokens` seeds; a fixture proves the estimator reads the seed and not the ceiling.
- **finding 381 — `EstimatedArmCostUsd` is an authored dollar figure living in code** (Phase 6): outside CONFIG_REFERENCE, gating the researcher's budget check; a calibration input to D130's caps — until it is calibrated, the budget binds on the estimator, not the money.
- **finding 382 — the MaxTokens overshoot** (Phase 6): the token guard is backward-looking (`state ≥ cap`) where the cost guard is pre-flight (`state + estimate > cap`), so a token ceiling admits one call past its limit. Stated in the derivation note; alignment is a named Phase 6 item.
