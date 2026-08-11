# Every path that can set or clear `IndistinguishableFromRandom` — D148

- Date: 2026-08-11
- Occasion: checkpoint 6.5a PR 7 (F3 + F10), before claiming the chip's logic is settled
- Method: repo-wide grep over `src/` for every producer, input and consumer, each verified by reading the code
- Status: **the setting/clearing paths are enumerated and CLOSED. The RENDERING path is not, and that is recorded rather than glossed** (§5)

**Why this artefact exists.** The first defect in this rail (D146, the gate's `<` boundary) was found by a
review. The second (this one, the single-point arms) was found only *after* the first was fixed, while
writing the PR that claimed to have settled it. Two defects in one rail, found sequentially, is exactly
the condition under which "and that's all of them" is a **claim** rather than an observation — so it is
demonstrated here or it is not made.

---

## 1. The chip is one expression with two inputs

`HonestyDtos.cs:52`:

```csharp
public bool IsIndistinguishable => State == None && Days >= MinTrackDays;
```

A computed property with **no setter**, so nothing can set the chip directly — it is a function of
`SeparationInfo`'s three fields. Any path to the chip is therefore a path to a `SeparationInfo`.

## 2. `SeparationInfo` has exactly one producer

`grep "new SeparationInfo(" src/` → **two hits, both inside `SeparationState.Resolve`**
(`SeparationState.cs:47` early return, `:96` the main return). No other file in `src/` constructs one.

So every path runs through `Resolve`, and `Resolve` reads exactly five things.

## 3. The five inputs, and every writer of each

| # | Input | Read at | Every writer in `src/` | Can it set/clear the chip? |
|---|---|---|---|---|
| 1 | `accounts` row for (strategy, runKind) | `SeparationState.cs:41` | `LedgerStore.OpenAccount`, called by `DummyRoster` and `StrategyRoster` | **Yes** — no account ⇒ `Days = 0` ⇒ chip never renders regardless of state |
| 2 | `equity_curve` row count | `:42` | `LedgerStore.cs:304` and `:350` (`RecordEquityPoint`), driven by `DailyPipeline` | **Yes** — sets `Days`, the chip's second condition |
| 3 | `overfitting_checks` where `signal='S3'` | `:46-51` | `OverfittingMonitor.AddCheck` (`:341`) — **the only S3 writer**. `TurnoverMatch.cs:45` also writes to this table but with `Signal = "turnover_match"` (`:26`), so it cannot reach the filter | **Yes** — the percentile path drives all three states |
| 4 | `power_reports.verdict`, latest by (AsOf, ReportId) | `:73-78` | `EvaluationStep.cs:102` (forward) and `ReplayVerification.cs:678` (writes `RunKind = Replay`, so invisible to a `live` read) | **Yes** — a decisive verdict forces `distinguishable`. **This is the path D146 fixed** |
| 5 | `VerdictsOptions` | `:44`, `:56` | config binding — `SeparationMinTrackDays`, `SeparationBandCentralFrac` | **Yes** — moves the chip's threshold and the band |

Two derived inputs live inside the S3 rows themselves and are therefore covered by (3):

- **the edge bar** — `threshold_json.p_edge_at`, else `healthy_anchor`. Written by `OverfittingMonitor`
  (`s3Thresholds`, `OverfittingMonitor.cs:185-202`).
- **the sustain** — `threshold_json.sustain_evals`, else `MonitorSignals.FlatAnchorSustainEvals`.

## 4. Why the list is closed

1. The chip has no setter; it is derived from `SeparationInfo` (§1).
2. `SeparationInfo` is constructed in exactly one method (§2) — a grep result, not a reading.
3. That method's body reads exactly the five inputs in §3 and nothing else. Every DB query in it is listed
   above; there are no other queries, no injected services, and no statics beyond
   `MonitorSignals.FlatAnchorSustainEvals`.
4. Every input's writer set was enumerated by grepping the table's `.Add(` sites, and each was read to
   confirm it either targets the filter or provably cannot (`turnover_match`'s signal token,
   `ReplayVerification`'s `RunKind = Replay`).
5. Every read is `runKind`-scoped, so the forward chip cannot be moved by a replay row — the rule-1
   quarantine holds on all five.

**One consequence worth stating:** inputs (1) and (2) can clear the chip *without any change to the
separation logic at all* — an account that is never opened, or an equity curve that stops accruing, both
withhold it silently by keeping `Days` below the minimum. That is correct behaviour (the chip asserts a
day count, so it must have one), but it means "the chip is missing" has a benign explanation that looks
identical to a defect, and no current fixture distinguishes them.

## 5. What is NOT closed, recorded rather than glossed

**The rendering path has no guard.** This enumeration covers what can SET or CLEAR the state. It does not
establish that a client renders it faithfully:

- `StrategiesReadModelBuilder.cs:102` puts the state on `StrategyRow.Separation`; `:116` reads it for the
  tier. Those are the only two consumers in `src/` today.
- Nothing prevents a future read-model builder from adding a third consumer, or from computing a
  competing state of its own. The D91 Signal-Library rail has a reflection closure that would catch that
  class of drift; **the D63 separation family has no equivalent**, and one is not added here.
- `AlphaLab.Web` is the deferred D65 workstream and was not examined. Whether the reference client renders
  the chip verbatim is outside this enumeration and is F6's neighbouring concern.

So the honest statement is: **the producing side is closed and demonstrated; the consuming side is
enumerated-as-of-today but unguarded.** A closure test over the read-model assemblies — the D91 pattern
applied to `SeparationInfo` — is the thing that would make the second half as solid as the first, and it
is recorded as a proposal rather than claimed.

---

## 6. What this enumeration found

It was not merely confirmatory. Writing it surfaced that **the `emerging` arm was also a single-point
test**, which the finding as filed did not mention — it named only the `distinguishable` arm's hardcoded
`95`. §20.8 says *"the path is **sustained** outside the population's central band"* for `emerging` too.
Both arms are fixed in D148; had the enumeration not been demanded, one of the two would have shipped
still broken, in the PR that claimed the rail was settled.
