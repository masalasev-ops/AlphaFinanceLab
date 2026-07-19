# Claude Code prompt — v1.9.27 docs pass: register UX_DESIGN_SYSTEM (component catalogue)

Paste everything below the line into Claude Code at the repo root (on current `main`).

---

You are running a **docs-only pass (v1.9.27)** that adds a new document, `docs/UX_DESIGN_SYSTEM_v1.9.md`, and wires it into the doc set. Read `CLAUDE.md` first and obey its hard rules. Then read `docs/UX_GUIDELINES_v1.9.md` in full (especially the "Visual system — design tokens" section and rules UX-1…UX-14), `docs/MANIFEST.md`, and the tail of `docs/CHANGELOG_v1.9.md`.

## Ground rules

1. **Docs only.** No code/schema/migration/config-key/test change. Test count is unchanged (currently 539); `pwsh tools/ci.ps1` stays green.
2. **No new decision number, no palette change.** This pass is purely additive — it introduces a component-anatomy layer that references the existing tokens and rules; it does not redefine any colour, font, semantic meaning, or UX-rule. So **no D-number**. It is recorded as **finding 208** (207 is current high — the v1.9.26 twin-scorer pass) under a new **v1.9.27** CHANGELOG header.
3. **Do not create a second UX_GUIDELINES and do not edit any UX-rule.** `UX_GUIDELINES_v1.9.md` is authoritative and load-bearing; this pass adds one pointer line under its token-table header and nothing else in that file.
4. One commit at the end, on a branch, stop for review before pushing.

## Step 1 — add the new document

Create `docs/UX_DESIGN_SYSTEM_v1.9.md` with the content provided separately (the component catalogue: 16 components each bound to a real read-model field — `verdict_chip`, `MetricCell {value,display,prefix,reason,mde}`, `separation_state`, `population_percentile`, `tier`, `clamp_bound`, `quarantined`, `ReadModelStamp`, etc. — plus the token-usage pointers and the spacing/radius scale). It must:

- reference `UX_GUIDELINES_v1.9.md`'s token table for all colour/type (never restate a hex value);
- reference UX-1…UX-14 for rule behaviour (never restate a rule);
- put no honesty logic in any component (D58 — the UI renders read-model fields verbatim);
- name, for each component, the exact read-model field it binds and the UX-rule it satisfies;
- state that `UX_GUIDELINES` wins on any colour/rule conflict and the mockup is a look reference, not a source of truth.

*(Paste the UX_DESIGN_SYSTEM_v1.9.md file content here when you run this — it is the artifact already produced.)*

## Step 2 — one-line pointer in UX_GUIDELINES

In `docs/UX_GUIDELINES_v1.9.md`, immediately under the header line `## Visual system — design tokens (v5 direction) *(client-side; not a read-model rule)*` (line ~81), add one sentence, in the doc's voice:

> *Component anatomy — the exact element, size, and token treatment that renders each honesty read-model field (`verdict_chip`, `MetricCell`, `separation_state`, `population_percentile`, …) — lives in `UX_DESIGN_SYSTEM_v1.9.md`. That doc references these tokens; it never redefines them. This table remains authoritative for colour, type, and their meaning.*

Do not touch anything else in this file — no rule text, no token value.

## Step 3 — MANIFEST entry

In `docs/MANIFEST.md`, add a bullet to the doc list (same format as the surrounding `- \`docs/..._v1.9.md\` — …` bullets), placed near the `UX_GUIDELINES` entry:

> `- \`docs/UX_DESIGN_SYSTEM_v1.9.md\` — the component catalogue: each honesty read-model field → its Blazor component, element, and token treatment. The visual-assembly layer under UX_GUIDELINES' tokens and UX-1…UX-14.`

If MANIFEST's intro line carries a doc-count or a "N files" figure, roll it by one. Leave the D-range banner (D1-D85) alone (no decision changed).

## Step 4 — CHANGELOG v1.9.27 section

Append a new `## v1.9.27 — UX component catalogue (design-system assembly layer)` section at the tail of `docs/CHANGELOG_v1.9.md` (after the v1.9.26 twin-scorer section and its P13 note), docs-only preamble (tests stay 539; ci.ps1 green; no D-number — additive). One finding row:

> `| 208 | **The visual token set (UX_GUIDELINES) and the honesty rules (UX-1…UX-14) existed, but nothing connected them into component anatomy** — a builder had the palette and the rules but no spec for "given a \`verdict_chip\` field, exactly what element renders it, in which tokens." A Phase-3+ screen build would approximate the mockup rather than match it, per screen, unenforceably | New \`docs/UX_DESIGN_SYSTEM_v1.9.md\`: 16 components each bound to a real read-model field (D58), referencing UX_GUIDELINES' tokens and UX-1…UX-14 (restating neither), putting no honesty logic in any component. UX_GUIDELINES gains a one-line pointer under its token table; MANIFEST lists the new doc. No palette change, no D-number — additive | \`docs/UX_DESIGN_SYSTEM_v1.9.md\` (new); \`docs/UX_GUIDELINES_v1.9.md\` (pointer); \`docs/MANIFEST.md\` |`

## Step 5 — verify + commit

- `grep -n "UX_DESIGN_SYSTEM" docs/UX_GUIDELINES_v1.9.md docs/MANIFEST.md docs/CHANGELOG_v1.9.md` — the new doc is referenced in all three, and nowhere stray.
- Confirm no hex value or UX-rule text was copied into the new doc (it references, never restates): `grep -nE "#[0-9A-Fa-f]{6}" docs/UX_DESIGN_SYSTEM_v1.9.md` should return nothing (no palette redefinition).
- Confirm UX_GUIDELINES has exactly one added line and no rule/token value changed (`git diff docs/UX_GUIDELINES_v1.9.md` shows a single insertion).
- Confirm the CHANGELOG finding numbers stay contiguous (…207, 208) and the new header reads v1.9.27.
- `pwsh tools/ci.ps1` — green, docs-only, 539 tests.
- Commit on a branch `docs/v1.9.27-ux-design-system`: `docs(v1.9.27): add UX_DESIGN_SYSTEM component catalogue (finding 208) + UX_GUIDELINES pointer`. Push and open a PR against main. **Do not merge; stop for review.**

## Out of scope

- No change to any UX-rule, token value, or read-model.
- No new Blazor code — this documents the components; building them is the D65 UI workstream (Phase 3+).
- No D-number — additive doc, no decision altered.
