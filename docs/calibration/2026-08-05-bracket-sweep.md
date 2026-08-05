# The 2-S bracket-deletion sweep — docs/MASTER_DESIGN_v1.9.md — 2026-08-05, v1.9.91

*Pre-edit sweep artifact for the v1.9.91 decisions pass, recorded BEFORE any edit so the scale is visible and the edits review as a set. The prose rule under test: a MASTER sentence must still read correctly with its trailing bracket deleted — the bracket is a pointer, never the carrier of the meaning. Now that the register lives in `docs/DECISIONS_v1.9.md`, a failing sentence forces the reader off the page. For each failure the repair is: STATE THE RULE in the prose, KEEP the bracket as a pointer. No bracket is deleted; no decision content changes.*

**Scope:** all of MASTER (863 lines) except the §0/§2 pointer stubs; mermaid node labels read but excluded (index labels, not prose).

## Failures (11)

| MASTER line | Section | Failing clause (trimmed) | What the bracket is carrying | Severity |
|---|---|---|---|---|
| 40 | §1.1 The power reality | "read α*(H) from the frozen `Calibration.DetectionPower` row and the ceiling from D116 — … at the D121 horizon" | D116 = the plausibility ceiling: the gate also refuses a claim above the top swept rung × the ladder's own geometric step (inclusive at the boundary; inert at/below the floor). D121 = the detectability horizon is 10 years (raised from 3). Both stated only at L475, 435 lines away, no pointer here. | **high** |
| 57 | §1.2 KPI: Edge-plant survival | "Fraction of the min-alpha D64 *edge* cohort (≥50 seeds) that survives 5y/10y" | What an edge plant IS (regime-conditional, autocorrelated, streaky alpha overlay — never constant drift; medianed over ≥50 seeds) and what "min-alpha" selects (the base ladder rung). First use; §20.9 never pointed to. | medium |
| 65 | §1.2 KPI: Proposal quality | "the detectability margin (`expected_effect_ann` ÷ the D89 admission floor …)" | The admission gate: CandidateFactory refuses a candidate whose pre-declared effect, net of the trials cost it adds, could not clear the NW-MDE inside `Gate.DetectabilityHorizonYears`. D89's home is §20.3, not pointed. | low |
| 117 | §4 Technology stack | "`AlphaLab.Worker` — .NET Generic Host owning the D53 staged pipeline and D47 catch-up" | D53 = fetch outside the transaction → ONE atomic write transaction per day → LLM batch post-commit. D47 = missed sessions replayed in order, one ACID transaction per day, idempotently. Neither stated in §4; no pointer. | medium |
| 382 | §16 rule 21 | "Ledger realism per D30 + full corporate-action semantics per §13.6 (D39)." | The whole rule: D30 = ledger on raw prices / signals on adj_close, dividends on ex-date, split adjustment, delist force-exit with haircut. No predicate of its own — the purest "Costs are D43." instance. | **high** |
| 392 | §16 rule 31 | "no calibrated curve is trusted without its D64 plant-sensitivity check archived in the calibration report" | What the check IS: calibration runs twice (realistic streaky vs naive constant-drift plant); a `P_edge(t)` divergence beyond `Calibration.Plant.SensitivityMaxGapPts` at any t ≥ 126 adopts the realistic curves and archives the divergence chart. | medium-high |
| 404 | §17.1 | "the machinery is proven, the D56 curves exist, and the D63 verdict economics … are *measured*" | D56 = the track-length-aware S3 trajectories `P_noise(t)`/`P_edge(t)` replacing flat anchors. §20.7 not pointed. | low |
| 459 | §20.1 | "the S&P 500 proxy is pinned across the D70 slice" | What "the slice" is (forward = S&P 100 through Phase-4 sign-off; replay = full S&P 500 as-of membership) and why the proxy must not move with it. | low |
| 598 | §23.1 | "…closed journal outcomes with lesson lines, and the trials ledger - all D80-compressed -" | The pack contract: raw series never enter a prompt; packs assembled only through versioned read services at a watermark, persisted with hash + token estimate + recipe version. Stated in §23.2 — but the sentence's trailing pointer is `(§23.4)`, the wrong way. | medium |
| 639 | §23.4 (D114) | "each arm persists its own subject-keyed rows, so the four D104 artefacts exist for the researcher too" | The countable set: (a) exact pack bytes, (b) raw model output, (c) parsed decision + what the funnel did with it, (d) model string/prompt version/sampling params. §23.8.1; no pointer. | medium |
| 790 | §24.7 | "The panel defers because the shell … does not exist yet, **not** on D65's expired cover" | What cover D65 granted (screens as a deferrable parallel workstream due before Phase-7 exit), hence what "expired" means. | low |

By severity: 2 high, 1 medium-high, 4 medium, 4 low. Nine of eleven fix as a one-clause gloss or an added § pointer; only L382 (rule 21) needs a real sentence.

## Deliberately NOT reported (checked, PASS) — so they are not re-litigated

- L624 "Frozen policy (D17)." — prose states the params and the fork consequence; the bracket adds nothing missing.
- Every "the D64 plants" site with a pointer (L494 `(§20.9)`, L512, L523, L534) and the two inline-glossed uses (L55, L62).
- L286 "D109 supersedes D87" — deletable; the stated rule survives.
- L172 "the D43 cost model" — §6's diagram + M.8 state it.
- L110 "— D46", L481/L559/L599 D24 sites — prose states the rule.
- L48 / L314 / L534 supersession-note brackets on sentences stating the current rule.
- All of §19, §20.2–20.9, §21, §22, §23.2–23.3, §23.8, §25 — spec sections; every D-number heads or follows its own statement.

## Adjacent defects found while sweeping (flagged, out of this sweep's remit)

1. **`rule N` pointers do not resolve against §16 (the golden rules)** — L171 "(rule 7)", L313 "(hard rule 7 / D29)", L286 "(D71/rule 23)", L475/L636/L767 "rule 8": each states its content (passes 2-S) but numbers the CLAUDE.md hard-rule list while sitting in a document whose own numbered list is §16 — the numbers mislead. Filed as a finding.
2. **"C-1" is never defined in MASTER** (L354, L475, L539, L718) — same pointer-carrying-meaning class, but its home is OVERFITTING_MONITOR/CHANGELOG, not the register. Filed as a finding.
