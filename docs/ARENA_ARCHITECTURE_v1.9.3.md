# AlphaLab — Arena Architecture (v1.9.3)

*Standalone companion to MASTER_DESIGN_v1.9. Defines how AlphaLab supports multiple isolated
universes ("arenas") — e.g. an S&P 500 lab and a future Russell 2000 lab — without ever combining
their competitions. Introduces decision **D71**. **No SCHEMA change.** Nothing here alters the
Phase 0–4 build; the S&P 500 arena is built exactly as specced today, and this document is the
blueprint for adding arena #2 later, cheaply.*

---

## 0. The one-paragraph summary

An **arena** is a complete, self-contained single-universe paper-trading lab: its own stock
universe, its own data, its own random control populations, its own cost model and calibration, its
own database file, and its own Worker+Api process pair. Arenas share **code**, never **data or
calibration**. You start with one arena (`sp500`). Adding another later is *copy the config, change
two fields, spin up a second instance* — not a rewrite. The frontend shows an arena **switcher** and
renders one arena at a time; it never merges two arenas into a single leaderboard, because ranking a
large-cap strategy against a small-cap strategy compares numbers produced under different cost models
and different control populations — the same category error as ranking a daily strategy against a
monthly one.

---

## 1. Decision D71 (the pinned rules)

> **D71 — Multi-arena isolation via the multi-instance model.**
>
> **(a) An arena is a single-universe lab, isolated at the storage layer.** Each arena has its own
> SQLite database file, its own `Worker` and `Api` instances, and its own snapshot/backup
> directories, all derived from a single `Arena.Id`. Isolation is **physical**, not a filter over a
> shared table — so cross-arena pooling is impossible by construction, not by discipline.
>
> **(b) Calibration is arena-scoped and never shared.** The cost model (D43), covariance estimator
> (D42), control-population seeds and turnover matching (D36), verdict/threshold curves (D56/D63),
> equal-weight benchmark (D68), and the Arena Replay calibration report are all stamped with
> `Arena.Id` and computed independently per arena. A large-cap calibration must never be reused for a
> small-cap universe (their spread/impact and covariance structure differ fundamentally); doing so
> would reintroduce exactly the silent bias the lab exists to eliminate.
>
> **(c) The frontend is arena-scoped; leaderboards never merge across arenas.** The UI selects one
> active arena and renders its screens against that arena's Api. A combined view is permitted only as
> **clearly-separated side-by-side panels**, never a single sorted ranking that mixes arenas.
>
> **(d) A cross-arena meta-allocator is explicitly out of scope.** Allocating real capital *across*
> universes (e.g. "best S&P strategy vs. best Russell strategy for one pool") is a distinct future
> layer above all arenas and is not part of this model. The cockpit *shows* arenas; it does not
> *fund-allocate across* them.
>
> **(e) SCHEMA is unchanged.** No `universe_id`/`arena_id` column is added to any table. The arena
> boundary lives in configuration, storage paths, and process instances — not in the row shape.
>
> **Rationale.** For a solo builder, physical isolation buys correctness (no accidental pooling) and
> near-zero new code (the DB path is already config-driven via `DbPathResolver`), at the cost of a
> shared cross-arena database view — which D71(c)'s side-by-side rule shows we don't want anyway. The
> membership-over-time machinery (D20/D39/D70) already handles a security moving between universes
> (e.g. a Russell 2000 name graduating to the S&P 500) as a temporal membership fact on a permanent
> `security_id`; that is independent of the arena model and needs no arena-level change.

---

## 2. The mental model: arena vs. universe vs. security

Three ideas are easy to conflate. Keeping them distinct is the whole design.

**Universe** — the *rule* that defines which stocks are eligible (e.g. "current S&P 500 members").
A universe is a named, validated membership feed with history (D35/D70). One universe per arena.

**Arena** — a *running lab* built around exactly one universe. The arena is the competition: the
strategies, the control populations, the ledger, the calibration, the screens. "The S&P 500 arena"
is a concrete database + process pair; "the S&P 500 universe" is the membership rule it runs on.

**Security** — a *company* with a permanent `security_id` (D39). A security is **not** owned by an
arena. It flows *through* arenas over time via membership: a name can be in the Russell universe
2016–2021 and the S&P universe 2021–present, and each arena's as-of membership decides whether that
arena may hold it on a given date. The same `security_id`, the same bars, can legitimately appear in
two arenas' databases (each arena backfills the securities its universe touches). This is not
duplication-as-error; it is two independent labs each keeping the local data its universe needs.

The graduation case therefore needs **no** arena machinery: it is already handled by the temporal
membership log (D70). The arena model is about isolating *competitions*, not about tracking which
index a stock is in.

---

## 3. Backend design

### 3.1 Arena identity (config)

A new top-level `Arena` block in each instance's `appsettings.json`:

```jsonc
"Arena": {
  "Id": "sp500",              // stable slug; the isolation key. lowercase, no spaces
  "DisplayName": "S&P 500"    // human label for the UI switcher and reports
}
```

Everything arena-specific **derives from `Arena.Id`** so two arenas can never collide:

- **Database file:** `Data Source=<DbBase>/{Arena.Id}/alphalab.db`, where `<DbBase>` is whatever
  base `ConnectionStrings:AlphaLab` carries. **This deployment uses the literal absolute base
  `E:/AlphaLabDatabase`** (relocated per `docs/DB_RELOCATION.md`); the portable alternative is the
  `{LocalAppData}/AlphaLab` token form. Path separators are normalized to the running OS (v1.9.36),
  so one template serves Windows and Linux alike. Either way the **`{Arena.Id}` token is what isolates
  arenas** — it is resolved by the shared `DbPathResolver` (see §3.2) and must never be removed
  when the base changes.
- **Snapshots / backups:** `<DbBase>/{Arena.Id}/snapshots/`, `<DbBase>/{Arena.Id}/backups/`.
- **Ports (dev):** each arena's API listen URL is the standard, committed `Urls` key in its
  appsettings profile (non-secret; never the `ASPNETCORE_URLS` env var — D67), and the Web dev
  port is per-instance config; a simple convention is a per-arena offset (e.g. sp500 → Api 5230 /
  Web 5210; a future arena → Api 5231 / Web 5211). Each Web registry entry's `baseUrl` must match
  that arena's `Urls` value exactly (CONFIG_REFERENCE).
- **Log lines:** every structured log record carries `arena={Arena.Id}` so multi-arena operation is
  legible in one console.

`Universe.*` (already per-config: `MembershipPrimary`, `CountSanity`, `Bootstrap.*`, historical
membership source) fully specifies the arena's stock universe. Adding an arena = a new config profile
with a new `Arena.Id` and a new `Universe.*` block. **No code change.**

### 3.2 `DbPathResolver` — the single change that makes isolation automatic

The Phase 0 `DbPathResolver` already resolves `{LocalAppData}` and creates the directory. It gains
one responsibility: **arena-namespacing the path.** Signature stays the same; the resolved path now
includes the arena segment.

```
Data Source={LocalAppData}/AlphaLab/alphalab.db              // v1.9 (single arena, implicit)
Data Source={LocalAppData}/AlphaLab/{Arena.Id}/alphalab.db   // v1.9.3 (arena-namespaced, portable token base)
Data Source=E:/AlphaLabDatabase/{Arena.Id}/alphalab.db       // as deployed (literal absolute base — docs/DB_RELOCATION.md)
```

Consumed identically by the Worker, the Api, and the EF design-time factory — so all three of an
arena's processes open the same file, and no two arenas ever open the same file. The design-time
factory reads `Arena.Id` from the same config the runtime does (or defaults to `sp500` when invoked
bare by `dotnet ef`, so migrations remain reproducible).

**Phase 0 impact (pinned — FR-37, v1.9.4):** `Arena.Id = "sp500"` is set from day one (SETUP §7);
the default `ConnectionStrings:AlphaLab` in CONFIG_REFERENCE already carries the `{Arena.Id}` token,
and the Phase 0 `DbPathResolver` resolves it alongside `{LocalAppData}`. The namespaced path
therefore exists from the first migration and no file ever needs to move when arena #2 arrives.
(The earlier "omit the segment now, move the file later" option is retired — it contradicted the
authoritative CONFIG default and the SETUP day-zero checklist.)

### 3.3 Calibration scoping (the load-bearing rule)

Everything that is *learned from data* is arena-local and stamped with `Arena.Id`:

| Artifact | Decision | Why it must be per-arena |
|---|---|---|
| Cost model (spread + √-impact coefficients) | D43 | Small-cap spreads/impact dwarf large-cap; a shared model flatters small-cap results |
| Covariance estimator (Ledoit–Wolf shrinkage) | D42 | A 2,000-name covariance is a different numerical problem than a 500-name one |
| Control populations (turnover-matched randoms) | D36 | Controls must be drawn from *this* arena's universe and turnover |
| Verdict / threshold curves | D56/D63 | Detection thresholds are calibrated against *this* arena's replay |
| Equal-weight benchmark | D68 | Built from *this* arena's eligible universe through *this* arena's cost model |
| Arena Replay calibration report | D37/D70 | Replays *this* arena's as-of membership only |

Because each arena is a separate database, this is *mostly automatic* — an arena physically cannot
read another's calibration tables. D71(b) states it as a rule so that no operator ever hand-copies a
calibration "to save time," which would be the one way to break isolation despite the separate DBs.

### 3.4 What does NOT change

- **SCHEMA:** unchanged. `strategies`, `accounts`, `control_populations`, `allocation_log`,
  `trials_registry`, and every other table keep their v1.9 shape. No universe/arena column.
- **The funnel, allocator, gate, monitor:** unchanged. They already operate on "the universe" —
  which is now simply "this arena's universe." They were never told there was more than one, and
  under the multi-instance model they never need to be.
- **The membership machinery (D20/D39/D70):** unchanged. Already temporal and universe-agnostic.
- **Phase order (D65):** unchanged. S&P 500 arena straight to Phase 4, exactly as planned.

This is the payoff of choosing multi-instance over a shared-database partition: the invasive change
(threading a key through funnel/allocator/gate/monitor/read-models) simply doesn't happen.

---

## 4. Frontend design

### 4.1 The arena registry

The Blazor client replaces the single `Api:BaseUrl` with an **arena registry** in
`wwwroot/appsettings.json`:

```jsonc
"Arenas": [
  { "id": "sp500", "displayName": "S&P 500", "baseUrl": "http://127.0.0.1:5230" }
  // future: { "id": "russell2000", "displayName": "Russell 2000", "baseUrl": "http://127.0.0.1:5231" }
]
```

An **active-arena** state (default: the first entry) drives which base URL the `ReadModelClient`
targets. The arena switcher sets it. Every screen re-fetches against the active arena's Api. With one
arena the switcher is a single static label; adding arena #2 is a one-line registry entry — no client
code change. The registry is wired in Phase 0 with the single `sp500` entry (FR-37); there is no bare
`Api:BaseUrl` key anywhere.

### 4.2 Screen behaviour

- **Every screen is arena-scoped.** Strategies, Live, Allocation, Populations, Risk, Regimes,
  Data-health, Journal, Go-live log, Trades, Activity, Why-trade, Overfitting-health — all render
  the active arena only.
- **The calibration provenance line is mandatory.** Below each leaderboard/health screen, a subtitle
  states the active arena's control population, cost-model version, and calibration id
  (e.g. *"Compared against the S&P 500 control population · cost model v4 · calibration EW-500"*).
  This makes D71(b)'s scoping *visible* — the user always sees which world's numbers they are reading.
  Like every honesty rule, the provenance data is a **D58 read-model**, never assembled in the client:
  each arena's Api serves it (e.g. `GET /api/v1/arena` → `{ id, display_name, cost_model_version,
  calibration_id, control_population }`), and the UI renders it verbatim.
- **No merged leaderboard (D71c).** The UI must never render a single sorted ranking mixing arenas.
  A cross-arena view is allowed only as side-by-side panels, each with its own provenance line and
  its own sort. There is no "sort all strategies across all arenas by alpha" control — it would be a
  lie.
- **The security-detail / why-trade screen shows membership as a timeline**, not a field: one
  `security_id`, with its index-membership intervals drawn in order (e.g. Russell 2016–2021 → S&P
  2021–present), making the graduation model legible and reinforcing that arenas share securities
  over time without sharing competitions.

### 4.3 Empty-state and Phase 0

Phase 0's empty Blazor client already renders every screen against one Api. Under v1.9.3 it renders
against the *active arena's* Api, with a one-entry registry. The NFR-3 empty-state ("No run yet") is
unchanged. Nothing in the Phase 0 DoD moves.

---

## 5. Operations — running N arenas

Running two arenas is two `(Worker + Api)` process pairs over two database files, one shared code
checkout:

- **Config profiles:** per-arena config **directories** — each instance runs from its own directory
  with its own `appsettings.json` + `appsettings.Secrets.json`, keeping the D67 two-file config
  builder intact. (A layered `appsettings.{arena}.json` would add a third config source the D67
  builder deliberately does not load — that option is retired.) Each profile sets `Arena.Id`,
  `Universe.*`, and its ports.
- **Snapshots/backups:** already arena-namespaced via `Arena.Id` (§3.1). `tools/snapshot-db.ps1` and
  `tools/migrate.ps1` take an `-Arena` parameter (default `sp500`) and operate on that arena's file.
- **CI:** `tools/ci.ps1` is arena-agnostic (it builds/tests the shared code); it needs no per-arena
  variant.
- **A strategy improvement lands once in code and benefits every arena** on its next run — the labs
  differ only in data and calibration, never in logic.

Sequencing recommendation: only ever spin up arena #2 *after* the S&P 500 arena has reached Phase 4
sign-off and is running reliably. The whole point of the vertical slice (D65) is to prove the
machinery once; arenas replicate a proven machine, they do not parallelise an unproven one.

---

## 6. How to add an arena (future checklist)

When the S&P 500 arena is validated and you want, say, Russell 2000:

1. **Source the universe feed.** Add the arena's `IIndexMembershipProvider` pair (primary +
   cross-check) behind the existing seam — e.g. the iShares IWM holdings CSV primary + a cross-check
   source — plus a historical as-of membership source for replay. Named, validated, fail-closed
   (Golden Rule 25), exactly like the S&P feeds.
2. **Write the config profile.** Copy the sp500 profile; set `Arena.Id = "russell2000"`,
   `DisplayName`, the `Universe.*` block, and the ports.
3. **Recalibrate — do not copy.** Run this arena's own cost-model calibration, covariance setup,
   control-population seeding, and a full Arena Replay to calibrate its verdict thresholds. Treat no
   small-cap result as valid until this pass completes (D71b).
4. **Backfill this arena's data.** Backfill bars for its universe (and, for replay, every historical
   member in the replay window) into *its* database file.
5. **Register it in the frontend.** Add one entry to the Web arena registry (§4.1).
6. **Run it.** Spin up its Worker+Api instance. The cockpit switcher now offers it.

No SCHEMA migration, no funnel/allocator/gate/monitor edits, no frontend code change beyond the
registry entry. That is the design goal of D71, achieved.

---

## 7. Trade-offs, stated honestly

**What this model gives you:** hard, physical isolation; near-zero new code to reach it; per-arena
calibration honesty by construction; a clean cockpit that scales to N arenas by config; and full
preservation of the v1.9 package (no schema churn, no regression risk).

**What it costs you:** no single database spanning all arenas (you inspect each arena separately —
which D71(c) shows is what you want anyway); a security in two arenas stores its bars twice (cheap —
bars are small, and each arena legitimately needs its own local copy); and running N arenas means N
process pairs (trivial for a personal, scheduled, non-interactive lab).

**The boundary you are explicitly declining:** a meta-allocator that competes strategies across
universes for one capital pool (D71d). If you ever want that, it is a new, separate design decision
layered *above* the arenas — "combine the winners, not the populations" — and it does not retroactively
require any arena to share data. Keeping it out is what lets each arena stay honestly isolated.

---

*Arena Architecture v1.9.3. One universe per arena; arenas share code, never data or calibration;
the cockpit shows arenas side by side but never merges their rankings; adding an arena is a config +
instance operation, not a rewrite. Supersedes nothing — additive to MASTER_DESIGN_v1.9 (D71).*
