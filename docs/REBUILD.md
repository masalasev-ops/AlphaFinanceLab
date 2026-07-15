# REBUILD — from a fresh clone to a working arena

**The job this doc does.** Take a clone of this repo on a machine with no database and end with your
own arena: a store you can run the lab against, anchored at whatever day you downloaded it. It is the
*data* bootstrap. `SETUP_v1.9` §6 covers the *repo* bootstrap (toolchain, secrets, docs baseline) and
§7 the provider-shape verification — do those first.

Related but different jobs: `DB_RELOCATION.md` moves an existing file; `FUTURE_DB_MIGRATION.md`
leaves SQLite; `RUNBOOK_v1.9` §3–4 back up and restore a store you still have.

**This is evergreen by design.** `--as-of` defaults to today, the calendar seeds ±30y around it, bars
are fetched relative to it, and the regime proxy's warm-up floor always clears at 20 years. Clone
this in 2029 and you get twenty years of history ending in 2029. Nothing here assumes 2026 — but see
§5 for the parts that age.

---

## 0. What a rebuild is, and what it is not

**It builds you an arena. It does not recover someone else's history.**

The store's value is not the prices — those are re-fetchable for ~304 API calls. It is the
**provenance**: `observed_at` records *when we saw this*, and the version chain records *what we
believed before the provider corrected it*. A fresh build starts that record from zero. That is
correct and honest: it is *your* lab's first day.

> ### Never backdate `--as-of`
>
> It looks like the knob for "start from an earlier date." It is not. Two things go wrong, and the
> second is worse than the first.
>
> **It forges provenance.** `BackfillOptions.ObservedAt` is derived as `{AsOf}T22:00:00Z`, so
> `--as-of 2020-01-01` stamps every row *"we saw this on 2020-01-01"* — on data fetched today,
> carrying every correction the provider has issued since. The result is indistinguishable from a
> real 2020 record and asserts knowledge nobody had. It defeats D40 while wearing its clothes.
>
> **It manufactures survivorship bias.** Bars honour `from`/`to`, so those come back right. But
> BlackRock OEF and Wikipedia have **no as-of** — they return *today's* roster. A backdated run
> builds a "2020 S&P 100" that is really the current roster with `added_on = 2020-01-01`: every name
> that left the index since is missing, every name added since is wrongly present. Every result the
> lab computes on that store is silently contaminated — the exact sin this project exists to refuse.
>
> **As-of reconstruction is what the replay arena is for** (the fja05680 historical roster +
> `SeedHistoricalMembershipStep`, deliberately kept out of the forward path — see §4). That is a
> Phase-4 capability, not a flag on the bootstrap.
>
> Let `--as-of` default to today. Choose your *depth* with `--years`, not your anchor.

**A rebuild is not a backup.** Today the store holds only provider data, so a rebuild gets most of
the way back. From **Phase 2** onward it accumulates the lab's own output — trades, decisions, equity
curves, the go-live log, the journal. No provider returns that. Once Phase 2 has run, this document
stops being an answer to *"I lost the database"*; only a restore is (`RUNBOOK_v1.9` §4).

---

## 1. What you need

| | |
|---|---|
| Toolchain | .NET 10 SDK (`dotnet --version` ≥ 10.0.x); PowerShell (5.1 is fine — scripts are ASCII-only) |
| EODHD | A paid-tier API token. The bootstrap spends **~304 calls** against a 100,000/day cap (**99.7% headroom**) |
| Free sources | BlackRock OEF holdings CSV; Wikipedia S&P 100 (cross-check) |
| Time | ~6 minutes for a full 20-year run |

**Secrets** (`SETUP_v1.9` §5, D67 — gitignored, no env vars, no User Secrets). Three copies; the CLI
needs **its own**, because the plain console SDK does not auto-glob `appsettings.*.json` the way the
Worker SDK does:

```
src/AlphaLab.Worker/appsettings.Secrets.json
src/AlphaLab.Api/appsettings.Secrets.json
tools/Backfill/appsettings.Secrets.json
```

```json
{ "Secrets": { "EodhdApiToken": "...", "AnthropicApiKey": "", "AlpacaKeyId": "", "AlpacaSecretKey": "" } }
```

**The connection string — four spots, and they must be byte-identical.** The committed value points
at `E:\AlphaLabDatabase\{Arena.Id}\alphalab.db` (the original deployment). On any other machine,
repoint **all four** — `ConnectionStrings:AlphaLab` in `src/AlphaLab.Worker/appsettings.json`,
`src/AlphaLab.Api/appsettings.json`, and `tools/Backfill/appsettings.json`, plus
`DefaultConnectionString` in `src/AlphaLab.Data/DbPathResolver.cs` — to the portable form:

```
Data Source={LocalAppData}\AlphaLab\{Arena.Id}\alphalab.db
```

Miss the CLI's copy and the backfill writes a full database the Worker and Api never open, with no
error anywhere. `ConfigConsistencyTests` guards all four (finding 138), so `dotnet test` catches a
half-applied edit — **run it after editing, before spending API calls.** `DB_RELOCATION.md` §2 owns
this procedure in full.

---

## 2. Procedure

Verify the toolchain before spending anything:

```
tools/ci.ps1                                  # build + tests + guards must be green
dotnet run --project tools/Backfill -- --universe sp100 --dry-run
```

`--dry-run` resolves config and prints the plan with **zero** network calls and **zero** writes. It
does not create the database. It does **not** check that the live sources still look as expected —
see §5.

**Smoke, then full.** A one-year run costs the *same* ~304 calls (spend is universe-driven, not
year-driven: 3 per member + 1 proxy), so it is a free rehearsal of every failure mode:

```
dotnet run --project tools/Backfill -- --universe sp100 --years 1
dotnet run --project tools/Backfill -- --universe sp100 --years 20
```

The database is created from EF migrations on first run. The backfill is **idempotent and re-run
safe** — an unchanged re-fetch is recognised and never spawns a phantom version — so running the
full pass over the smoke run's partial state is safe and expected.

Sequence: seed calendar → GSPC regime proxy → membership refresh + reconcile (+ GICS sectors) →
per-member bars, dividends, splits → flush API usage. It **fails closed at membership** if OEF and
Wikipedia disagree, or if either roster falls outside `[99, 103]`: nothing is written and an audit
row records why.

---

## 3. Verification

Landmarks from the 2026-07-15 reference run (`--universe sp100 --years 20`, ~5m51s, exit 0). **These
are not acceptance criteria** — a later clone legitimately differs (more bars, a drifted roster, more
dividends). Judge the invariants.

| Check | Reference (2026-07-15) | Invariant |
|---|---|---|
| `securities` | 102 | members + 1 GSPC proxy |
| `bars` | 488,217 (2006-07-17 … 2026-07-14) | newest bar may lag as-of by a session — normal EOD lag |
| GSPC proxy bars | 5,029 | **≥ 956** — the D73 warm-up floor (200 SMA + 3×252). Below it the regime guard fails closed |
| `index_membership` open | 101 | **∈ [99, 103]**, and OEF ↔ Wikipedia **agreed** (applied, not held) |
| `corporate_actions` | 6,273 (6,204 div / 69 split) | dividends dominate; splits are rare |
| `trading_calendar` | 15,332 (1996 … 2056) | ±30y around as-of |
| EODHD spend | 304 / 100,000 | = 3 × members + 1. **Not** year-driven |
| Guards fired | zero | count sanity passed; no headroom breach; no no-bar flags |

**Spot-check the point-in-time read** rather than trusting row counts: read one security's series at
a watermark before and after a known correction and confirm the earlier watermark reproduces the
earlier version. That is the property the store exists to provide; a row count proves nothing about
it.

---

## 4. Deliberately not part of the forward bootstrap

**Historical membership** (the fja05680 S&P 500 roster) is a **Phase-4 replay prerequisite**, seeded
separately via `SeedHistoricalMembershipStep` — never chained into `RunAsync`. Co-mingling it makes a
re-run's forward reconcile classify the ~400 historical-only members as drops and permanently close
their replay intervals (the reconciler is universe-blind). Do not "helpfully" add it to the sequence.

---

## 5. Known limitations — read before you debug

The machinery is date-agnostic. Its **contact surface with the outside world** is not, and these are
what bite a clone taken long after the repo was last touched.

**`sp100` is the only wired universe.** `BackfillArgs.Parse` accepts `sp500` and sets its count band
`[495, 510]`, but `tools/Backfill/Program.cs:71,75` hardcode the OEF (S&P **100**) holdings feed and
the Wikipedia S&P 100 cross-check. The flag selects only the *band*. So `--universe sp500` fetches
~101 names, reconciles them against `[495,510]`, and fail-closes with
`count sanity breach: primary=101, crosscheck=101, band=[495,510]` — which reads like broken data and
is actually unwired code. An `ISharesHoldingsOptions.Ivv()` preset exists and is unused.

**The default arena is named for a universe it doesn't hold.** `tools/Backfill/appsettings.json` sets
`Arena.Id = "sp500"` / `DisplayName = "S&P 500"`, so a default clone lands S&P **100** data in a
folder called `sp500`, rendered "S&P 500" in the UI. Set your own `Arena.Id` before the first run —
the DB path, snapshots, and backups all namespace under it (D71).

**The trading calendar's one-off closures are frozen.** `NyseCalendar.SpecialClosures` is a
hard-coded set ending at `2025-01-09` (Carter). It is *generated*, not fetched, so a clone taken
years later will confidently assert trading days for every market closure since. Nothing errors: the
calendar claims a session, no bar arrives, and the quality gate raises a `MissingBar` warning per
affected security — misreporting a stale closure list as a provider gap. Check the list against
exchange notices before trusting a calendar far from the repo's last update.

**Green CI does not mean the repo still clones.** Every provider test is fixture-backed —
`grep -rl "new HttpClient()" tests/` is empty. `tools/ci.ps1` reports all-green while the OEF CSV URL
has moved, Wikipedia's table markup has changed, or an EODHD endpoint has been revised. Green means
*the fixtures still parse*, which is a different claim. This has already happened once: the first
live backfill hit a Wikimedia **403** because .NET sends no default `User-Agent` — caught only by
running against the real thing (finding 130). `SETUP_v1.9` §7 is the manual checklist for exactly
this; no command performs it.

All three live sources fail **closed**, which is right — a drifted provider halts the run rather than
writing bad data. The cost is that a stale clone stops with a data-shaped error whose real cause is
bit-rot. Start at §7's checklist, not at your API token.

> Research/paper-trading only. Not investment advice.
