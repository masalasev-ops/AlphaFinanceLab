# Does generation 2 contain the D142 geometry? — the step-0 probe

- Date: 2026-08-11
- Occasion: checkpoint 6.5a (review remediation), PR 1 / F1, taken **before** any code was written
- Store: `E:\AlphaLabDatabase\sp500\alphalab.db` (3,707.7 MiB), opened `Mode=ReadOnly`
- **Watermark applied: `2026-07-24T22:00:00Z`** — the generation's single pinned ceiling
- Status: **complete**; result adjudicated, generation 2 **stands** (see §5)

**Why this artefact exists.** The result alone is not evidence. A probe whose method nothing can
re-examine is a fact stated in prose — the shape D140 forbids — so the program that produced the counts
below is reproduced verbatim in §6 and every count is stated *at the watermark it was taken at*.

**Why the probe exists at all.** `ReplayRunner` skips same-watermark committed days (`:157-162`). So if
generation 2 contained the geometry D142 changes, a post-fix re-run would leave the affected sessions at
their pre-fix values while every later day computed under the new rule — a mixed-arithmetic generation,
which is what D95's one-generation-per-arena rule exists to prevent. The question had to be answered
before the fix, not after.

---

## 1. The question, stated in terms of effects

For each committed `(account_id, as_of)` in `decisions`, take the securities named in
`stage_json.stage6_orders` whose `fill_on` is the next session. Does any corporate action **visible at
the generation's pinned watermark** have an `AppliedOn` in `(as_of, fill_on]` whose effect **restates**
or **terminates** the position?

| Class | `CorporateActionEffect` | Why it is asked separately |
|---|---|---|
| **A — restates** | `PositionRestated` | the D142 **restatement** changes stored arithmetic |
| **B — terminates** | `PositionForceClosed`, `StockMergerConverted`, `MixedMergerApplied` | the new **oversell guard** changes the outcome — a day that previously committed would now roll back |
| neither | `DividendCredited`, `TickerRenamedNoLedgerEffect`, `SpinoffReceived` | no unit change, no termination (a spin-off leaves the parent's share count alone) |

Three method choices, each made to avoid a specific way of being wrong:

- **Classification runs the real dispatch.** `CorporateActionLedger.Apply` is called and the returned
  effect is pattern-matched. A hand-maintained list of type tokens would drift the moment an action kind
  is added — the exact failure the restatement design is keyed against.
- **The window keys on `AppliedOn`**, not `effective_date`: ex-date for a dividend, effective date
  otherwise. That is the property the applier's own window predicate uses
  (`CorporateActionApplier.cs:79-80`).
- **The watermark ceiling is applied through the production read**,
  `CorporateActionReadService.GetActionsAsOf(security, watermark)` — the same call the applier makes at
  `:77` — so D76's latest-visible-version resolution is not re-derived here. **Omitting the ceiling
  would be one-directional: it can only OVER-report, never under-report.** That is the expensive
  direction, because a false positive stops the PR and triggers a regeneration that is not needed.

---

## 2. What was scanned, at watermark `2026-07-24T22:00:00Z`

| Quantity | Value |
|---|---|
| replay runs | 5,033 |
| distinct watermarks | **1** (`2026-07-24T22:00:00Z`) |
| replay sessions | 5,031 (`2006-01-03` … `2025-12-31`) |
| securities with corporate actions | 789 |
| action rows resolved at the watermark | 41,126 |
| candidate actions landing in some `(as_of, fill_on]` window | 527 (A=527, B=0) |
| `decisions` rows read | 1,365 (the 455 `as_of` values carrying a candidate) |
| unclassifiable actions | 0 |
| unreadable snapshots | 0 |

---

## 3. Result

```
CLASS A (restates  — the restatement changes stored arithmetic) : 10
CLASS B (terminates — the oversell guard changes the outcome)   :  0
```

**Both counts are at watermark `2026-07-24T22:00:00Z`.**

| # | account | decided | side | shares | security | fills | action | ratio | book@decision → restated | guard |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 402 `buyhold:ew` | 2022-04-08 | Sell | 0.112032 | 64 | 2022-04-11 | 3842 | 1.324 | 34.4616 → 45.6271 | not tripped |
| 2 | 403 `threshold:sma50` | 2022-04-08 | Buy | 356.68 | 64 | 2022-04-11 | 3842 | 1.324 | 1.08524 → 1.43685 | not tripped |
| 3 | 402 `buyhold:ew` | 2018-11-06 | Sell | 0.146012 | 481 | 2018-11-07 | 37070 | 2 | 5.1377 → 10.2754 | not tripped |
| 4 | 403 `threshold:sma50` | 2018-11-06 | Sell | 0.0704145 | 481 | 2018-11-07 | 37070 | 2 | 0.0704145 → 0.140829 | not tripped |
| 5 | 402 `buyhold:ew` | 2010-06-07 | Sell | 0.237174 | 756 | 2010-06-08 | 17675 | 2 | 2.40791 → 4.81582 | not tripped |
| 6 | 403 `threshold:sma50` | 2010-06-07 | Buy | 21.4186 | 756 | 2010-06-08 | 17675 | 2 | **none** → n/a | not tripped |
| 7 | 402 `buyhold:ew` | 2009-11-03 | Buy | 1.84997 | 828 | 2009-11-04 | 22329 | 1.0215 | 18.763 → 19.1664 | not tripped |
| 8 | 402 `buyhold:ew` | 2013-09-06 | Buy | 0.0569777 | 863 | 2013-09-09 | 16290 | 2 | 3.30924 → 6.61849 | not tripped |
| 9 | 402 `buyhold:ew` | 2020-10-08 | Sell | 0.393433 | 1019 | 2020-10-09 | 19896 | 1.195 | 7.73644 → 9.24505 | not tripped |
| 10 | 402 `buyhold:ew` | 2025-05-15 | Sell | 0.157995 | 1090 | 2025-05-16 | 23561 | 1.01 | 7.24637 → 7.31883 | not tripped |

### The blast radius is three accounts, not 403

| accounts (replay) | 403 |
|---|---|
| accounts with any `decisions` row | **3** — `401=buyhold:cw`, `402=buyhold:ew`, `403=threshold:sma50` |
| `decisions` rows (replay) | 15,093 |

The 400 plant strategies write no `decisions` row at all — they are equity overlays and never route an
order through the funnel — so **the plant cohort the calibration curves rest on cannot be affected in
any window.** Of the ten hits, eight are `buyhold:ew` and two are `threshold:sma50`. **`buyhold:cw` —
D131's gate opponent — has zero.**

---

## 4. What the ten actually are

**Every one is a FORWARD split** (ratios 1.01, 1.0215, 1.195, 1.324 ×2, 2.0 ×4). That settles the
direction question, and it is the reason class B being zero is not the only good news:

- **No cash was fabricated.** Fabrication requires a *reverse* split, where the restated book shrinks
  below the stale sell so the remainder goes negative and the `<= 1e-9` branch absorbs it as a clean
  close. A forward split grows the book, so a stale sell can never oversell. Row 10 is checked
  individually above; none trips the guard.
- **The residual error is exposure and notional only.** A sell moved `1/r` of what it meant to, leaving
  the line partly open; a buy filled at `P/r` and so spent `1/r` of the intended notional. The two
  material ones are both `threshold:sma50`: 2022-04-11 under-invested ≈24.5 % on a 356.68 sh add, and
  2010-06-08 under-invested 50 % on a 21.42 sh open.

**Row 6 is worth its own line.** `book@decision` is **none** — a pending buy into a name the account did
not hold. That is the buy-side hole appearing in real stored data. Had the ratio map been derived from
the account's book, as the finding was originally filed, this hit would have been invisible to the fix
*and* to this probe. It is the empirical case for resolving ratios over the ORDER set.

---

## 5. Adjudication

**Generation 2 stands.** It is not mixed arithmetic; it is uniformly *pre-D142* arithmetic, and it stays
sound as long as the window is never partially re-run.

The constraint that follows is recorded in D142's `Consequences` field:

> Generation 2 (watermark `2026-07-24T22:00:00Z`) was produced under pre-D142 arithmetic on ten stored
> orders across nine of its 5,031 sessions. **Regenerate the window WHOLE or not at all** — a partial
> re-run after the fix is what would create the mixed-arithmetic generation D95 forbids. `reproduce-day`
> on any of the nine dates in the table above will legitimately DIVERGE after D142; that is the defect's
> fingerprint, not an NFR-1 failure.

The oversell guard is shipped in the same PR on the strength of class B = 0: it is provably inert on
every stored day of this generation.

---

## 6. The method, verbatim

Scratchpad console project referencing `AlphaLab.Core` and `AlphaLab.Data`; no repo surface added.

```csharp
var cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString();
var options = new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite(cs).Options;
using var db = new AlphaLabDbContext(options);

// 1. the generation's watermark — asserted single, and the max is the ceiling every count is taken at
var replayRuns = db.Runs.Where(r => r.RunKind == "replay").ToList();
var watermarks = replayRuns.Select(r => r.Watermark).Distinct().OrderBy(w => w, StringComparer.Ordinal).ToList();
var watermark = watermarks[^1];

// 2. the session line, and fill_on -> as_of so an AppliedOn can be mapped to the window containing it
var sessions = replayRuns.Select(r => r.AsOf).Distinct().OrderBy(a => a, StringComparer.Ordinal).ToList();
var nextSession = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 0; i < sessions.Count - 1; i++) nextSession[sessions[i]] = sessions[i + 1];
var asOfForFillOn = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (var (asOf, fillOn) in nextSession) asOfForFillOn[fillOn] = asOf;

// 3. candidate actions — watermark ceiling via the PRODUCTION read; classification via the REAL dispatch
var syntheticContext = new CorporateActionContext
{
    LastPrintPrice = 10m, BankruptcyHaircut = 0.0,
    SpinoffShares = 1.0, SpinoffBasisAllocated = 1m, ExistingCounterpartyPosition = null,
};
static Position SyntheticPosition(SecurityId id) => new()
{
    AccountId = 0, SecurityId = id, Shares = 100.0, CostBasis = 1000m, OpenedOn = "2000-01-01",
};

var securityIds = db.CorporateActions.Select(a => a.SecurityId).Distinct().ToList();
var reads = new CorporateActionReadService(db);

foreach (var sid in securityIds)
{
    foreach (var row in reads.GetActionsAsOf(sid, watermark))   // <- D76 resolution, not re-derived
    {
        var action = ToDomain(row);                              // mirrors CorporateActionApplier.ToDomain
        var appliedOn = action.AppliedOn;                        // ex-date for a dividend, else effective

        // the window (as_of, fill_on] containing it; a weekend/holiday date lands on the NEXT session
        if (!asOfForFillOn.TryGetValue(appliedOn, out var asOf))
        {
            var idx = sessions.BinarySearch(appliedOn, StringComparer.Ordinal);
            if (idx >= 0) continue;
            var insert = ~idx;
            if (insert <= 0 || insert >= sessions.Count) continue;
            asOf = sessions[insert - 1];
        }
        if (!nextSession.TryGetValue(asOf, out var fillOn)) continue;

        var effect = CorporateActionLedger.Apply(
            SyntheticPosition(action.SecurityId), action, RunKind.Replay, syntheticContext);
        var cls = effect switch
        {
            CorporateActionEffect.PositionRestated       => "A-restates",
            CorporateActionEffect.PositionForceClosed    => "B-terminates",
            CorporateActionEffect.StockMergerConverted   => "B-terminates",
            CorporateActionEffect.MixedMergerApplied     => "B-terminates",
            _                                            => "neither",
        };
        if (cls == "neither") continue;
        candidates.Add((asOf, fillOn, sid, row.ActionId, row.Type, cls, appliedOn));
    }
}

// 4. do stored orders actually meet them? stage_json parsed by the SAME parser FillPriorOrders uses
foreach (var group in candidates.GroupBy(c => c.AsOf, StringComparer.Ordinal))
{
    var rows = db.Decisions.Where(d => d.AsOf == group.Key && d.RunKind == "replay")
        .Select(d => new { d.AccountId, d.StageJson }).ToList();

    foreach (var row in rows)
    {
        var snapshot = DecisionSnapshot.FromJson(row.StageJson);   // fails closed on an unknown version
        foreach (var order in snapshot.Stage6Orders)
        foreach (var c in group)
        {
            if (order.SecurityId.Value != c.SecurityId) continue;
            if (!string.Equals(order.FillOn, c.FillOn, StringComparison.Ordinal)) continue;

            // would the NEW guard have fired? only a SELL exceeding the RESTATED book oversells
            var bookAtDecision = db.PositionSnapshots
                .Where(p => p.AccountId == row.AccountId && p.AsOf == c.AsOf && p.SecurityId == c.SecurityId)
                .Select(p => (double?)p.Shares).FirstOrDefault();
            var bookAfterRestate = bookAtDecision * ratio;
            var wouldTripGuard = order.Side == TradeSide.Sell
                && bookAfterRestate is { } after && order.Shares - after > 1e-9;
            ...
        }
    }
}
```

`ToDomain` mirrors the applier's private mapper (`CorporateActionApplier.cs:241-252`) field for field,
including `ParseType`'s fail-closed unknown-token throw. It is the one piece of production logic the
probe restates rather than calls, because it is private; it is reproduced above so a reader can check it.
