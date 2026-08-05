# MANIFEST-vs-file audit (the 2-L(ii) sweep) — 2026-08-05, v1.9.91

*Pre-edit sweep artifact for the v1.9.91 decisions pass. Every entry in `docs/MANIFEST.md`'s "What's in here" (lines 9–65) checked against the file it names — existence and description-vs-content fidelity — plus a sweep for corpus members with no entry. Recorded BEFORE any edit of this pass so the scale is visible and the edits review as a set. Of the mismatches below, only the two 2-H items (the architecture-SVG line; the two-loops entry + caveat) are FIXED in this pass; the rest are filed as findings.*

**Repo state:** branch `docs/v1.9.91-decisions-pass`, one commit past the v1.9.90 register extraction.
**Result:** 31 files claimed, **31 present, 0 missing**. **2 DRIFTED**, **5 MISSING-ENTRY** (1 file + 1 root doc + 3 directories).

## Entry-by-entry verdicts

| MANIFEST entry | Verdict | Notes |
|---|---|---|
| L10 `START_HERE.md` | MATCHES | The entry point, as described. |
| L11 `docs/README_v1.9.md` | MATCHES | Description is literally its table of contents. |
| L12 root `README.md` | MATCHES | Landing page: status/architecture/getting-started present. |
| L13 `CLAUDE.md` | MATCHES | Hard rules, solution layout, commands present. |
| L16–18 `docs/DECISIONS_v1.9.md` | MATCHES | The v1.9.90 entry is present and accurate (register + history; rule 25/D109; check-register). |
| L19–21 `docs/MASTER_DESIGN_v1.9.md` | MATCHES | §0/§2 pointer stubs verified literally. |
| L22–23 `docs/ARENA_ARCHITECTURE_v1.9.3.md` | MATCHES | Near-verbatim to its own front matter. |
| L24 `docs/SCHEMA_v1.9.md` | MATCHES | |
| L25 `docs/CONFIG_REFERENCE_v1.9.md` | MATCHES | |
| L26 `docs/INTEGRATIONS_v1.9.md` | MATCHES | |
| **L29–31 `docs/BUILD_AND_PROMPTS_v1.9.md`** | **DRIFTED (Medium)** | Says "FR-1…FR-46"; the doc now specifies **FR-47** (D123, `construction-study`, v1.9.88) with its own Phase 5.5 section. Same stale range duplicated at `docs/README_v1.9.md:19` and `CLAUDE.md:23` — one straggler, three copies. **Filed as a finding.** |
| L32–33 `docs/TEST_PLAN_v1.9.md` | MATCHES | The 39-case §8 claim verified verbatim. |
| L34 `PROGRESS.md` | MATCHES (thin) | "Phase-gate checklist" understates the live ledger; MANIFEST L3 carries the fuller role. Cosmetic; filed. |
| L35–43 SETUP / RUNBOOK / DB_RELOCATION / FUTURE_DB_MIGRATION / REBUILD | MATCHES | RUNBOOK entry omits its later §8/§8.5 growth — omission, not contradiction. |
| L46–51 CATALOG / MONITOR / DESIGN_IMPROVEMENTS(+EXPLAINED) / UX_GUIDELINES / UX_DESIGN_SYSTEM | MATCHES | UX-1…UX-16 confirmed as of this sweep. |
| L54–55 POST_PHASE8_IMPROVEMENTS / _PLAN | MATCHES | |
| L58–59 mockups (consolidated + two panels) | MATCHES | Panel titles self-identify with the exact UX/D ids. |
| **L62 `docs/diagrams/alphalab-architecture.svg`** | **DRIFTED (High)** | Still reads "the architecture picture (projects, the sole-writer path, the Api/UI boundary)". The SVG contains **none** of those (the strings "Api"/"Worker"/"sole writer" appear nowhere in it); it is the one-page conceptual **research-flow** picture (market history → the field → the measured-alongside group → judging → verdict → money split / dashboards / researcher, calibration sealed off). CLAUDE.md:39 was corrected in v1.9.70 (finding 335); MANIFEST was not swept with it. **Fixed in this pass (2-H).** |
| L65 `docs/CHANGELOG_v1.9.md` | MATCHES | |

## Corpus members with NO "What's in here" entry

| File / dir | Severity | What it is |
|---|---|---|
| **`docs/diagrams/two-loops.svg`** | **High** | Committed `dc13c25 "diagram"` after the v1.9.90 MANIFEST commit; **referenced by nothing in the repo** (repo-wide grep for `two-loops`: zero hits). Its one-year claim ("about a year, and it needs nothing from the researcher") carries **no caveat of any kind**, while the figure is DERIVED (the M.1 near-clone noise-cancellation argument), not measured — the arena's measured horizon is ten years with a live band [6.95%, 32%]/yr (D121/D116). **Fixed in this pass (2-H): caveat added in the SVG, MANIFEST line added, referenced from MASTER §23.3.** |
| `ORIENTATION.md` (root) | Medium | The plain-language orientation; root README documents it, MANIFEST names it only in revision-history prose. **Filed.** |
| `docs/phase5/` (10 files) | Medium | The Phase 5 per-checkpoint prompts, kept as the built record. **Filed.** |
| `docs/phase5.5/` (5 files) | Medium | The construction-question record (D123). **Filed.** |
| `docs/calibration/sp500/` (12 artifacts) | Medium | Holds the frozen sign-off artefact `Calibration.ReportRef` hashes against. **Filed.** |

## Pattern worth recording (goes into the finding)

Both HIGH items are one failure mode: a correction (v1.9.70/finding 335) or a new artifact (`dc13c25`) touched one map while `docs/MANIFEST.md` went unswept. MANIFEST is the only one of the four maps (MANIFEST, CLAUDE.md docs-map, `docs/README_v1.9.md` §1, root README) with **no tool checking it** — the same argument v1.9.55 made for `check-register.ps1`.
