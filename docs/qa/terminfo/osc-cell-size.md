# Exact OSC cell-size reports (#993)

This contract keeps `extensions.osc1337-cellsize` and
`extensions.osc776-cell-size` separate. CSI 16 t is a delivery/geometry control,
never evidence that an OSC spelling is supported. The live publication is still
pending; deterministic tests alone do not establish runtime qualification.

## Protocol definitions

The [iTerm2 specification](https://iterm2.com/documentation-escape-codes.html)
defines `OSC 1337 ; ReportCellSize ST`. The reply orders **height, width** in
logical points, optionally followed by the physical-pixels/logical-points scale.
Ntilde emits the modern three-field reply with invariant decimal formatting and
ST, using the direct view's cell dimensions transformed by interface zoom into
window logical points, and the actual top-level backing scale. Interface zoom
is part of the reported point dimensions, rather than the backing scale. The
raw view metrics in Avalonia DIPs and the effective render scale are retained
separately from both point and physical-pixel dimensions.

The [urxvt reference](http://pod.tst.eu/http://cvs.schmorp.de/rxvt-unicode/doc/rxvt.7.pod#XTerm_Operating_System_Commands)
and its [primary implementation](http://cvs.schmorp.de/rxvt-unicode/src/command.C?view=markup)
define `OSC 776 ; ? ST`: the common `query` guard requires the payload to be
exactly `?`, and `URxvt_cellinfo` replies with **width, height, font ascent** in
pixels. Its `st.v` preserves the request terminator. Bare `OSC 776 BEL` does not
ask this query. Ntilde follows those units/order and mirrors BEL/ST. Its integer
policy rounds a measured physical dimension to the nearest pixel, halfway up;
font ascent comes from the font metric, rather than guessed cell height or
rendered baseline padding.

The primary urxvt source read on 2026-11-16 has SHA-256
`4ed7b743aaddc38a5c441b2899d089a0a5b420645e67c153792e5a4a0ed250c6`;
the reference POD has SHA-256
`59922e31deeebca1d0f00feee9c84e30fa384e8a8097704ea841f0df557680b1`.
These are retrieved-byte identities, not an assertion that an unversioned URL
will always serve those bytes.

## Frozen benchmark criteria

The inspected `terminfo.dev@4.0.0` bundle has SHA-256
`aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6`
and probe hash `16a690f34c0e`. Both registrations use `oscSimpleQueryProbe`,
`oscReplyResult` and query-only paths. The termless path calls `feedCapture`;
the terminal path calls `queryWithSentinelOutcome`. The new adapter appends
exports and pins both helpers and registrations byte-for-byte.

| Benchmark ID | Exact pinned request | Pinned positive criterion | Documented difference |
|---|---|---|---|
| `extensions.osc1337-cellsize` | `ESC ] 1337 ; ReportCellSize BEL` | Complete reply with exactly two positive decimal dimensions | A modern third scale field makes this pinned pattern fail; the frozen verdict remains inconclusive (`invalid-reply`). |
| `extensions.osc776-cell-size` | `ESC ] 776 BEL` | Complete reply with three unsigned integers | The request omits the documented `;?`; valid ordered silence remains inconclusive (`no-response`). |

Neither unchanged pinned helper can return unsupported: a nonmatching or absent
reply is inconclusive. No production behavior is changed to satisfy these
criteria. Stock outcomes remain the stock collector's actual outcomes; frozen
Core/Modern requirements and common-harness comparisons are separate.

## Independent effect contract and controls

`ntilde-osc-cell-size-effect-v1` uses a disposable direct-local pane at fixed
geometry. It seeds an attributed alternate-screen fixture once, then performs
19 independently fenced exchanges across the two cases. Each case includes a
CSI 16 t positive control before and after the target requests; its exact pinned
request; the documented request with BEL and ST; and unsupported forms such as
incoming replies, trailing fields, empty/doubled question marks and wrong case.

The parent observes complete stored cells, attributes, cursor, margins, modes,
palette bindings and history. Its read-only `readPaneMetadata` projection also
records actual view rows/columns, logical cell width/height, font ascent and
effective render scaling and top-level backing scale independently of parser replies. Every exchange must
preserve the seeded state, and all observations must have identical valid
geometry. No resizing, synthetic pointer events or reconstruction writes are
needed to ask a size query.

Raw reply count, order and framing are retained beside their own completed DA
barrier. A complete barrier establishes ordered absence; a timeout, stale reply,
partial frame or unexpected input aborts collection. Failed delivery controls
make the effect inconclusive. A conclusive protocol mismatch requires valid
controls. The complete parent/worker journal, fixed request schedule, unchanged
state, nonce binding and final restoration are independently rederived before
the shared receipt admits a verdict.

The App supplies one immutable host metric record to VT. Absent or invalid facts
produce no fabricated reply. Mux, replay and headless hosts without these facts
are outside this implementation's positive claim. The new metadata projection
is an observation capability, not terminal support itself. The source snapshot
includes selected App/VT reference bytes and exact executable/assembly hashes;
it is not complete binary-to-source or reproducible-build attestation.
