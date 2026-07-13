# INTEGRATIONS_v1.9 — external endpoints reference

> **D49 launch configuration (SETUP_v1.9 §1):** on the All World tier, §1's constituents/sector/fundamentals rows are **dormant** — membership runs IVV-CSV-primary + Wikipedia-cross-check, sectors come from the IVV CSV's GICS column, and replay's as-of membership seeds from the fja05680/sp500 community CSV (§8). The rows remain specified here so the post-upgrade flip is a config change (`Universe.MembershipPrimary` etc.), not new integration work. **D70:** the S&P 100 launch slice is itself a named feed — see §2b — and replay never uses the slice (S&P 500 as-of membership only).

*Single source of truth for every external call. Claude Code: implement providers against THIS file, not memory — LLM training data about third-party APIs goes stale. Items marked ⚠VERIFY must be confirmed against the provider's live docs during Phase 1/5 setup and this file updated in the same PR (URL shapes and plan limits drift).*

## 1. EODHD (primary — D35) — base `https://eodhd.com/api`, auth `?api_token={Secrets:EodhdApiToken}&fmt=json` (value from the gitignored `appsettings.Secrets.json`, D67)

| Feed | Endpoint | Notes |
|---|---|---|
| Daily bars (raw) | `GET /eod/{SYMBOL}.US?from=&to=&period=d` | Backfill + delta. Adjusted close included as `adjusted_close`; store raw OHLCV + adjusted series per SCHEMA `bars`. ⚠VERIFY whether full adjusted OHLC needs the `filter`/splitadjusted variant on your plan |
| Bulk last day | `GET /eod-bulk-last-day/US?date=` | Efficient daily delta for the whole exchange; filter to universe locally |
| Splits | `GET /splits/{SYMBOL}.US?from=` | → `corporate_actions(type='split')` |
| Dividends | `GET /div/{SYMBOL}.US?from=` | → `corporate_actions(type='dividend')`; ex-date semantics (D30) |
| Index constituents (current + historical) | `GET /fundamentals/GSPC.INDX` | JSON contains `Components` (current) and `HistoricalTickerComponents` (adds/drops with dates) — feeds `index_membership` + as-of reconstruction (D47/D37). ⚠VERIFY field names on your plan |
| Sector/industry | `GET /fundamentals/{SYMBOL}.US?filter=General` | `Sector`, `Industry` → `securities` + `sector_changes` |
| Symbol changes / delistings | `GET /exchange-symbol-list/US?delisted=1` + fundamentals `General::IsDelisted` | Feed the security master (D39). ⚠VERIFY the dedicated symbol-change endpoint availability on your plan |
| News | `GET /news?s={SYMBOL}.US&from=&to=&limit=` | Input to the D46 budget pipeline only |
| Fundamentals (Phase 8 candidate) | `GET /fundamentals/{SYMBOL}.US` | Quarterly `Financials::*`; run the §7.0 PIT protocol against `filing_date`/report-date fields before ANY strategy use. ⚠VERIFY as-reported vs restated behavior — this is the gate |

**Plan & limits:** All-World / Fundamentals-inclusive tier (~$20–30/mo). ⚠VERIFY current call limits and that constituents + fundamentals are on the purchased tier (plan boundaries drift). Rate-limit posture: providers implement retry-with-backoff (3 attempts, jitter), and the daily job's call volume must fit the plan's per-day limit with ≥50% headroom (logged to `api_usage_log` — SCHEMA_v1.9).

## 2. iShares IVV holdings CSV (membership cross-check — D35)
- `GET https://www.blackrock.com/us/individual/products/239726/ishares-core-sp-500-etf/1467271812596.ajax?fileType=csv&fileName=IVV_holdings&dataType=fund` ⚠VERIFY — BlackRock's ajax URL pattern changes occasionally; locate via the IVV product page "Download holdings" link at setup and pin here.
- Free, no auth, official, ~1-day lag. Parse tickers; map through `ticker_history`; equity holdings only (drop cash/futures rows). Divergence vs EODHD ⇒ fail closed per FR-4.

## 2b. iShares OEF holdings CSV (the D70 S&P 100 slice — forward universe through Phase 4)
- Same BlackRock ajax pattern as §2, for the **iShares S&P 100 ETF (OEF)**. ⚠VERIFY — locate the real CSV URL via the OEF product page's "Download holdings" link at setup and pin it here (the ajax path and product id must be taken from the live page, never from memory).
- Free, no auth, official, ~1-day lag. Parse tickers; map through `ticker_history`; equity holdings only. Cross-check: the Wikipedia S&P 100 constituents table (§7). Divergence ⇒ fail closed; count sanity 99–103 (`Universe.Bootstrap.CountSanity`).
- Retires when the universe widens to the S&P 500 after Phase 4 sign-off (D70) — the provider stays behind the same `IIndexMembershipProvider` seam.

## 3. Ken French Data Library (factors + RF — D41)
- Landing: `https://mba.tuck.dartmouth.edu/pages/faculty/ken.french/data_library.html`
- Files (CSV zips, daily): `F-F_Research_Data_5_Factors_2x3_daily_CSV.zip` (Mkt−RF, SMB, HML, RMW, CMA, RF) and `F-F_Momentum_Factor_daily_CSV.zip` (UMD). ⚠VERIFY exact file paths at setup (they're stable but hand-maintained).
- Monthly refresh job (config `FactorData.RefreshDayOfMonth`): download → checksum → parse (note the library's header/footer junk lines and −99.99 missing codes) → date-continuity check → `factor_returns` + `factor_refresh_log`. Values are in percent — divide by 100.

## 4. FRED (RF fallback — D41)
- `GET https://fred.stlouisfed.org/graph/fredgraph.csv?id=DGS3MO` — no key needed for CSV. Only used if the French RF series is unavailable.

## 5. Anthropic (D46)
- **Scheduled reads:** Message Batches API — `POST https://api.anthropic.com/v1/messages/batches` with the day's requests; poll for results. Half price vs synchronous. ⚠VERIFY current batch API version headers against docs.claude.com at Phase 5.
- **Interactive research assistant:** `POST /v1/messages`.
- **Prompt caching:** mark the static instruction block with `cache_control` so only the day's news is fresh tokens.
- Models per task from `Llm.Tasks` config (CONFIG_REFERENCE). Auth: `x-api-key: {Secrets:AnthropicApiKey}` (from `appsettings.Secrets.json`, D67) + `anthropic-version` header.
- Hard budget enforcement wraps the client (D24): pre-flight cost estimate → refuse + degrade if over; log to `llm_budget_log`.

## 6. Alpaca (bar cross-check fallback — D19/D35)
- `GET https://data.alpaca.markets/v2/stocks/{symbol}/bars?timeframe=1Day&start=&end=` with `APCA-API-KEY-ID`/`APCA-API-SECRET-KEY` headers (free tier: IEX feed). Used only by the rotating-sample quality gate (FR-6); optional in dev.

## 7. Wikipedia (membership cross-check / fallback)
- `https://en.wikipedia.org/wiki/List_of_S%26P_500_companies` — parse the constituents table. **D49 launch role: the daily cross-check against the IVV primary**; post-upgrade it demotes to fallback (activated only if both EODHD and IVV are unavailable; log the degraded-source flag on `index_membership_log`).
- `https://en.wikipedia.org/wiki/S%26P_100` — parse the components table; the cross-check for the D70 S&P 100 slice (`Universe.Bootstrap.MembershipCrossCheck`).

## 8. fja05680/sp500 community CSV (historical membership at launch — D49/D70)
- `https://github.com/fja05680/sp500` — community-maintained historical S&P 500 constituents (per-date membership snapshots). ⚠VERIFY the current file name/format in the repo at setup and pin the raw-file URL here.
- One-time ingestion (Phase 1, FR-4) into historical membership for as-of reconstruction; spot-check ≥3 known index changes (SETUP §7). Community-sourced ⇒ the survivorship/accuracy caveat is logged and stamped into the Phase-4 calibration report (D64 vintage stamp). Post-upgrade this slot reverts to EODHD historical snapshots.

## 9. Regime proxy index series (the market-level proxy for D50 labels — D73/FR-38, v1.9.7 finding 110)

The PIT regime label (D50, MASTER §20.1) is computed on a **cap-weight market proxy**. It is a named,
validated, fallback-bearing feed like every other (Golden Rule 25) — it was the one data dependency
the earlier named-feed passes left unresolved (`Regime.ProxySecurityId: null // set at Phase 1` named
no source). It sits on the **calibration critical path**: the D64 edge plant modulates its drift by
the regime label, so a missing or degenerate proxy silently mis-calibrates the D56 curves the whole
monitor trusts.

| Feed | Primary | Validation | Fallback |
|------|---------|-----------|----------|
| Cap-weight regime proxy (daily raw + adjusted close) | **EODHD `GSPC.INDX` EOD** — `GET /eod/GSPC.INDX?from=&to=&period=d` (the membership index symbol, reused). ⚠VERIFY that index EOD (not just `/fundamentals/GSPC.INDX`) is served on the launch tier | Rotating-sample cross-check vs `SPY.US` **daily returns** (tolerance alarm; SPY's daily tracking error is negligible for a trend/vol label) | **Self-built cap-weight index** over the backfilled universe bars with as-of membership (the machinery D68 already builds for the EW benchmark, cap-weighted), stored as an index series with a stable `security_id` so `regime_labels.inputs_hash` keys a real row |

- **Backfill prerequisite (Phase 1 DoD, FR-38):** the vol component needs the proxy's trailing **3-year** daily distribution and a **200-day SMA** before the first label — so ≈ **3.8 years** of proxy history must exist before Phase 2's first Stage-2 regime write, and the **full replay window** (≥15y) before Phase 4. Backfill the proxy in the same Phase-1 pass as the universe bars. Label computation **fails closed** (refuses + logs) until the warm-up exists — never a fabricated label (`FX-RegimeProxyBackfill`).
- **Proxy stability across the S&P 100 → S&P 500 widen (D70):** regimes are market-level facts. Pin the **S&P 500 proxy** even during the S&P 100 forward slice — switching proxies at the Phase-4 widen would fabricate a label discontinuity. `Regime.ProxySecurityId` resolves from `Regime.ProxySource` at Phase 1 (CONFIG_REFERENCE).

## Provider implementation rules (all integrations)
1. Every provider behind its interface; HTTP via a shared resilient client (timeout 30s, 3 retries with exponential backoff + jitter, circuit-break after 5 consecutive failures ⇒ the daily run fails cleanly and catch-up recovers tomorrow — never partial writes).
2. Raw payloads for the day are archived to `tools/raw-cache/{source}/{date}/` (gitignored) for 30 days — every ingestion is re-auditable.
3. All ingestion writes stamp `source` and `observed_at`; nothing external is trusted without the FR-6 quality gate.
4. ⚠VERIFY items are a Phase-1/5 checklist: confirm, correct this file, commit — before building on them.
