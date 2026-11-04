# Historical source scopes before inventory growth

The live measured Node input graph and a published receipt's historical graph
have different lifetimes. Adding a suite must not reinterpret an older receipt
using the new input inventory or source-snapshot policy.

The two existing v8 forms keep fixed scopes:

| v8 form | Measured input paths | Policy |
| --- | --- | --- |
| Original metadata run (`dc32fc8e`) | 51, including `terminfo-owned.mjs` | Original pre-snapshot execution scope |
| Captured-source run (`1f4403f2`) | 52, with `terminfo-owned-runner.mjs` and `owned-source-inventory.json` | `readonly-owned-source-graph-v1` |

`legacyV8SourceFiles` remains the explicit historical 51-path map.
`capturedV8SourceFiles` is its fixed captured-era variant: one runner-path override
and the inventory input. `capturedV8SourcePolicy` is the immutable v1 identity.
The v8 receipt validator and published-data guard use these historical constants;
only live capture/unchanged-source checks use `ownedSourceFiles` and the active
`sourceSnapshotPolicy`.

Current live capture still has the same 52 inputs and v1 policy. This change adds
no suite, measurement or grade. A larger future graph must introduce an explicit
new policy/receipt path. A coherent expanded inventory cannot silently obtain a
v8 receipt under v1. New source/plan versions must preserve the old builders and
receipt branches rather than silently append stages or redefine published counts.

## Validation

The future-growth regressions copy the current validator, independently grow its
active inventory and change its active policy, then validate a complete historical
52-input synthetic receipt through the full thirteen-suite finalizer. All three
cases fail with the previous dynamic v8 lookup and pass with the fixed scope.
They also compare the fixed map to the actual published archive manifest. A fourth
case rejects a coherently rehashed 53-input graph that retains the old v1 policy.
These synthetic cases do not replace runtime qualification.

Both frozen archives were separately extracted into private temporary copies.
The current full v8 finalizer reproduced every original receipt field except its
new completion timestamp for both datasets. The source files, reports, archived
bytes and historical grades were not rewritten. The portable CI Node command
runs the new regressions on both operating-system lanes.

The original 51-input run retains its static-import timing limitation. The newer
run retains its pre-import capture and cleanup guarantees. Neither gains a new
binary/source attestation claim from this compatibility fix.
