# OSC 66 text sizing — 2026-11-01

The fresh owned macOS collection changes from **76 supported / 2 unsupported /
177 inconclusive** to **77 supported / 1 unsupported / 177 inconclusive**.
Only `extensions.osc66-text-sizing` changes outcome. The other 254 observations
retain their outcome and reason. This is a cursor-advance measurement, with
separate domain and shared-renderer pixel evidence below.

| Build | Source commit | Owned collection | Measured at (UTC) |
|---|---|---|---|
| Before | `52ed24cd4bb2eb8d69d747c6ba6ee063d372709e` | `owned-KlBHUy` | 12:31:08.706 |
| After | `836c81429517ce5dd124d200953e742fa79c7b21` | `owned-yVenFW` | 16:41:02.515 |

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
at 16:41:02.541 UTC.

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

Eighteen shared-renderer pixel tests cover enlarged ink across rows, fractional
font sizing/alignment, two small glyphs in one cell, compressed long text,
visible lower fragments whose anchor is in history, cached-row refresh, italic baseline anchoring, and plain/styled underline policy.
The full VT suite passes 1,472 tests. The earlier complete App gating rerun passed
7,552 tests (56 platform/environment skips), and the MCP suite passes 223. Separate App replay (30), render-metrics
(40), shared golden PNG (8), mux (1,123 with 20 platform skips), architecture
(178), and platform-boot (75 with 4 font-platform skips) checks pass locally.
The [VT matrix](../../vt_coverage_matrix.md) records implementation scope.

The follow-up fixes two Codex findings: sized ANSI exports reset the column with
CRLF independently of the receiving terminal's LNM mode, and complete replay
checkpoints retain block metadata only in their complete state. Buffer-only
checkpoints retain the optional legacy field. Both regressions fail before the
fix and pass afterward. The comparison retains both earlier after-runs separately.

The CodeRabbit follow-up covers DECALN cleanup, cursor-adjacent combining text,
ordinary-cell attachment and split regional indicators, leading-mark fallback,
full-width detected URL hit areas, strict alternate-screen import geometry, and
checkpoint restoration under metadata pressure. New regressions reproduced eight
domain failures and two pixel failures before the fixes. Live repeated budget
refusals now reuse pruning until eviction/reset or screen replacement changes
retention; the allocation regression drops from more than 5 MiB over 20 refusals
to below its 128 KiB ceiling. A separate valid, non-overlapping import fixture
reaches the summed storage guard rather than the block-count guard. Legacy
checkpoints retire only enough old rectangles to restore the visible checkpoint;
without pressure, retained history remains. Six real TerminalView hover/click
checks cover the beginning, middle and end of both rows of a sized URL block.

The second review follow-up makes history reclamation independent of the active
screen plane. A saturated-history alternate-screen checkpoint exports and imports
again within the aggregate budget; this regression failed before the correction.
ANSI export bootstraps clipped anchors with bounded scrolling before its viewport
walk, including anchors evicted from history. Two mixed-height round trips failed
before the fix and now preserve both fragments. Lower-row URL extraction maps to
the same payload and physical width; three real view cases failed before the fix.
All 124 focused App sizing, link, replay and render-metrics tests pass after the
subsequent review fixes, with 8 shared goldens passing in a separate process.

The next Codex review found invalid main-history blocks surviving an alternate-
screen height resize. Main-history cleanup now retires the whole rectangle in
its own plane. Two height cases failed before the fix; eight invalid resize cases
across both active screens and two retained-fit controls now pass.

The next review batch fixes compressed fractional alignment, full-block styled
underlines, placement below short scroll margins, and ordinary-base combining
next to a sized anchor. History mutations now advance a dedicated revision in
both live and isolated paged snapshots so App picture caches cannot retain old
ink. Six domain failures and six pixel failures were reproduced before the fixes.
The history pixel fixture uses a nonzero history row identity and compares the
cached draw path with an uncached reference. Ordinary writes with retained sized
history no longer allocate an empty erase dictionary for every grapheme: the
regression previously measured 104 KB versus a 24 KB baseline per 1,000 writes;
the fixed path stays within 16 KiB of the baseline.

The following source inspection added a width-expanding VS16 attachment case.
Before its continuation is written, any intersecting sizing rectangle is retired
whole. Its regression failed before the fix and now verifies packed continuation
content, all removed pieces, cursor position and a successful state round trip.
Inactive-plane retirement now uses neutral default cells instead of the active
other screen's pen. Four colored-pen resize cases failed before that correction;
the full VT suite and 124 focused App tests pass afterward.

The next Codex review extends admission and height fitting to cursors above
short scroll regions, resolves combining attachments from inside rectangles, and
retires old spanning rectangles before legacy viewport replacement. Automatic
width now tracks presentation selectors while explicit widths remain fixed.
Replacement admission reserves against the old block before mutation; drawing
keeps its original colors and side metadata, and wrap/scroll logic updates the
whole rectangle. History and clipped-anchor fragments use the same replacement
model. Ten initial regressions failed before the fixes. Additional tests cover
full-screen and region-bottom scrolling, collision retirement, style retention,
VS15/VS16 chunk boundaries, joined emoji, and refusal under a saturated budget
with byte-identical exported state. A stale joiner from an unrelated cell also
failed its regression before correction. First-column combining and ZWJ continuation tests cover both top and lower rows;
the two joiner cases failed before their correction. All 27 attachment cases
and the saturated-budget replacement case pass. The ordinary default fast path remains
for non-emoji output; emoji sent through OSC 66 retains metadata for later selectors.

The next source review fixes wrap metadata when an attachment relocates. The
vacated source row becomes a soft wrap with padding after the authored prefix;
conservative written extents before the old rectangle preserve possibly authored
blanks. Complete and incremental input now agree on wrap metadata and a search
spanning the prefix and payload, including when the source row scrolls into
history and when the prefix ends in authored spaces. Ordinary wide glyphs
intersecting either horizontal edge of initial or replacement sizing footprints
are retired whole, including text/link/underline metadata, in viewport and history.
Eight initial boundary regressions failed before these fixes; ten boundary cases
and the full VT suite pass afterward. All 124 focused App checks pass.

The insert-mode follow-up shifts all affected rows before final footprint
cleanup. Intact styled/linked ordinary wide glyphs and single-row sizing blocks
move with their metadata; only glyphs split at the insertion or source-clipping
boundary are retired. Ordinary inserted text also preserves movable sizing
blocks. Insert-mode reservations conservatively avoid crediting blocks that may
remain after the shift. Seven added cases cover these paths and refusal with
byte-identical state at the metadata limit. Four initial insert cases, the
budget case, and the ordinary-output case failed before their fixes.

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
