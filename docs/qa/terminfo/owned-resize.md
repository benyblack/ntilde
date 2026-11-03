# Owned resize and reflow observations

`ntilde-owned-resize-reflow-v1` follows the owned history suite in the disposable
macOS runner. It measures three ASCII hard-line cases using the real App window,
its VT buffer and the worker's independently verified kernel PTY:

| Case | Smaller grid | What is compared |
| --- | --- | --- |
| Width round trip | Seven fewer columns | One hard line rewraps and its cursor maps to the continuation; restoring width rejoins it. |
| Height/history round trip | Four fewer rows | A full viewport of short hard lines displaces four ordered rows into history. |
| Combined round trip | Seven fewer columns and four fewer rows | Full-viewport long hard lines rewrap through history; restoring geometry remaps the visible logical-line anchor. |

Every case has a complete before, smaller-grid and restored-geometry state.
Both screen and complete bounded history are read twice independently per frame.
There are twelve paired frames, including the initial state, nonce binding and
final fixture reset, and six parent-selected App actions. Stored cell details,
all exposed buffer modes, cursor state and actual ordered history remain in the
raw journal. The expectations are derived from the checked-in input plan.

The input writes no space padding into unused row tails. It distinguishes
unwritten cells from authored content. Initial grids must have 12–52 rows,
55–256 columns and at most 8,192 cells. The complete displaced history stays
within the 64-row read budget. Unsupported initial geometry and the App's typed
screen-details refusal are retained as initial unavailability, with no fixture
writes or positive measurement. A later failure cannot become unavailability.

Ntilde preserves the current viewport's top anchor when growing. Geometry
restoration therefore does not pull retained history back into the viewport.
The independent restored-state expectation maps the known first visible
logical-line offset into the original width, retains the corresponding ordered
history and adds blank viewport padding. All original hard lines must remain
intact and ordered across history plus viewport. This follows the existing
`VerticalGrow_ShouldNotPullHistory_AndAddPadding` buffer contract; these probes
do not change VT behavior.

## Action and observation boundary

The runner enables the existing default-off Agent Access act permission only
in its newly created private settings root. It keeps one local worker profile,
persistence off, updates off and clipboard writes disabled. Normal user settings
and sessions are not used. The ordinary `agentRequest` observer allowlist remains
read-only; a separate `ownedResizeRequest` sends only `resizePane`.

The worker can request the next known case/stage, never dimensions or another
pane. The parent selects dimensions from its own source plan and proves the
exact App/worker/executable/user/TTY relationship before and after the action.
The App must report measured geometry with `layoutSettled` and `exact` true.
The parent then runs the explicit system `/bin/stty` against that same proved
PTY and checks both geometry and disabled output translation. An App result alone
does not attest kernel geometry. A partial result is retained and cannot advance
the plan.

The App's two-second timeout assumes a responsive UI. The parent independently
bounds its transport and process queries; the existing disposable-process
cleanup remains the final boundary if the UI stalls. Each successful action is
followed by a DA ordering fence before either observer captures state.

## Receipt and cleanup

A v6 receipt requires all ten preceding suite bindings plus the resize binding,
exact retained source copies/hashes, hashed initial settings, twelve frames and
six matching parent/kernel action records. The report is rederived from both
journals. A parent resize declaration, source/context marker or retained resize
stream prevents downgrade to an older receipt by stripping the new binding.

Every normal case restores original geometry; final cleanup resets the private
main screen and history and compares the complete initial state. On failure,
only original geometry may be requested as an abort, then bounded output cleanup
is attempted; failures preserve the original error and cleanup errors. An abort
cannot produce a successful resize receipt. Output flags are restored before the
stock collector runs. The existing worker-exit, repeated-signal and fixture-removal
checks remain mandatory.

These observations qualify the stated ASCII geometry/history policy. They do
not qualify Unicode reflow, history attributes, images, pixels, complete saved
banks, other platforms, or desktop timing. Original stock grades remain
78 supported / 0 unsupported / 177 inconclusive, with `suiteComplete: false`.
