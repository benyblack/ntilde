# Private clipboard fixture foundation (#774)

This App-only foundation provides private in-memory storage for future owned
terminfo clipboard qualification. It does not enable any collector probe, publish
a grade, prove process/PTY ownership, or qualify native clipboard interchange.
The three retained Q11 spellings remain refused in published stock evidence:
`extensions.osc52-clipboard`, `extensions.osc52-write`, `extensions.osc52-read`.

The pinned collector's shared `liveClipboardProbe` writes OSC 52 **c** with an
ASCII `terminfo-osc52-<lowercase UUID>` nonce, fences it with cursor readback, then
independently reads storage. The roundtrip and read cases independently seed a
second nonce before querying. Ntilde intentionally replies to that OSC 52 query
with an empty payload. The fixture does not change this denial or turn these
read cases into positive capability evidence.

## Startup and authority

A disposable App process must explicitly include both
`--ntilde-terminfo-owner=<runId>` and
`--ntilde-terminfo-private-clipboard=<runId>`, with matching `TERMINFO_RUN_ID`
(32 lowercase hexadecimal characters) and `NTILDE_APPDATA_ROOT` naming an existing
absolute nonsymlink scratch root. Unix roots must have no group/other permission
bits. Malformed explicit activation fails startup. Root metadata checks do not
attest ownership: the caller must separately verify process identity, private
root ownership/ACLs, PTY binding, executable provenance and cleanup.

The mode is latched before App composition. Every App UI clipboard resolver
returns unavailable, and panes never construct the OSC 5522 desktop backends. Startup class handlers
also cancel Avalonia TextBox copy/cut/paste and SelectableTextBlock copy events
before those framework controls resolve their native provider.
This refusal applies before binding, after failed binding, after close/abort,
and to replacement panes/windows. No system clipboard contents are saved,
restored, inspected or overwritten. Normal launches retain their existing
clipboard paths and session-only OSC 5522 consent; no grant is saved to disk.

## Raw App action

`clipboardFixture` is an act-only AgentHost method, with no MCP tool or product
setting. Parameters are `paneId`, `runId`, `operation`, optional `fixtureId`, and
optional `value`. Unknown fields and mixed operations are rejected.

| Operation | Additional fields | Observation |
| --- | --- | --- |
| `begin` | none | One new private fixture ID; at most one fixture per process |
| `armWrite` | fixtureId, value | Authorizes exactly one OSC 52 c write of that exact canonical nonce |
| `seed` | fixtureId, value | Independently stores a canonical test nonce, without terminal input |
| `read` | fixtureId | Returns only the private fixed seed or an admitted synthetic nonce |
| `restore` | fixtureId | Cancels any armed write and restores the fixed private seed, keeping the fixture active |
| `close` | fixtureId | Restores fixed `terminfo-private-seed-<runId>` storage and closes |

A fixture captures its originating parser object, and every write callback carries
its own captured parser identity into the locked fixture check. A paused retired
callback cannot write into a fixture created after parser replacement.

The current visible single direct local pane must satisfy the same layout gates
as resize: no remote, mux/deferred, windowless, split, inactive, stale or dead
target. App registration, parser, buffer and session identity bind the fixture.
The dispatcher rechecks permissions/identity before applying; cancelled queued
operations cannot run later. Actual OSC writes recheck the bound identity and
preserve `AllowOsc52ClipboardWrite`. A wrong selection, unarmed/mismatched write,
pending write at close, RIS, reconnect, process exit, disposal or explicit
interruption aborts the fixture and restores its private seed. Aborted state
cannot become complete on a later close. Successful close is idempotent too.

Each pinned per-probe callback must `restore` and verify the returned seed hash
before returning. Restore never marks the fixture complete. Later admitted writes
or independent seeds clear the restored fact. Only final `close` may complete the
whole fixture. Restore refuses aborted/closed fixtures and cannot reactivate one.

Results report fixture ID, state, admitted write count, restore/completion facts,
SHA-256 of the actual source-known private value, and `value` only for `read`. No native-delivery or ownership claim is made. The
ordinary action journal records only method, pane and generic outcome/target;
it never records nonce values, encoded bytes or run identifiers. Transport
failure or timeout makes the caller's operation result unknown; do not invent
a completion receipt from missing evidence. Independent owned collection and
receipt integration remain follow-up work under #774.

Native OS clipboard interchange remains in its separate qualification lane.
This foundation's tests use fake sessions, private temporary roots and headless
Avalonia; they do not access the OS clipboard or launch a desktop App. Process
policy tests run in a nonparallel collection, with one scoped test-only process
latch installed before window construction and removed after window teardown.
The explicit production startup latch cannot be replaced. Framework control
coverage uses a recording headless clipboard feature with normal-access positive
controls, never an OS provider.

The test-only NativeHost also runs a separate headless startup proof before its
CI-only native interchange entry. Fresh ordinary/private child processes execute
real `App.Initialize` with actual command-line/environment markers and a recording
headless clipboard feature. They use no scoped override. The ordinary copy must
resolve/write the recorder once; the private copy must not resolve it. Removing
the production startup guard-install call makes the private control fail. This
proof does not initialize a desktop backend or qualify native OS interchange.
A local fake-only run is `scripts/build.sh run --project
tests/Ntilde.Clipboard.NativeHost/Ntilde.Clipboard.NativeHost.csproj --no-build --
--headless-startup-selftest <existing-private-scratch-root>` after building that
project with the repository wrapper. Normal native qualification still requires
its existing CI-only `--isolated-desktop` contract.
