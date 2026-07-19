# SETUP_v1.9 — prerequisites & day-zero checklist

*Everything to do before pasting the Phase 0 prompt. Records decision **D49 (budget-tier launch configuration)**. Companion: README_v1.9 (workflow), INTEGRATIONS_v1.9 (endpoints), CONFIG_REFERENCE_v1.9 (keys).*

## 1. D49 — budget-tier launch configuration (the plan decision)

**Decision:** launch on **EODHD "EOD Historical Data — All World" (~$19.99/mo)** — bars (raw+adjusted), splits, dividends, exchange symbol lists (incl. delisted), and the News API (5 calls/request/ticker). The fundamentals API (index constituents, sectors, company financials) is **not** on this tier, so:

| Feed | Launch source (D49) | Reverts to EODHD-primary on upgrade |
|---|---|---|
| Index membership | **iShares IVV holdings CSV = primary**, Wikipedia scrape = cross-check (dual free sources — still satisfies the D35 named+validated standard); fail-closed on divergence unchanged | Yes (constituents endpoint slot is dormant, not deleted) |
| Historical membership (replay/catch-up as-of) | Free community CSV (github.com/fja05680/sp500), caveat logged in the Phase-4 calibration report | Yes (S&P 500 historical snapshots) |
| Sector / industry | **Sector column of the same IVV holdings CSV** (GICS-based, refreshed with the daily membership pull) | Optional |
| News | EODHD News API (on-plan) | — |
| S&P 100 slice (forward universe through Phase 4 — D65/D70) | **iShares OEF holdings CSV primary + Wikipedia S&P 100 cross-check** (count sanity 99–103); replay never uses the slice | n/a — retires when the universe widens after Phase 4 sign-off (target amended to the S&P 1500 by D87, contingent; else S&P 500) |
| Fundamentals | **Phase 8 entry condition becomes: upgrade to a fundamentals-bearing tier for one trial month → run the §7.0 PIT protocol → keep paying only on a pass** | n/a |

Everything runs Phases 0–7 completely on this tier; Phase 8 is the only blocked phase and was already contingent. Consequences are patched into INTEGRATIONS_v1.9 §1–2 and MASTER §2 (D49).

## 2. Machine & OS
Windows (your setup) is fine. ~10GB free disk (DB <2GB for years + backups + 30-day raw-payload cache). Prefer a machine that's on evenings — the daily job runs after close; catch-up (D47) covers outages but an always-on machine keeps the forward record gapless.

## 3. Toolchain
1. **.NET 10 SDK** — `dotnet --version` shows 10.x.
2. **Git + private GitHub repo** — created before any code; commits are phase-gate markers.
3. **Claude Code** — Plan Mode workflow per README_v1.9 §3.
4. **PowerShell 7** — tools scripts are Windows-first.
5. **DB Browser for SQLite** (optional, recommended) — eyeballing tables in Phases 1–3.

## 4. Accounts
- **EODHD** — register; use the **free tier (20 calls/day + 500-call welcome bonus)** for the §7 endpoint verification; subscribe to All World when starting the real backfill.
- **Anthropic Console API key** — separate from the Claude Code subscription; needed only at Phase 5; $5–10 credit covers months under the D46 budget.
- **Alpaca** (optional, free) — bar cross-check provider; deferrable.
- No accounts needed: IVV CSV, Ken French library, FRED, Wikipedia.

## 5. Secrets — one gitignored `appsettings.Secrets.json` (D67; no env vars)

This is a local-only, single-machine tool, so secrets live in **one JSON file that sits next to `appsettings.json` and is gitignored** — no environment variables, no .NET User Secrets store. Create one copy in each runnable project's content root — **AlphaLab.Worker** and **AlphaLab.Api**, the only two processes that load secrets (keep the two files identical; the Worker's is the one the daily job reads):

```jsonc
// appsettings.Secrets.json  — add to .gitignore FIRST, then create it
{
  "Secrets": {
    "EodhdApiToken":   "YOUR_TOKEN",
    "AnthropicApiKey": "YOUR_KEY",     // Phase 5
    "AlpacaKeyId":     "...",          // optional
    "AlpacaSecretKey":  "..."
  }
}
```

Phase 0 wires the config builder as `AddJsonFile("appsettings.json", optional:false).AddJsonFile("appsettings.Secrets.json", optional:true)` in **AlphaLab.Api and AlphaLab.Worker** — nothing else. The standalone-WASM **AlphaLab.Web** client never loads secrets (its `wwwroot/appsettings.json` is served to the browser and holds only the non-secret `Arenas` registry — one `sp500` entry at launch; D71, CONFIG_REFERENCE). **Never put keys in the committed `appsettings.json`** (CI grep fails the build on committed key patterns). Ensure `.gitignore` lists `appsettings.Secrets.json` before you write real keys into it. Because it's a plain file the daily job reads directly, there's no per-Windows-user or scheduler-account constraint.

## 6. Repo bootstrap (first 30 minutes)
1. Clone the repo; `CLAUDE.md` + `PROGRESS.md` → root; all other docs → `docs/`.
2. `.gitignore`: VS template + `appsettings.Secrets.json`, `tools/raw-cache/`, `*.db` (with `-wal`/`-shm` sidecars — snapshots and backups themselves live under the DB base **outside the repo**, `<DbBase>\{Arena.Id}\…` per RUNBOOK §3 / DB_RELOCATION.md; the `*.db` ignore only catches stray copies dropped into the working tree).
3. Commit "docs v1.9, pre-code baseline".
4. Claude Code, Plan Mode on, paste the Phase 0 prompt (BUILD_AND_PROMPTS §4).

**How you run it (D61).** The lab updates on demand: run `AlphaLab.Worker` each evening after the US close and it catches up whatever it missed, then exits — your machine need not be always on. A desktop shortcut to that command is the whole daily ritual. (An always-on host can instead set `Worker.Mode=Scheduled`.)

## 7. Day-zero verification (~1 hour, free tier) — each confirmation = edit INTEGRATIONS_v1.9 + commit
- [ ] `GET /api/eod/AAPL.US` — confirm raw OHLCV + `adjusted_close` shape
- [ ] `GET /api/splits/AAPL.US`, `/api/div/AAPL.US` — event shapes
- [ ] `GET /api/exchange-symbol-list/US?delisted=1` — delisted list for the security master
- [ ] `GET /api/news?s=AAPL.US&limit=3` — confirm on-plan + payload shape (5-call cost noted)
- [ ] IVV product page → "Download holdings" → pin the real CSV URL; confirm ticker + **sector** columns parse
- [ ] OEF product page → "Download holdings" → pin the real CSV URL (D70 S&P 100 slice); confirm the ticker column parses
- [ ] Set `Arena.Id = "sp500"` (D71) — the DB path, snapshots, and backups namespace under it. You only run one arena now; a second is a future config + instance operation (see ARENA_ARCHITECTURE_v1.9.3 §6), never a rewrite
- [ ] Download both Ken French daily CSV zips manually; note header/footer junk lines for the parser
- [ ] Clone/download the fja05680/sp500 historical membership CSV; spot-check 3 known index changes
- [ ] Generate the NYSE trading calendar (±30y) from the D54 rules script; spot-check 2 recent holidays + 1 half-day against exchange notices
- [ ] (Phase 5, later) Anthropic Messages smoke test + Batches endpoint headers vs docs.claude.com

## 8. Costs & expectations
EODHD $19.99/mo from backfill day (free before); Anthropic a few $/mo from Phase 5; all else free. Phases 0–3.5 ≈ 2–3 months of evenings before the arena proves itself on dummies — PROGRESS.md's gates make that feel like progress, because it is.

> Research/paper-trading only. Not investment advice.
