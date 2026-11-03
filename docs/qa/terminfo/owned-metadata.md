# Owned pane metadata qualification

The disposable runner adds a separate `ntilde-owned-pane-metadata-v1` suite after
saved-state restoration. It uses the App-owned [metadata API](owned-pane-metadata-api.md),
complete active cells/attributes/history, and independently read host status.
Stock collector grades and historical reports remain unchanged.

## Fixed experiments

Thirteen source-known experiments retain 43 stages and 46 paired snapshots:

| Experiment | Independent observation | Scope |
| --- | --- | --- |
| OSC 2 title | Raw title, display title and actual tab text | Includes a separate explicit empty-title request |
| OSC 0 title | Same three observations | Icon-name component is unqualified |
| OSC 7 cwd | Exact decoded host path | Includes an encoded space; no filesystem-existence claim |
| OSC 8 hyperlink | Exact viewport run, URI, id and snapshot-local identity | Closed-link `x` witness and complete-cell comparison; no URL opening |
| OSC 133 A/B/C/D | Precise status, input window and raw input/output anchors | Command text on C; D process exit code is not interpreted as command exit |
| OSC 133 P stock probe | Exact `P;Cwd=/tmp` input and retained host state | Inconclusive; consumed bytes do not prove a cwd or prompt-part property |
| OSC 133 P prompt-part probe | Exact `P;k=r` input and retained host state | Inconclusive without independent prompt-part boundary readback |
| OSC 9 normal/error/paused/indeterminate progress | Stored kind/percent/owner and actual horizontal tab suffix | Four experiments plus independent withdrawal controls; no OS taskbar claim |
| OSC 9 notification | Actual private in-window toast title/message/visibility and auto-hide | Observed opt-in and inactive-window preconditions; focused delivery remains inconclusive |

The P prompt-part distinction follows the [Contour OSC 133 description](https://contour-terminal.org/vt-extensions/osc-133-shell-integration/).
The pinned collector's `P;Cwd` consumed-byte probe is retained as a probe
limitation, without inventing a cwd property for this protocol.

## Ownership and restoration

Only the source-selected direct worker of the private App can request an
observation. The parent re-proves App/worker/user/executable/TTY/run ownership
before each request; the endpoint belongs to the private app-data tree. The
transport allowlist adds only `readPaneMetadata` and `getSessionStatus`, both
read-only. No production/personal pane, clipboard or external notification
recipient participates.

Each snapshot retains two complete viewport/host/status observations. Stable
facts must agree; raw timestamp-bearing status responses are retained separately
and validated. Exact pane/grid/cursor/history facts must agree across components.
The suite has a 64-snapshot ceiling, complete-cell and metadata payload limits,
fixed ordering, nonce binding and a bounded six-second-toast auto-hide observation.
UI and VT components remain separately observed, without a global atomicity claim.

The first snapshot retains the unprepared initial metadata. Fixed writes then
establish an independently checked title/cwd baseline and main-screen nonce.
The suite restores that **prepared** title/cwd baseline, withdraws progress,
clears links/shell anchors, observes toast hiding and restores the complete
initial cell state. It does not claim original null title/cwd or hidden control
contents restored. App/worker exit and removal of the private fixture are
mandatory completion conditions. The exact raw initial/final properties remain
in evidence; a callback or cleanup method invocation cannot replace them.

## Receipt and tests

Version 8 requires all thirteen suite bindings. It rederives metadata inputs,
controls, results and counts from the complete paired journal and checks exact
retained report/source bytes, private settings digest, common pane/kernel geometry
and completed cleanup. Any metadata context field, source copy, parent marker,
journal or either report forces this chain. Contradictory available/unavailable
reports and attempts to omit the metadata binding are rejected. Only an initial
typed refusal before fixture writes can become an unavailable record.

Portable tests cover wire bounds, fixed bytes, pairing, actual host-effect checks,
ordering, ownership binding, restoration, counts, source/report/journal corruption,
contradictory reports and attempted receipt downgrades. Synthetic fixtures are
explicitly test-only. A deliberate removal of the cwd comparison makes the cwd
regression fail; this is a test-sensitivity check, not runtime qualification.

A committed-source real-App measurement and its complete retained/public evidence
must accompany qualification claims. This implementation document makes none.
