# The `ResolveCurrent` enumeration — P24, resolved as D141

- Date: 2026-08-09
- Occasion: checkpoint 6.5, P24, opened by D140's sweep
- Status: **complete**; the edits it authorises landed in the same PR
- Method: repo-wide call-path tracing with adversarial re-verification of every classification, then
  independent re-checking of each load-bearing claim against primary sources

**Why this artefact exists.** P24 asked for the classification to be reported and reviewed **as a set**
before anything was edited, on the reasoning that a wrong call is visible against its neighbours in a
report and invisible in a diff. That turned out to be the right call three times over: the enumeration
corrected the list that commissioned it, and one of its findings would have made the obvious remedy
actively harmful.

---

## 1. Assertion vs usage — the candidate list was wrong in three ways

P24 named six `ResolveCurrent` callers. A grep for a method name matches doc comments alongside real
calls, and separating the two changes the list:

| P24 named | Verdict |
|---|---|
| `AsOfDetectabilityFloor` | **phantom** — doc comment at `:22`; the actual call at `:49` is `ResolveAsOf` |
| `DetectionCurves` | **phantom** — doc comment at `:11`; no call at all |
| `DetectabilityGate` | real (`:173`, plus the `ResolveCurrentFloor()` wrapper at `:84`) |
| `ProposalScoreKeys` | real (`:52`) |
| `OverfittingMonitor` | real (`:348`, `:359`) |
| `SignalLibraryBuilder` | real (`:163`, `:175`, `:176`, `:206`) |
| *(unnamed)* | **`SignalBackfillRunner`** `:216-217` — real |
| *(unnamed)* | **`CalibrationOrchestrator`** `:194`, `:328` — real |

Still six files: four the same, two swapped. The six named were all in `AlphaLab.Evaluation`; both
omissions are in `AlphaLab.Worker`, which is the shape of the mistake.

`ResolveCurrentSymbols` (`PipelineComposition.cs:156,163`) is unrelated noise — universe symbols, not
config.

**THE THIRD ERROR, and the one that mattered.** The site with the largest consequence was structurally
invisible to an enumeration built from a method name: **`DummyRoster.cs:68-72` is `ResolveCurrent`'s body
written out by hand** against `db.Config` — latest-wins, no watermark, no `ConfigReadService`. See §4.

---

## 2. The classification, per call path

`ConfigReadService` usage in `src/` is a closed set. Nothing under `Recompute/` reads config at all.

| Site | Call path | Class | Defective |
|---|---|---|---|
| `OverfittingMonitor:348,359` | daily · catchup · `replay-calibrate` · Api replay job · `reproduce-day`, all via `DailyPipeline.cs:648` | REPLAY | **No** — `DailyPipeline.cs:126` coalesces the watermark non-null, so every production path already took `ResolveAsOf` |
| | `RecomputeParityTests:80,88` (watermark omitted) | **REPLAY** | **Yes** — the generation's ground truth was seeded through the `ResolveCurrent` branch |
| | other unit tests (watermark omitted) | LIVE | No |
| `SignalLibraryBuilder:163,175,176,206` | `GET /api/v1/signals`, no `asOf` | LIVE | No — live panel, rendered, never stored |
| | researcher seat → `ResearchJobExecutor.cs:125` `Build(anchor.AsOf)` | REPLAY | No — takes the `ResolveAsOf` branch; finding 292's seam holds |
| `DetectabilityGate:173` | `POST /candidates` admission · `POST /analysis/hypotheses` · `analysis_hypotheses` job | LIVE | No — deliberate twin of `AsOfDetectabilityFloor`; admission is an operational act |
| `ProposalScoreKeys:52` | `POST /analysis/hypotheses` | LIVE | No — **carve-out 1** |
| `SignalBackfillRunner:216-217` | `signal-backfill` verb | LIVE | No — **carve-out 1** |
| `CalibrationOrchestrator:328` | `replay-calibrate` | REPLAY | Judgement — provenance text; now labelled with its resolution mode and instant |
| `CalibrationOrchestrator:194` | `replay-calibrate` | **REPLAY** | **No — and conversion is FORBIDDEN.** See §3 |
| `DummyRoster:68-72` *(unlisted)* | `replay-calibrate --reset` · `reproduce-day` · daily · catchup | **REPLAY** | **Yes** — see §4 |

**Carve-out 1 — presence-only guards.** The value is `is null`-tested and discarded, and under rule 24
config is append-only, so presence is monotone in version. Resolution mode cannot change the answer.

---

## 3. The finding that would have made the DoD's remedy harmful

The DoD said *"every REPLAY-path site converted to `ResolveAsOf`"*. At `CalibrationOrchestrator:194` that
is wrong, and the archived record proves it.

`patienceAlreadySet` is REPLAY by the criterion — it was genuinely reproduced with a differing answer:

- `docs/calibration/sp500/2026-07-31-calibration.md:10` — froze `Monitor.S6.AutoRetireEvals`
- `docs/calibration/sp500/2026-08-03-calibration.md:10` — same generation, same frozen watermark
  `2026-07-24T22:00:00Z`, both "0 committed by THIS invocation" — did **not**

But it must stay latest-wins, because **as-of resolution cannot express it**: the chain stamps its own
config rows `ChangedOn = DateTime.UtcNow` (`CalibrationOrchestrator.cs:175-176`), always later than the
frozen DATA watermark. An as-of read returns null on every run, flips the guard false, and re-stamps the
Appendix-A default over an operator's raise — breaking D98's seed-once.

**Hence D141's most useful sentence: REPLAY IS NOT SYNONYMOUS WITH CONVERT IT.** The prohibition is
enforced by `FX_D141_CarveOut2_PatienceGuardResolvesCurrent_NotAsOf`, **proved to fire** by a hand-edit
probe that converted the line (both that fixture and the pre-existing patience test went red; reverted
clean). A comment saying "conversion forbidden" would have been the unverified self-description D140
forbids.

---

## 4. What gated the confirmation slice

**The slice is `replay-calibrate --reset --report-only`, not `replay-recompute`.** D139's row and
`CalibrationOrchestrator.cs:114` both say so, and `:100-103` gives the reason the harness cannot
substitute: *"parity exercises the unchanged path, so it structurally cannot validate the changed one."*
`--report-only` is parsed only for `replay-calibrate` (`WorkerCommand.cs:212`); `replay-recompute` accepts
the flag and silently ignores it.

So the slice drives the **live** monitor through `DailyPipeline`, where the watermark is non-null and the
monitor was already clean. The blocker was upstream:

`DummyRoster.ResolveStartingCash` resolves `Accounts.StartingCash` — the accounts' **opening capital**, a
simulation input upstream of every equity curve, every population comparison, and therefore every S6 band
the remedy is judged on. It is normally masked because the accounts already exist, and **`--reset` —
which D139's own procedure mandates — deletes them (`ReplayRunner.cs:361`) and re-opens them through this
read.** The slice's own required flag is what arms it.

**Measured, not assumed** (read-only against production, `mode=ro`):

```
Accounts.StartingCash → v1, changed_on '2006-01-03', value '100000'   (count = 1)
```

One version, stamped ordinally before every watermark in play. As-of and latest-wins agree today, so the
binding is **behaviourally free** — it removes a future divergence rather than correcting a present one,
and the slice is gated, not blocked. Production was left byte-identical at 3,887,775,744.

---

## 5. Also found while measuring

- **A `--report-only` run writes exactly one config row** on a store that has never opened accounts:
  `Accounts.StartingCash@v1`, the roster bootstrap. The write stays — finding K exists to make the
  opening capital auditable rather than a literal only the code knew — but the blanket claim "report-only
  writes no config rows" was false. `FX_Calibration_ReportOnly_WritesNoConfigRows` now snapshots the whole
  config table instead of checking three keys, and permits exactly that one row.
- **The watermark pin is now enforced** (`ReplayRunner`), not remembered: a `--reset` against a store that
  already holds a committed generation, with no explicit `--watermark`, is refused.
- **P25** — `MonitorRecompute` reads S6 patience from a compile-time constant while the live monitor
  resolves the calibrated row. Not currently firing (the chain only freezes the constant), but it arms
  silently at the first patience recalibration. Shipped with a **tripwire** that refuses the recompute the
  moment any stored version diverges.
- **A stale citation corrected**: `FX_Calibration_Rerun_NeverRestampsOperatorPatience` cited "RUNBOOK
  §8.4" and "the documented recalibration loop". There is no §8.4, and `RUNBOOK:148` records that the
  raise-and-re-run loop was proven NOT to converge (finding 270; it is gone). The test is right; its
  stated reason was not. D98's seed-once is the reason.

---

## 6. The counter-example, preserved deliberately

`ScratchStore.cs:37-43` documents this exact gap under **"KNOWN AS-OF GAPS (documented, not silently
accepted — PROGRESS proposal P14)"**, states what it cannot verify, and bounds the consequence. One
verifier classified it as a P22-shaped defect; that is **wrong**, and the disagreement is recorded here
because the distinction is the whole of D140. A line may state a fact it verifies, **or state that it
cannot verify it**. `ScratchStore` does the second. It is the pattern the other sites should copy, not an
instance of the defect — the `RecomputeOrchestrator.cs:258` shape D140 already named.

---

## 7. Method note

Fourteen agents traced and adversarially re-verified; every load-bearing claim was then re-checked by
hand against the file it cites. One agent citation was wrong (`RecomputeHarness.cs:326` for a quote that
lives at `RecomputeOrchestrator.cs:326`) — the quote was real, the file was not, which is exactly why the
re-check happens. Two classifications were overturned by the adversarial pass, and one of those (§3) is
the reason the DoD's blanket remedy was not applied.
