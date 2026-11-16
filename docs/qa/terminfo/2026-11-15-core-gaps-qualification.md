# Core-to-inventory reconciliation: `text.reverse-index-scroll` and `reset.method` (2026-11-15)

Both ids already pass in the frozen Core requirement results ([`2026-11-11-core-results`](2026-11-11-core-results/report.md), run `a3269032…`), yet neither was in the
[qualification inventory](2026-11-09-qualification-inventory.md): the inventory admits only registered graded suites, and the Core result publications are registered
as a different evidence class (`nonInventoryPublicationTests`). This note records the reconciliation. It changes no production code, takes no new desktop collection,
rewrites no Core, stock or common-harness result and adds no benchmark verdict of its own.

## What was reconciled, and from which measurement

The Core rows are backed by the suite `ntilde-owned-core-gaps-v1` (`core-gaps.mjs`), which every owned collection since then re-runs and retains as `cg-*` files.
The newest merged run, the extended-mouse run (`59b3fbe5…`, App and scripts `aaaece99`, source policy v58, receipt schema 64, [retained](2026-11-15-mouse-extended/)),
carries a complete Core-gaps measurement at the production code now on `main` (`git diff aaaece99 HEAD -- src` is empty), so no new collection was needed and the frozen
Core measurement (App `1c3400f4`, policy v35) is not what the inventory cites.

[`2026-11-15-core-gaps/`](2026-11-15-core-gaps/) restates two of that suite's four cases in the report shape the inventory reads
(`core-gaps-publication.mjs`, contract ids in `core-gaps-contracts.mjs`, suite `core-gaps` in the registry). It is a projection, not a second archive: it cites the
carrying publication's `owned-run.zip` and `summary.json` by SHA-256 and the publication test rederives it from that archive:

- the complete owned receipt is re-issued from the retained run (which re-checks the Core-gaps binding), and the retained `cg-observations.json` must equal what the live
  grading module rebuilds from the retained journal (`cg-run.json` and 29 journaled, paired state reads in `cg-snapshots.jsonl`), byte for byte;
- the projected `summary.json` and `report.json` must equal the retained files byte for byte;
- refusal tests alter the effect verdict, rewrite report and binding coherently, strip a case, change the run id, a journal frame and the adapter bundle, and cite the
  wrong carrying run.

## Verdicts (kept separate)

| Id | Stock | Pinned callback (replayed over the journal) | Effect contract `ntilde-core-gaps-effect-v1` | Layers |
|---|---|---|---|---|
| `text.reverse-index-scroll` | inconclusive (unchanged) | supported | supported | stored state, actual scrollback |
| `reset.method` | inconclusive (unchanged) | supported | supported | stored state, actual scrollback, desktop backend reset() API |

No divergence: the two verdicts agree. The pinned verdict is the unchanged pinned callback replayed over the independently captured states of its exact writes
(the callback's recorded result must equal an independent re-implementation's replay of the same journal); it is not a stock collector grade, and the stock terminal path stays inconclusive for both ids.

### What was observed

- **Reverse index.** Pinned fixture: margins 1;5, `MARKER` at the home cell, `ESC M` from home: `MARKER` is on row 2, the cursor stays home, actual scrollback is empty,
  and the margins are restored afterwards. Separately seeded interior region (rows 2-5, cursor on the region's top row): the rows scroll down one line inside the region,
  `ABOVE` and `OUTSIDE` are untouched. Control with the cursor below the top margin: only the cursor moves up (row 4 to row 3), nothing scrolls. Every cell kept its default
  attributes.
- **reset().** Three real asynchronous `reset()` calls, each with the contracted bytes: after `Hello World` (cursor `0,11`), after a dirty main bank (4 history lines, margins
  3;9, origin and insert on, auto-wrap off, hidden cursor, an SGR pen), and from the alternate screen entered over that dirty main bank. Each is followed by an empty
  viewport, the cursor at home and visible, full margins, origin, insert and pending wrap off, auto-wrap on, no history, default rendition and the main screen; a character
  written afterwards is stored at home with the default pen.

## Boundary of the `reset.method` claim

`reset()` is **not an App endpoint**. The pinned criterion belongs to the Termless backend API; the desktop App has none, and the legacy App callback still returns
insufficient-evidence for it. What passes is the desktop collector's own asynchronous backend `reset()` (`createCoreBackend` in `core-gaps.mjs`), which writes one documented
sequence (leave the alternate screen, `ESC c`, then explicit normalization of origin, wrap, insert, cursor visibility, margins, rendition, display and scrollback) through
the fenced write path. The claim is therefore that the terminal reaches the promised pristine state after that API's sequence, in the three conditions above. It is not
a claim that the App exposes a reset method, that `ESC c` alone clears the DECOM/insert/wrap modes (`reset.ris` is a separate case and is not substituted), or anything
about host presentation, title/icon metadata, remote/mux, Windows/Linux or a common-harness result.

## Inventory deltas (derived from the regenerated inventory)

| Measure | Before | After |
|---|---:|---:|
| Unique benchmark ids with qualified retained effect evidence | 125 | **127** |
| Suite claims | 127 | **129** |
| Effect contract supported | 124 | **126** |
| Pinned-supported / pinned+effect supported | 89 / 89 | **91 / 91** |
| Effect supported, pinned inconclusive | 35 | 35 |
| Effect unsupported | 1 | 1 |
| Not effect-supported, `effectUnqualified` | 45 | **43** |
| of which not measured | 44 | **42** |
| Not pinned-supported | 80 | **78** |

Unchanged: the stock baseline (168 inconclusive / 86 supported / 1 unsupported) and every stock grade, the Core requirement result (60 pass / 20 inconclusive of 80) and the
common-harness comparison. Inventory ids and Core points are different counts: these two ids are Core passes already counted; they are new to the inventory only.

## Not admitted here

The same measured suite also grades `device.env-term` (the actual session child's `TERM`) and `extensions.osc1-icon`. `device.env-term` is outside this reconciliation and
remains absent from the inventory. `extensions.osc1-icon` already has a stronger claim from the `osc-title` suite and keeps only that claim.

## Limits

- The evidence is the retained run at App `aaaece99`; the projection and inventory entry are publication-only and bind that run, not later scripts. A change to reverse
  index, `reset()`'s sequence or the Core-gaps measurement needs a new run and a new projection.
- Stored cells, cursor, margins, modes and actual scrollback only; the run is macOS, one geometry (40 rows by 140 columns), the App version string is operator supplied.
- The pinned `reset.method` verdict depends on the collector's backend API as described above.

## Reproduce

    node --test scripts/terminfo/core-gaps-publication.test.mjs
    node scripts/terminfo-inventory.mjs --check --rederive
    node --test scripts/terminfo/qualification-inventory.test.mjs
