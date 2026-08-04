# AlphaLab

A personal **paper-trading research laboratory** — C# / .NET 10, SQLite, EODHD market data, and
Claude as a batched research assistant. It runs fake-money strategies against honest benchmarks and
random control populations, and surfaces every result through read-models that carry their own
statistical caveats (MDE bands, verdict chips, population percentiles) so a number is never shown
without the honesty that qualifies it.

> **Research only.** AlphaLab is never investment advice, never places real orders, and never touches
> real money. It is a single-machine tool for studying whether a strategy is distinguishable from
> random — not for trading.

## Status

**Phases 0–5.5 complete and merged.** Phase 4 (Arena Replay) signed off 2026-07-31; Phase 4.5 (the Signal Library) and Phase 5 (the LLM layer + AI seats) shipped; **Phase 5.5 (the construction question) closed 2026-08-04**. (Phase, test count and the decision register move fast; **[`PROGRESS.md`](PROGRESS.md) and [`docs/CHANGELOG_v1.9.md`](docs/CHANGELOG_v1.9.md) are the source of truth** — this section describes the shape of the build and deliberately keeps no counters of its own.)

Phases 0–2 stood up the skeleton, the market-data layer and the six-stage funnel + ledger + D43 cost model + the staged daily pipeline hosted in `AlphaLab.Worker`. Phase 3 added the honest-arena evaluation — MDE, gate, overfitting monitor, allocator, random control populations — and Phase 3.5 the save/continue hardening (`reproduce-day` makes byte-identical reproducibility an executable proof). The store now holds **~3.65M versioned bars across 912 securities (2001–2026)** on the S&P 500 as-of membership (D109).

**Phase 4 built the sealed room that proves the honesty engine works before forward judgment begins, then ran it twice.** The whole pipeline replays over **5,031 sessions** under `run_kind='replay'` quarantine against planted strategies with known truth. Generation 1 ran on a corpus that predated its own data-quality guard; the v1.9.77 sweep diagnosed it (55 securities carrying 1,763 impossible jump-days), 29 names were excluded from the roster, and **generation 2 re-ran clean**. The monitor's calibrated pass marks are frozen as append-only config rows with the report archived at [`docs/calibration/sp500/`](docs/calibration/sp500/).

**The two numbers worth knowing.**

1. **The lab's detection power, measured** (generation 2, monthly edge plants, 50 each): **2 %/yr – 9/50 · 4 %/yr – 35/50 · 8 %/yr – 50/50 · 16 %/yr – 50/50**, with 0/50 no-edge plants promoted. In plain terms **a 4 %/yr edge is found about 70 % of the time** over twenty years. The admissible band is **[6.95 %, 32 %]/yr** (α*(10 y) = 6.947 %/yr floor, D116 ceiling). *(An earlier version of this README quoted 1/5/26/43 and "about 10 %" — generation 1's contaminated ladder. It is superseded.)*
2. **The lab's resolving power, measured** (Phase 5.5, D123): at the ten-year horizon this arena can only adjudicate a strategy whose active-return information ratio is **≥ 0.886, sustained** — that follows from the horizon and the confidence/power pair alone. Measured across all seven registered signals under both a long-only and a long-short construction, the best of fourteen pairs is **0.392**. Long-short does not close the gap: it is ~2× leverage on the same bet, scaling tracking error and effect together, so it buys no detectability. **The long-short build was therefore not started** — a phase of work saved by one report.

Still ahead in Phases 6–8 (see [`docs/BUILD_AND_PROMPTS_v1.9.md`](docs/BUILD_AND_PROMPTS_v1.9.md) §2 and [`PROGRESS.md`](PROGRESS.md)): real strategies + French factor attribution, risk/regimes/observability, and (contingent) fundamentals. **No forward pipeline run has been committed yet** — every one of the 5,033 `runs` rows is `run_kind='replay'` — so the strategy/evaluation screens still return empty, `no_run_yet`-stamped read-models. `tools/ci.ps1` is green (build + the full test suite + guard greps).

**What "working" will look like — set expectations now.** By construction, the lab's *fast* outputs are the honest-but-unglamorous ones: **anti-predictive kills** (a strategy the monitor can show is worse than random) and **`IndistinguishableFromRandom`** findings (an edgeless strategy that costs nothing against its cost-matched null). **Promotions are slow.** Every head-to-head gap is judged against its Newey–West-corrected MDE, so a small real edge can take *years* of paper trading to clear the noise — inside the MDE the verdict is `TooEarly`, not a number. The bar above (IR ≥ 0.886 at ten years) is that statement made exact. This is the design working as intended, not a bug: a lab that promoted quickly would be lying about its statistical power. **The lab is graded on proposal quality and verdict honesty (§1.2), never on having found a winner.**

## Architecture

Three processes over one SQLite file (per arena), so a UI, a scheduler, and the writer never race:

- **`AlphaLab.Worker`** — a .NET Generic Host and the **sole DB writer** (D59). Runs **OnDemand** by
  default (launch → catch up through the last completed session → exit); an optional `--serve`
  (Scheduled/Quartz) mode stays resident. Applies the schema and enables WAL at startup.
- **`AlphaLab.Api`** — an ASP.NET Core minimal-API under `/api/v1` (D57): the single boundary every
  UI talks to. A reader (plus a few bounded command writes from Phase 3) with a uniform error
  envelope, native OpenAPI, and a Scalar UI. It never runs the pipeline.
- **`AlphaLab.Web`** — a standalone Blazor WebAssembly client of the API (swappable for any front
  end). All honesty-carrying presentation logic lives in serializable read-models (D58), not the UI.

Supporting libraries: `AlphaLab.Core` (domain + read-model DTOs), `AlphaLab.Data` (EF Core + SQLite),
`AlphaLab.Strategies`, `AlphaLab.Evaluation` (metrics, MDE, gate, allocator, monitor, populations),
`AlphaLab.Llm`.

**Stack:** .NET 10 · EF Core 10 + SQLite (WAL) · ASP.NET Core minimal-API + Scalar · Quartz.NET ·
Blazor WebAssembly · xUnit. Package versions are pinned centrally in `Directory.Packages.props`.

## Getting started

**Prerequisites:** the .NET 10 SDK (`dotnet --version` ≥ 10.0.x) and PowerShell (Windows PowerShell
5.1 is fine; the scripts are ASCII-only for it).

1. **Secrets** — copy the example into each runnable project's content root (both are gitignored;
   Phase 0 needs no real keys to build/run):
   ```
   src/AlphaLab.Worker/appsettings.Secrets.json
   src/AlphaLab.Api/appsettings.Secrets.json
   ```
   Shape: `{ "Secrets": { "EodhdApiToken": "...", "AnthropicApiKey": "...", "AlpacaKeyId": "", "AlpacaSecretKey": "" } }`
   (see [`docs/SETUP_v1.9.md`](docs/SETUP_v1.9.md) §5).

2. **Database setup (first run / new machine)** — the database is **not** in the repo; there is no
   `.db` to import. It is created from EF migrations the first time you run the Worker. Two things to
   know on a fresh clone:
   - **Where it lives.** The committed connection string points at `E:/AlphaLabDatabase/{Arena.Id}/alphalab.db`
     (this deployment). Path separators are normalized to the running OS (v1.9.36), so the same template
     is valid on Linux — moving to a cloud VM is a config-value edit, not a code change.
     On a machine without an `E:` drive, repoint it to the portable form — the
     **same value in all four spots** (they must be byte-identical or `ConfigConsistencyTests` fails):
     `ConnectionStrings:AlphaLab` in `src/AlphaLab.Worker/appsettings.json`,
     `src/AlphaLab.Api/appsettings.json`, and `tools/Backfill/appsettings.json`, and
     `DefaultConnectionString` in `src/AlphaLab.Data/DbPathResolver.cs` — each set to:
     ```
     Data Source={LocalAppData}\AlphaLab\{Arena.Id}\alphalab.db
     ```
     `{LocalAppData}` resolves to `%LOCALAPPDATA%` (known-folders API) and `{Arena.Id}` to `sp500`, so it
     lands under your user profile on any Windows machine. Full procedure:
     [`docs/DB_RELOCATION.md`](docs/DB_RELOCATION.md).
   - **How it gets created.** Running **`AlphaLab.Worker` creates the store** — its `SchemaStartup`
     makes the directory, creates the SQLite file, applies `InitialCreate` (the five infra tables +
     the seeded `worker_state` row), and enables WAL. The **Api never creates the store** (it's a
     reader), so on a fresh clone **run the Worker before the Api** (step 4). Equivalently,
     `dotnet tool restore` then `pwsh tools/migrate.ps1 -Arena sp500` creates it via `dotnet-ef`.

3. **Build, test, and lint:**
   ```
   pwsh tools/ci.ps1          # build + all tests + guard greps
   ```

4. **Run it (Worker first, then two more terminals):**
   ```
   dotnet run --project src/AlphaLab.Worker     # FIRST RUN: creates the DB (migrate + WAL), then exits 0
   dotnet run --project src/AlphaLab.Api        # http://127.0.0.1:5230  (Scalar UI at /scalar/v1)
   dotnet run --project src/AlphaLab.Web         # http://localhost:5210  (empty-state client)
   ```
   `dotnet run --project src/AlphaLab.Worker -- --serve` keeps the Worker resident on the Quartz schedule.

5. **Schema changes** are snapshot-gated:
   ```
   pwsh tools/migrate.ps1 -Arena sp500          # snapshot, then migrate the same file via --connection
   ```

## Repository layout

```
src/     AlphaLab.{Core, Data, Strategies, Evaluation, Llm, Worker, Api, Web}
tests/   mirrored *.Tests (Core, Data, Strategies, Evaluation, Llm, Worker, Api)
tools/   Backfill/ (the bootstrap + D70 historical CLI) · ci.ps1, migrate.ps1, snapshot-db.ps1,
         backup-offsite.ps1, register-nightly-backup.ps1, audit-dividend-unadjusted.ps1  (+ shared resolver)
docs/    the full design package — decisions, schema, config, test plan, runbook
CLAUDE.md, ORIENTATION.md, PROGRESS.md, START_HERE.md
```

## Documentation

The `docs/` folder is the authoritative design package. Start with
[`docs/README_v1.9.md`](docs/README_v1.9.md) (the file map and build workflow). Key entries:

| Doc | What it is |
|---|---|
| [`docs/MASTER_DESIGN_v1.9.md`](docs/MASTER_DESIGN_v1.9.md) | The decision register (§2), architecture, golden rules, the UI boundary — §2 is the count, so no range is restated here |
| [`docs/SCHEMA_v1.9.md`](docs/SCHEMA_v1.9.md) | The database schema — the single source of truth for table shapes |
| [`docs/CONFIG_REFERENCE_v1.9.md`](docs/CONFIG_REFERENCE_v1.9.md) | Every config key, default, and owning decision |
| [`docs/BUILD_AND_PROMPTS_v1.9.md`](docs/BUILD_AND_PROMPTS_v1.9.md) | Functional requirements + the gated phase plan (Phase 0 = checkpoints 0.1–0.6) |
| [`docs/TEST_PLAN_v1.9.md`](docs/TEST_PLAN_v1.9.md) | The fixtures and tests each phase must pass (§8 = the Phase-0 inventory) |
| [`docs/RUNBOOK_v1.9.md`](docs/RUNBOOK_v1.9.md) | Operations: daily cycle, catch-up, backups, the Phase-4 sign-off run (§8 — executed 2026-07-31) |
| [`docs/phase5.5/`](docs/phase5.5/) | Phase 5.5 — the construction question: the measured answer on long-only vs long-short, and the IR bar it produced |
| [`docs/calibration/sp500/`](docs/calibration/sp500/) | The archived calibration reports — the Phase-4 sign-off artifact and its frozen numbers |
| [`ORIENTATION.md`](ORIENTATION.md) | The plain-language tour — what the lab is and how the whole system runs |
| [`CLAUDE.md`](CLAUDE.md) | The standing hard rules the build obeys |
| [`PROGRESS.md`](PROGRESS.md) | The honest ledger — what shipped, what's red, what was deferred |
