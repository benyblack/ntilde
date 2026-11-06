# SGR reset and standard-color benchmark qualification — 2026-11-06

Five pinned terminfo.dev Core cases — `sgr.selective-reset.bold`,
`sgr.selective-reset.italic`, `sgr.reset`, `sgr.fg.standard` and
`sgr.bg.standard` — measured **supported** against a fresh disposable macOS
desktop instance, each conclusion derived from the pinned collector's own
criteria replayed byte-exactly and graded from parent-journaled stored
attributes and palette indices, bound to a completed schema-14 cleanup
receipt.

This is a **local collector-extension measurement**, not stock CLI output. The
same instance's owned-adapter collector run (the derived
`ntilde-owned-macos-v1` adapter, not the unmodified stock CLI) retains its
recorded identity (86 supported / 1 unsupported / 168 inconclusive,
`suiteComplete=false`) with SGR scoring unchanged from stock definitions, and
grades all five cases inconclusive with `consumed` evidence — the pin's own
terminal path states "Cursor advance does not verify SGR styling".

## What each conclusion establishes

| Case | Measured evidence (zero-based cells) | Conclusion |
|---|---|---|
| `sgr.selective-reset.bold` | Control `C` plain; `X` (SGR 1;2;3) bold+dim+italic all initially **active**; `Y` (after SGR 22) bold and dim **cleared**, italic **preserved**, colors untouched | **supported** |
| `sgr.selective-reset.italic` | Control `C` plain; `X` (SGR 1;3) bold+italic active; `Y` (after SGR 23) italic cleared, bold preserved, no other flag moved | **supported** |
| `sgr.reset` | Control `C` plain; `X` (SGR 1;3;4) bold+italic+underline active; `Y` (after SGR 0) all three cleared **and** colors still on their default markers — the fixture sets no colors, so this proves SGR 0 introduces no palette-index-0 or literal color, not a restoration of changed colors | **supported** |
| `sgr.fg.standard` | Control `C` on default markers; `X` (SGR 31) stores exact **palette index 1**; `Y` (SGR 34) stores exact **index 4**; backgrounds keep default markers, no style flag moves | **supported** (stored indices) |
| `sgr.bg.standard` | Control `C` on default markers; `X` (SGR 41) stores exact **palette index 1**; `Y` (SGR 44) stores exact **index 4**; foregrounds keep default markers | **supported** (stored indices) |

The selective-reset conclusions independently prove the target attribute was
initially active, became cleared, and that unrelated attributes remained
unchanged — the pinned `measuredReset` criterion executed over the observed
cells agrees with the independent contract grader and the parent-side pinned
replay on every field.

The standard-color conclusions cover the **stored palette indices** (31→1,
34→4, 41→1, 44→4) — never the theme-dependent RGB appearance. The pinned
named-color criterion compares *exposed RGB values*, which the stored
projection deliberately does not carry; executing the pinned code over these
observations yields its own "Baseline, theme colors, or color readback were
indistinguishable" inconclusive, and that verdict is retained beside the
supported index-level conclusion. No index is inferred from rendered
appearance, and no screenshot evidence is claimed.

## Remaining inconclusive scope

The RGB-appearance level of `sgr.fg.standard`/`sgr.bg.standard` remains
inconclusive under the pinned criterion: the missing evidence is an exposed,
theme-resolved RGB readback per cell, which Ntilde's stored projection does
not provide by design. The pinned terminal path of all five cases likewise
remains `consumed`-inconclusive in the stock run.

## Evidence chain

The worker replayed each case's exact pinned fixture bytes through the owned
fence (every write followed by a DA ordering barrier), read the resulting
attribute runs through the parent-journaled `readScreen` observer
(`NTILDE_SC_`-nonce-bound pane, geometry checked per read), and executed the
pinned probe's own termless code over those observed screens. An independent
contract grader (`sgr-colors-contracts.mjs`, versioned v1) and a second
parent-side replay of the pinned code over the retained journal must agree
before any outcome is published. Eight frames — start, nonce bind, five read
groups, finish — are retained with hashes; the start and finish frames must
match exactly (alternate-screen restoration).

`unsupported` requires a measured mismatch **behind a valid control**
(initially-unset baseline, proven-active setup, and setup-cell colors still on
their default markers — the pinned fixtures set no colors, so color drift
invalidates the control). Wrong input bytes, missing
captures, changed observations, mismatched run identities, fabricated pinned
fields and incomplete restoration each prevent any conclusive grade (covered
by CI regressions, including default-versus-palette-index confusion and
accidental clearing of unrelated attributes).

## Collector extension identity

| Measure | Value |
|---|---|
| Layered grading adapter | `ntilde-owned-sgr-colors-grading-v1` (contracts v1) |
| Derived from | `ntilde-owned-ascii-sgr-grading-v1` over `ntilde-owned-macos-v1` over pinned `terminfo.dev@4.0.0` |
| Pinned bundle SHA-256 | `aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6` |
| Layered bundle SHA-256 | `7dae2a660ef3819b5f3f1fd642564193f00b7d2a10ad4c8172c996fb73dc2698` |
| Receipt schema | 14; SGR-colors binding provisional until the shared cleanup receipt |

The derivation refuses unless the pinned bundle still contains the exact five
probe definitions; the upstream bundle and both lower-layer adapters stay
byte-identical underneath the appended exports, and the receipt re-derives
the layered adapter from the retained owned adapter rather than trusting
declarations.

## Measurement

| Measure | Value |
|---|---|
| Run / receipt completed | run `cac0d29b9972473f8121500581b07ffb`, receipt 2026-11-06T07:36:09.956Z (schema 14) |
| App informational version | `0.13.0+1e96d5f5c48f036f0d3c5386cb03069275e59140` |
| App source | `1e96d5f5…` (= main at the #877 merge; this branch changes scripts/docs only) |
| Adapter/scripts source | `4fe59935…` (this branch; recorded separately because it differs from the App source) |
| Owned-adapter collector run | 86 supported / 1 unsupported / 168 inconclusive; `suiteComplete=false`; SGR scoring unchanged from stock definitions (derived adapter output, not unmodified stock CLI bytes) |
| Five-case collector outcomes | inconclusive (`consumed`) |
| First-batch grading suite | still 4/4 supported in the same run (independent rederivation preserved) |
| Geometry | 40 rows × 140 columns |
| Local evidence | `artifacts/terminfo/owned-qibNZN` (machine-readable copy with digests in the [JSON](2026-11-06-sgr-colors-qualification.json)) |

The collector run's single `unsupported` remains `extensions.osc5522-clipboard`
("Mode 5522 not supported", evidence `query`) — outside this batch and
unchanged by it.

## Preserved failures

One preparation attempt is retained with its original facts: the first run
aborted during evidence retention with `sgrColorsObserver is not defined` —
this batch's own runner scoping regression, fixed before the successful run.
No historical publication, receipt or archived-evidence rederivation changed.

## Reproduce

```sh
node --test scripts/terminfo/sgr-colors-*.test.mjs scripts/terminfo/sgr-colors-grading-collector.test.mjs
```

The publication test re-derives the whole graded report from the committed
journal and run log, re-checks the contract-grader agreement and observed
values, cross-references the collector run's unchanged five-case outcomes and verifies older
publications byte-for-byte. The full live measurement rerun requires macOS,
Node 24+, the pinned collector prepared from the checked-in lockfile, an
awake display and a disposable sidecar build ([QA_TERMINFO.md](../QA_TERMINFO.md)).

No VT parser, renderer or capability claim changed in this batch.
