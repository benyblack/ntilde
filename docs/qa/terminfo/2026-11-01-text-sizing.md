# OSC 66 text sizing — 2026-11-01

The fresh owned macOS collection changes from **76 supported / 2 unsupported /
177 inconclusive** to **77 supported / 1 unsupported / 177 inconclusive**.
Only `extensions.osc66-text-sizing` changes outcome. The other 254 observations
retain their outcome and reason. This is a cursor-advance measurement, with
separate domain and shared-renderer pixel evidence below.

| Build | Source commit | Owned collection | Measured at (UTC) |
|---|---|---|---|
| Before | `52ed24cd4bb2eb8d69d747c6ba6ee063d372709e` | `owned-KlBHUy` | 12:31:08.706 |
| After | `6843c998837f402041144752a906f723fa66db5a` | `owned-DPo1I6` | 13:37:52.060 |

Both builds are fresh uninstalled sidecars outside `.app` bundles. Their DLL
informational versions contain the full source commit. The selected assembly
hashes, raw hashes, collector and owner run IDs, input/source pins and ordered
traces are in the [derived comparison](2026-11-01-text-sizing-comparison.json).
Raw collector identity remains `unverified`; operator-supplied version metadata
is not an independent terminal identity check. Archives remain under the ignored
`artifacts/terminfo/` directory in this worktree, including retained collector
source and ownership/cleanup proof. No personal clipboard was read.

Both runs use the same pinned collector revision, suite/probe hash, adapter,
manifest/lock, runner sources and Node v24.22.1. Raw SHA-256 values were recomputed;
both ownership proofs record completion and cleanup. The after cleanup completed
at 13:37:52.087 UTC.

## Observed behavior

The collector clears the line and requests CPR, writes `OSC 66;w=2; <BEL>` and
requests CPR, then writes `OSC 66;s=2; <BEL>` and requests CPR again.

| CPR | Before | After |
|---|---|---|
| Initial | `CSI 1;1 R` | `CSI 1;1 R` |
| After width 2 | `CSI 1;1 R` | `CSI 1;3 R` |
| After scale 2 | `CSI 1;1 R` | `CSI 1;5 R` |

This establishes width and scale cursor advance. The collector explicitly says
that glyph appearance needs a visual check; its result alone is not pixel proof.

## Implementation and separate evidence

The [Kitty text-sizing specification](https://sw.kovidgoyal.net/kitty/text-sizing-protocol/)
defines the protocol. VT owns immutable block text/options and rectangular cell geometry in optional
row side tables; packed cells remain 12 bytes. Payloads are strict safe Unicode,
at most 4096 UTF-8 bytes, with at most 256 metadata characters. An additional
16 MiB retained-metadata budget rejects new blocks before cursor/scroll mutation.
Explicit width packs the payload into one block; automatic width scales each
grapheme. Fractions scale the font once and leave occupied geometry unchanged.

The OSC 66 tests cover negotiation behavior, payload boundaries and malformed
metadata; overwrite/erase, shifts and line movement; combining text and history
fragments; physical resize barriers; JSON state transfer and legacy/current
replay; metadata budgets; ANSI export and bounded text-client fallback. Initial
probe tests failed before implementation. A fractional-pair pixel regression
also failed with double shrinking and passes with font scaling applied once.

Six shared-renderer pixel tests cover enlarged ink across rows, fractional
font sizing/alignment, two small glyphs in one cell, compressed long text,
visible lower fragments whose anchor is in history, and cached-row refresh.
The full VT suite passes 1,395 tests. Separate App replay (30), render-metrics
(40), shared golden PNG (8), mux (1,123 with 20 platform skips), architecture
(178), and platform-boot (75 with 4 font-platform skips) checks pass locally.
The [VT matrix](../../vt_coverage_matrix.md) records implementation scope.

## Limits

Sized rows remain fixed physical layout barriers during resize; ordinary lines
outside them reflow. A block clipped by a new width or too tall for the new
screen is discarded whole. Cursor drawing keeps its ordinary cell shape.
Unnegotiated mux text clients use a bounded normal-size fallback. Search matches
map to the full width on the block's top row; selecting only lower continuation
cells does not copy the anchor payload. This does not claim exact font-pixel
parity with Kitty or exhaustive native-platform qualification.

OSC 5522 is the one remaining unsupported observation. Inconclusive reasons
remain 152 insufficient-evidence, 22 no-response and 3 policy-refused. One
ungraded reverse-wrap geometry diagnostic remains and `suiteComplete` is false.
No other terminal binary was measured, and these counts are not an overall
terminal compatibility score or ranking.
