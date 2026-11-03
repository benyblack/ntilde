# Pane metadata observation API foundation

The raw AgentHost `readPaneMetadata` method adds independent host/UI readback for
#768. It requires observe access and does not require act permission. It leaves
existing method responses unchanged. [Protocol and limits](../../agent-host/DIRECTION.md#raw-read-only-pane-metadata)
describe the actual control properties, raw anchor semantics and refusal bounds.

## Retained API integration smoke

A private macOS arm64 App at committed source
`ad414bbef583b3a14107300b76b98be615536238` was built through the repository wrapper
as a fresh uninstalled Release sidecar, with zero warnings/errors. The smoke ran
on Node `v24.22.1` with **Act disabled**, one source-selected worker and a separate
private settings/profile tree. App/worker/executable/user/TTY ownership was
re-proved before each read. The kernel and observed App grid agreed at **41 × 140**.

The API independently returned the controlled OSC title/cwd, complete four-cell
hyperlink run and identity, OSC 133 input-window/anchor facts, and stored progress
with its actual tab-text suffix. Subsequent observations verified progress
withdrawal and clearing of the fixture cells, links and input window. No URLs
were opened or native notification recipients used. App and worker exit were
verified; the private evidence tree was intentionally retained.

The complete local evidence is `artifacts/terminfo/metadata-api-AL69La`, with a
byte-verified complete copy in the primary checkout. It includes incremental
read responses, ownership records, exact worker/probe inputs, private settings,
build/probe logs and binary hashes. Build source is associated by the operator's
fresh build, not independently attested by those hashes.

The smoke also recorded an explicit existing limitation: **empty OSC 2 leaves
the previous title unchanged** at that source. This remains a qualification gap
for #768/#772; it is not counted as supported or hidden by the API smoke result.
The original title/cwd are not claimed restored: this integration fixture ends
by disposing its private App and worker.

This is API integration validation, not a completed owned metadata suite or a
v7 receipt. It does not measure painted pixels, occlusion, desktop timing,
OS-taskbar delivery, native notifications, hidden panes or Windows/Linux runtime.
The production collector adapter's read-only method list and all frozen reports
remain unchanged. A later #768 slice must add the owned suite, independent
controls, exact source/data bindings and cleanup receipts before making protocol
qualification claims. Stock grades remain 78 supported / 0 unsupported /
177 inconclusive, with `suiteComplete=false` and ungraded reverse-wrap.

## Regression authority

`AgentPaneMetadataCaptureTests` covers complete active-viewport link identity,
history/alternate separation, raw shell anchors, complete-grid/run/text limits
and refusal to wait behind a busy parser. `AgentHostMetadataProtocolTests` covers
read permission without acting, identity changes during an await, cancellation,
deadline and failure behavior. `MainWindowAgentMetadataTests` drives actual
parser-to-host title/cwd/link/progress/notification properties and startup wiring.
The startup-publication regression failed before the one-line fix, matching the
first real-API refusal, and passes afterward.
