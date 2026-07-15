# DB_RELOCATION — moving the SQLite database to another directory

*Ops runbook. How to relocate AlphaLab's SQLite database file(s) to a different folder or drive with
nothing left pointing at the old location. This is a **path change**, not an engine change — if you
are ever leaving SQLite for PostgreSQL/SQL Server, that is a different job with its own document
(`FUTURE_DB_MIGRATION.md`). Research/paper-trading only. Not investment advice.*

---

## 0. When to use this

Use it when you want the `.db` file somewhere else: a bigger or faster disk, a backed-up location, a
new drive letter, or just a tidier path. Typical triggers: the E: drive fills up, you move the lab to
a new machine, or you want the DB back under your user profile for portability.

Everything below is a config edit plus a file move. No schema change, no migration, no code logic
change — the arena design (D71) and the entity models are untouched.

---

## 1. The one value that decides where the DB lives

There is a single connection string, `ConnectionStrings:AlphaLab`. Today it is:

```
Data Source=E:\AlphaLabDatabase\{Arena.Id}\alphalab.db
```

Two tokens are resolved at runtime by the shared C# `DbPathResolver` (and, for tooling, by
`snapshot-db.ps1`, which reads the same string):

- **`{Arena.Id}`** → the arena slug (`sp500`, and any future arena). **Keep this token.** It is what
  gives every arena its own physically-isolated file so two arenas can never collide (D71). When you
  relocate, change only the **base** — the part *before* `{Arena.Id}` — never the token itself.
  The value that *fills* this token is each process's `Arena:Id` config key: the four spots in §2 are the
  path **template**; `Arena:Id` is the value that **resolves** it. Relocation never touches `Arena:Id`.
  But because Worker, Api, and Backfill each supply their own, a *second* arena (D71) must set the same
  new `Arena:Id` in **all three** backend appsettings — a half-applied edit gives them identical
  connection strings that still open different databases. `ConfigConsistencyTests` now guards that the
  three `Arena:Id` agree (finding 148), just as it guards the four template copies.
- **`{LocalAppData}`** *(optional)* → expanded to your Windows *Local AppData* folder via the
  known-folders API, **never** an environment variable (D67 bans env-var reads). Use this token
  instead of a hard drive letter if you want the DB to follow your user profile (portable across
  machines/users). Example portable form: `Data Source={LocalAppData}\AlphaLab\{Arena.Id}\alphalab.db`.
- **No tokens** → the value is taken as a literal absolute path (this is the current E: form).

Absolute-anchored is required (never a relative path): the Worker, the Api, and the EF design-time
factory run from three different working directories, so a relative path would mean three different
databases.

---

## 2. What to change

The connection string is conceptually **one value**, but it is physically written in four C# spots
that a test forces to stay identical, plus the docs. The snapshot/backup tooling is **not** an edit
site any more — it reads the value out of the Worker's `appsettings.json`.

| # | Edit | Role | Guarded by a test? |
|---|------|------|--------------------|
| 1 | `src/AlphaLab.Data/DbPathResolver.cs` → `DefaultConnectionString` const | the default used by bare `dotnet ef` / the design-time factory | ✅ |
| 2 | `src/AlphaLab.Worker/appsettings.json` → `ConnectionStrings:AlphaLab` | what the **Worker** (sole writer) actually opens | ✅ |
| 3 | `src/AlphaLab.Api/appsettings.json` → `ConnectionStrings:AlphaLab` | what the **Api** actually opens | ✅ |
| 4 | `tools/Backfill/appsettings.json` → `ConnectionStrings:AlphaLab` | what the **Backfill CLI** (Phase-1 bootstrap writer) opens — added in checkpoint 1.10, guarded from v1.9.10 | ✅ |
| — | `tools/snapshot-db.ps1` | reads #2 and resolves the tokens itself | n/a — **auto-follows**, no edit |
| 5 | `docs/CONFIG_REFERENCE_v1.9.md` (the `ConnectionStrings` note) + a `PROGRESS.md` session-log line | keep docs in step with code (rule 14) | — |

Set #1–#4 to the **same** new value, keeping `{Arena.Id}` in place. Change only the base directory.
Miss #4 and the CLI writes to the *old* path while the Worker/Api read the new one — a full database
the rest of the lab never sees, with no error (the trap v1.9.10 closed).

---

## 3. The safety net

`tests/AlphaLab.Data.Tests/ConfigConsistencyTests.cs` asserts that the Worker's, the Api's, and the
Backfill CLI's `ConnectionStrings:AlphaLab` all equal `DbPathResolver.DefaultConnectionString`. So if
you edit three of the four spots and forget the fourth, `dotnet test` goes **red and names the
offending project**. You cannot half-relocate by accident.

`snapshot-db.ps1` is not test-covered, but it can no longer drift either — it derives the path from
the Worker's `appsettings.json` (spot #2) rather than holding its own copy.

---

## 4. Move the actual file

The config tells the app *where to look*; it does not move data. For each arena (currently just
`sp500`):

- Move `<oldbase>\<arena>\alphalab.db` → `<newbase>\<arena>\alphalab.db`.
- Move the **`-wal` and `-shm` sidecars too, if they exist** (they exist only if the Worker was
  mid-write and hadn't checkpointed). Move all three together so the DB stays self-consistent.
- Optionally move the `<oldbase>\<arena>\snapshots\` folder as well, or leave old snapshots behind.

If you *skip* the move, the next Worker run simply creates a **fresh, empty** database at the new
path. That is harmless while the DB is empty (Phase 0), but it silently **loses your data** once the
DB holds anything. Rule: whenever the DB is non-empty, move the file — do not let it be recreated.

Take a `tools/snapshot-db.ps1` copy first if the DB matters (it is your rollback).

---

## 5. Procedure (in order)

1. **Snapshot** the current DB: `tools/snapshot-db.ps1` (writes under the *old* base — keep it as a
   rollback).
2. **Edit #1–#4** to the new base (same value in all four; keep `{Arena.Id}`).
3. **`dotnet test`** → green confirms the four spots agree (§3). A red `ConfigConsistencyTests`
   means you missed one.
4. **Move the file(s)** per §4 (`alphalab.db` + any `-wal`/`-shm`, per arena).
5. **`dotnet run --project src/AlphaLab.Worker`** → it opens the new path and exits 0 (no
   re-migration; it is the same file).
6. **`tools/snapshot-db.ps1`** → it now writes under the **new** base (proves the tooling followed).
7. **Update docs** (#5): the `CONFIG_REFERENCE` connection-string note and a `PROGRESS.md`
   session-log entry. Commit.

---

## 6. Two forms cheat-sheet

| Want | Connection string | Result |
|------|-------------------|--------|
| Fixed location on a specific drive (current) | `Data Source=E:\AlphaLabDatabase\{Arena.Id}\alphalab.db` | Always at `E:\AlphaLabDatabase\<arena>\alphalab.db`. Machine-specific. |
| Follows the Windows user profile (portable) | `Data Source={LocalAppData}\AlphaLab\{Arena.Id}\alphalab.db` | Resolves to `%LOCALAPPDATA%\AlphaLab\<arena>\alphalab.db` on whatever machine runs it. No drive letter to chase. |
| Any other fixed folder | `Data Source=D:\lab\db\{Arena.Id}\alphalab.db` | Literal path; base is whatever you set. Keep `{Arena.Id}`. |

Switching *between* these forms is exactly the §5 procedure — it is just a different value for #1–#3.

---

*Companion to CONFIG_REFERENCE_v1.9 (the `ConnectionStrings` key) and MASTER_DESIGN_v1.9 (D59 sole
writer, D67 no env vars, D71 arena isolation; rule 14 docs-in-sync). For changing the database
**engine** (not its location), see `FUTURE_DB_MIGRATION.md`. Research/paper-trading only.*
