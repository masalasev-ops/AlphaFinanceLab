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

**Phase 2 complete — funnel + ledger merged.** (Phase and test count move fast; **`PROGRESS.md` is the source of truth** for both. This section describes the shape of the build, not the live count.) Phase 0 stood up the skeleton (solution, schema, process
model, API boundary, empty UI); **Phase 1 adds the market-data layer** — the EODHD provider, the
security master, versioned append-only bars + watermark reads, index-membership reconciliation (iShares
OEF + Wikipedia cross-check), the trading calendar, the regime-proxy feed, and the data-quality gate,
all driven by a bootstrap backfill CLI. That CLI has been run **live against the S&P 100** — 101
members, ~488k versioned bars over 20 years, plus the GSPC regime proxy. Still ahead in Phases 3–8 (see
[`docs/BUILD_AND_PROMPTS_v1.9.md`](docs/BUILD_AND_PROMPTS_v1.9.md) §2 and [`PROGRESS.md`](PROGRESS.md)):
the six-stage funnel, the ledger + cost model, the strategies, the honest-arena evaluation, and the
daily pipeline hosted in `AlphaLab.Worker`. No forward pipeline run has been committed yet, so the
strategy/evaluation screens still return empty, `no_run_yet`-stamped read-models. `tools/ci.ps1` is
green (build + the full test suite + guard greps); see `PROGRESS.md` for the current test count.

**What "working" will look like — set expectations now.** By construction, the lab's *fast* outputs
are the honest-but-unglamorous ones: **anti-predictive kills** (a strategy the monitor can show is
worse than random) and **`IndistinguishableFromRandom`** findings (an edgeless strategy that costs
nothing to its cost-matched null). **Promotions are slow.** Because every head-to-head gap is judged
against its Newey–West-corrected MDE, a small real edge can take *years* of paper trading to clear
the noise — inside the MDE the verdict is `TooEarly`, not a number. This is the design working as
intended, not a bug: a lab that promoted quickly would be lying about its statistical power. Don't
judge it broken for being honest about how long real evidence takes to accrue.

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
tools/   ci.ps1, migrate.ps1, snapshot-db.ps1  (+ shared resolver)
docs/    the full design package — decisions, schema, config, test plan, runbook
CLAUDE.md, PROGRESS.md, START_HERE.md
```

## Documentation

The `docs/` folder is the authoritative design package. Start with
[`docs/README_v1.9.md`](docs/README_v1.9.md) (the file map and build workflow). Key entries:

| Doc | What it is |
|---|---|
| [`docs/MASTER_DESIGN_v1.9.md`](docs/MASTER_DESIGN_v1.9.md) | Decisions D1–D91, architecture, golden rules, the UI boundary |
| [`docs/SCHEMA_v1.9.md`](docs/SCHEMA_v1.9.md) | The database schema — the single source of truth for table shapes |
| [`docs/CONFIG_REFERENCE_v1.9.md`](docs/CONFIG_REFERENCE_v1.9.md) | Every config key, default, and owning decision |
| [`docs/BUILD_AND_PROMPTS_v1.9.md`](docs/BUILD_AND_PROMPTS_v1.9.md) | Functional requirements + the gated phase plan (Phase 0 = checkpoints 0.1–0.6) |
| [`docs/TEST_PLAN_v1.9.md`](docs/TEST_PLAN_v1.9.md) | The fixtures and tests each phase must pass (§8 = the Phase-0 inventory) |
| [`docs/RUNBOOK_v1.9.md`](docs/RUNBOOK_v1.9.md) | Operations: daily cycle, catch-up, backups |
| [`CLAUDE.md`](CLAUDE.md) | The standing hard rules the build obeys |
| [`PROGRESS.md`](PROGRESS.md) | The honest ledger — what shipped, what's red, what was deferred |
