# The 2-L(i) occurrence sweep — the twin, the headline number, the researcher's proposal surface, calibration skill — 2026-08-05, v1.9.91

*Pre-edit sweep artifact for the v1.9.91 decisions pass, recorded BEFORE any edit so the scale is visible and the edits review as a set. Every STALE site below is edited in this pass; DUPLICATE sites are edited only where the pass's own items direct (a DUPLICATE is a restatement whose authoritative home is named — the pass's rule is one claim, one home, pointers elsewhere).*

**Corpus state:** branch `docs/v1.9.91-decisions-pass`, post-v1.9.90 extraction (register in `docs/DECISIONS_v1.9.md`; MASTER §0/§2 are stubs).
**Scope:** `docs/**/*.md` + root `README.md`, `START_HERE.md`, `ORIENTATION.md`, `CLAUDE.md`, `PROGRESS.md`. **Excluded as frozen history:** CHANGELOG, `docs/phase5/*`, `docs/phase5.5/*`, `docs/calibration/**`, PROGRESS `## Session log` + `## Decision proposals`.
**Nil returns:** START_HERE, CLAUDE.md, ARENA_ARCHITECTURE, RUNBOOK, INTEGRATIONS, DB_RELOCATION, FUTURE_DB_MIGRATION, REBUILD, SETUP, POST_PHASE8_PLAN.

## Topic 1 — the contestant's headline number (M.1 near-twin claims): CURRENT 4 · STALE 3 · DUPLICATE 11 (18 sites)

Authoritative homes: the D81 row (`DECISIONS_v1.9.md:160`, "the paired difference is the headline number") and D79's rationale (`:158`); MASTER §23.3 for mechanics.

STALE:
- `MASTER:421` (M.1) — "near-twin comparisons … reach verdicts in a year": a verdict timescale from an ASSUMED σ_d ≈ 0.05%/day and an implied ~2%/yr effect. D122 withdrew asserted-constant expected effects; §1.1:40 carries the "WORKED EXAMPLE" caveat, M.1 does not; no contestant/twin pair has ever been measured.
- `DESIGN_IMPROVEMENTS:193` (§6 table near-twin row, with the "Years to detect 2%/yr" column at :188) — the arithmetic M.1's "in a year" rests on; §2 (:94) got the D122 caveat at v1.9.87, §6 did not.
- `DESIGN_IMPROVEMENTS_EXPLAINED:134` — "near-identical comparisons resolve in ~1 year": same defect.

DUPLICATE (homes noted; representative): MASTER:599 (§23.1 restates D81), MASTER:389 (rule 28), MASTER:194 (§7; side-defect: "~10–20 names" vs ShortlistSize 25), DESIGN_IMPROVEMENTS:151/:198, EXPLAINED:87, UX_GUIDELINES:10 (UX-14 pricing clause), STRATEGY_CATALOG:324, ORIENTATION:132/:138/:450.
CURRENT: DECISIONS:160/:158, MASTER:685 (§23.8.2 leakage), UX_GUIDELINES:26 (says "in years" — consistent with §1.1's tight-pairing figure).

## Topic 2 — the twin difference (computation, MDE, uses): CURRENT 25 · STALE 1 · DUPLICATE 10 (36 sites)

Authoritative homes: D81/D85 rows; MASTER §23.3 (rules 2–4); §23.8.3 (divergence index); OVERFITTING S8 (:75) for the monitor input; UX-14 for the screen; M.2/rule 22 for the MDE rail.

STALE:
- `TEST_PLAN:166` (`FX-TwinPairing`) — "the paired daily difference is the ONLY promotable signal" contradicts MASTER §23.3 rule 3 (:625), which names THREE evidence channels (population percentile / D44 trade-level track / the twin).

DUPLICATE (representative): OVERFITTING:83 (§3½ restates S8+D81), OVERFITTING:82 (restates S4 :63 — by §3½'s collecting design), STRATEGY_CATALOG:335, BUILD:270/:120/:55, ORIENTATION:134/:136 + mermaid nodes (:144, :294, :300, :304, :319, :323).
CURRENT (representative): DECISIONS:160/:164/:28, MASTER:624–:626/:687/:663/:658/:383/:423/:806, OVERFITTING:75/:63/:12, TEST_PLAN:167–171/:175/:69, UX_DESIGN_SYSTEM:75, UX_GUIDELINES:10, CONFIG:414, SCHEMA:286, PROGRESS:226/:227, BUILD:118/:13 (D36 populations-not-twins — deliberately contrastive).

## Topic 3 — the researcher's proposal surface: CURRENT 18 · STALE 11 · DUPLICATE 8 (37 sites)

Authoritative homes: D79 (`DECISIONS:158`, SIX inputs — frozen, rule 25: do NOT edit; superseded-in-practice by the D113 digest + D116 band rows, which is how a register works) and D82 (`:161`); MASTER §23.4:631 for the output object ("a **draft** `journal_entries` row (`kind='hypothesis'`, unlocked): the AI proposes; only the operator pre-registers — rule 30 unchanged").

STALE — the input enumerations:
- `MASTER:598` (§23.1, seven items incl. "the trials ledger") — omits the D91 signal digest (D113 treatment arm; §24.6:784) and the D116 detectability band (§23.5:650) the pack carries today; also the trailing pointer sends the reader to §23.4 for a §23.2 contract.
- `DESIGN_IMPROVEMENTS:150` (§4.1, seven items) — same omission + DUPLICATE of §23.1.

STALE — the output object (one repair repeated):
- `ORIENTATION:97` (mermaid "proposes the next strategy"), `:100` (mermaid edge; also elides the D89/D116 gate + D112 diet), `:112` (prose "the next new strategy worth testing"), `:176` (mermaid), `:314` (prose — doubly wrong: output object AND "joins the field next evening" cadence, contradicting §23.2:617 and ORIENTATION's own :228), `:332` (mermaid "Proposes one strategy").

STALE — shipped-code contradictions:
- `MASTER:637` (§23.4) — "that control arm runs in the SAME arena … therefore doubling that arena's tax": **D113 explicitly withdrew this**, verified against code; the very next bullet (:639) states the paper control correctly — self-contradiction two lines apart. Highest priority.
- `MASTER:790` (§24.7) — "what remains open is the seam wiring itself": wired and blinded at v1.9.70 (D114; PROGRESS:216).
- `POST_PHASE8_IMPROVEMENTS:69` (item 6) — frames digest-into-pack as post-Phase-8; D113 + v1.9.70 shipped it. The five guards remain future work; the item scopes down.

DUPLICATE (representative): MASTER:209 (§8 three-item mini-list), :186/:198 (§7), EXPLAINED:85 ("proposes the next experiments" — closer to D82 than ORIENTATION was), BUILD:55/:118 (accurate restatements).
CURRENT (representative): MASTER:631–:640 (minus :637), :657, :63 (Researcher-yield KPI), :784; TEST_PLAN:173/:180/:181/:182; MANIFEST:329; docs/README:61; PROGRESS:205/:206/:215/:218; ORIENTATION:193/:205/:226/:228.

## Topic 4 — calibration skill (D110): CURRENT 14 · STALE 0 · DUPLICATE 5 (19 sites)

Authoritative home: the D110 row (`DECISIONS:190`) as amended by D113 (`:193`).
DUPLICATE: DECISIONS:75 (pass-index one-liner, by design), MASTER §1.2:65, §18:411, §23.4:636, MANIFEST:286–291.
CURRENT (representative): SCHEMA:500–525, CONFIG:241–249, TEST_PLAN:177/:178/:179/:183, PROGRESS:215.

**Watch item (omission, not STALE):** D113's clause "calibration skill … can be scored only on ADMITTED proposals, since it needs closed outcomes" exists ONLY at `DECISIONS:193`. MASTER §1.2:65, §18:411 and §23.4:636 describe the score "per proposal" without the admitted-only scoping — which reads as though the never-admitted paper control's priors are scored too. They are not. The pass adds the clause where those sites are already being edited.

## The latent citation defect (declared in PR 1)

`MASTER:364` (golden rule 3): "…and grade the lab on §1.2's KPIs, never on 'found a winner' **(D38)**." — the only D38 citation in the corpus naming no successor (D38 is superseded-by D122; §1.2's own heading, the tombstoned row, §0 item 1 and PROGRESS:14 all name it). Minimal repair: `(D38, superseded by D122)`. Repaired in this pass; filed as a finding with its consequence.

## Summary

| Topic | CURRENT | STALE | DUPLICATE | Sites |
|---|---:|---:|---:|---:|
| 1 headline number | 4 | 3 | 11 | 18 |
| 2 twin difference | 25 | 1 | 10 | 36 |
| 3 proposal surface | 18 | 11 | 8 | 37 |
| 4 calibration skill | 14 | 0 | 5 | 19 |
| **Total** | **61** | **15** | **34** | **110** |

## STALE sites requiring edits (all edited in this pass)

1. `MASTER:637` — the withdrawn doubling premise (D113).
2. `ORIENTATION:314` — output object + cadence.
3–7. `ORIENTATION:112`, `:97`, `:100`, `:176`, `:332` — one repair five ways: "proposes the next hypothesis worth testing / a proposed hypothesis → the operator pre-registers → a candidate joins the field".
8. `MASTER:598` — §23.1 input list (digest + band omission; wrong trailing pointer).
9. `DESIGN_IMPROVEMENTS:150` — same list (becomes a pointer per 2-G(d)).
10. `MASTER:790` — §24.7 seam-wiring claim (shipped v1.9.70).
11. `POST_PHASE8_IMPROVEMENTS:69` — item 6 scoped down to the five guards.
12. `TEST_PLAN:166` — FX-TwinPairing's "only promotable signal" over-narrowing.
13. `MASTER:421` — M.1's "in a year" gains the D122 worked-example caveat (derived, not measured).
14. `DESIGN_IMPROVEMENTS:193` (+ table header :188) — the same caveat on the §6 power table.
15. `DESIGN_IMPROVEMENTS_EXPLAINED:134` — the same caveat.

Adjacent items folded into the pass: MASTER:364 (D38→D122); MASTER:194 ("~10–20 names" → the `Ai.Contestant.ShortlistSize` key, no number restated); the D113 admitted-only clause added at §1.2:65 / §18:411 / §23.4:636. Register rows D79/:158 and D110/:190 retain superseded-in-practice content BY DESIGN (rule 25) — flagged so a later reader does not "fix" them.
