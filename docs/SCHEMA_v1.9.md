# SCHEMA_v1.9 — SQLite DDL Reference (single source of truth)

*Every table in the system. Claude Code: never invent a column — extend this file (same PR) if a phase genuinely needs one, and note it in PROGRESS.md. Types use SQLite affinities; all dates ISO-8601 TEXT unless noted; **ledger money columns are C# `decimal` persisted as TEXT** (EF Core's default SQLite decimal mapping — exact; D69) while market-data prices and derived statistics are REAL; all identity via `security_id`.*

> **v1.9.7 errata note (findings 108, 109, 112, 121).** Four consistency fixes merged in place: `config` gains a **composite `(key, version)` PK** — the previous `key`-only PK could physically hold one row per key, making the documented "versioned rows, never silent edits" contract (and the D56/D63 calibrated curves stored as versioned rows) unimplementable; the change rides a snapshot-gated `ConfigVersionedRows` migration (rule 14). The `runs` **forward-run uniqueness invariant** is stated with its Phase-2 partial index. `worker_state.heartbeat_at` gains its **D72 liveness semantics**. And enum-CHECK columns are noted as migration-extend-only.

```sql
------------------------------------------------------------------
-- IDENTITY & MARKET DATA (D39, D40)
------------------------------------------------------------------
CREATE TABLE securities (
  security_id      INTEGER PRIMARY KEY,          -- permanent internal id
  current_symbol   TEXT NOT NULL,
  name             TEXT,
  exchange         TEXT,                          -- e.g. 'US'
  sector           TEXT,                          -- EODHD classification (latest)
  industry         TEXT,
  first_seen       TEXT NOT NULL,
  delisted_on      TEXT                           -- terminal only (no successor)
);
-- Ticker reuse (D39): symbols are recycled across distinct securities over time, so
-- symbol uniqueness holds only among ACTIVE listings; delisted rows keep their last symbol.
CREATE UNIQUE INDEX ux_securities_active_symbol
  ON securities(current_symbol, exchange) WHERE delisted_on IS NULL;

CREATE TABLE ticker_history (
  security_id  INTEGER NOT NULL REFERENCES securities(security_id),
  symbol       TEXT NOT NULL,
  valid_from   TEXT NOT NULL,
  valid_to     TEXT,                              -- NULL = current
  PRIMARY KEY (security_id, valid_from)
);
CREATE INDEX ix_ticker_hist_symbol ON ticker_history(symbol, valid_from);

CREATE TABLE sector_changes (                     -- classification change log (D35)
  security_id INTEGER NOT NULL REFERENCES securities(security_id),
  changed_on  TEXT NOT NULL,
  old_sector  TEXT, new_sector TEXT,
  old_industry TEXT, new_industry TEXT,
  PRIMARY KEY (security_id, changed_on)
);

CREATE TABLE bars (                                -- versioned append-only (D40)
  security_id  INTEGER NOT NULL REFERENCES securities(security_id),
  date         TEXT NOT NULL,
  version      INTEGER NOT NULL DEFAULT 1,
  observed_at  TEXT NOT NULL,                     -- when WE first saw this version
  open REAL, high REAL, low REAL, close REAL, volume INTEGER,          -- raw
  adj_open REAL, adj_high REAL, adj_low REAL, adj_close REAL,          -- adjusted
  source       TEXT NOT NULL DEFAULT 'eodhd',
  PRIMARY KEY (security_id, date, version)
);
CREATE INDEX ix_bars_observed ON bars(observed_at);
-- READ RULE: latest version WHERE observed_at <= run.watermark. No UPDATE/DELETE ever.

CREATE TABLE corporate_actions (                   -- §13.6 semantics
  action_id    INTEGER PRIMARY KEY,
  security_id  INTEGER NOT NULL REFERENCES securities(security_id),
  type         TEXT NOT NULL CHECK (type IN
    ('dividend','split','ticker_change','merger_cash','merger_stock',
     'merger_mixed','spinoff','delist')),
  ex_date      TEXT,                               -- dividends
  effective_date TEXT NOT NULL,
  cash_per_share TEXT,                             -- dividend / merger cash leg (decimal TEXT, D69)
  ratio        REAL,                               -- split / exchange / spinoff ratio
  counterparty_security_id INTEGER REFERENCES securities(security_id), -- acquirer / spun-off
  new_symbol   TEXT,                               -- ticker_change
  observed_at  TEXT NOT NULL,
  source       TEXT NOT NULL DEFAULT 'eodhd',
  processed_on TEXT                                -- NULL until ledger applied
);

CREATE TABLE index_membership_log (                -- D35 daily refresh audit
  log_id       INTEGER PRIMARY KEY,
  as_of        TEXT NOT NULL,
  source_count INTEGER, crosscheck_count INTEGER,
  agreed       INTEGER NOT NULL,                  -- 0/1
  adds_json    TEXT, drops_json TEXT,             -- applied diff (security_ids)
  note         TEXT
);

CREATE TABLE index_membership (                    -- as-of state (never deleted)
  security_id INTEGER NOT NULL REFERENCES securities(security_id),
  added_on    TEXT NOT NULL,
  removed_on  TEXT,                                -- NULL = currently in index
  PRIMARY KEY (security_id, added_on)
);

------------------------------------------------------------------
-- FEATURES & REFERENCE DATA (D34, D41)
------------------------------------------------------------------
CREATE TABLE features (
  security_id INTEGER NOT NULL,
  as_of       TEXT NOT NULL,
  name        TEXT NOT NULL,                       -- e.g. 'mom_126_21', 'rsi_14'
  value       REAL,
  PRIMARY KEY (security_id, as_of, name)
);

CREATE TABLE regime_labels (                        -- PIT labels (D34/D50)
  as_of   TEXT PRIMARY KEY,
  trend   TEXT NOT NULL CHECK (trend IN ('bull','bear')),
  vol     TEXT NOT NULL CHECK (vol IN ('normal_vol','high_vol')),
  label   TEXT NOT NULL,                            -- denormalized cross product, e.g. 'bull/high_vol' (D50)
  inputs_hash TEXT NOT NULL                         -- provenance of the PIT computation
);
-- Episodes (D45) run on the trend component; regime-halt guardrails may key on either component (D50).

CREATE TABLE regime_episodes (                      -- D45
  episode_id INTEGER PRIMARY KEY,
  label      TEXT NOT NULL,
  start_date TEXT NOT NULL,
  end_date   TEXT                                   -- NULL = ongoing
);

CREATE TABLE factor_returns (                       -- French library (D41)
  date TEXT NOT NULL, factor TEXT NOT NULL,         -- MKT_RF,SMB,HML,UMD,RMW,CMA,RF
  value REAL NOT NULL,
  PRIMARY KEY (date, factor)
);
CREATE TABLE factor_refresh_log (
  refreshed_at TEXT PRIMARY KEY, files_json TEXT, checksum TEXT, rows_added INTEGER
);

------------------------------------------------------------------
-- STRATEGIES, ACCOUNTS, LEDGER (D29, D30, D43)
------------------------------------------------------------------
CREATE TABLE strategies (
  strategy_id   TEXT PRIMARY KEY,                  -- e.g. 'momentum:L126:K21:N40'
  family        TEXT NOT NULL,                     -- momentum|meanrev|lowvol|...
  config_json   TEXT NOT NULL,                     -- params + seed (frozen, D17)
  exit_policy_json TEXT NOT NULL,
  holding_horizon_days INTEGER,
  created_on    TEXT NOT NULL,
  parent_strategy_id TEXT,                         -- fork lineage
  status        TEXT NOT NULL DEFAULT 'candidate'  -- candidate|live|baseline|retired|control
);

CREATE TABLE accounts (
  account_id  INTEGER PRIMARY KEY,
  strategy_id TEXT NOT NULL REFERENCES strategies(strategy_id),
  starting_cash TEXT NOT NULL,                     -- decimal TEXT (D69)
  run_kind    TEXT NOT NULL DEFAULT 'live'         -- live|replay
);

CREATE TABLE positions (
  account_id  INTEGER NOT NULL REFERENCES accounts(account_id),
  security_id INTEGER NOT NULL REFERENCES securities(security_id),
  shares      REAL NOT NULL,
  cost_basis  TEXT NOT NULL,                       -- raw-price basis (D30); decimal TEXT (D69)
  opened_on   TEXT NOT NULL,
  frozen      INTEGER NOT NULL DEFAULT 0,          -- fail-closed flag (D39)
  frozen_reason TEXT,
  PRIMARY KEY (account_id, security_id)
);

CREATE TABLE trades (
  trade_id    INTEGER PRIMARY KEY,
  account_id  INTEGER NOT NULL,
  security_id INTEGER NOT NULL,
  side        TEXT NOT NULL CHECK (side IN ('buy','sell')),
  decided_on  TEXT NOT NULL,                       -- close T
  filled_on   TEXT NOT NULL,                       -- open T+1
  shares      REAL NOT NULL,
  raw_fill_price TEXT NOT NULL,                    -- decimal TEXT (D69)
  commission TEXT NOT NULL, spread_cost TEXT NOT NULL, impact_cost TEXT NOT NULL,  -- decimal TEXT (D69)
  cost_model_version TEXT NOT NULL,                -- D43 stamp
  reason      TEXT NOT NULL,                       -- wishlist|exit_policy|corp_action|guardrail
  action_id   INTEGER REFERENCES corporate_actions(action_id),
  run_kind    TEXT NOT NULL DEFAULT 'live'
);

CREATE TABLE capacity_rejections (                 -- D43 participation cap log
  account_id INTEGER, security_id INTEGER, as_of TEXT,
  intended_shares REAL, allowed_shares REAL, adv21 REAL,
  PRIMARY KEY (account_id, security_id, as_of)
);

CREATE TABLE cash_events (
  event_id INTEGER PRIMARY KEY,
  account_id INTEGER NOT NULL, security_id INTEGER,
  as_of TEXT NOT NULL,
  type TEXT NOT NULL,                              -- dividend|merger_cash|deposit|...
  amount TEXT NOT NULL,                            -- decimal TEXT (D69)
  action_id INTEGER REFERENCES corporate_actions(action_id),
  run_kind TEXT NOT NULL DEFAULT 'live'
);

CREATE TABLE equity_curve (
  account_id INTEGER NOT NULL, as_of TEXT NOT NULL,
  equity TEXT NOT NULL, cash TEXT NOT NULL,        -- decimal TEXT (D69)
  run_kind TEXT NOT NULL DEFAULT 'live',
  PRIMARY KEY (account_id, as_of, run_kind)
);

CREATE TABLE decisions (                            -- "Why this trade" provenance
  decision_id INTEGER PRIMARY KEY,
  account_id INTEGER NOT NULL, as_of TEXT NOT NULL,
  stage_json TEXT NOT NULL,                        -- funnel stages 1-6 snapshot
  run_kind TEXT NOT NULL DEFAULT 'live'
);

------------------------------------------------------------------
-- CONTROL POPULATIONS (D36)
------------------------------------------------------------------
CREATE TABLE control_populations (
  population_id INTEGER PRIMARY KEY,
  family        TEXT NOT NULL,                     -- daily|banded|monthly|quarterly
  family_seed   INTEGER NOT NULL,
  m             INTEGER NOT NULL,                  -- population size
  costs_on      INTEGER NOT NULL,                  -- 0 = cost-free (display-only)
  matched_params_json TEXT NOT NULL                -- N, sizing, ExitPolicy shape, costs
);

CREATE TABLE control_equity (                       -- compact per-member curves
  population_id INTEGER NOT NULL,
  member_index  INTEGER NOT NULL,
  as_of         TEXT NOT NULL,
  equity        TEXT NOT NULL,                     -- decimal TEXT (D69)
  run_kind      TEXT NOT NULL DEFAULT 'live',
  PRIMARY KEY (population_id, member_index, as_of, run_kind)
);

------------------------------------------------------------------
-- EVALUATION, GATE, MONITOR (D31, D44, D48; MONITOR doc)
------------------------------------------------------------------
CREATE TABLE trials_registry (
  trial_id INTEGER PRIMARY KEY,
  strategy_id TEXT NOT NULL,
  registered_on TEXT NOT NULL,
  kind TEXT NOT NULL,                              -- new|fork|retrain|sibling
  run_kind TEXT NOT NULL DEFAULT 'live'            -- replay trials separate (D37)
);

CREATE TABLE power_reports (                        -- NW-corrected MDE (D48)
  report_id INTEGER PRIMARY KEY,
  as_of TEXT NOT NULL,
  strategy_a TEXT NOT NULL, strategy_b TEXT NOT NULL,
  t_days INTEGER NOT NULL, sigma_lr REAL NOT NULL, nw_lag INTEGER NOT NULL,
  mde_ann REAL NOT NULL, observed_gap_ann REAL, verdict TEXT,
  run_kind TEXT NOT NULL DEFAULT 'live'
);

CREATE TABLE trade_evidence (                       -- D44
  strategy_id TEXT NOT NULL, as_of TEXT NOT NULL,
  n_trades INTEGER, expectancy REAL, ci_lo REAL, ci_hi REAL,
  mde REAL, block_len INTEGER,
  run_kind TEXT NOT NULL DEFAULT 'live',
  PRIMARY KEY (strategy_id, as_of, run_kind)
);

CREATE TABLE go_live_log (
  event_id INTEGER PRIMARY KEY, as_of TEXT NOT NULL,
  promoted TEXT, demoted TEXT, verdict TEXT NOT NULL,  -- Promoted|Refused|TooEarly|Revert
  evidence_json TEXT NOT NULL, run_kind TEXT NOT NULL DEFAULT 'live'
);

CREATE TABLE allocation_log (
  event_id INTEGER PRIMARY KEY, as_of TEXT NOT NULL,
  weights_json TEXT NOT NULL, reason TEXT NOT NULL,
  run_kind TEXT NOT NULL DEFAULT 'live'
);

CREATE TABLE overfitting_checks (
  check_id INTEGER PRIMARY KEY, strategy_id TEXT NOT NULL, as_of TEXT NOT NULL,
  signal TEXT NOT NULL,                            -- S1..S8
  value REAL, threshold_json TEXT NOT NULL, contribution TEXT NOT NULL,
  run_kind TEXT NOT NULL DEFAULT 'live'
);

CREATE TABLE overfitting_status (
  strategy_id TEXT NOT NULL, as_of TEXT NOT NULL,
  status TEXT NOT NULL CHECK (status IN ('healthy','warning','suspect','retired')),
  trigger_json TEXT NOT NULL, run_kind TEXT NOT NULL DEFAULT 'live',
  PRIMARY KEY (strategy_id, as_of, run_kind)
);

CREATE TABLE parameter_scans (
  scan_id INTEGER PRIMARY KEY, strategy_id TEXT NOT NULL, as_of TEXT NOT NULL,
  neighbor_json TEXT NOT NULL, alpha REAL, sharpe REAL
);

CREATE TABLE feature_baselines (
  strategy_id TEXT NOT NULL, feature TEXT NOT NULL,
  snapshot_on TEXT NOT NULL, deciles_json TEXT NOT NULL,
  PRIMARY KEY (strategy_id, feature, snapshot_on)
);

------------------------------------------------------------------
-- LLM (D16, D24, D46)
------------------------------------------------------------------
CREATE TABLE news_items (                           -- post-budget only
  news_id INTEGER PRIMARY KEY, as_of TEXT NOT NULL,
  title_hash TEXT NOT NULL, title TEXT, source TEXT,
  symbols_json TEXT, truncated_chars INTEGER,
  UNIQUE (as_of, title_hash)
);

CREATE TABLE analysis_cache (
  prompt_hash TEXT NOT NULL, model TEXT NOT NULL, as_of TEXT NOT NULL,
  task TEXT NOT NULL,                              -- regime_brief|brief|skeptic|hypotheses
  output_json TEXT NOT NULL,
  input_tokens INTEGER, output_tokens INTEGER, cost_usd REAL,
  PRIMARY KEY (prompt_hash, model, as_of)
);

CREATE TABLE llm_budget_log (
  as_of TEXT PRIMARY KEY, calls INTEGER, tokens INTEGER, cost_usd REAL,
  degraded INTEGER NOT NULL DEFAULT 0, note TEXT
);

------------------------------------------------------------------
-- RUNS & OPERATIONS (D40, D47)
------------------------------------------------------------------
CREATE TABLE runs (
  run_id INTEGER PRIMARY KEY,
  as_of TEXT NOT NULL,                             -- trading day processed
  run_kind TEXT NOT NULL CHECK (run_kind IN ('live','catchup','replay')),
  watermark TEXT NOT NULL,                         -- max observed_at visible (D40)
  started_at TEXT NOT NULL, finished_at TEXT,
  status TEXT NOT NULL DEFAULT 'running',          -- running|ok|failed
  inputs_hash TEXT
);
-- UNIQUENESS INVARIANT (v1.9.7 finding 109): at most ONE status='ok' row per as_of among FORWARD
-- kinds. Failed runs legitimately retry (a second row, same as_of), and replay produces many runs
-- over the same historical dates by design, so a blanket unique(as_of) would be wrong. Phase 2
-- enforces the real invariant with a partial index (created when Stage-2 first writes runs):
--   CREATE UNIQUE INDEX ux_runs_ok_forward ON runs(as_of)
--     WHERE status='ok' AND run_kind IN ('live','catchup');
-- This is what makes catch-up idempotency ("re-running a recovered day is a no-op") and
-- catchup_log(as_of PK) mutually consistent.

CREATE TABLE catchup_log (
  as_of TEXT PRIMARY KEY, recovered_at TEXT NOT NULL, run_id INTEGER NOT NULL
);

CREATE TABLE config (
  key TEXT NOT NULL, value_json TEXT NOT NULL,
  version INTEGER NOT NULL, changed_on TEXT NOT NULL, reason TEXT,
  PRIMARY KEY (key, version)                       -- v1.9.7 finding 108: composite PK
);
-- READ RULE: the current value of a key = the row with MAX(version) for that key.
-- Threshold/config changes are versioned rows here (MONITOR §5), never silent edits: a change
-- INSERTs (key, version+1) — never UPDATE, never DELETE (the same append-only discipline as bars).
-- Pre-v1.9.7 the PK was `key` alone, which could hold ONE row per key — making the versioned-rows
-- contract (and D56/D63's calibrated P_noise(t)/P_edge(t) curves stored as versioned rows)
-- unimplementable (finding 108).
```

------------------------------------------------------------------
-- v1.8 ADDITIONS (D52, D54, D55, API headroom; D50 regime params live in appsettings)
------------------------------------------------------------------
```sql
CREATE TABLE trading_calendar (                    -- D54
  date        TEXT PRIMARY KEY,                    -- trading sessions only
  session     TEXT NOT NULL CHECK (session IN ('full','half')),
  close_time_local TEXT NOT NULL                   -- ET close, e.g. '16:00' / '13:00'
);

CREATE TABLE journal_entries (                     -- D52
  entry_id    INTEGER PRIMARY KEY,
  created_on  TEXT NOT NULL,
  kind        TEXT NOT NULL CHECK (kind IN
    ('hypothesis','observation','decision_note','skeptic_review','outcome')),
  title       TEXT NOT NULL,
  body_md     TEXT NOT NULL,
  strategy_id TEXT REFERENCES strategies(strategy_id),
  linked_entry_id INTEGER REFERENCES journal_entries(entry_id), -- outcome -> hypothesis
  metric      TEXT,                                -- pre-declared confirm/refute metric
  evidence_window_days INTEGER,                    -- pre-declared window
  outcome     TEXT CHECK (outcome IN ('confirmed','refuted','inconclusive')),
  locked      INTEGER NOT NULL DEFAULT 0           -- 1 once linked at candidate creation
);
-- RULE (D52): a locked hypothesis row is immutable except via the outcome-closure
-- flow. CandidateFactory requires a linked hypothesis OR an 'unregistered' marker
-- in strategies.config_json (rendered permanently on the strategy card).

CREATE TABLE admin_actions (                       -- D55 audit trail
  admin_action_id INTEGER PRIMARY KEY,
  performed_at TEXT NOT NULL,
  kind        TEXT NOT NULL CHECK (kind IN
    ('manual_corporate_action','membership_override')),
  target_json TEXT NOT NULL,                       -- security_id / action / diff payload
  reason      TEXT NOT NULL,
  affected_accounts_json TEXT,
  resulting_row_ref TEXT                           -- e.g. 'corporate_actions:1234'
);

CREATE TABLE api_usage_log (                       -- INTEGRATIONS headroom rule
  as_of   TEXT NOT NULL, source TEXT NOT NULL,
  calls INTEGER NOT NULL, plan_limit INTEGER,
  PRIMARY KEY (as_of, source)
);

------------------------------------------------------------------
-- WORKER / API INFRA (D59/D60) — created by Phase 0's InitialCreate
------------------------------------------------------------------
CREATE TABLE jobs (                                -- async-command queue (API enqueues, Worker executes)
  job_id       INTEGER PRIMARY KEY,
  kind         TEXT NOT NULL CHECK (kind IN ('replay','analysis_brief','analysis_skeptic')),
  status       TEXT NOT NULL DEFAULT 'queued' CHECK (status IN ('queued','running','done','failed')),
  submitted_at TEXT NOT NULL,
  started_at   TEXT,
  finished_at  TEXT,
  request_json TEXT NOT NULL,
  result_ref   TEXT,                               -- e.g. 'runs:812' / 'journal_entries:44'
  error_json   TEXT
);

CREATE TABLE worker_state (                        -- single row, seeded by Phase 0's InitialCreate
  id              INTEGER PRIMARY KEY CHECK (id = 1),
  run_in_progress INTEGER NOT NULL DEFAULT 0,      -- 0/1; the API's 409/queue decision reads this
  current_run_id  INTEGER,
  heartbeat_at    TEXT                             -- D72 (v1.9.7): written by the running Worker at
                                                   -- least every Worker.HeartbeatSeconds; a
                                                   -- run_in_progress=1 with a heartbeat older than
                                                   -- Worker.StaleRunThresholdSeconds is treated as a
                                                   -- crashed run (cleared on launch; runs row marked
                                                   -- 'failed'; the Api's 409 decision ignores it)
);
```

## Enforcement notes
- **Quarantine (D37):** every forward view/query filters `run_kind='live' OR 'catchup'`; a dedicated test inserts replay rows and asserts zero leakage into forward views.
- **CI greps:** `DELETE FROM bars`, `UPDATE bars` anywhere in `src/` fail the build.
- **EF Core:** map these shapes 1:1; migrations are versioned; SCHEMA_v1.9.md updates ride the same PR as any migration.
- **Integer PKs are plain `INTEGER PRIMARY KEY` — NO `AUTOINCREMENT` (rule 14):** every `… INTEGER PRIMARY KEY` above (e.g. `runs.run_id`, `jobs.job_id`) is a plain rowid alias, exactly as written — never `AUTOINCREMENT`. EF Core 10's convention **adds** `AUTOINCREMENT` to value-generated integer keys, and its model snapshot cannot express "rowid without AUTOINCREMENT" (`SqliteValueGenerationStrategy.None` never round-trips → perpetual `PendingModelChangesWarning`). So after scaffolding `InitialCreate`, **hand-edit the generated `*_InitialCreate.cs` to delete the `.Annotation("Sqlite:Autoincrement", true)` lines** on those keys (leave the `.Designer.cs`/`ModelSnapshot.cs` untouched — the model keeps `ValueGeneratedOnAdd`, so model == snapshot and there is no pending-model warning; rowid still auto-assigns). Re-apply the edit if `InitialCreate` is ever regenerated. `config.version` and `worker_state.id` carry no autoincrement by construction (both `ValueGeneratedNever()`). Guarded by `SchemaFidelityTests.Schema_IntegerPrimaryKeys_HaveNoAutoincrement` (+ `Schema_IntegerPrimaryKey_StillAutoAssignsOnInsert`). The BUILD Phase-0 prompt (checkpoint 0.3) carries this as a build step.
- **D52:** UPDATEs to a `locked=1` hypothesis row outside the outcome-closure code path are a bug; add a trigger or repository guard + test.
- **D55:** the only write path to `admin_actions` (and to `source='manual'` domain rows) is the typed-confirmation admin flow.
- **D57/D58:** no schema change — read-models are computed projections over these tables, served by `AlphaLab.Api`; the UI never queries the DB directly.
- **D59/D60:** the `jobs` and `worker_state` tables (full DDL above) back the API's async-command pattern and the Worker's queue; `worker_state` is a single seeded row exposing the writer's status to the API for the 409/queue decision. Neither is on the statistical hot path.
- **D69:** ledger money columns marked `decimal TEXT` above map to C# `decimal` (EF Core's default SQLite mapping stores exact decimal strings). Never map a money property to `double`/REAL; REAL is reserved for market-data prices and derived statistics.
- **v1.9.7 finding 108 — `config` is append-only-versioned:** the PK is `(key, version)`; the current value is `MAX(version)` per key; a change INSERTs `version+1` and never UPDATEs or DELETEs an existing row. The Phase-4 calibration curves (D56/D63) rely on this to keep their version history. **Migration-path note (v1.9.8, A.3-1):** for a **from-scratch build** the composite PK ships **inside `InitialCreate`** (there is no prior `config` table to migrate) — this is what BUILD checkpoint 0.3 does and what `SchemaFidelityTests` asserts on-disk. The "snapshot-gated `ConfigVersionedRows` migration" language elsewhere describes the *other* context: retrofitting an **already-deployed** single-column `config` store. Both are correct in their setting; a from-scratch rebuild will not find (and should not hunt for) a separate `ConfigVersionedRows` migration.
- **v1.9.7 finding 109 — forward-run uniqueness:** enforced by the partial index `ux_runs_ok_forward` (created in Phase 2) — at most one `status='ok'` row per `as_of` for `run_kind IN ('live','catchup')`; replay runs and failed retries are deliberately exempt.
- **v1.9.7 finding 121 — enum CHECK columns extend only via migration:** `jobs.kind`, `jobs.status`, `runs.run_kind`, `corporate_actions.type`, `overfitting_status.status`, `journal_entries.kind`/`outcome`, `admin_actions.kind`, `trading_calendar.session`, and `regime_labels.trend`/`vol` carry CHECK-IN constraints; adding an allowed value (e.g. a future `jobs.kind`) is a versioned migration + a SCHEMA edit in the same PR — never an unlisted write.
