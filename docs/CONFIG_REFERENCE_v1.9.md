# CONFIG_REFERENCE_v1.9 — every key, default, unit, owning decision

*Single source of truth for configuration. Non-secret values live in `appsettings.json` (shape below); secrets only in the gitignored `appsettings.Secrets.json` (D67 — never in env vars). Claude Code: never hard-code a value that belongs here; never invent a key — extend this file in the same PR.*

> **v1.9.7 errata note (findings 110–116).** New keys, merged below: `Regime.ProxySource` names the regime proxy feed (D73/FR-38, finding 110; INTEGRATIONS §9); `Worker.DrainQueuedJobsOnLaunch` / `Worker.HeartbeatSeconds` / `Worker.StaleRunThresholdSeconds` complete the D72 process model (findings 111–112); `Populations.TurnoverMatchTolerancePct` backs the cost-match verification (finding 115); `Replay.EdgePlantSurvivalFloor5y` and `Replay.JointFalseAlarmMaxFrac` are the new Phase-4 calibration bounds (findings 113–114); the `Allocator` block carries the floor-feasibility rule (finding 116). No secret changes.

> **v1.9.12 errata note (findings 158–159).** The bootstrap CLI's own config surface is now documented: a new **`tools/Backfill/appsettings.json`** subsection below carries the `Eodhd` (`BaseUrl`, `ExchangeSuffix`) and `Backfill` (`BackfillYears`, `ApiPlanLimit`, `RawCacheRoot`, `WikipediaSp100Url`, `HistoricalMembershipUrl`) sections the CLI actually consumes — previously absent from this "only source of truth" (finding 158). `BackfillYears` moved out of the `Data` block: the live key is **`Backfill:BackfillYears`**, not `Data:BackfillYears`. And a **binding caveat** was added to Key rules — `CalendarOptions`/`RegimeOptions`/`DataQualityOptions` declare a `SectionName` but are currently DI-registered as default instances, so a value placed in those sections is ignored until the consuming phase wires the bind (finding 159). No new keys, no secret changes. The `Universe.Bootstrap.Universe` "consumed by the backfill CLI" line is **unchanged** — correcting it is the parked **D76** decision (finding 151).

## Secrets (D67 — single gitignored `appsettings.Secrets.json`; no env vars, no User Secrets store)
This is a local-only, single-machine tool. Secrets live in **one gitignored JSON file layered on top of `appsettings.json`** — no environment variables and no .NET User Secrets store anywhere. The config builder is exactly `AddJsonFile("appsettings.json", optional:false).AddJsonFile("appsettings.Secrets.json", optional:true)` in **AlphaLab.Api and AlphaLab.Worker** — **no** `AddEnvironmentVariables`, **no** `AddUserSecrets`. The standalone-WASM **AlphaLab.Web** client cannot hold secrets (its `wwwroot/appsettings.json` is served to the browser); it loads **only** the non-secret **`Arenas` registry** (id / displayName / baseUrl per arena — D71; a single `sp500` entry at launch, see the AlphaLab.Web section below) and never reads `appsettings.Secrets.json`. There is no bare `Api:BaseUrl` key — the active arena's registry `baseUrl` plays that role.

```jsonc
// appsettings.Secrets.json  — GITIGNORED, never committed (CI grep enforces)
{
  "Secrets": {
    "EodhdApiToken":   "…",        // EODHD data APIs (D35/D49)
    "AnthropicApiKey": "…",        // Claude Messages + Batches (D46) — Phase 5
    "AlpacaKeyId":     "…",        // optional — Alpaca cross-check
    "AlpacaSecretKey": "…"
  }
}
```

Config keys are unchanged (`Secrets:EodhdApiToken`, `Secrets:AnthropicApiKey`, `Secrets:AlpacaKeyId`, `Secrets:AlpacaSecretKey`) — only their *source* is fixed to this file. `.gitignore` must list `appsettings.Secrets.json` (and `appsettings.*.Secrets.json`). The keys never enter git; the committed `appsettings.json` holds only non-secret config. No `UserSecretsId` is needed in `Directory.Build.props`.

## appsettings.json (defaults; every change is a versioned `config` row with a reason)

```jsonc
{
  "Arena": {                                       // D71 — the isolation identity (see ARENA_ARCHITECTURE_v1.9.3)
    "Id": "sp500",                                 // stable slug; drives the DB path, snapshot/backup dirs, and log tags. lowercase, no spaces
    "DisplayName": "S&P 500"                       // human label for the UI arena switcher and reports
  },
  "Universe": {
    "Index": "GSPC.INDX",            // S&P 500 (D20) — the membership machinery always tracks this index
    "MembershipCountSanity": [495, 510],          // D35 fail-closed band (S&P 500); the S&P 1500 target (D87, contingent) needs ~[1490,1520] — a Phase-4 prerequisite, not set here
    "MembershipPrimary": "ivv_csv",               // D49 launch: ivv_csv; post-upgrade: eodhd
    "MembershipCrossCheck": "wikipedia",          // D49 launch: wikipedia; post-upgrade: ivv_csv
    "SectorSource": "ivv_csv",                    // D49 launch; post-upgrade: eodhd
    "HistoricalMembershipSource": "community_csv",// D49/D70 launch: fja05680/sp500; post-upgrade: eodhd
    "Exclusions": ["SUN"],                        // finding 266 — canonical symbols permanently OUT of the universe. TWO consumers read this ONE list: the historical backfill SKIPS them on ingest (skip-and-record, like a ticker-reuse suspect) and the replay composition DENIES them from the roster (ExclusionScopedMembershipRead). The escape hatch for single-spell symbol reuse the >2y disjoint-spell heuristic cannot see (SUN = old Sunoco's ticker reused by Sunoco LP, whose in-window bars are the wrong company). Case-insensitive. MUST match across the Worker + Backfill appsettings (ConfigConsistencyTests). Default [].
    "Bootstrap": {                                // D65/D70 — the S&P 100 slice (forward universe through Phase 4 sign-off)
      "Universe": "sp100",                        // consumed by the backfill CLI and Stage-1 eligibility until the post-Phase-4 widen
      "MembershipPrimary": "oef_csv",             // iShares OEF holdings CSV (same BlackRock pattern as IVV)
      "MembershipCrossCheck": "wikipedia_sp100",  // en.wikipedia.org/wiki/S%26P_100 constituents table
      "CountSanity": [99, 103]                    // fail-closed band for the slice
    }                                             // replay NEVER uses the slice — S&P 500 as-of membership only (D70)
  },

  "Data": {                                        // NOTE: bound by DataQualityOptions (SectionName="Data") — see Key rules caveat; `Provider` is aspirational (unread today)
    "Provider": "eodhd",                          // D35; fallback: alpaca
    "BarCrossCheckSampleSize": 10,                // rotating names/day vs Alpaca (FR-6)
    "BarCrossCheckTolerancePct": 0.5,
    "OutlierZ": 8.0                               // quality gate daily-return z cutoff
    // BackfillYears moved to the Backfill CLI section — the live key is `Backfill:BackfillYears` (v1.9.12 finding 158)
  },

  "Costs": {                                       // D43 — the falsifiable cost model
    "ModelVersion": "cm-1.0",
    "CommissionPerTrade": 0.0,
    "HalfSpreadBpByBucket": { "mega": 1.0, "large": 2.5, "other": 5.0 },
    "BucketAdvUsdThresholds": { "mega": 4.0e8, "large": 1.0e8 },   // 21d ADV notional
    "ImpactK": 0.1,
    "AdvWindowDays": 21,
    "ParticipationCapPctAdv": 2.0                 // excess rejected + logged
  },

  "CorporateActions": {                            // §13.6 part 2 — the forced-event ledger's configurable rules (CHANGELOG findings B, C). Feeds DORMANT at launch (D49); semantics + fixtures ship, production behaviour is freeze+alert
    "BankruptcyHaircutPct": 0.0,                    // delist force-exit at last_print × (1 − pct/100); 0 = take the last print (invent no loss); raise per-event for a KNOWN wipeout (Phase-7 admin / versioned config row)
    "SpinoffLiquidationDays": 0                    // 0 = exit-only (owner's ExitPolicy manages the spun-off receipt); >0 = liquidate after N sessions — EXECUTION is Phase 7, key carried for fidelity
  },

  "Accounts": {                                    // v1.9.19 (finding K) — the ledger's opening capital
    "StartingCash": 100000                         // each account (baseline + dummy) opens at $100,000 (decimal → TEXT, D69).
                                                   // The AUTHORITATIVE runtime value is the versioned `config` row Accounts.StartingCash
                                                   // (MAX(version) — the Regime.ProxySecurityId precedent), written by DummyRoster from this
                                                   // default on a fresh store; appsettings documents the default, the DB row is what the accounts opened at.
  },

  "Benchmark": {                                   // v1.9.20 (finding LL) — the §5.1 cap-weight benchmark's traded ETF proxy
    "CapWeightProxySecurityId": null               // NEVER set in appsettings — the key exists ONLY as a versioned `config` row
                                                   // Benchmark.CapWeightProxySecurityId (MAX(version) — the Regime.ProxySecurityId precedent), holding
                                                   // the resolved proxy ETF's security_id. The OPERATOR writes it after ingesting the proxy as a real
                                                   // security with its own bars + dividends (OEF.US while Universe.Bootstrap.MembershipPrimary=oef_csv;
                                                   // IVV.US when the D70 widening flips it to ivv_csv — CapWeightProxy.SymbolFor, never hardcoded).
                                                   // The D53 pipeline reads it every run: the proxy joins the Stage-1 fetch set, and it IS the CW
                                                   // account's universe. ABSENT ⇒ the CW account holds cash — a LOGGED readiness gap (rule 10),
                                                   // never a guessed symbol.
  },

  "Sizing": {                                      // D32/D42
    "Mode": "inverse_vol",                        // inverse_vol | equal(dummies) | kelly(P6+)
    "PortfolioVolTargetAnn": 0.12,
    "PositionCapPct": 0.05,
    "Covariance": { "Estimator": "ledoit_wolf", "WindowDays": 252,
                    "Fallback": "ewma_single_index", "EwmaLambda": 0.97 },
    "Kelly": { "FractionCap": 0.25, "MinTradesForB": 30, "ShrinkBToward": 1.0 }
  },

  "Guardrails": {                                  // DESIGN_IMPROVEMENTS §3.4; fail closed
    "MinScore": 0.0,                              // per-strategy override in config_json
    "MaxConcurrentPositions": 60,
    "HeatMaxPredictedVolAnn": 0.15,
    "ReentryCooldownDays": 3,
    "DrawdownCircuitBreakerPct": 25.0
  },

  "Gate": {                                        // D31/D48
    "EvaluationCadenceDays": 21,
    "MinTrackDays": 63,
    "Confidence": 0.95, "Power": 0.80,
    "NwLagCapDays": 21,
    "DetectabilityHorizonYears": 3                  // D89 (v1.9.35): the FR-40 detectability-at-admission gate refuses a candidate whose pre-registered
                                                   // expected_effect_ann could not clear the NW-MDE within this horizon, calibrated against the C-1 detection-power curves
  },

  "Populations": {                                 // D36
    "Size": 200, "CostFreeSize": 50,
    "FamilySeeds": { "daily": 1001, "banded": 1002, "monthly": 1003, "quarterly": 1004 },
    "AuditFullLedgerSample": 5,                   // members with full trade logs
    "TurnoverMatchTolerancePct": 30.0             // v1.9.7 finding 115: cost-match verification — flag a strategy whose realized annualized turnover is outside ±this% of its matched population's median (caveat on the S3 panel + StrategyRow read-model)
  },

  "Allocator": {                                   // D51 — full spec: MASTER §20.2
    "BandPts": 5.0, "CadenceDays": 21,
    "TooEarlyTiltCapPts": 10.0, "SuspectDecayPctPerEval": 25.0,
    "TemperaturePctAlpha": 2.0,                    // softmax temperature (%/yr shrunk alpha)
    "TauMinPctAlpha": 0.5,                         // shrinkage dispersion floor
    "WeightFloorPct": 5.0, "WeightCeilingPct": 60.0
    // v1.9.7 finding 116: floors apply PRE-renormalization and scale down proportionally when Σfloors
    // would exceed 100% (equivalently the promotable roster caps at floor(100/WeightFloorPct) — 20 at
    // the default). MASTER §20.2 carries the normative sentence.
  },

  "Regime": {                                      // D50 — full spec: MASTER §20.1
    "ProxySecurityId": null,                       // resolved at Phase 1 from ProxySource: the cap-weight proxy's security_id
    "ProxySource": "eodhd_gspc",                   // D73/FR-38 (v1.9.7 finding 110): eodhd_gspc (GSPC.INDX EOD, primary) | self_built_capweight (fallback);
                                                   // validated vs SPY.US daily returns — INTEGRATIONS §9. Pinned to the S&P 500 proxy even during the D70
                                                   // S&P 100 slice (regimes are market-level facts). ≈3.8y warm-up backfill required before the first label
    "TrendSmaDays": 200, "TrendHysteresisPct": 1.0, "TrendConfirmDays": 5,
    "VolWindowDays": 21, "VolPercentile": 80, "VolLookbackYears": 3
    // v1.9.18 finding F/X: this section BINDS from Phase 2 (checkpoint 2.8) — the FR-26/D50 label service
    // is the first consumer, so the composition root binds `Regime` into RegimeOptions and passes it to
    // AddAlphaLabMembership (before this, an unbound default silently ignored every value here). The label
    // params map 1:1 to RegimeLabeler; VolLookbackYears is converted to sessions (×252) to match the
    // RegimeProxyReadiness warm-up. ProxySecurityId stays null here — the live value is the versioned config row.
  },

  "Urls": "http://127.0.0.1:5230",              // D71 — the API's listen URL (standard ASP.NET Core key; committed, non-secret; NEVER via the ASPNETCORE_URLS env var — D67 bans env-var reads). Per-arena profiles change only the port (convention: sp500 → 5230, next arena → 5231, …); the Web arena registry's baseUrl for the arena must match this value exactly

  "ConnectionStrings": {
    "AlphaLab": "Data Source=E:/AlphaLabDatabase/{Arena.Id}/alphalab.db"  // D71: arena-namespaced so each arena has its own file and no two arenas collide; absolute-anchored so Worker, Api, and the design-time factory all resolve the SAME file (never a relative path — three CWDs would mean three DBs). THIS deployment uses a literal absolute base (E:/AlphaLabDatabase) — the sanctioned "a literal absolute path is also valid" option below. The portable alternative is the {LocalAppData} token, which the shared AlphaLab.Data DbPathResolver expands via Environment.GetFolderPath(SpecialFolder.LocalApplicationData) — the known-folders API, NOT an environment variable (D67: no env-var reads anywhere). Either form has {Arena.Id} resolved and its directory auto-created. OS-AGNOSTIC SEPARATORS (v1.9.36): write the template with forward slashes; DbPathResolver.ResolvePath rebuilds the DataSource with the RUNNING platform's separator after token substitution, so the same string is valid on Windows (→ E:\AlphaLabDatabase\sp500\alphalab.db) and on Linux — a cloud lift-and-shift is a config-value edit in the four spots, with NO code change. Wired in Phase 0; used by the Worker, the Api, and the EF design-time factory. FOUR-SPOTS RULE: this string must be BYTE-IDENTICAL in four places — this key (Worker appsettings), the Api appsettings, DbPathResolver.DefaultConnectionString, and the Backfill CLI's tools/Backfill/appsettings.json (a separate Phase-1 runnable added in checkpoint 1.10, guarded by ConfigConsistencyTests from v1.9.10 — finding 138) — pick ONE form (this deployment: the E:/AlphaLabDatabase literal) and use it in all four; do NOT set the const to the {LocalAppData} form while appsettings uses the E: literal. ConfigConsistencyTests asserts the equality; to relocate the DB, edit all four together per docs/DB_RELOCATION.md (snapshot-db.ps1 reads this value, so tooling follows automatically)
  },

  "Api": {                                         // D57/D60 — the API's bind (host + port) is carried by the top-level `Urls` key above; the former `Api.Bind` key is RETIRED (v1.9.5 finding 103: once finding 94 moved binding to `Urls`, `Api.Bind` was never read — a dead key in a "never invent a key" system). Localhost-only remains the posture: keep the `Urls` host at 127.0.0.1; changing it is the (future) LAN-exposure switch
    "OpenApi": "native",                           // Microsoft.AspNetCore.OpenApi + Scalar (recommended on .NET 10; Swashbuckle dropped from ASP.NET Core templates in .NET 9). Route /swagger preserved (redirect to Scalar) for the CLAUDE.md/DoD reference. Fallback: "swashbuckle"
    "CorsAllowedOrigins": [ "http://localhost:5210" ]  // dev-only: the AlphaLab.Web WASM origin(s) — the browser-served client is cross-origin to the API even on localhost. Set the real dev URL(s) in Phase 0; the API itself stays localhost-bound
  },

  "Worker": {                                      // D59/D61
    "Mode": "OnDemand",                            // OnDemand (default): launch -> catch up -> exit
                                                   // Scheduled: stay resident, Quartz triggers at close+offset
    "ProcessThroughLastCompletedSessionOnly": true, // never half-run a day whose close hasn't happened (D61)
    "DrainQueuedJobsOnLaunch": true,               // D72 (v1.9.7 finding 111): OnDemand launch = schema → catch-up → drain queued jobs → backup → exit;
                                                   // jobs never run inside the daily write transaction; catch-up always precedes them
    "HeartbeatSeconds": 30,                        // D72 (finding 112): the running Worker writes worker_state.heartbeat_at at least this often
    "StaleRunThresholdSeconds": 300                // D72 (finding 112): on launch, a run_in_progress=1 whose heartbeat is older than this is treated as a
                                                   // crashed run — cleared, its runs row marked 'failed', logged; the Api's 409 decision ignores stale flags
  },

  "Eodhd": {                                       // v1.9.18 finding D — the Worker's OWN EODHD section (the D53 staged pipeline, checkpoint 2.10, fetches the
                                                   // daily delta). The token is Secrets:EodhdApiToken (hard rule 11), read DEFENSIVELY: a missing token is NOT a
                                                   // startup failure (a no-op launch spends nothing); the provider only 401s if a real fetch happens without it
    "BaseUrl": "https://eodhd.com/api",            // no trailing slash; endpoints appended (/eod, /div, /splits)
    "ExchangeSuffix": "US"
  },
  "Costs": "see the Costs section of this reference — the D43 coefficients; the Worker binds them for the pipeline's cost model (checkpoint 2.10)",
  "Sizing": { "Mode": "equal" },                   // finding E — Phase 2 runs equal sizing only; the sizer REFUSES inverse_vol/kelly until FR-11 full (Phase 6)
  "Guardrails": "see the Guardrails section — the three the funnel structurally needs (MinScore, PositionCapPct on Sizing, MaxConcurrentPositions); Phase 7 wires the rest",
  "Data": "see the Data section — DataQualityOptions (OutlierZ); the D77 gate binds it in the Worker (finding F)",
  "CorporateActions": "see the CorporateActions section (findings B/C) — the delist haircut + spin-off liquidation rule the CA ledger binds",

  "Calendar": {                                    // D54
    "Exchange": "NYSE",
    "RunAfterCloseOffsetMinutes": 150              // trigger = session close (ET) + offset; used only in Scheduled mode
  },

  "Monitor": "see OVERFITTING_MONITOR_v1.9 Appendix A. The Phase-4 calibration (D98, v1.9.39) freezes the calibrated values as VERSIONED CONFIG ROWS (never appsettings keys): Monitor.S3.PNoiseCurve.{family} + Monitor.S3.PEdgeCurve.{family} (piecewise-linear S3Curve JSON — knots on the eval-cadence grid, 25-75% band, sustain_evals, false-alarm rate, the C-2 sampling band, the D64 vintage), Monitor.S6.AutoRetireEvals (the finding-113 patience knob; recalibrate THIS on a survival-floor failure, never the plant), Calibration.DetectionPower (the C-1 curves - the FR-40 gate's empirical floor), and Calibration.ReportRef ({path, sha256} of the archived report). Read AS-OF the run watermark (D96); the flat Appendix-A anchors are the pre-calibration fallback; every change = a new version row",

  "Replay": {                                      // D37
    "ValidationYears": 15,
    "PrunePerMemberLedgersAfterSignoff": true,
    "EdgePlantSurvivalFloor5y": 0.90,              // v1.9.7 finding 113: Phase-4 DoD floor — fraction of D64 edge plants still promotable at 5y of simulated
                                                   // track; a floor failure recalibrates S6's patience, never the plant (the lab must not kill its own honest winners)
    "JointFalseAlarmMaxFrac": 0.10                 // v1.9.7 finding 114: bound on the fraction of no-edge plants ever reaching Suspect via ANY signal over the
                                                   // replay window; per-signal contribution is a permanent calibration-report section
  },

  "Calibration": {                                 // D64 — plants under P_noise(t)/P_edge(t)
    "Plant": {
      "AlphaAnnualPct": 2.0,                       // edge plant target (§1.1 realistic prize)
      "AntiAlphaAnnualPct": -2.0,                  // anti-predictive plant (the Suspect fixture)
      "ActiveDayFrac": 0.25,                       // lumpy delivery — edge arrives in streaks
      "PersistencePhi": 0.9,                       // run persistence, scaled to family horizon (mean active run = max(1/(1−φ), horizon), v1.9.39)
      "RegimeMultipliers": { "bull": 1.25, "bear": 0.5 },  // renormalized to the target (v1.9.39: by the RUNNING realized mix ≤ t — PIT-clean)
      "SeedsPerPlant": 50,                         // curves = multi-seed medians + 25–75% bands
      "SensitivityMaxGapPts": 10                   // naive-vs-realistic P_edge divergence trigger
    },
    "ReportDir": "docs/calibration"                // v1.9.39 (D98): `replay-calibrate` archives {Arena.Id}/{date}-calibration.md here; Calibration.ReportRef (a config ROW) cross-references it by path + sha256
  },

  "Verdicts": {                                    // D63 — separation state (read-models)
    "SeparationMinTrackDays": 252,                 // chip renders past this track length
    "SeparationBandCentralFrac": 0.50              // 'none' = inside the 25th–75th pct region
  },

  "Kpi": {                                         // D88 - cohort maturation curve (read-model; descriptive only, never a gate/monitor/allocator input)
    "CohortBucketMonths": 6,                       // admission-vintage bucket width over strategies.created_on (default half-year cohorts)
    "CohortMinStrategies": 3                       // below this live-member count at t the segment renders dimmed (reason 'thin_cohort')
  },

  "SignalLibrary": {                               // D91 - Phase 4.5 signal grading (descriptive only; read by the FR-44 IC engine + FR-46 read-model)
    "HorizonsDays": [ 21, 63 ],                    // pre-registered grade horizons k, in trading days; adding 126 is open (PROGRESS P15)
    "RollingWindowsYears": [ 1, 5 ],               // rolling mean rank-IC windows for the trend inference (Newey-West lag = horizon)
    "TrendDecayZ": null,                           // decaying = the 1y trend significantly negative at this z; value pinned at build checkpoint 4.5.2, before the first grade row is written
    "TrendGoneZ": null                             // gone = the 1y mean not significantly above zero at this z (stable = otherwise); value pinned at build checkpoint 4.5.2
  },

  "Llm": {                                         // D24/D46
    "Tasks": {
      "news_extraction": { "Model": "claude-haiku-4-5-20251001" },
      "regime_brief":    { "Model": "claude-sonnet-4-6" },
      "research_brief":  { "Model": "claude-sonnet-4-6" },
      "skeptic":         { "Model": "claude-sonnet-4-6" }
    },
    "UseBatchesApiForScheduled": true,
    "PromptCacheStaticBlock": true,
    "NewsBudget": { "MaxArticlesPerRead": 25, "MaxCharsPerArticle": 2000,
                    "DedupeBy": "title_hash" },
    "DailyBudget": { "MaxCostUsd": 1.00, "MaxCalls": 10 },
    "DegradationOrder": ["held_positions", "cached", "neutral_fallback"],
    "ScopeLevel": 1                               // 1 market-read; 2 shortlist(<=20); 3 unreachable
  },

  "Ops": {                                         // RUNBOOK. Phase 2 (checkpoint 2.12) binds ONLY BackupRetentionDays (OpsOptions); the other two keys are dormant (see comments)
    "BackupNightlyLocal": "02:00",                // Scheduled mode only; in OnDemand mode (the default) the backup runs as the final step of each Worker launch (RUNBOOK §3)
    "BackupRetentionDays": 30,                    // D72/2.12: local copies under <DbBase>\{arena}\backups\alphalab-{date}.db older than this are pruned (by the date IN the filename); one copy per day, skipped if today's exists
    "AlertSink": "log+gui"                        // extend: email/webhook later (Phase 7 alerting)
  },

  "FactorData": { "RefreshDayOfMonth": 5 }         // D41 French library pull
}

  "Ai": {                                          // D79-D82 (v1.9.21) — the AI seats; budgets are per-seat hard caps (D24)
    "PackRecipeVersion": "cp-1.0",                 // context-pack recipe id; a frozen param (D80) — a change forks candidates
    "Contestant": {                                // the LLM decision layer as a first-class IModel (D81)
      "Model": "llm-a",                            // vendor-neutral model id (Anthropic, local LM Studio, etc.) — set per frozen param, never hardcoded in code
      "ShortlistSize": 25,                         // the deterministic local pre-filter hands the LLM at most this many names (Level-3 whole-universe scoring stays unreachable, D24)
      "DailyBudgetUsd": 0.05                        // on exhaustion the contestant ABSTAINS (empty score map — the funnel's honest "nothing scored"), never a padded/stale decision
    },
    "Researcher": {                                // the generative seat (D82); runs weekly/on-demand
      "Model": "llm-b",                            // may differ from the contestant's; a frozen param
      "MonthlyBudgetUsd": 5.0                       // on exhaustion the researcher job simply queues
    }
  },                                               // NOTE: per-strategy frozen params (prompt hash, model id, shortlist size, memory option + rule R, the no-LLM twin's scoring rule — D85) live in strategies.config_json, NOT here (key rule 1). The twin's scoring rule is a FIXED FORM (equal-weight z-score blend of the pack features), not a tunable key; its feature set follows Ai.PackRecipeVersion, so a recipe change forks like any frozen-policy change.

  "Research": {                                    // D82 — the trials budget that rations self-improvement's deflated-Sharpe spend (S2)
    "ForkBudgetPerYear": 6,                        // fork cadence; surfaced beside the trials count in the research UI
    "MaxConcurrentCandidates": 3                   // matches the "1 Live + 2-3 Candidates" roster shape (§8)
  },

```

## tools/Backfill/appsettings.json (the one-time bootstrap CLI — D65/D70)

The backfill CLI is a **separate runnable** with its own `appsettings.json`; it does **not** read the Worker/Api superset above. Besides the shared `Arena` + `ConnectionStrings` keys (the connection string is byte-identical across all four spots — the four-spot rule), it carries a `Backfill` section the other processes don't, plus an `Eodhd` section — which, as of v1.9.18 (finding D, checkpoint 2.10), the **Worker also carries** (the forward pipeline fetches the daily delta), so `Eodhd` is no longer backfill-only:

```jsonc
{
  "Eodhd": {                                       // the EODHD market-data feed (INTEGRATIONS §1)
    "BaseUrl": "https://eodhd.com/api",            // no trailing slash; endpoints appended (/eod, /div, /splits)
    "ExchangeSuffix": "US"                          // ticker suffix, e.g. AAPL.US
  },
  "Backfill": {
    "BackfillYears": 20,                           // history depth for the bootstrap — the LIVE key (NOT Data:BackfillYears)
    "ApiPlanLimit": 100000,                        // EODHD 100k/day cap; the headroom check (INTEGRATIONS §1, VERIFIED 2026-07-15)
    "RawCacheRoot": "tools/raw-cache",             // dated raw-payload archive root; 30-day retention (v1.9.10 finding 144)
    "WikipediaSp100Url": "https://en.wikipedia.org/wiki/S%26P_100",                                    // S&P 100 cross-check (INTEGRATIONS §7)
    "HistoricalMembershipUrl": "tests/Fixtures/S_P_500_Historical_Components___Changes__Updated.csv",  // D49/D70 community CSV (a fixture path at launch; the fja05680 URL or a downloaded copy for the live D70 run) — consumed by `--historical sp500` (v1.9.39); an explicit --csv overrides
    "CoverageArtifactDir": "docs/calibration"      // v1.9.39 (D97): the historical backfill's durable coverage artifact lands at {dir}/{Arena.Id}/historical-coverage-{from}-{to}.json — deterministic content (a re-run is a clean git diff); committed with the calibration evidence
  }
}
```

The slice is selected by the CLI's **`--universe`** argument (default `sp100`), not by a config key; `--universe sp500` is **rejected at parse** until the D70 widening *mechanism* is wired (findings 149/151 — still an open proposal, see PROGRESS). The widening **target** is the S&P 1500 by **D87** (contingent on a verified-depth 400/600 historical-membership source; else S&P 500); the sp1500 `--universe` arm and its count-sanity band (~[1490,1520], **pending D87 verification**) are Phase-4 prerequisites, not defined yet. `Eodhd:BaseUrl` is also stated in INTEGRATIONS §1.

## AlphaLab.Web `wwwroot/appsettings.json` (non-secret, browser-served — D71/FR-37)

The standalone-WASM client's only configuration is the **arena registry**. It replaces any single
`Api:BaseUrl` key; the active arena (default: the first entry) drives which Api the
`ReadModelClient` targets. Adding an arena later = one new entry (ARENA_ARCHITECTURE_v1.9.3 §4.1).

```jsonc
{
  "Arenas": [
    { "id": "sp500", "displayName": "S&P 500", "baseUrl": "http://127.0.0.1:5230" }
    // future: { "id": "russell2000", "displayName": "Russell 2000", "baseUrl": "http://127.0.0.1:5231" }
  ]
}
```

Each entry's `baseUrl` must equal that arena's Api `Urls` value above. This file is served to the
browser — it must never contain a secret (D67).

## Key rules
1. Per-strategy parameters (lookbacks, N, exit params, seeds) live in `strategies.config_json` — **frozen** (D17); this file holds only system-level knobs.
2. Monitor thresholds start from Appendix A values but are **replay-calibrated in Phase 4** before forward trust; the calibration report references the exact config version it produced.
3. Any config change at runtime inserts a versioned `config` row with a reason — the system's knobs have an audit trail like everything else.
4. **v1.8:** the Phase-4 calibration job writes the D56 `P_noise(t)` / `P_edge(t)` S3 curves as versioned config rows referencing the archived report; the flat S3 anchors (Healthy ≥ 95 / Suspect < 25 — MONITOR Appendix A) apply only before that.
5. **v1.9.1 (D69):** ledger money properties are C# `decimal` persisted as TEXT; never bind a money value to `double`. The D60 API serialization (strings/minor units) is unchanged.
6. Where a key also appears in OVERFITTING_MONITOR Appendix A (gate/verdicts/calibration blocks), **this file is authoritative**; the appendix mirrors values for reading convenience.
7. **v1.9.12 binding caveat (finding 159):** some blocks documented here are the *designed* surface for a later phase and are **not yet section-bound**. `CalendarOptions`, `RegimeOptions`, and `DataQualityOptions` declare a `SectionName` (`Calendar`/`Regime`/`Data`) but are currently DI-registered as **default instances** (not `GetSection(...).Bind`), so a value placed in those sections is silently ignored until the phase that consumes them wires the bind. No value drift today — the defaults equal the documented defaults — but treat these as compile-time constants, not live knobs, at the current phase. (The `Universe.Bootstrap` binding gap is tracked separately under **D76** — finding 151.) The `Kpi` block (D88, v1.9.34) is likewise a designed surface: no options class exists until the Phase-3 read-model build wires it.
