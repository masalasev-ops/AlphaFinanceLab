# INTEGRATIONS_v1.9 — external endpoints reference

> **D49 launch configuration (SETUP_v1.9 §1):** on the All World tier, §1's constituents/sector/fundamentals rows are **dormant** — membership runs IVV-CSV-primary + Wikipedia-cross-check, sectors come from the IVV CSV's GICS column, and replay's as-of membership seeds from the fja05680/sp500 community CSV (§8). The rows remain specified here so the post-upgrade flip is a config change (`Universe.MembershipPrimary` etc.), not new integration work. **D70:** the S&P 100 launch slice is itself a named feed — see §2b — and replay never uses the slice (S&P 500 as-of membership only).

*Single source of truth for every external call. Claude Code: implement providers against THIS file, not memory — LLM training data about third-party APIs goes stale. Items marked ⚠VERIFY must be confirmed against the provider's live docs during Phase 1/5 setup and this file updated in the same PR (URL shapes and plan limits drift).*

## 1. EODHD (primary — D35) — base `https://eodhd.com/api`, auth `?api_token={Secrets:EodhdApiToken}&fmt=json` (value from the gitignored `appsettings.Secrets.json`, D67)

| Feed | Endpoint | Notes |
|---|---|---|
| Daily bars (raw) | `GET /eod/{SYMBOL}.US?from=&to=&period=d` | Backfill + delta. VERIFIED 2026-07-13 (`AAPL.US`, 200): each bar is `{date, open, high, low, close, adjusted_close, volume}`. **O/H/L are RAW — only `close` has an adjusted counterpart (`adjusted_close`, split+dividend adjusted); there is NO adjusted OHLC.** This matches SCHEMA `bars` exactly: store raw OHLCV and derive the per-day adjustment factor as `adjusted_close / close` — the `filter`/split-adjusted variant is NOT needed on this plan (resolved). |
| Bulk last day | `GET /eod-bulk-last-day/US?date=` | Efficient daily delta for the whole exchange; filter to universe locally |
| Splits | `GET /splits/{SYMBOL}.US?from=` | → `corporate_actions(type='split')`. VERIFIED 2026-07-13 (`AAPL.US`, 200): `array` of `{date, split}` where `split` is a **string ratio** (`"4.000000/1.000000"`), NOT a number — parse on `/`, never `Convert.ToDecimal` the whole field. |
| Dividends | `GET /div/{SYMBOL}.US?from=` | → `corporate_actions(type='dividend')`; ex-date semantics (D30). VERIFIED 2026-07-13 (`AAPL.US`, 200): `array` of `{date, declarationDate, recordDate, paymentDate, period, value, unadjustedValue, currency}`. Ex-date = `date`; both adjusted `value` and `unadjustedValue` supplied. |
| Index constituents (current + historical) | `GET /fundamentals/GSPC.INDX` | **DORMANT per D49 (Fundamentals OFF on the launch tier — this endpoint is not reachable on the current key).** Membership runs IVV-CSV-primary + Wikipedia cross-check (§2/§2b/§7) and historical membership seeds from the fja05680 CSV (§8). This row reactivates only on a Phase-8 fundamentals upgrade. |
| Sector/industry | `GET /fundamentals/{SYMBOL}.US?filter=General` | `Sector`, `Industry` → `securities` + `sector_changes` |
| Symbol changes / delistings | `GET /exchange-symbol-list/US?delisted=1` + fundamentals `General::IsDelisted` | Feed the security master (D39). VERIFIED 2026-07-13 (200): returns the **full** US roster incl. delisted (~58,577 rows) as `{Code, Name, Country, Exchange, Currency, Type, Isin}`. **Caveat: this payload has NO per-row delisting date and NO `IsDelisted` flag** — it is a flat roster. To identify *which* names are delisted, diff against `delisted=0`; the delisting *date* is not here (it lives in `General::IsDelisted`/fundamentals — OFF-PLAN per D49, so leave delisting-date dormant). Sufficient to *resolve* the §8 bankruptcy `*Q` tickers, not to date them. |
| News | `GET /news?s={SYMBOL}.US&from=&to=&limit=` | Input to the D46 budget pipeline only. VERIFIED 2026-07-13 (`AAPL.US`, `limit=3`, 200): `array` of `{date, title, content, link, symbols, tags, sentiment}` — `sentiment` is returned **inline** per article (no separate call). |
| Fundamentals (Phase 8 candidate) | `GET /fundamentals/{SYMBOL}.US` | **DORMANT per D49 (Fundamentals OFF — not reachable on the current key).** Quarterly `Financials::*`; run the §7.0 PIT protocol against `filing_date`/report-date fields before ANY strategy use. ⚠VERIFY as-reported vs restated behavior on a Phase-8 fundamentals upgrade — this is the gate. |

**Plan & limits:** All-World tier — **Fundamentals OFF** (D49 budget config; the `/fundamentals/*` rows above are dormant). ⚠VERIFY current call limits at first backfill (the daily job must fit the plan's per-day limit with ≥50% headroom, logged to `api_usage_log` — SCHEMA_v1.9). Rate-limit posture: providers implement retry-with-backoff (3 attempts, jitter).

## 2. iShares IVV holdings CSV (membership cross-check — D35)
- `GET https://www.blackrock.com/varnish-api/blk-one01-product-data/product-data/api/v1/get-fund-document?appType=PRODUCT_PAGE&appSubType=ISHARES&targetSite=us-ishares&locale=en_US&portfolioId=239726&userType=individual&component=holdings`
  VERIFIED 2026-07-13 (returns the real CSV; count 504 in-band). **`component=holdings` is what makes this endpoint serve the CSV — the older `.ajax?fileType=csv` pattern and the `component=fundDownload` variant both returned an HTML page or an XLS/XML workbook, not CSV (see the two traps below).** `portfolioId=239726` = IVV.
- **DROP `asOfDate` for the daily fetch.** BlackRock returns the *latest* holdings when `asOfDate` is omitted; a pinned `asOfDate=YYYYMMDD` freezes the download to one stale day, so the daily job would re-ingest the same file forever. The provider fetches this URL WITHOUT `asOfDate`.
- Free, no auth, official, ~1-day lag. Parse tickers; map through `ticker_history`; **equity holdings only** — drop cash/derivative/`-` rows (the trailing `Asset Class != 'Equity'` and placeholder `"-"` rows). Divergence vs the Wikipedia cross-check ⇒ fail closed per FR-4; count sanity 495–510 (`Universe.Bootstrap.CountSanity`).
- **File shape (snapshot for the C-4 header fixture — FX-CsvHeaderShape):** 8 preamble lines (`iShares Core S&P 500 ETF` / `Fund Holdings as of,"<date>"` / `Inception Date` / `Shares Outstanding` / `Stock`/`Bond`/`Cash`/`Other`), one blank line, then the header row, then data. **Do NOT assume a fixed skip-count — scan for the header line**, and assert it equals verbatim:
  `Ticker,Name,Sector,Asset Class,Market Value,Weight (%),Notional Value,Quantity,Price,Location,Exchange,Currency,FX Rate,Market Currency,Accrual Date`
  If the first non-preamble line does not match this header ⇒ **fail loudly** (C-4): a renamed/moved column, or an HTML/XLS body where CSV was expected, must never be silently ingested as an empty "agreement". Columns consumed: **Ticker**, **Sector** (GICS). Values are quoted with in-field thousands-commas (`"70,061,069,946.24"`) — use a quote-aware CSV parser, never a naive comma split.
- **Two download traps observed at setup (2026-07-13), recorded so a rebuild doesn't repeat them:** (1) the plain product-page "Download holdings" link / `component=fundDownload` returned a **BlackRock HTML page** saved as `.csv`; (2) `get-fund-document?...&component=holdings` **without** the right params returned a **SpreadsheetML XML workbook named `.xls`** (`<?xml … ss:Workbook>`), not CSV. Only the URL above (`component=holdings`, CSV) returns plain comma-separated text. The header-shape assertion above is the guard that catches all three cases.

## 2b. iShares OEF holdings CSV (the D70 S&P 100 slice — forward universe through Phase 4)
- `GET https://www.blackrock.com/varnish-api/blk-one01-product-data/product-data/api/v1/get-fund-document?appType=PRODUCT_PAGE&appSubType=ISHARES&targetSite=us-ishares&locale=en_US&portfolioId=239723&userType=individual&component=holdings`
  VERIFIED 2026-07-13 (returns the real CSV; count 101 in-band). `portfolioId=239723` = OEF (iShares S&P 100 ETF). Same `get-fund-document?...&component=holdings` endpoint and identical CSV shape as §2.
- **DROP `asOfDate` for the daily fetch** (same freeze trap as §2 — omit it and BlackRock returns the latest holdings).
- Free, no auth, official, ~1-day lag. Parse tickers; map through `ticker_history`; equity holdings only. **Same header-shape assertion as §2** (fail loud on drift — one C-4 fixture covers both feeds). Cross-check: the Wikipedia S&P 100 table (§7). Divergence ⇒ fail closed; count sanity 99–103 (`Universe.Bootstrap.CountSanity`).
- Retires when the universe widens to the S&P 500 after Phase 4 sign-off (D70) — the provider stays behind the same `IIndexMembershipProvider` seam.

## 3. Ken French Data Library (factors + RF — D41)
- 5 factors + RF (daily): `https://mba.tuck.dartmouth.edu/pages/faculty/ken.french/ftp/F-F_Research_Data_5_Factors_2x3_daily_CSV.zip`  (Mkt-RF, SMB, HML, RMW, CMA, RF)
- Momentum (daily): `https://mba.tuck.dartmouth.edu/pages/faculty/ken.french/ftp/F-F_Momentum_Factor_daily_CSV.zip`  (UMD)
  VERIFIED 2026-07-13 — the files live under the `ftp/` subfolder (the data_library.html page links into it; miss the `ftp/` segment and you get a 404 or the HTML page). URLs are stable/hand-maintained. Each zip contains exactly ONE inner CSV: `F-F_Research_Data_5_Factors_2x3_daily.csv` and `F-F_Momentum_Factor_daily.csv` respectively.
- Free, no auth. Monthly refresh (D41); the publication lag of weeks is fine — attribution is diagnostic-only, never a funnel or gate input (§1.4). Fetch the zip, read the single inner CSV (`namelist()[0]`), decode as **latin1** (NOT UTF-8). The file has junk lines the parser MUST skip: a multi-line title/copyright block at the top, then the daily block of `YYYYMMDD,Mkt-RF,SMB,HML,RMW,CMA,RF` rows, then a trailing "Annual Factors: January-December" section (and its own header) — anchor on the daily `YYYYMMDD,` rows, do not assume a fixed skip count. Convert French missing-value codes (**-99 / -999 / -99.99**) to null. Values are percent (divide by 100 for decimal returns). Checksum + date-continuity validation per D41.
- **Phase 6 input — not a Phase 1 dependency.** Join key is date; the RF series here is the one referenced across the metrics stack (Jensen's alpha, Sharpe, deflated Sharpe — DESIGN_IMPROVEMENTS §1.1).

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
- `GET https://raw.githubusercontent.com/fja05680/sp500/master/S%26P%20500%20Historical%20Components%20%26%20Changes%20(Updated).csv`
  VERIFIED 2026-07-13. Use the raw.githubusercontent.com URL, NOT the github.com/.../blob/... page URL (the blob returns HTML). Header `date,tickers`; one row per date; the ticker roster is a SINGLE quoted comma-separated field per row (quote-aware parse, then split the inner field). Daily snapshots 1996-01-02 → present (~30y) — this sets the replay-window floor (well beyond the Phase-4 ≥15y requirement).
- Free, no auth, community-maintained (caveat logged in the Phase-4 calibration report per D49). Ingested into historical membership for as-of reconstruction; FX-AsOfMembership. This is a **Phase 4** input (replay), not a Phase 1 dependency.
- **Symbology normalization (map through `ticker_history` — FR-3; two gotchas observed at verification):** (1) **dot vs dash** — this file uses dots (`BRK.B`, `BF.B`, `RDS.A`); EODHD uses dashes (`BRK-B`, `BF-B`), so normalize or Berkshire/Brown-Forman drop from every roster (fixture: `BRK.B` resolves to a `security_id`). (2) **bankruptcy `*Q` suffixes** (`ENRNQ`, `AAMRQ`, `EKDKQ`, `MTLQQ`) — these delisted names are the whole anti-survivorship point; resolve them via the delisted symbol list (`exchange-symbol-list/US?delisted=1`, §1), never discard `*Q` rows as junk.
- Prefer this `date,tickers` snapshot file over `sp500_changes_since_2019.csv` (a deltas-only, 2019-start file — wrong shape and too short for the replay window).

## 9. Regime proxy index series (the market-level proxy for D50 labels — D73/FR-38, v1.9.7 finding 110)

The PIT regime label (D50, MASTER §20.1) is computed on a **cap-weight market proxy**. It is a named,
validated, fallback-bearing feed like every other (Golden Rule 25) — it was the one data dependency
the earlier named-feed passes left unresolved (`Regime.ProxySecurityId: null // set at Phase 1` named
no source). It sits on the **calibration critical path**: the D64 edge plant modulates its drift by
the regime label, so a missing or degenerate proxy silently mis-calibrates the D56 curves the whole
monitor trusts.

| Feed | Primary | Validation | Fallback |
|------|---------|-----------|----------|
| Cap-weight regime proxy (daily raw + adjusted close) | **EODHD `GSPC.INDX` EOD** — `GET /eod/GSPC.INDX?from=&to=&period=d` (the membership index symbol, reused). VERIFIED 2026-07-13 (200): index EOD IS served on the launch tier — `array` of `{date, open, high, low, close, adjusted_close, volume}` with full OHLC + `adjusted_close` (resolved; not just `/fundamentals/GSPC.INDX`). | Rotating-sample cross-check vs `SPY.US` **daily returns** (tolerance alarm; SPY's daily tracking error is negligible for a trend/vol label) | **Self-built cap-weight index** over the backfilled universe bars with as-of membership (the machinery D68 already builds for the EW benchmark, cap-weighted), stored as an index series with a stable `security_id` so `regime_labels.inputs_hash` keys a real row |

- **Backfill prerequisite (Phase 1 DoD, FR-38):** the vol component needs the proxy's trailing **3-year** daily distribution and a **200-day SMA** before the first label — so ≈ **3.8 years** of proxy history must exist before Phase 2's first Stage-2 regime write, and the **full replay window** (≥15y) before Phase 4. Backfill the proxy in the same Phase-1 pass as the universe bars. Label computation **fails closed** (refuses + logs) until the warm-up exists — never a fabricated label (`FX-RegimeProxyBackfill`).
- **Proxy stability across the S&P 100 → S&P 500 widen (D70):** regimes are market-level facts. Pin the **S&P 500 proxy** even during the S&P 100 forward slice — switching proxies at the Phase-4 widen would fabricate a label discontinuity. `Regime.ProxySecurityId` resolves from `Regime.ProxySource` at Phase 1 (CONFIG_REFERENCE).

## Provider implementation rules (all integrations)
1. Every provider behind its interface; HTTP via a shared resilient client (timeout 30s, 3 retries with exponential backoff + jitter, circuit-break after 5 consecutive failures ⇒ the daily run fails cleanly and catch-up recovers tomorrow — never partial writes).
2. Raw payloads for the day are archived to `tools/raw-cache/{source}/{date}/` (gitignored) for 30 days — every ingestion is re-auditable.
3. All ingestion writes stamp `source` and `observed_at`; nothing external is trusted without the FR-6 quality gate.
4. ⚠VERIFY items are a Phase-1/5 checklist: confirm, correct this file, commit — before building on them.
