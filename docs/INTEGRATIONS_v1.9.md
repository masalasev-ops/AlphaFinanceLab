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

**Plan & limits:** All-World tier — **Fundamentals OFF** (D49 budget config; the `/fundamentals/*` rows above are dormant).

**Call limits — VERIFIED 2026-07-15** at the first live sp100 backfill (`--universe sp100 --years 20`):
- **Daily cap: 100,000 requests/day** (paid-plan default; `Backfill:ApiPlanLimit=100000` is correct — no change). The ≥50%-headroom rule (`api_usage_log`, SCHEMA_v1.9) means the daily job must stay ≤ 50,000.
- **Observed spend:** the full 20-year backfill cost **304 EODHD calls** (101 members × 3 [`/eod`+`/div`+`/splits`] + 1 `GSPC.INDX` proxy) ⇒ **0.30% of the cap, 99.7% headroom**. Call count is **universe-driven, not year-driven** (a 1-year run costs the same 304); a daily incremental job over the same universe sits at ~304/day.
- **Call cost is PER-ENDPOINT, not flat** — `api_usage_log` must weight by endpoint or it silently undercounts:

  | Endpoint | Cost / request |
  |---|---|
  | `/eod`, `/div`, `/splits` (single-symbol calls) | **1** |
  | `/news` (D46 pipeline, Phase 5) | **5** |
  | `/eod-bulk-last-day` (Bulk — the Phase-2 daily delta) | **100** |

  The 304 count is accurate because the backfill uses only cost-1 endpoints. **⚠Phase-2 item:** when `/eod-bulk-last-day` (100/req) and `/news` (5/req) come online, `api_usage_log` recording MUST weight by endpoint cost — a flat per-call count would badly under-report consumption against the 100k cap and could pass a headroom check that should have failed.
- **Second, independent limit: 1,000 requests/minute**, surfaced per-response via **`X-RateLimit-Limit` / `X-RateLimit-Remaining`** headers (distinct from the daily cap). The single-threaded 304-call backfill was nowhere near it, but the full (S&P 500) universe or a burst could approach it — and a separate arena runs its own Worker against the same account (D109/D71), so headroom is a LAB-WIDE budget, not a per-arena one — see §9 (honoring the header is a Phase-2 item; not yet enforced). **At the D87 S&P 1500 target (~4,500 cost-1 calls, contingent) the minute limit becomes binding and `/eod-bulk-last-day` (100/req, above) stops being optional for the daily delta — both are (D87 note) Phase-4 prerequisites.**
- **GMT-midnight reset quirk:** the daily counter resets at 00:00 GMT but **still reads the prior day's value until the first post-midnight request is made** — don't misread a stale pre-first-call counter as the day's remaining headroom.
- Rate-limit posture: the shared resilient client does retry-with-backoff (3 attempts, jitter) + circuit-break, and sets a descriptive User-Agent (§9).

## 2. iShares IVV holdings CSV (membership cross-check — D35)
- `GET https://www.blackrock.com/varnish-api/blk-one01-product-data/product-data/api/v1/get-fund-document?appType=PRODUCT_PAGE&appSubType=ISHARES&targetSite=us-ishares&locale=en_US&portfolioId=239726&userType=individual&component=holdings`
  VERIFIED 2026-07-13 (returns the real CSV; count 504 in-band). **`component=holdings` is what makes this endpoint serve the CSV — the older `.ajax?fileType=csv` pattern and the `component=fundDownload` variant both returned an HTML page or an XLS/XML workbook, not CSV (see the two traps below).** `portfolioId=239726` = IVV.
- **DROP `asOfDate` for the daily fetch.** BlackRock returns the *latest* holdings when `asOfDate` is omitted; a pinned `asOfDate=YYYYMMDD` freezes the download to one stale day, so the daily job would re-ingest the same file forever. The provider fetches this URL WITHOUT `asOfDate`.
- Free, no auth, official, ~1-day lag. Parse tickers; map through `ticker_history`; **equity holdings only** — drop cash/derivative/`-` rows (the trailing `Asset Class != 'Equity'` and placeholder `"-"` rows). Divergence vs the Wikipedia cross-check ⇒ fail closed per FR-4; count sanity 495–510 (`Universe.MembershipCountSanity`).
- **File shape (snapshot for the C-4 header fixture — FX-CsvHeaderShape):** 8 preamble lines (`iShares Core S&P 500 ETF` / `Fund Holdings as of,"<date>"` / `Inception Date` / `Shares Outstanding` / `Stock`/`Bond`/`Cash`/`Other`), one blank line, then the header row, then data. **Do NOT assume a fixed skip-count — scan for the header line**, and assert it equals verbatim:
  `Ticker,Name,Sector,Asset Class,Market Value,Weight (%),Notional Value,Quantity,Price,Location,Exchange,Currency,FX Rate,Market Currency,Accrual Date`
  If the first non-preamble line does not match this header ⇒ **fail loudly** (C-4): a renamed/moved column, or an HTML/XLS body where CSV was expected, must never be silently ingested as an empty "agreement". Columns consumed: **Ticker**, **Sector** (GICS). Values are quoted with in-field thousands-commas (`"70,061,069,946.24"`) — use a quote-aware CSV parser, never a naive comma split.
- **Two download traps observed at setup (2026-07-13), recorded so a rebuild doesn't repeat them:** (1) the plain product-page "Download holdings" link / `component=fundDownload` returned a **BlackRock HTML page** saved as `.csv`; (2) `get-fund-document?...&component=holdings` **without** the right params returned a **SpreadsheetML XML workbook named `.xls`** (`<?xml … ss:Workbook>`), not CSV. Only the URL above (`component=holdings`, CSV) returns plain comma-separated text. The header-shape assertion above is the guard that catches all three cases.

## 2b. iShares OEF holdings CSV (the D70 S&P 100 slice — forward universe through Phase 4)
- `GET https://www.blackrock.com/varnish-api/blk-one01-product-data/product-data/api/v1/get-fund-document?appType=PRODUCT_PAGE&appSubType=ISHARES&targetSite=us-ishares&locale=en_US&portfolioId=239723&userType=individual&component=holdings`
  VERIFIED 2026-07-13 (returns the real CSV; count 101 in-band). `portfolioId=239723` = OEF (iShares S&P 100 ETF). Same `get-fund-document?...&component=holdings` endpoint and identical CSV shape as §2.
- **DROP `asOfDate` for the daily fetch** (same freeze trap as §2 — omit it and BlackRock returns the latest holdings).
- Free, no auth, official, ~1-day lag. Parse tickers; map through `ticker_history`; equity holdings only. **Same header-shape assertion as §2** (fail loud on drift — one C-4 fixture covers both feeds). Cross-check: the Wikipedia S&P 100 table (§7). Divergence ⇒ fail closed; count sanity 99–103 (`Universe.Bootstrap.CountSanity`).
- Retires when this arena widens to its full S&P 500 universe after Phase 4 sign-off, which is where that universe stops (D70; **D109** supersedes D87 — further breadth is a separate arena, never an in-place widen) — the provider stays behind the same `IIndexMembershipProvider` seam.

## 2c. Mid/small-cap membership feeds — UNVERIFIED research, no longer a prerequisite of this arena (D109 supersedes D87)
**The S&P 1500 widening these feeds were recorded for is VOID: D109 supersedes D87.** This arena stops at the S&P 500, so nothing below is a prerequisite of it, and none was ever a launch dependency.

They are **retained as research, not repurposed.** D109 says additional breadth arrives as a SEPARATE arena, and that such an arena *"needs its own D70-style sourcing gate at ITS OWN Phase-4 sign-off"* — so whether any feed below is the right source for, say, an `sp400` arena is **settled at that arena's registration (ARENA_ARCHITECTURE §6), not here.** What survives unchanged is the finding that motivated the list: **the depth problem is real** — no S&P 400/600 as-of-membership source of sufficient depth has been confirmed, and any arena needing one inherits that unsolved gate. All entries remain **UNVERIFIED**; recorded so a future arena is never sourced silently (Golden Rule 25).

- **iShares IJH holdings CSV (S&P 400 MidCap) — live membership, UNVERIFIED:** the same BlackRock `get-fund-document?...&component=holdings` pattern as §2, at IJH's portfolioId (**TBD**); before use it must be VERIFIED with a count-sanity band and the C-4 header-shape fixture exactly like §2.
- **iShares IJR holdings CSV (S&P 600 SmallCap) — live membership, UNVERIFIED:** same pattern at IJR's portfolioId (**TBD**), same discipline.
- **Independent 400/600 cross-checks — UNVERIFIED:** Wikipedia S&P 400 / S&P 600 constituent tables (the §7 pattern) or another independent feed, for the FR-4 divergence gate.
- **Historical S&P 400/600 as-of membership (the DEPTH gate) — UNVERIFIED, the go/no-go condition:** the fja05680 community CSV (§8) is **S&P 500 only**; the 400/600 sources located so far (EODHD Marketplace "Historical Constituents" ~12y; N-PORT-derived ~2019+) may not reach the full replay window. If no source of sufficient **depth + accuracy** is confirmed, an arena that needs one **cannot be registered** — the gate is now that arena's own, at its own sign-off (D109), not a fallback for this one. If EODHD is the only source, this couples the widening to the **Fundamentals tier** (currently OFF per D49 / §1) — making *membership*, not Phase 8, the fundamentals trigger.

## 3. Ken French Data Library (factors + RF — D41)
- 5 factors + RF (daily): `https://mba.tuck.dartmouth.edu/pages/faculty/ken.french/ftp/F-F_Research_Data_5_Factors_2x3_daily_CSV.zip`  (Mkt-RF, SMB, HML, RMW, CMA, RF)
- Momentum (daily): `https://mba.tuck.dartmouth.edu/pages/faculty/ken.french/ftp/F-F_Momentum_Factor_daily_CSV.zip`  (UMD)
  VERIFIED 2026-07-13 — the files live under the `ftp/` subfolder (the data_library.html page links into it; miss the `ftp/` segment and you get a 404 or the HTML page). URLs are stable/hand-maintained. Each zip contains exactly ONE inner CSV: `F-F_Research_Data_5_Factors_2x3_daily.csv` and `F-F_Momentum_Factor_daily.csv` respectively.
- Free, no auth. Monthly refresh (D41); the publication lag of weeks is fine — attribution is diagnostic-only, never a funnel or gate input (DESIGN_IMPROVEMENTS §1.4). **Dual role since D83 (v1.9.23):** the same series is additionally an **availability-lagged signal input for residual momentum only** (catalog §6.5; MKT-fallback for the lag hole per D83) — the daily-anchored parser and the validation below are unchanged by this. Fetch the zip, read the single inner CSV (`namelist()[0]`), decode as **latin1** (NOT UTF-8). The file has junk lines the parser MUST skip: a multi-line title/copyright block at the top, then the daily block of `YYYYMMDD,Mkt-RF,SMB,HML,RMW,CMA,RF` rows, then a trailing "Annual Factors: January-December" section (and its own header) — anchor on the daily `YYYYMMDD,` rows, do not assume a fixed skip count. Convert French missing-value codes (**-99 / -999 / -99.99**) to null. Values are percent (divide by 100 for decimal returns). Checksum + date-continuity validation per D41.
- **Phase 6 input — not a Phase 1 dependency.** Join key is date; the RF series here is the one referenced across the metrics stack (Jensen's alpha, Sharpe, deflated Sharpe — DESIGN_IMPROVEMENTS §1.1).

## 4. FRED (RF fallback — D41)
- `GET https://fred.stlouisfed.org/graph/fredgraph.csv?id=DGS3MO` — no key needed for CSV. Only used if the French RF series is unavailable.

## 5. Anthropic (D46)

*The Phase-5 ⚠VERIFY is **CLOSED (2026-08-01, v1.9.60)** against Anthropic's current published API reference — and **LIVE-CONFIRMED (2026-08-01, v1.9.69)**. The published-reference check confirmed the **endpoints, headers, polling shape and result semantics** below; the live run confirmed the whole round trip end to end (batch create → poll to `ended` → stream results → usage → cost) in **2m15s** against the real endpoint.*

*The two checks were **not** redundant, and this is the clearest evidence in the corpus for §23.8's bet that a spec written before its code can be contradicted by it. The published-reference pass could not have found **finding 328** — the defect is in what the API **RETURNS**, not in what it accepts — and no mocked test could either, because a fake transport echoes back the model string it was handed. The first live call failed, and the forward LLM path was dead on real traffic until it was fixed.*

*Nor was ONE live call enough (**finding 329**). The first run exercised `claude-haiku-4-5` — the cheap tier, chosen to prove the wire contract without spending — which turned out to be **the one tier the lab never calls**: all four dispatched tasks resolve to `claude-opus-5`, and `news_extraction` is pinned to Haiku but dispatched by nothing. The smoke test is now a Theory over **both pinned tiers**, and the second one immediately refuted a rule the first had suggested.*

- **Scheduled reads — Message Batches API (GA, no beta header):** `POST https://api.anthropic.com/v1/messages/batches` with the day's requests, each carrying a **`custom_id`**. Poll `GET /v1/messages/batches/{id}` until `processing_status == "ended"`, then stream `GET /v1/messages/batches/{id}/results`. **Half price** vs synchronous.
  - **Results arrive in ANY order — key by `custom_id`, never by position.** This is the single most likely silent bug in a batch client.
  - Each result carries `result.type` ∈ `succeeded` / `errored` / `canceled` / `expired`; **all four must be handled**. `errored` splits further: a validation error is not retryable, a server error is.
  - Limits: ≤100,000 requests or 256 MB per batch; most batches end within 1 hour, **maximum 24 hours**; results retrievable for 29 days.
- **Interactive research assistant:** `POST /v1/messages`.
- **Prompt caching:** mark the static instruction block with `cache_control` so only the day's news is fresh tokens. Caching **works inside a batch** — the shared L0/L1 prefix is written once and read by every request in the day's job, which is what makes the §23.2 economics hold.
- **Auth and headers:** `x-api-key: {Secrets:AnthropicApiKey}` (from `appsettings.Secrets.json`, D67) + `anthropic-version: 2023-06-01` + `content-type: application/json`. Models per task from `Llm.Tasks` config (CONFIG_REFERENCE).
- **THE SERVED MODEL STRING MAY BE THE BARE ALIAS OR A DATED SNAPSHOT, AND WHICH ONE IS PER-FAMILY (findings 328 + 329, both live-observed 2026-08-01).** Measured on the two pinned tiers:
  | pinned | reported by the API |
  |---|---|
  | `claude-opus-5` | `claude-opus-5` — the bare alias |
  | `claude-haiku-4-5` | `claude-haiku-4-5-20251001` — a dated snapshot |
  
  **Do not assume either form.** finding 328 saw only the Haiku call and generalised the snapshot form into a rule; running the tier that actually serves traffic refuted it within the hour (finding 329). The safe reading is the weaker one: **the served string always STARTS WITH the pinned alias, and nothing more can be relied on.**
  
  This matters because the lab costs the model that actually SERVED the call (D104 artefact (d) is about what ran, not what was asked for). That rule and `PricingFor` failing closed on an unpriced model (D24/rule 10) are each correct alone and together threw on every Haiku call. Resolved by keying `Llm.Pricing` on the **alias** and resolving **exact-first, then longest configured prefix** — a rule that is correct under BOTH forms, which is precisely why it was chosen over anything that assumes one of them. Do NOT configure dated keys: they would need a config edit every time the vendor rolls a snapshot, which is the brittleness the alias exists to remove.
- **Rejected on the Batches API — do not design around it:** the server-side `fallbacks` parameter. A refused or failed scheduled read degrades through the D24 `DegradationOrder`, never through a fallback model.
- **The shared resilient client applies — but it was NOT REACHABLE as written** (findings 318, 323; resolved at checkpoint 5.1). §9.1's policy (30 s timeout, 3 retries with exponential backoff + jitter, circuit-break after 5 consecutive failures) is what the LLM path uses, and the note that it simply "inherits" it was wrong in two concrete ways discovered when the code was built: `IResilientHttpClient` lives in `AlphaLab.Data`, which **`AlphaLab.Llm` may not reference** (the `ci.ps1` reference graph allows it only `Core`), and it was **GET-only** while the Batches API needs POST. Two narrow additions fixed it without a second HTTP stack:
  - **`IResilientHttpSender`** (a separate interface over the same client, not more members on the shared one) adds POST and per-request headers. Separate because widening the shared contract broke three existing test stubs that had no reason to care about POST.
  - **`IModelTransport`** in `AlphaLab.Core` is the port the LLM layer talks to; `AnthropicHttpTransport` in `AlphaLab.Data` satisfies it over the resilient client. So the resilience policy stays in exactly one place for every provider in the lab, and the LLM layer holds no transport concerns at all.
- **POST is retried, and the safety argument is specific rather than general:** the two endpoints served are batch-create and single-message, both safe to repeat — a duplicate batch costs a duplicate read the FR-21 cache then absorbs, whereas a lost batch is a no-read day. **This is not a licence to retry any POST**; a future non-idempotent endpoint needs its own path.
- Batches is asynchronous and its own limits differ in kind from EODHD's two ceilings, so if a later checkpoint finds the shared policy unfit, the fix is a recorded policy in this file rather than an ad-hoc client setting.
- **The API key is attached per request, never to the shared `HttpClient`** — that client is also used by EODHD, BlackRock and Wikipedia, none of which should ever see an Anthropic credential on the wire.
- Hard budget enforcement wraps the client (D24): pre-flight cost estimate → refuse + degrade if over; log to `llm_budget_log`.

## 6. Alpaca (bar cross-check fallback — D19/D35)
- `GET https://data.alpaca.markets/v2/stocks/{symbol}/bars?timeframe=1Day&start=&end=` with `APCA-API-KEY-ID`/`APCA-API-SECRET-KEY` headers (free tier: IEX feed). Used only by the rotating-sample quality gate (FR-6); optional in dev.

## 7. Wikipedia (membership cross-check / fallback)
- `https://en.wikipedia.org/wiki/List_of_S%26P_500_companies` — parse the constituents table. **D49 launch role: the daily cross-check against the IVV primary**; post-upgrade it demotes to fallback (activated only if both EODHD and IVV are unavailable; log the degraded-source flag on `index_membership_log`).
- `https://en.wikipedia.org/wiki/S%26P_100` — parse the components table; the cross-check for the D70 S&P 100 slice (`Universe.Bootstrap.MembershipCrossCheck`).
- **Requires a descriptive `User-Agent` (Wikimedia UA policy).** A header-less request returns **403 Forbidden** (a 126-byte error body, not the table) — observed 2026-07-14 at the first live sp100 backfill, where it blocked the cross-check and (fail-closed) the whole membership step. .NET's `HttpClient` sends **no** default UA, so the shared resilient client now sets one (see §9.1). EODHD and BlackRock do not enforce this.

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
1. Every provider behind its interface; HTTP via a shared resilient client (timeout 30s, 3 retries with exponential backoff + jitter, circuit-break after 5 consecutive failures ⇒ the daily run fails cleanly and catch-up recovers tomorrow — never partial writes). The client sets a **descriptive `User-Agent`** on every request (`ResilientHttpOptions.UserAgent`, default `AlphaLab/1.9 (paper-trading research lab)`): **Wikimedia returns 403 to header-less requests** and .NET's `HttpClient` sends no default UA (observed 2026-07-14, first backfill — §7). EODHD/BlackRock don't require it. Overridable to add a contact per the Wikimedia UA policy.
2. Raw payloads for the day are archived to `tools/raw-cache/{source}/{date}/` (gitignored) for 30 days — every ingestion is re-auditable.
3. All ingestion writes stamp `source` and `observed_at`; nothing external is trusted without the FR-6 quality gate.
4. ⚠VERIFY items are a Phase-1/5 checklist: confirm, correct this file, commit — before building on them.
5. **EODHD has two independent rate limits (§1, VERIFIED 2026-07-15):** 100,000 requests/**day** and 1,000 requests/**minute** (the latter surfaced per-response via `X-RateLimit-Limit`/`X-RateLimit-Remaining` headers). The daily budget is tracked in `api_usage_log` (≥50% headroom, **weighted by per-endpoint cost** — §1: `/eod`·`/div`·`/splits`=1, `/news`=5, `/eod-bulk-last-day`=100). The **minute** limit is **not yet enforced** (the shared client does not read `X-RateLimit-*`); the single-threaded sp100 backfill (304 calls) stays far below it, but a wider universe should honor the header — a Phase-2 item.
