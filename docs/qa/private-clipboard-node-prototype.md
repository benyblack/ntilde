# Private clipboard Node prototype (#774)

This is an unintegrated, new-file prototype over the App foundation merged in
#820. None of the existing worker, adapter, runtime, source inventory, receipt,
CI or published measurement files imports it. The original issue remains open.
It qualifies no real terminal, native clipboard, permission, child delivery or
process/PTY ownership. Its tests use synthetic memory and temporary local sockets;
no App, GUI, OS clipboard, native peer or Node clipboard package is launched.

## Bounded interfaces

`private-clipboard-request.mjs` sends only the leaf `clipboardFixture` protocol,
with validated run/pane/fixture IDs, six exact operations, and canonical 51-byte
synthetic nonces. One request is half-closed on the write side. The server's
existing read-line loop answers, observes EOF and closes the read side. The
client accepts exactly one complete, correlated EOF-terminated frame, at most
4096 bytes, within a caller-supplied 1–4000 ms deadline. This catches a duplicate
frame arriving in a later chunk, invalid UTF-8, truncation and missing EOF.
Timeout/disconnect remains indeterminate and must never trigger a replay. This
transport has synthetic Unix-socket coverage locally; actual App transport and
Windows named-pipe half-close behavior remain integration work.

`PrivateClipboardController` requires an injected `rpc(request, timeout)` and
`verifyBinding(timeout)` plus remaining-budget function. Verification must prove
actual independently controlled process/executable/source/worker/PTY/pane/session
identity. It returns a stable lowercase SHA-256 equality witness; neither this
witness nor any App result is itself ownership attestation. The verifier must be
bounded under the same caller deadline. The controller checks it before and after
each operation and retains validated App facts if a later check fails.

The three exact IDs are accepted once in their pinned tail order:
`extensions.osc52-clipboard`, `extensions.osc52-write`, `extensions.osc52-read`.
`transaction(id, work)` begins lazily on the first callback, so earlier RIS probes
do not consume the one process fixture. It restores and independently reads the
fixed seed, then supplies a callback-lifetime fixture with `beforeOsc52Write`,
`readText`, and `writeText`. Each call must be awaited; overlapping, stale or
fire-and-forget fixture calls fail closed. A pending operation settles before
restoration, and its handle cannot act in a later callback.

The expected operation sequences are arm/read/seed, arm/read, and seed. Accepted
OSC52 write deltas are 1/1/0, with two independently verified seed controls. Every
callback restores the fixed `terminfo-private-seed-<runId>` value and independently
reads its UTF-8 SHA-256 before returning its original result. Restore cancels a
pending arm; it does not close or reactivate anything. A failed restore/read is
not retried by cleanup. A failed callback still attempts distinct restoration if
its earlier operation was not itself a failed restoration. Original and cleanup
errors are retained together. An unknown/malformed response value is rejected
before journaling; only admitted source-known values enter this private evidence.
Ordinary App logs remain unchanged.

Any refusal, interrupted callback, identity/hash/count mismatch or ambiguous
transport result sets a sticky fatal state. A collector that catches callback
exceptions cannot convert that into completion. Only explicit `finish()` after all
three callbacks and exactly two observed writes may independently read the fixed
seed and close the still-live fixture. There is no begin retry, automatic action
retry, native fallback, permission upgrade or saved grant. Process/socket/App
cleanup and its independent evidence remain the caller's responsibility.

## Exact source preparation

`private-clipboard-source.mjs` accepts only the complete pinned upstream bundle
SHA-256 `aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6`.
It also verifies the exact callback/grader slice SHA-256
`d83822d3948d34479e10215e11bee87cb9a8d58024e60a268c55c98e6419dd3f`.
The test-only fixture retains that original slice verbatim as a string. The sole
source change requires and awaits `fixture.beforeOsc52Write(nonce)` after the
original UUID creation and before its unchanged synchronous OSC52 packet. Nonce
generation, `btoa`, packet/BEL bytes, cursor/query calls and original grader are
preserved. Source preparation reports the original and derived digests; the full
prototype-derived bundle digest is
`e091a64f5a8dea7df001a6de04e42505b874aa329117ec3fed1a6f4c5c566f4c`.
This is a read-only audit/syntax result, not execution provenance.

This preparation is for a separately registered private-App adapter graph only.
Do not substitute it for the ordinary/native adapter graph: a native clipboard
fixture does not expose the private preparation hook. It must run against the
pinned original source before other derived-source transforms, or central
integration must explicitly compose a single verified transform while retaining
the original input and final full-source digest. Do not bypass the source pin by
feeding a partially adapted bundle or accepting a caller-supplied digest.

The stock grader remains authoritative. Synthetic integration through the actual
callback bodies yields roundtrip=false, write=true, read=false for an empty
OSC52 read reply. A source-known seed does not grant OSC52 reads or activate
OSC5522. Private write storage is not native interchange, primary/secondary
selection, arbitrary Unicode/large payload support or consent evidence.

## Central integration still required

The existing shared Node owner must review and wire these modules into its next
actual schema/version after the current image integration. No version is guessed
here. Requirements:

1. Retain original/derived collector source, exact helper/test/fixture sources,
   App/contract/binary/build identity and all next-version source inventory pins.
2. Register only the three Q11 callbacks on the existing WeakMap-verified owner
   for the exact captureRunId/out object. Pass each exact probe ID into
   `transaction`; preserve the pinned tail ordering and default policy refusal.
3. Launch the disposable App with its real explicit private startup latch and
   private AppData. The independently owned launcher supplies all actual source,
   process/UID/executable/worker/run-marker/PTY/output/pane/session proof and
   checks it at emission/query boundaries. A digest-equality witness is insufficient.
4. Bind the controller RPC to the independently verified private endpoint and
   budget; no endpoint discovery from the user's App. Test actual App half-close
   semantics and cancellation/refusal before measurement.
5. Keep a separately labeled private fixture journal and summary. Do not forge
   native fixture receipts, clipboard permissions, OS baseline values or child
   delivery facts from these DTOs. Surface sticky failure even when the upstream
   batch converts callback exceptions into observations.
6. Run final close inside owner disposal while the worker/session is still live,
   before overall completion is sealed. Require both fixture completion and the
   independent App/worker/socket cleanup evidence. Failure retires the disposable
   process without fabricating an aborted API fact or successful close.
7. Add shared source-inventory/runtime/receipt/CI integration tests, substituted
   source/identity/trace negative controls and actual latest-head reviews. Only
   then coordinate a real owned GUI/PTY collection. Frozen stock78/0/177 and all
   historical evidence remain unchanged.

Run the standalone synthetic suite with:

```sh
rtk proxy node --test scripts/terminfo/private-clipboard-request.test.mjs scripts/terminfo/private-clipboard-source.test.mjs scripts/terminfo/private-clipboard-controller.test.mjs
```
