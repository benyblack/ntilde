# ASCII-text and SGR benchmark qualification — 2026-11-05

Four pinned terminfo.dev Core cases — `text.basic`, `text.overwrite`,
`sgr.bold` and `sgr.italic` — measured **supported** against a fresh
disposable macOS desktop instance, each conclusion derived from the pinned
collector's own criteria replayed byte-exactly and graded from
parent-journaled cell/flag observations, bound to a completed schema-13
cleanup receipt.

This is a **local collector-extension measurement**, not stock CLI output.
The unchanged stock run of the same instance retains its identity and grades
all four cases inconclusive — exactly as the pinned terminal paths demand
("Cursor advance does not verify SGR styling"; the text paths' cursor query
"does not measure rendered Hello text" / "overwritten B cell").

## What each conclusion establishes

| Case | Pinned criterion (termless path) | Observed (zero-based cells) | Conclusion |
|---|---|---|---|
| `text.basic` | "Hello appears after a measured empty control" | Empty control row → `Hello`, cursor `(0,5)` | **supported** |
| `text.overwrite` | "C overwrites A while B remains" | Control `A,B` → `C,B` after `CSI 1 G C` | **supported** |
| `sgr.bold` | stored `bold` flag on the rendered `X` cell | `X`, flags `0x301` (default pen + bold) | **supported** |
| `sgr.italic` | stored `italic` flag on the rendered `X` cell | `X`, flags `0x304` (default pen + italic) | **supported** |

The bold/italic conclusions cover the **stored cell flags** — the only pass
criterion the pinned scorer defines. They do not claim glyph appearance: the
measured pixel comparison for bold remains the separate scope of
[bold-glyphs.md](bold-glyphs.md), including its earlier retained negative and
the later deterministic source regressions.

## Evidence chain

The worker replayed each case's exact pinned fixture bytes through the owned
fence (every write followed by a DA ordering barrier), read the resulting
cells and attribute runs through the parent-journaled `readScreen` observer
(nonce-bound pane, geometry checked per read), and executed the pinned
probe's own termless code over those observed screens. An independent
contract grader (`ascii-sgr-contracts.mjs`, versioned v1) and a second
parent-side replay of the pinned code over the retained journal must both
agree before any outcome is published. Nine frames — start, nonce bind, six
read groups, finish — are retained with hashes; the start and finish frames
must match exactly (alternate-screen restoration).

`unsupported` requires a measured mismatch **behind a valid control**; the
pinned controls ("Control already contained Hello", "AB control was not
measured", "No rendered X cell to evaluate") keep those failures
inconclusive. Wrong input bytes, missing captures, changed observations,
mismatched run identities and incomplete restoration each prevent any
conclusive grade (covered by CI regressions).

## Collector extension identity

| Measure | Value |
|---|---|
| Grading adapter | `ntilde-owned-ascii-sgr-grading-v1` (contracts v1) |
| Derived from | owned adapter `ntilde-owned-macos-v1` over pinned `terminfo.dev@4.0.0` |
| Pinned bundle SHA-256 | `aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6` |
| Grading bundle SHA-256 | `f9558aa71b6c06b19a01d0883becbe536dc4e5ffb1ee843c5faa42b12559f90c` |
| Receipt schema | 13; graded binding provisional until the shared cleanup receipt |

The derivation refuses unless the pinned bundle still contains the exact four
probe definitions; the upstream bundle and the owned adapter stay
byte-identical underneath the appended exports.

## Measurement

| Measure | Value |
|---|---|
| Run / receipt completed | run `b31dae14c2025b7154f072627168cc48`, receipt 2026-11-05T18:35:42.864Z |
| App informational version | `0.13.0+21393245f58b7faeb980d3103a049f60f3cae79b` |
| App source | `21393245f58b7faeb980d3103a049f60f3cae79b` (= origin/main; this branch changes scripts/docs only) |
| Stock run (untouched) | 86 supported / 1 unsupported / 168 inconclusive; `suiteComplete=false` |
| Stock four-case outcomes | inconclusive (`query` for the text cases, `consumed` for SGR) |
| Geometry | 40 rows × 140 columns |
| Local evidence | `artifacts/terminfo/owned-dgb3Fk` (machine-readable copy with digests in the [JSON](2026-11-05-ascii-sgr-qualification.json)) |

The stock run's single `unsupported` is `extensions.osc5522-clipboard`
("Mode 5522 not supported", evidence `query`) — outside this batch's four
cases and unchanged by it.

## Preserved failures and the observer fix

Five preparation attempts are retained in the machine-readable evidence: one
display-sleep abort (Avalonia `RenderTimer` `-6661`), two
private-clipboard observation failures after wide stock probes, and two
retention failures of this batch's own making (`pathToFileURL is not
defined`, surfacing under the private-clipboard summary, and
`gradingEvidence is not defined`) — both runner regressions fixed before the
successful run.

The private-clipboard observation failure is **pre-existing on current
main**: a pristine `21393245` worktree failed identically (the legacy
`attributeLines` projection omits wide continuation columns left by the
private stock prefix). The batch therefore includes, explicitly and with
attribution, the observer fix `8ffc6d1997b2b283b2383c52c0fd41900d8e1f5a`
from `codex/terminfo-benchmark-refresh` (cherry-pick `413a95a6`): not
because the four ASCII/SGR cases need it — they do not — but because the
shared owned pipeline cannot complete its cleanup receipt without it. A
pristine `21393245` plus that fix completes the full pipeline, which
isolates the remaining behavior difference to this batch's own (fixed)
runner defects.

## Reproduce

```sh
node --test scripts/terminfo/ascii-sgr-*.test.mjs scripts/terminfo/grading-collector.test.mjs
```

The publication test re-derives the whole graded report from the committed
journal and run log, re-checks the contract-grader agreement, cross-references
the untouched stock outcomes and verifies older publications byte-for-byte.
The full live measurement rerun requires macOS, Node 24+, the pinned
collector, an awake display and a disposable sidecar build
([QA_TERMINFO.md](../QA_TERMINFO.md)).

No VT parser, renderer or capability claim changed in this batch.
