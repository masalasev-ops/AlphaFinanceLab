# POST_PHASE8_PLAN

Status: regenerated 2026-07-22; supersedes the lost 2026-07-20 original. Companion doc: POST_PHASE8_IMPROVEMENTS.md holds the what and why; this doc holds the build sequence only.

## 1. Governing principle

The existential risk for the lab is crowning a paper winner that is not a real winner. Validity before power: every pass below must strengthen the lab's ability to tell a real edge from noise before it adds reach, scale, or automation.

## 2. Hooks that already exist when post-8 begins

- Phase 4 detectability-at-admission gate (D89, FR-40, Gate.DetectabilityHorizonYears=3), refusing underpowered candidates at admission.
- Phase 4 seams: per-regime replay persistence (FX-ReplayPerRegime, FR-41, replay_regime_outcomes keyed to regime_episodes) feeding idea 3, and learn/validate partitioning (FX-ReplayPartition-NoLeak) feeding idea 6.
- Phase 4.5 Signal Library (D91, FR-43..46): per-signal rank-IC record with trend flags, and the per-signal digest lines. The scheduled input to idea 6's evidence prior.
- Phase 5 evidence-prior seam: swappable, disableable, placebo-able. The digest wires in here.
- D88 cohort maturation curve: the yardstick for whether the researcher loop improves, and the control curve idea 6 must beat.
- trials_registry and power_reports: the substrate for idea 4.

## 3. The passes, in order

### Pass 1: Validity hardening (ideas 3 and 4)

Idea 3, multi-regime survival for crowning.
- Crowning requires survival across regimes, not one strong stretch; regime-conditional outcomes come from the FR-41 persistence over regime_episodes.
- Display: regime-conditional panel view beside the existing verdict views.
- DoD: crowning rule amended with pre-registered thresholds held in config; a candidate strong in only one regime cannot be crowned; panel shows per-regime outcomes; tests pin the rule.

Idea 4, lab-level power accounting.
- An exhaustion readout: the probability that an edge greater than X was missed, computed over trials_registry and power_reports. The aggregate cousin of the admission gate.
- DoD: readout available as a read-model and panel with a stated refresh cadence; interpretation documented beside it; no decision component consumes it (descriptive).

### Pass 2: The Learning Researcher, guarded (idea 6)

- The evidence-prior digest of the MATH-verdict record, now including the Phase 4.5 signal digest lines (D91), is fed to the researcher via the section 23 context pack through the Phase 5 seam.
- Five guards, each mandatory:
  1. Control researcher: the learning researcher must beat the control cohort curve by more than the MDE.
  2. Out-of-sample validation: replay partition A to B, no leakage (FX-ReplayPartition-NoLeak).
  3. Frozen pre-registered learning rule (D52); no rule edits mid-experiment.
  4. Inflated trials accounting: learned proposals pay the full trials tax.
  5. Coarse auditable digest: the prior is small, inspectable, and reproducible.
- Load-bearing danger, recorded here on purpose: overfitting to the lab's own noise, which would make the cohort curve lie. The control researcher exists for exactly this failure.
- DoD: seam demonstrated in all three modes (swapped, disabled, placebo); guard tests named and passing; cohort-curve comparison wired; digest composition documented.

### Pass 3: Widening (idea 1 plus SP1500)

- Capacity and market-impact model: cost scales with participation, giving each strategy a capacity ceiling. Lands on the D43 seam in the fill model.
- SP1500 widening (D87) is bundled as this pass's prerequisite and remains gated on a verified historical 400/600 membership source.
- DoD: participation-scaled impact active in fills; capacity ceiling surfaced per strategy; SP1500 arena live only after the membership-source gate clears.

### Final pass: Independent breadth (idea 5)

- Cross-asset ETF sleeve as its own phase, last. The square-root-of-N breadth argument pays only if the additions are independent, so independence is measured, not assumed.
- DoD: the sleeve runs with its own populations and cadence; an independence readout exists before any combined reporting.

## 4. Dropped

- Idea 2, real-money confirmation sleeve. Dropped deliberately: no real-money spend or risk. It stays dropped.

## 5. Ordering rationale

Validity passes come before power passes. The learning researcher comes before widening because a learning loop proven on the narrow arena carries its guards with it, while widening first would multiply noise before the guards exist. Breadth comes last because independence is its entire point, and independence can only be judged against a mature single-arena record.

## 6. Open items

- Exact multi-regime crowning thresholds (pre-registered at Pass 1 build).
- Power-readout refresh cadence.
- Digest composition beyond the signal lines (what else from the verdict record enters the prior).
- Calibration source for the capacity model.
- ETF sleeve universe definition.
