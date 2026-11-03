# Owned history and scrolling observations

`ntilde-owned-history-scrolling-v1` follows the nine earlier owned suites.
Thirty bounded ASCII cases retain 63 paired logical frames, with independent
64-attempt observers. Each capture reads the actual complete history from the
proved local pane: at most 64 rows, starting at zero, with a matching reported
count. The fixture expects no more than four saved rows. Windowless or missing
history facts cannot qualify a local observation.

Cases cover LF, CRLF and IND accumulation at the bottom of the main screen;
omitted/zero/two-line SU and SD on main and alternate screens; partial-margin
SU/SD; reverse index at full-screen/margin tops and inside/outside the region;
DECSTBM set/default/zero reset, reversed/equal invalid margins, and origin-relative
homing. Known padded ASCII rows surround each target. Full viewport cells,
relevant attributes, cursor, margins/modes and complete ordered history must match
independent expected inputs. Invalid-region cases start with the cursor already
home and qualify rejection of the margins only, without asserting unspecified
invalid-command cursor behavior.

Main full-screen upward scrolling must append the known top rows in order.
Alternate-screen and partial-margin scrolling must preserve main history.
Downward scrolling and reverse index discard the bottom row rather than adding
history. LF accumulation starts from independently observed empty history;
other cases retain two known history lines as controls. The already qualified
ED2/ED3 operations clear only the disposable private fixture during setup and
restoration. Failed controls are inconclusive; a qualified mismatch fails the
local comparison. No outcome changes a stock collector grade.

The source expectations follow the [VT100 programmer manual](https://vt100.net/docs/vt100-ug/chapter3.html)
and [xterm control sequences](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html).
Resize/reflow, saved-bank completeness, history attributes, Unicode editing,
rendered pixels and Windows/Linux runtime coverage remain separate measurements.
This history slice alone does not complete issue #766.

## Untranslated private PTY output

Node raw input mode alone retains output processing on macOS: writing a bare
LF produces CRLF at the PTY master. That transformation would invalidate a
bare-LF fixture before the bytes reached Ntilde. After the existing ownership
handshake proves the worker's exact PTY, the worker therefore saves its mode
using the absolute `/bin/stty`, disables output processing with `-opost`, and
verifies the result. The parent independently re-reads the flags under the
existing shared process-query deadline before accepting any owned cell writes.
The ownership proof retains the observed flags, tool and timestamp. No personal
terminal or unrelated session is selected. Ordinary Node raw-input handling
and every fence deadline remain unchanged.

All owned suites run with output translation disabled. A finally block restores
the exact saved mode, then disposes the input fence, before the stock collector
runs. Setup failures attempt restoration too; restoration failures stay fatal.
A deterministic private-PTY test demonstrates the original CRLF transformation,
controlled LF delivery and exact saved-mode restoration. Portable tests cover
missing/contradictory flags, invalid paths, tool failures and restoration failure.
The collector does not claim to attest system-tool bytes or the host kernel.

## Retained evidence

`history-snapshots.jsonl` and `history-observations.json` retain the known inputs,
all paired state/history reads, hashes and comparisons. The final main screen,
cursor, modes and actual history must exactly equal the pristine initial state.
Fatal fences permit bounded teardown output without qualifying completion.
Only an initial typed `screenDetailsUnavailable` before fixture writes may
produce an unavailable marker; missing facts and later refusals remain fatal.

Receipt v5 re-derives history evidence, compares its journal and counts, requires
the parent-observed output-mode proof and preserves all nine earlier bindings.
Context identities, supplied evidence, the output policy, archived v5 source
markers or a retained output-mode proof trigger mandatory validation, so stripping
an identity and binding cannot silently downgrade the receipt. The independently
recorded kernel geometry must match the bounded App snapshot geometry. Finalization still
requires verified worker/app/socket shutdown and fixture removal. Sources and
exact dependency inputs remain archived and hashed. Published historical v1-v4
reports keep their original grades and remain exactly reproducible.

Stock results remain 78 supported, 177 inconclusive, zero unsupported,
`suiteComplete=false`, with an ungraded reverse-wrap geometry diagnostic.
The earlier explicit-RGB bold pixel negative remains recorded for #772.

The [2026-11-03 committed-source measurement](2026-11-03-history.md) retains all
30 passing comparisons, the complete report and parent-observed output flags.
