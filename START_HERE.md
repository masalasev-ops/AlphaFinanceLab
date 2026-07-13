# START HERE — build the AlphaLab (design revision v1.9) with Claude Code, starting today

You're holding **AlphalabTradingplanV1.9** — the complete, self-contained documentation set for building a personal C#/.NET paper-trading research lab entirely through Claude Code. Everything is merged in; there's nothing to reconcile. *(The v1.9.1 consistency errata — CHANGELOG findings 59–75, decisions D68–D69 — and the v1.9.2 errata — findings 76–86, decision D70 — are merged throughout; file names keep the v1.9 label. The v1.9.3 multi-arena capability — decision D71 — is specified in the companion `ARENA_ARCHITECTURE_v1.9.3.md`; it is additive, changes no schema, and does not affect the S&P 500 build you start with. The v1.9.4 arena-integration errata — findings 92–99, FR-37 — are merged throughout: Phase 0 resolves the `{Arena.Id}` path token and the Web client carries the one-entry arena registry from day one. The v1.9.5 post-Phase-0 errata — findings 100–106 — are merged throughout: the database base is now stated as configurable everywhere (this deployment: `E:\AlphaLabDatabase`, see `docs/DB_RELOCATION.md`), the Phase-8 fundamentals decision takes the next free D-number, and the retired `Api.Bind` key is superseded by `Urls`. The v1.9.7 deep-dive errata — findings 108–121, decisions D72–D73, FR-38 — are merged throughout: WAL is established at schema startup, `config` is genuinely versioned, the OnDemand Worker drains queued jobs and recovers a crashed run flag, the regime proxy is a named feed, and the Phase-4 calibration gains an edge-plant-survival floor and a joint false-alarm bound; the full review is archived at `docs/reviews/DEEP_DIVE_REVIEW_2026-07-12.md`.)*

**New in this revision (v1.9):** the design's claims were audited against its own mechanics, and three fixes are merged in. **The lab's fast product is named honestly (D63):** because the random controls are cost-matched, an edgeless strategy is never "falsified in months" — it is declared **`IndistinguishableFromRandom`**, a first-class chip rendered beside every verdict, with fast kills reserved for the trade-level track and anti-predictive breaches; the KPIs are re-split to match. **The calibration is grounded (D64):** the planted strategies that produce every monitor threshold are now fully specified (regime-conditional, lumpy, multi-seed — never constant drift), with a mandatory sensitivity check archived in the calibration report. **The build order is a route, not just gates (D65):** S&P 100, API-only (Scalar), straight to Phase 4 replay — screens are a parallel workstream due by Phase 7. (Specs: `MASTER_DESIGN_v1.9.md` §20.8–20.9, §17.1.)

**Carried from the v1.8 pass:** the UI is fully swappable, and the process model is coherent. A dedicated **`AlphaLab.Api`** project is the single boundary every front end talks to; all "honesty" display logic (dimming uncertain numbers, verdict labels, percentile chips, replay quarantine) is computed once in C# and shipped as plain data — so the Blazor UI can be replaced with Angular, React, or a mobile app **without touching the quant core and without any risk of a new UI showing a number the backend would have hidden.** The nightly work lives in a separate **`AlphaLab.Worker`** process — the sole database writer — and by default it runs **on demand**: you open it each evening, it works out which trading days it missed, updates them in order, and exits. **Your computer does not need to be always on** (D61). The API contract is pinned (versioning, error format, async jobs for long tasks, exact money) so any second UI is a drop-in. (Decisions D57–D61; specs in `MASTER_DESIGN_v1.9.md` §21–22.)

---

## Start today — the exact steps

**1. Prerequisites (about an hour, mostly free).** Open `docs/SETUP_v1.9.md` and do it end to end:
- Install the **.NET 10 SDK**, **Git**, and **VS Code** (or your IDE). Confirm `dotnet --version` shows 10.x.
- Create a free **EODHD** account and get an API token (the paid tier isn't needed to start — Phase 0 and the skeleton run with no data at all). Get an **Anthropic API key** for later phases.
- Put `Secrets:EodhdApiToken` and `Secrets:AnthropicApiKey` in a gitignored `appsettings.Secrets.json` (D67 — no env vars; add it to `.gitignore` first, never commit it).
- Run the day-zero endpoint checks in SETUP so you know your data access works before you rely on it.

**2. Make the repo (5 minutes).**
- Create a new **private GitHub repo** and clone it locally.
- Copy `docs/CLAUDE.md` and `docs/PROGRESS.md` into the **repo root**.
- Copy everything else from this package's `docs/` folder (all the `.md` files and all four `.html` mockups) into a **`docs/` folder** in the repo.
- Commit: `docs v1.9, pre-code baseline`.

**3. First Claude Code session — Phase 0 (the skeleton).**
- Open the repo in **Claude Code** and turn **Plan Mode ON**.
- Paste the **Phase 0 prompt** from `docs/BUILD_AND_PROMPTS_v1.9.md` §4, word for word.
- Claude Code will propose a plan that creates the .NET solution — including the new `AlphaLab.Api` project and a Blazor client wired to it — sets up the database migrations and CI, and stands up an empty app. Review the plan, approve it, let it run.
- **You'll know Phase 0 is done when:** the build and tests pass in CI, `AlphaLab.Worker` launches and exits cleanly (nothing to catch up yet), `AlphaLab.Api` serves its API docs (Scalar UI at `/scalar/v1`), and the empty Blazor app renders every screen by calling the API. Commands: `dotnet run --project src/AlphaLab.Worker` (the evening update — catches up and exits; this is the one you'll run daily), `dotnet run --project src/AlphaLab.Api` (the API), and `dotnet run --project src/AlphaLab.Web` (the UI). Only the Worker touches the data; the API and UI are optional and read-mostly.

**How you'll actually use it day to day:** each evening after the US market closes, run `AlphaLab.Worker` (make a desktop shortcut for it). It updates whatever it missed — one day or ten — and quits. Open the UI whenever you want to look. No always-on machine, no scheduler to babysit. (If you ever put it on a server, flip `Worker.Mode=Scheduled` and it triggers itself — D61.)

**4. Then go phase by phase, in order.** 0 → 1 → 2 → 3 → 3.5 → 4 → 5 → 6 → 7 → 8. Each phase has a ready-to-paste prompt in `BUILD_AND_PROMPTS_v1.9.md` §4 and a Definition-of-Done checklist in `PROGRESS.md`. **Never start a phase while the previous one's checklist is red.** End every session with tests green (or honestly noted as red in PROGRESS.md), a PROGRESS.md entry, and a commit. **Ride the vertical slice (D65):** your strategic target is Phase 4 (Arena Replay) — operate the lab through the API (Scalar UI) until replay signs off, and treat the Blazor screens as a parallel workstream due before Phase 7 exit. (The S&P 100 slice is a real named feed — OEF holdings CSV + Wikipedia S&P 100 cross-check — and replay itself always runs on S&P 500 as-of membership, with the full historical backfill as a Phase 4 prerequisite; D70.) Replay is where you'll *measure* how fast the lab kills anti-predictive strategies and how long a no-edge idea takes to earn its `IndistinguishableFromRandom` chip — until then, every screen is speculative.

That's it — you can complete steps 1–3 today and have a running (empty) app by the end of your first session.

---

## How the pieces fit (so the swappable-UI design makes sense)

```
 data providers ─┐
                 ▼
   AlphaLab.Worker  ──►  SQLite (all state)     ← run each evening: catch up → exit (D59/D61)
                        ▲
   AlphaLab.Evaluation / AlphaLab.Core   ← all the math + the honesty read-models (D58)
                        ▼
        AlphaLab.Api  (/api/v1 JSON)  ← the ONE thing any UI talks to (D57/D60)
                        ▼
   AlphaLab.Web (Blazor)  ◄── swappable for ──►  Angular / React / mobile
```

- The **quant core** never knows a UI exists.
- **`AlphaLab.Api`** turns each screen into a plain JSON endpoint and handles the handful of user actions (create a strategy, ask for a research brief, run the skeptic, apply an admin fix, launch a replay).
- The **honesty rules live in the data**, not the UI: a number that's too uncertain to trust arrives already flagged "show this dimmed." So every UI, present or future, obeys the same rules automatically.
- To swap the UI later: build a new front end against the same `AlphaLab.Api` endpoints. Nothing else changes.

## What the four HTML files are

Static **pictures** of the finished screens — open them in a browser to see the intended look. You never run or edit them. When a phase prompt says "per `lab_honesty_ux_mockups.html`", Claude Code reads that file and reproduces its layout in the real UI. The enforceable rules they illustrate live in `docs/UX_GUIDELINES_v1.9.md` (UX-1…UX-13) and are enforced in the read-models.

## Which document answers which question

| You want… | Read |
|---|---|
| The whole design in one place | `docs/MASTER_DESIGN_v1.9.md` (UI boundary: §21–22) |
| The exact prompt for the current phase | `docs/BUILD_AND_PROMPTS_v1.9.md` §4 |
| A table/column name | `docs/SCHEMA_v1.9.md` (never invent columns) |
| A config key/default | `docs/CONFIG_REFERENCE_v1.9.md` |
| An external API endpoint | `docs/INTEGRATIONS_v1.9.md` |
| A strategy's exact behavior + tests | `docs/STRATEGY_CATALOG_v1.9.md` |
| Why a number is computed a certain way | `docs/DESIGN_IMPROVEMENTS_v1.9.md` (plain-language math in MASTER §19) |
| A friendly, non-technical overview | `docs/AlphaLab_Explained_Plain_Language.pdf` |
| How to operate it day to day | `docs/RUNBOOK_v1.9.md` |

> Research/paper-trading only. Not investment advice.
