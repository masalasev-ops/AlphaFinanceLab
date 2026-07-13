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

**Phase 0 complete — the skeleton.** This is the wiring, not the lab yet: the solution, the database
schema, the process model, the API boundary, and an empty UI all build, run, and talk to each other
end-to-end. There are **no data providers, no strategies, and no daily pipeline yet** — those arrive
in Phases 1–8 (see [`docs/BUILD_AND_PROMPTS_v1.9.md`](docs/BUILD_AND_PROMPTS_v1.9.md) §2 and
[`PROGRESS.md`](PROGRESS.md)). Every screen currently returns an empty, `no_run_yet`-stamped
read-model. `tools/ci.ps1` is green: build + 39 tests + guard greps.

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

2. **Database location** — the committed connection string points at `E:\AlphaLabDatabase\{Arena.Id}\alphalab.db`
   (this deployment). To run on a machine without an `E:` drive, relocate it per
   [`docs/DB_RELOCATION.md`](docs/DB_RELOCATION.md) (edit the three connection-string spots together —
   `ConfigConsistencyTests` guards them).

3. **Build, test, and lint:**
   ```
   pwsh tools/ci.ps1          # build + all tests + guard greps
   ```

4. **Run it (three terminals):**
   ```
   dotnet run --project src/AlphaLab.Worker     # migrate + WAL, catch up (nothing yet), exit 0
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
| [`docs/MASTER_DESIGN_v1.9.md`](docs/MASTER_DESIGN_v1.9.md) | Decisions D1–D73, architecture, golden rules, the UI boundary |
| [`docs/SCHEMA_v1.9.md`](docs/SCHEMA_v1.9.md) | The database schema — the single source of truth for table shapes |
| [`docs/CONFIG_REFERENCE_v1.9.md`](docs/CONFIG_REFERENCE_v1.9.md) | Every config key, default, and owning decision |
| [`docs/BUILD_AND_PROMPTS_v1.9.md`](docs/BUILD_AND_PROMPTS_v1.9.md) | Functional requirements + the gated phase plan (Phase 0 = checkpoints 0.1–0.6) |
| [`docs/TEST_PLAN_v1.9.md`](docs/TEST_PLAN_v1.9.md) | The fixtures and tests each phase must pass (§8 = the Phase-0 inventory) |
| [`docs/RUNBOOK_v1.9.md`](docs/RUNBOOK_v1.9.md) | Operations: daily cycle, catch-up, backups |
| [`CLAUDE.md`](CLAUDE.md) | The standing hard rules the build obeys |
| [`PROGRESS.md`](PROGRESS.md) | The honest ledger — what shipped, what's red, what was deferred |
