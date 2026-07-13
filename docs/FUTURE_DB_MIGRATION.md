# FUTURE_DB_MIGRATION — moving off SQLite (later, only if needed)

*Standalone companion to MASTER_DESIGN_v1.9. **Nothing here is a Phase 0–8 task.** This document
exists so that if you ever migrate from SQLite to a professional engine (PostgreSQL or SQL Server),
future-you — or a Claude Code session — has the landmines written down and can execute the move as a
config-and-provider change rather than a rewrite. Until the migration trigger fires, ignore this file
and build on SQLite exactly as designed.*

> Research/paper-trading only. Not investment advice.

---

## 0. When (and whether) to do this at all

**The migration trigger is unchanged from D3/D22: multi-user hosting or intraday tick ingestion —
neither is on the roadmap.** A personal, single-writer, evenings-only lab has no pressure to leave
SQLite; §14.2's capacity analysis shows the database stays comfortably under a couple of GB for
years. **You may never need this.** Do not migrate for its own sake; migrate only when a concrete
need (a hosted multi-client story, a second operator, or tick data) actually arrives.

If that day never comes, this document costs you nothing. If it does, it turns an intimidating task
into a checklist.

---

## 1. Why this stays easy (the two facts that do the work)

Two design decisions, already baked into the build, are what keep the engine swap tractable:

1. **The database is a ledger, not a compute engine (§14.2).** All statistical work — regressions,
   percentiles, bootstraps, covariance, MDE — happens in C# on in-memory arrays, never in SQL. There
   are no stored procedures, no SQL-side math, no engine-specific analytical queries to port. The
   thing that is usually hardest to migrate simply isn't in the database.

2. **The arena boundary is in config and file paths, not in the schema (D71).** No table carries an
   `arena_id` column. So the engine choice and the isolation model are independent: you can move
   engines without touching the arena design, and you can change the isolation model (if you ever
   want to) as an *additive* migration rather than a rewrite. See §5.

Because the code talks to EF Core entities and LINQ — not to SQLite directly — the bulk of the move
is swapping the provider package and the connection string. EF exists precisely so this is not a
hand-rewrite of every query.

---

## 2. The two isolation options on a professional engine

D71 gives you a clean choice at migration time. Both are straightforward; pick per your needs then.

**Option A — keep per-arena isolation (recommended default).** Each arena becomes its own PostgreSQL
database (or its own schema) on one server. The `DbPathResolver` changes from resolving a *file path*
to resolving a *connection string / schema* per `Arena.Id` — same shape, different target. The
physical-isolation guarantee (D71a) carries over and is actually *stronger*, because the server can
enforce per-database permissions. No `arena_id` column is added; nothing about the arena design
changes.

**Option B — consolidate into one database with an `arena_id` column.** If, on a professional engine,
you decide a single shared database now makes sense (the server handles multi-tenant filtering
natively, and you want cross-arena ops tooling), this is the point at which you add the column. It is
an **additive migration**, not a rewrite, precisely because the schema was never entangled with
arenas (D71e). You would then thread `WHERE arena_id = ?` through the query layer — and lean on the
server's row-level-security / policy features to enforce it, rather than raw discipline (which is why
this option only becomes attractive *with* a professional engine, not on SQLite). Note that Option B
reintroduces the accidental-pooling risk D71 removed, so only take it if the server's enforcement
tooling genuinely replaces the physical-isolation guarantee.

Unless a real hosted/multi-tenant need exists, **Option A is the right call** — it preserves the
correctness-by-construction property with near-zero code change.

---

## 3. The four things that actually change

Everything else is a provider swap. These four are the only substantive edits, and none requires
database expertise:

| # | SQLite today | On PostgreSQL / SQL Server | Notes |
|---|---|---|---|
| 1 | **Ledger money = C# `decimal` persisted as TEXT** (D69 — EF's default SQLite decimal mapping, exact strings) | Native `numeric`/`decimal(p,s)` column type | This is a **simplification** — Postgres has exact fixed-point natively, so the TEXT workaround goes away. The D60 API contract (money serialized as strings/minor-units over JSON) is unchanged; only the storage type improves. Market-data prices and derived statistics stay floating-point (`REAL`→`double precision`). |
| 2 | **Connection string uses the `{LocalAppData}` + `{Arena.Id}` path tokens** resolved by `DbPathResolver` (D67/D71) | A per-arena connection string (Option A) or one connection string + `arena_id` filter (Option B) | `DbPathResolver` stops resolving a filesystem path and starts resolving a connection string / schema per `Arena.Id`. The Worker, Api, and EF design-time factory still all resolve through the same resolver, so they still target the same store. |
| 3 | **WAL mode + single-writer discipline** (D22/D59 — the Worker is the sole writer; the Api takes the write lock only briefly) | Native MVCC concurrency; the constraint relaxes | A server handles concurrent readers/writers natively, so the WAL-specific reasoning just falls away. You *keep* the Worker-as-sole-writer design if you like (it's still clean), but it is no longer a database limitation you're working around. The `run_in_progress` flag and the 409-on-overlap guard (D59) remain useful application logic regardless. |
| 4 | **Backups = file-copy of `alphalab.db` per arena + `snapshot-db.ps1` before migrations** (D22, RUNBOOK §3–4) | Server-native backup (`pg_dump` / point-in-time recovery) per arena database | The restore *drill* (RUNBOOK §4) stays the same in spirit — the mechanism changes from copy-a-file to the server's backup tooling. The pre-schema-migration snapshot rule (Golden Rule 17) still applies; it just uses the server's dump. |

Things that explicitly **do not** change: the entity models, the LINQ queries, all C# statistics, the
read-model DTOs (D58), the API contract (D60), the arena design (D71), and every test's intent.

---

## 4. The migration checklist (hand this to Claude Code someday)

Run in order. The test suite is the safety net at every step.

1. **Snapshot everything first.** Full backup of every arena's `.db` file, kept aside (Golden Rule
   17). This is your rollback.
2. **Add the new EF provider** (`Npgsql.EntityFrameworkCore.PostgreSQL` or the SQL Server provider),
   stand up the server instance, and put the new connection string(s) behind `DbPathResolver`.
3. **Retype the four spots in §3** — chiefly the D69 money columns (TEXT → native `numeric`) via a
   value-conversion/property-config change, and the path-token resolution → connection-string
   resolution. Leave the market-data/statistics columns as floating-point.
4. **Generate a fresh migration against the new provider.** EF produces the new engine's schema DDL
   from the existing entity models — you do not hand-write it. Review it against SCHEMA_v1.9.md
   (same tables, engine-appropriate types) and update SCHEMA in the same PR (Golden Rule 14).
5. **Move the data, per arena, one arena at a time.** Export from each SQLite file and import into its
   corresponding new database (Option A) or into the shared database stamped with its `arena_id`
   (Option B). Volumes are modest (§14.2), so a straightforward EF-based or scripted copy is fine.
   Verify row counts and spot-check one account's ledger against the pre-migration file.
6. **Re-run the full test suite against the new engine.** This is the proof. The fixtures
   (`FX-BarCorrection`, `FX-Outage5d`, `FX-PopBands`, the determinism tests, the leakage suite, the
   money-serialization test) either pass on the new engine or they don't. **Green = the migration
   preserved behavior.** A red determinism or money test blocks the cut-over regardless of anything
   else.
7. **Cut over one arena, watch one live daily run**, confirm Data-health is green, then migrate the
   rest. Keep the SQLite snapshots until you've had several clean runs.

Steps 2, 4, and 5 are mechanical for Claude Code; step 3 is the short known list above; step 6 is your
independent confirmation it worked — you do not need to trust the plumbing, only the green suite.

---

## 5. What you deliberately are *not* deciding now

- **You are not choosing an engine now.** SQLite is sufficient for the foreseeable life of the lab.
- **You are not choosing an isolation model for the professional engine now.** D71 keeps both Option
  A and Option B open; you decide at migration time, informed by whether a hosted/multi-tenant need
  actually exists.
- **You are not pre-building anything.** The two facts in §1 that make the swap easy are already true
  in the SQLite build; no work today preserves them.

The only thing this document asks of you is to keep it in `docs/` so the reasoning outlives the
conversation that produced it.

---

*Companion to MASTER_DESIGN_v1.9 (D3, D22, D59, D67, D69, D71; §14.2). Not part of the Phase 0–8
build. Consult only if the D3 migration trigger — multi-user hosting or intraday tick ingestion —
actually arrives. Research/paper-trading only. Not investment advice.*
