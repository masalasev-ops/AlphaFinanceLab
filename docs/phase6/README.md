# Phase 6 (the mechanical spine) + Phase 6.5 (the AI seat) — the per-checkpoint build record

*One file per checkpoint, on the `docs/phase5/` and `docs/phase5.5/` precedent. Each is a ready-to-paste
build prompt: what to build, the rails it must not cross, the fixtures that gate it, and the traps already
known. This folder is the executable form of the Phase-6 paragraph in
[`BUILD_AND_PROMPTS_v1.9.md`](../BUILD_AND_PROMPTS_v1.9.md) §4 — that paragraph and its v1.9.91 scope
amendments stay the authority on scope; these files expand it into build order **without adding scope**.*

**Scope is never added here.** If one of these files disagrees with the BUILD prompt or a register row in
[`DECISIONS_v1.9.md`](../DECISIONS_v1.9.md), the prompt and the register win, and the disagreement is a
finding — the rule-25 discipline applied to this folder rather than an exception to it.

**Expansion policy, stated so a gap is never mistaken for an omission:** the checkpoint table below is the
authoritative decomposition and carries each checkpoint's scope, DoD and fixtures. A per-checkpoint file is
written when that checkpoint is picked up, not all at once — Phase 5 wrote its nine at 5.0 because the
prompt was one 9 KB paragraph; Phase 6's decomposition is larger and its later checkpoints depend on
decisions the earlier ones make (the `config_json` shape, the parity scope, the S6 remedy), so writing
them now would be writing against unmade decisions.

## Why this folder exists

The Phase-6 prompt is one paragraph carrying FR-5, FR-11 full, FR-13, FR-15, FR-18 full, MASTER §23 in its
entirety, and the whole v1.9.91 amendment block (the breaker pull-forward, the behavioural reads, the
two-light UI, the fixture pairing) plus D130's calibration items. It is *complete*, which is why it is
authoritative — and unusable as a working instruction in one sitting, which is why it is expanded here.

## The split (a BUILD-phasing edit, finding 169's precedent — no D-number)

| | Scope |
|---|---|
| **Phase 6** — the mechanical spine | 6.1–6.14: real strategies, factors + attribution, LW covariance + FR-11 full, the trade track, the breaker, the completed monitor |
| **Phase 6.5** — the AI seat | 6.5.1–6.5.5: the pair instrument, the proposal guards + the D110 scorer, the D127 shortlist, the seat's schema, the contestant + twin, S8's twin input + §3½ |

**The reason, recorded — tractability is not it.** The AI seat is the only part that can fail in a way that
invalidates **design** rather than implementation. Splitting means such a failure does not leave the
mechanical spine unsigned. D127's shortlist depends on a daily dispersion pass over registered signals, so
the dependency already sits exactly where the seam goes.

**Hard constraint.** The contestant, its twin, the divergence index and the paired-difference instrument are
**ONE unit** and may not be separated across phases — a contestant without its twin is unmeasurable, which
is the whole point of D125. All four sit in Phase 6.5.

**Consequence for the monitor.** S8's *twin* input and the §3½ AI-seat handling move to **6.5.5**; S8's
other divergence inputs all exist in Phase 6.

## The checkpoints — Phase 6

| # | What lands | Gate |
|---|---|---|
| 6.1 | This folder; **D131** (the gate's opponent) + **D132** (the features cache); the rewritten gate box + reading diet; the B8 UI ruling; findings 376/381/382; the D130 p90 item | `check-register` clean; no strategy code |
| 6.2 | The lifecycle seam: a **readable `config_json`** (its own row), the config-driven registry, admission→running, the control path | `D132_ConfigJson_RoundTripsEveryFrozenRow`, `FX-AdmittedCandidateOpensAccount` |
| 6.3 | Funnel primitives: **D134** (the seeded tie-break + the `percentile_rank` convention) and **D135** (the declared cadence family); `AllPositive`; the population hookup | `PopulationHookup_MatchesByCadenceFamily`, `FX-SeededTieBreak`, `FX-PercentileRankConvention` |
| 6.4 | Forward membership refresh + the FR-6 divergence alarm (finding 197, **authorized explicitly** — FR-6 is outside Phase 6's FR set) | `FX-ForwardMembershipRefresh` |
| 6.5 | The **S6 remedy** + the monitor→gate consequence rails (two rows) | `FX-RecomputeParity` green + the D117 confirmation slice |
| 6.6 | French factors + RF + the attribution panel (+ a row on attribution persistence) | `FX-FactorIngest`, `FX-AttributionLagNote` |
| 6.7 | Ledoit–Wolf, FR-11-full inverse-vol, heat, the D43 capacity readout | `D42_LedoitWolf_MatchesHandComputedShrinkageIntensity` |
| 6.8 | The D44 trade-level track | `FX-TradeTrack` on synthetic clustered trades |
| 6.9 | The drawdown breaker **WIRED** + UX-20 (+ a row on where a halt persists) | `FX-DrawdownBreakerHalts`, `UX20_*` |
| 6.10 | Momentum + MeanReversion (std + fast); the parity scope and the exit grammar (two rows); **THE FIRST REGISTRATION** | `FX-SignalParity`; a committed forward run; `reproduce-day` byte-identical |
| 6.11 | LowVol + BettingAgainstBeta + the sector cap (+ a row on where the cap binds) | `FX-AntiCloneBabLowVol`, `FX-SectorReclass` |
| 6.12 | ResidualMomentum + TimeSeriesMomentum + RandomPop-Event | F-LEAK **through** the residualization; the lag-hole determinism fixture |
| 6.13 | Breakout (optional; the cut line) + Blended (+ a row on `ToChannelExit` if built) | `Blended_OutOfFoldProvenance` |
| 6.14 | Monitor S1/S4/S5/S7/S8 + **UX-5 full** + the phase close | `FX-MonitorSignals`, `FX-AutoRetire` |

## The checkpoints — Phase 6.5

| # | What lands | Gate |
|---|---|---|
| 6.5.1 | The pair instrument, built against a **SYNTHETIC** pair: the divergence index, the behavioural reads, the D125 paired difference, UX-18 | `FX-TwinDivergenceIndex`; the gap cannot render without its threshold |
| 6.5.2 | The **D110 scorer**, the D126/D128/D129 proposal guards, UX-17 | `FX-InconclusiveScoresZero` (both halves) |
| 6.5.3 | The **D127 shortlist builder**, UX-19, the contestant pack recipe (+ its own row) | `FX-Shortlist{Determinism,SignalSetFrozen,NoDirectionalRead}` |
| 6.5.3b | **The seat's schema, alone** — the `analysis_cache` CHECK rebuild + `ai_decisions` doc catch-up. No seat code. | The row-survival test green; the snapshot retained |
| 6.5.4 | The contestant + its mandatory no-LLM twin, **registered** | `FX-TwinPairing`, `FX-ContestantReplayRefused`, zero API calls on re-run |
| 6.5.5 | S8's twin input, the §3½ AI-seat handling, the phase close | `§3half_ContestantEmitsS1SkipAndS4NotApplicableRows` |

## Rails that bind every checkpoint

Not restated per file; they hold throughout.

1. **The first registration is a one-way door (D17 / rule 8).** Sizing mode, the alpha definition, the RF
   series, the population family, the shortlist recipe and the pack recipe are frozen params or estimator
   definitions. Changing one after a strategy trades is a **fork** that spends a trial and raises the D89
   floor for every candidate after it (N′ = live trials + 1). *The rails that change what a number MEANS
   land before the number is produced; the rails that change what it COSTS land before it is spent.*
2. **Built ≠ registered.** CATALOG §12's trials arithmetic admits **Momentum + MeanReversion only** on day
   one. Every other family is implemented, green, and un-registered shelf capacity.
3. **Never author a table SCHEMA already specifies.** `factor_returns`, `factor_refresh_log`,
   `trade_evidence`, `parameter_scans` and `feature_baselines` all already exist in
   [`SCHEMA_v1.9.md`](../SCHEMA_v1.9.md) — implement its DDL verbatim. Changing a specified shape (e.g.
   adding `run_kind`) is a decision, not an implementation detail.
4. **Migrations are snapshot-first** (rule 14), with SCHEMA, `SchemaFidelityTests`' table list **and its
   deliberately-absent comment block**, and the `ScratchStore` classification updated in the same PR. A
   CHECK change is an EF **table rebuild** that re-adds AUTOINCREMENT (finding 324) — hand-written SQL plus
   a row-survival test.
5. **Honesty lives in read-models** (D58 / rule 18). UX-5/17/18/19/20 are DTO fields with
   framework-agnostic unit tests, never browser tests — plus a `UX_DESIGN_SYSTEM` component entry each
   (the half of the 4.5 precedent that carries the rule to whoever renders it).
6. **Rule 32 and its corollary.** No AI output is an input to a component that judges AI outputs; no
   monitor signal, gate input, allocator term or population comparison reads `ai_context_packs` or
   `ai_decisions`. A D128 **proposal trigger** is not on that enumerated list and is sanctioned explicitly.
7. **Fail closed** (rule 10). A missing risk input rejects with a logged reason; a silent fallback that
   sizes by a rule the config does not claim is the defect, not the safety net.
8. **The remote check is part of the gate** (finding 383). `tools/ci.ps1` green locally is necessary and
   not sufficient — `gh pr checks` on the pwsh-7 leg before merge.

## Where the decisions live

Register rows land in [`DECISIONS_v1.9.md`](../DECISIONS_v1.9.md) as they are made. **A D-number is claimed
when its row lands, never before** — this folder deliberately names each pending decision by its SUBJECT
rather than by a number, because `tools/check-register.ps1` treats any `D<n>` in `docs/` as a citation and
refuses one that resolves to no row. That refusal is correct and is not worked around: a forward-allocated
number is a citation of something that does not exist, which is the class of defect rule 25 exists to stop.

Landed at phase open: **D131** (the gate's opponent) and **D132** (no feature cache in Phase 6). Landed at 6.2: **D133** (the readable `config_json` shape). Landed at 6.3: **D134** (the Stage-3 seeded tie-break, which also fixes STRATEGY_CATALOG §3's dangling `D40` citation and settles the `percentile_rank` convention) and **D135** (the declared cadence family).

Still owed, by subject: the `FX-SignalParity` scope per family · the S6
behavioural remedy · the monitor→gate consequence ordering and the auto-retire reconciliation · whether
attribution needs a durable results table · where a per-run covariance log lives *(may need no row)* ·
where a halt persists · the `TargetOrTimeStop` exit grammar · where the sector cap binds · `ToChannelExit`
*(conditional on Breakout)* · D110's two deliberately-open questions (per-arena vs lab-wide scoring; how the
base rate is computed across arenas) · the contestant pack recipe id.
