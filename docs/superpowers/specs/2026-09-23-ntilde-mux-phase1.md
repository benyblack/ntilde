# ntilde multiplexer — Phase 1: `Ntilde.Mux` core (in-process, no daemon)

**Status:** approved task, design below. **Branch:** `feat/mux-phase1-core` off `origin/dev-mux`,
PR targets `dev-mux`. **Predecessor:** Phase 0 (PR #472, `docs/superpowers/specs/2026-09-22-ntilde-mux-phase0.md`).
**Closes:** #473.

## 1. Goal

Build the multiplexer core as a library, exercised entirely in-process over in-memory streams. The
mux owns a real `ITerminalSession` plus the authoritative headless `AnsiParser` + `TerminalBuffer`;
clients attach, receive a snapshot tagged with a stream position, then every later output chunk and
resize as ordered stream events, and keep their own parser+buffer equal to the mux's.

Phase 2 hosts `MuxServer` in `ntilde mux serve` and attaches `TerminalPane` through
`MuxClientSession`. Everything here is shaped so that Phase 2 is wiring: a listener that yields
`Stream`s, and a session type that already implements `ITerminalSession`.

### Non-goals (Phase 1)

No daemon process, no sockets or named pipes, no endpoint discovery, no GUI wiring, no
`TerminalSettings` fields, no user-visible behaviour change. SSH sessions are not spawnable through
the mux (see §9).

## 2. Assemblies

| Assembly | References | Role |
|---|---|---|
| `Ntilde.Mux.Contracts` (new) | none (leaf) | Wire protocol: framing, frame kinds, binary payload codecs, JSON DTOs + source-generated context, error codes, version negotiation. |
| `Ntilde.Mux` (new) | Pty, VT, Replay, Mux.Contracts | `HeadlessTerminalSession`, `MuxServer`, `MuxClient`, `MuxClientSession`, in-memory transport. `IsAotCompatible=true`. |

Both new libraries set `IsAotCompatible=true` and `TreatWarningsAsErrors=true`, which turns the
trim/AOT analyzer warnings (`IL2026`, `IL3050`, …) into build errors for them regardless of whether
anything publishes them yet. `Ntilde.Pty` still does not reference `Ntilde.VT`: the headless session
lives in `Ntilde.Mux`.

## 3. Raw byte tap (`Ntilde.Pty`, `Ntilde.Platform`)

`ITerminalByteOutput { event Action<ReadOnlyMemory<byte>>? OnRawOutputReceived; }` is an optional
interface next to `ITerminalSessionCapabilities`. Every chunk handed to a handler is a **fresh array
the receiver owns**. The shared implementation is `RawOutputTap` (Pty, public so `Ntilde.Platform`
can use it):

- `Publish(ReadOnlySpan<byte>)` runs on the session's producer thread (RustPtySession: the read
  loop, immediately after `RecordChunk`; NativeSshSession: the poll loop, immediately after
  `RecordChunk`). Same order as the recorders and as the string event.
- **First-subscriber replay, like the string path.** Chunks published before the first raw
  subscriber are retained and replayed to it, because `RustPtySession` starts reading in its
  constructor and the mux can only subscribe after the factory returns.
- **Retention ends when anyone subscribes to the string event first.** A normal GUI session's pane
  subscribes to `OnOutputReceived` immediately, which drops the retained raw chunks and stops
  retention for good. Result: when nobody taps the bytes, the read loop allocates nothing new
  after startup.
- A throwing raw handler is contained and logged. It must not end the read loop the way an escape
  into `ReadLoop`'s catch-all would.
- `OpenSshSession`/`SshSession` forward the event to their inner session when it implements the
  interface. `NativeSshSession` also publishes its synthetic diagnostic text (warnings, failure
  banners) as UTF-8, so the raw stream carries everything the string stream does.

This closes the Phase 0 follow-up "expose stream position from `ITerminalSession`": the mux runs its
own `Utf8ChunkDecoder` over the tapped bytes, so its `ConsumedBytes`/`PendingTail` are the stream
position.

## 4. The model

- **Stream offset** (`rawOffset`): total raw bytes the mux has fed its decoder. `Output.seq` is the
  offset *before* that chunk; `ResizeEvent.seq` is the offset at which the resize applies.
- **Snapshot position:** `StreamSeq = decoder.ConsumedBytes` and `DecoderTail = decoder.PendingTail`,
  so `StreamSeq + DecoderTail.Length == rawOffset` at capture. `Attached.seq == StreamSeq`.
- **Client continuity check:** after a snapshot the client expects the next frame at
  `StreamSeq + DecoderTail.Length`. Each `Output` must start exactly where the previous one ended,
  and each `ResizeEvent` at the current offset. Any gap or overlap is a protocol error and the
  connection is dropped. It is never healed silently.
- **Device queries:** only the mux parser writes `OnResponse` to the session. `MuxClientSession`
  reports `AnswersDeviceQueries = true`, which Phase 0 already wired to suppress a pane's replies
  and its in-band resize reports.
- **Size policy:** latest resize wins. Attaching also counts as a resize, to the attaching client's
  presentation size.
- **Presentation** (`cols, rows, cellWidthPx, cellHeightPx, defaultFg, defaultBg,
  kittyKeyboardEnabled`) from the latest attach or resize request is applied to the mux parser
  (`CellWidth`, `CellHeight`, `DefaultForeground`, `DefaultBackground`, `KittyKeyboardEnabled`) so
  that `CSI 14 t`, `CSI 16 t`, OSC 10/11 and `CSI ? u` answers describe the client the user is
  looking at.

## 5. Threading

| Thread | Owner | Does |
|---|---|---|
| `PtyRead-*` (existing) | RustPtySession | `pty_read`, recorders, **tap publish** → enqueue into the session's bounded data queue (blocks when full = back-pressure to the PTY, same as today's string queue). |
| `MuxParse-{id}` | HeadlessTerminalSession | The **only** thread that touches the mux parser/buffer. Drains two queues with `BlockingCollection.TakeFromAny(control, data)`: control items (resize, presentation, attach, detach, invoke) take priority and run *between* `Process()` calls; data items (chunks, exit, flush barriers) run in arrival order. After parsing a chunk it builds one pooled `Output` frame and offers it to every attached client (skipped entirely when none is attached). |
| `MuxAccept` | MuxServer | Blocks in `IMuxListener.Accept`. |
| `MuxConnRead-{id}` / `MuxConnSend-{id}` | per server connection | Reader parses frames and dispatches requests (never captures snapshots, it posts them to the parse thread). Sender drains a bounded, byte-budgeted queue into the stream. |
| `MuxClientRead` / `MuxClientSend` | MuxClient | The reader is the **delivery thread**: it raises `SnapshotReceived`, `OnOutputReceived`, `StreamResize`, `OnExit` for all sessions on that connection, strictly in frame order. The sender drains a bounded frame queue. |

No thread-pool work on the output path. Snapshot capture runs on the parse thread (buffer read lock,
~27 ms for 10k rows per Phase 0), never on a client reader thread.

**Resize on the parse thread:** apply presentation → `buffer.Resize(cols, rows)` → broadcast
`ResizeEvent{seq = rawOffset}` → `session.Resize(cols, rows)` → if mode 2048 is on,
`parser.SendInBandResize`. Bytes the child writes after learning the new size therefore sort after
the event. A resize to the current size is a no-op and emits no event.

**Attach on the parse thread (atomic with respect to the stream):** drop any prior subscription of
this client → apply presentation and resize (broadcast to the *other* clients) → capture →
serialize → size check → enqueue the `Snapshot` frame to this client → subscribe it. If the session
has exited, an `Exited` notification follows. Every later chunk reaches this client after its
snapshot, and no chunk reaches it twice.

## 6. Wire protocol (`Ntilde.Mux.Contracts`)

Frame = `u8 kind`, `u32 payloadLength` (little-endian), payload. `MaxFrameBytes = 64 MiB` of payload.
A header announcing more is refused with `frame_too_large` before any payload is read.

| Kind | Byte | Payload |
|---|---|---|
| `Request` | 0x01 | JSON `MuxRequest { id, method, params }`. `id == 0` means no response wanted (used for `resize`). |
| `Response` | 0x02 | JSON `MuxResponse { id, result? , error? }`. `id == 0` with an error is connection-level, sent right before the server closes. |
| `Notification` | 0x03 | JSON `MuxNotification { method, params }`. |
| `Input` | 0x10 | `sessionId[16]` + UTF-8 bytes of one `SendInput` string. |
| `Output` | 0x11 | `sessionId[16]` + `seq i64` + raw bytes. |
| `ResizeEvent` | 0x12 | `sessionId[16]` + `seq i64` + `cols i32` + `rows i32`. |
| `Snapshot` | 0x13 | `requestId i64` + `sessionId[16]` + `seq i64` + snapshot JSON (`TerminalStateSerializer`). This is `Attached`. |

Integers are little-endian. Guids are `Guid.TryWriteBytes` layout.

Methods (task-text name → shipped shape):

| Task text | Method / frame | Params → result |
|---|---|---|
| `Hello` → `Welcome` | `hello` | `{minVersion,maxVersion,clientKind}` → `{version,forceConPtyFiltering}` |
| `ListSessions` → `SessionList` | `listSessions` | `{}` → `{sessions:[{sessionId,title,command,arguments,cols,rows,running,exitCode,attachedClients,faulted}]}` |
| `Spawn` → `Spawned` | `spawn` | `{command,arguments,startingDirectory,cols,rows,environmentOverrides,skipPowerShellPostLaunchInit,title}` → `{sessionId}` |
| `Attach` → `Attached` | `attach` | `{sessionId,maxScrollbackRows,presentation}` → **`Snapshot` frame** (or an error `Response`) |
| `Detach`, `Kill` | `detach`, `kill` | `{sessionId}` → `{}` |
| `Input` | `Input` frame | — |
| `Output` | `Output` frame | — |
| `ResizeRequest` | `resize` (id 0) | `{sessionId,cols,rows,presentation?}` |
| `ResizeEvent` | `ResizeEvent` frame | — |
| `Exited` | notification `exited` | `{sessionId,exitCode}` |
| `SessionInfo` | `sessionInfo` | `{sessionId}` → `{running,exitCode,hasActiveChildProcesses,pid}` |
| `StartRecording`, `StopRecording` | `startRecording`, `stopRecording` | `{sessionId,path}` / `{sessionId}` → `{}` |
| `EnableFlightRecording`, `DisableFlightRecording` | same names | `{sessionId,maxBytes}` / `{sessionId}` → `{}` |
| `ExportFlight` | `exportFlight` | `{sessionId}` → `{exported,bytes,eventCount,firstEventMs,lastEventMs,truncatedAtStart}` |
| `Ping`/`Pong` | `ping` | `{}` → `{}` |

**Version negotiation:** `chosen = min(serverMax, clientMax)`, accepted if
`chosen >= max(serverMin, clientMin)`. Otherwise `version_mismatch`, and the server closes. The client
also rejects a `Welcome` outside its own range.

**Error codes:** `version_mismatch`, `unknown_session`, `session_exited`, `frame_too_large`,
`snapshot_too_large`, `client_too_slow`, plus `protocol_error`, `spawn_failed`, `internal_error`.

**Untrusted input:** every frame header is validated before its payload is read, every JSON payload
goes through the source-generated context (and a `required` member that is missing is a
`JsonException`), and binary payloads are length-checked before they are sliced. Malformed JSON,
an unknown frame kind, or a frame the peer may not send (for example `Output` from a client) closes
the connection with `protocol_error`, and nothing reaches a buffer.

## 7. Resource policy (closes #473)

- **Server:** `maxScrollbackRows` is clamped to `MuxServerOptions.MaxAttachScrollbackRows` (default
  20 000). A serialized snapshot larger than `MaxSnapshotBytes` (default `MaxFrameBytes` minus the
  32-byte snapshot header) is refused with `snapshot_too_large`. Every peer-supplied geometry
  (`spawn`, an attach's presentation, `resize` and its presentation) must fit
  `MuxServerOptions.MaxDimension` (default 10 000 per dimension) and `MaxCells` (default 1 000 000,
  the client's default ceiling): anything over is a `protocol_error` request error, the connection
  stays open, and the geometry never reaches a buffer or a `ResizeEvent`. A factory-created child
  that the server then fails to wrap is disposed and reported as `spawn_failed`.
- **Client:** `MuxAttachLimits { MaxSnapshotBytes, MaxCells (cols×rows), MaxScrollbackRows }`
  (defaults 64 MiB, 1 000 000, 50 000). The byte ceiling is checked before deserialization, and the
  cell and scrollback ceilings are checked after deserialization but before `SnapshotReceived` is
  raised. `SnapshotReceived` is the only path to `TerminalStateTransfer.Restore`, so no oversized
  snapshot can reach a buffer. A refused attach tells the server to detach.
- **Send budget:** each server connection has a byte budget (default 16 MiB). A frame is accepted
  when the queue is empty or when it fits in the remaining budget, so one snapshot larger than the
  budget still goes out on a fresh attach. Overflow disconnects that client immediately, and the
  parse thread and the other clients never wait.
- #473 asked for a geometry *rejection ceiling* (not a clamp). The ceiling lives at the transport
  boundary, on the client, where the payload's origin is known. `ValidateBufferState` stays
  structural.

## 8. Allocation budget on the output path

Per chunk: one tap copy (the array the data queue holds), the `string` that
`AnsiParser.Process(string)` requires (the same as every existing consumer), and, only when at
least one client is attached, one `ArrayPool` frame buffer shared by every attached client and
returned when the last sender has written it. Adding a span overload to `AnsiParser.Process` is out
of scope, because it would touch the VT hot path.

## 9. Deliberate differences from the task text

1. **Control frames use JSON envelopes with a method name** (`Request`/`Response`/`Notification`,
   following `Ntilde.AgentHost.Contracts`) rather than one kind byte per message. Data frames are
   binary. `Attached` is the binary `Snapshot` frame, correlated to the `attach` request by id,
   because base64-in-JSON would add a second 4/3 inflation to a payload that is already base64 cells.
2. **`spawn` has no SSH descriptor.** `SshSessionDescriptor.InteractionHandler` is a live object and
   cannot cross a wire. SSH through the mux is a later phase.
3. **Recording and flight frames carry `sessionId`.** The task text listed some of them without one,
   but every one of them is per session.
4. **The PtySmoke test lives in `tests/Ntilde.App.Tests`.** That is where the real
   `DefaultTerminalSessionFactory` and the `rusty_pty` native library are. `Ntilde.Mux.Tests`
   stays hermetic, with no native dependency.
5. **`client_too_slow` is recorded server-side, not delivered.** The peer is by definition not
   reading, so the reason cannot travel. It is the connection's `CloseReason` and is logged.
6. **Subscribe, then attach:** `MuxClient.OpenSession(id, …)` returns an unattached
   `MuxClientSession`. The caller wires events, then calls `AttachAsync`, so no event can be raised
   before a handler exists and nothing has to be buffered.
7. **One delivery thread per connection,** not per session. All sessions on a `MuxClient` share its
   reader thread, which preserves frame order trivially.

## 10. Tests

`tests/Ntilde.Mux.Tests` (new, xunit.v3) uses a scripted fake `ITerminalSession` +
`ITerminalByteOutput` that emits given chunks with given splits and records `SendInput` and `Resize`.
Equality is `TerminalStateAssert.AssertEquivalent`, the Phase 0 parity comparison extracted from
`ParityHarness` and linked in, covering both screens, scrollback, all cursors, tab stops, modes,
kitty stacks, grapheme state, hyperlinks, and parser state. The coverage list is the task's §4,
verbatim. `ParityCorpus` is linked in as well, so the mid-stream attach tests run the same 36
streams Phase 0 proved.

## 11. Open questions carried to Phase 2

Recorded in the PR description at the end of the phase: daemon hosting and discovery, `TerminalPane`
attach wiring (parser construction from `ForceConPtyFiltering`, `StreamResize` → local resize when
`OrdersResizeInStream`), resize plumbing in `TerminalView` (latest-wins versus pane-local size),
render-scaling semantics of `cellWidthPx`, kitty graphics (the mux parser has no image decoder, so
images live only in clients and are absent from a reattach snapshot), and moving snapshot export off
the buffer read lock.
