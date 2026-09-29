# ntilde multiplexer — Phase 3: multi-client attach, text client, hardening carry-overs

**Status:** design for review. **Branch:** `feat/mux-phase3` off `origin/dev-mux` (21e42a5), PR targets
`dev-mux`. **Predecessors:** Phase 0 (#472), Phase 1 (#474), Phase 2 (#489,
`docs/superpowers/specs/2026-09-23-ntilde-mux-phase2.md`).

## 1. Goal

A user can attach more than one client to one daemon session on purpose, and safely:

1. **Server-side attach modes.** Exclusivity is decided atomically on the session's parse thread, so
   restore never duplicates a shell, and a user can knowingly share one.
2. **"Attach to session…" in the GUI.** It works from a second window or a second ntilde instance.
   It adds a live "shared with N" indicator, a *detach* action separate from *close*, and a
   confirmation when closing a shell that another client is attached to.
3. **`ntilde mux attach <id> [--read-only]`.** A text client for a foreign terminal. It renders from
   its own buffer and never relays the raw stream, so it never forwards device queries. A chord
   detaches it.
4. **The PR #489 carry-overs.**

Everything stays behind `TerminalSettings.SessionPersistence`. With it `Off`, no daemon is spawned,
none of the new commands are registered, and no pane behaves differently.

### Non-goals

- Remote daemons, and a `--stdio` transport (Phase 4).
- Inline images across attach.
- Per-client pane sizes. Latest resize wins, as before.
- Mouse forwarding in the text client beyond what falls out for free (§6.6).
- Authentication. `ReadOnly` is a convenience, not a security boundary: the endpoint stays
  unauthenticated and current-user-only (Phase 2 §3).

## 2. Protocol v2 delta (`Ntilde.Mux.Contracts`)

`MuxProtocol.MaxSupportedVersion` becomes **2**. `MinSupportedVersion` stays **1** on both ends. A
new constant, `MuxProtocol.SessionEventsVersion = 2`, is the one feature gate every v2 behaviour
checks. Every change below is additive. The JSON context stays source-generated
(`MuxJsonContext`), with `WhenWritingNull`, so a v1 peer sees exactly the v1 wire shape.

| Item | Change |
|---|---|
| `AttachParams.Mode` | New `string?`. Null or absent means `shared`. Values: `"shared"`, `"ifUnattached"`, `"readOnly"`. A string rather than a JSON enum: an unknown value then gets a request-level `protocol_error` reply instead of a malformed-params connection close. |
| `MuxAttachMode` | New enum (`Shared`, `IfUnattached`, `ReadOnly`) with `MuxAttachModes.ToWire` / `TryParse`. `Shared` maps to null on the wire. |
| `SessionSummary.Cwd` | New `string?`: the last OSC 7 directory the mux parser saw. |
| `SessionInfoResult` | New `Title` (`string?`), `Cwd` (`string?`), `AttachedClients` (`int?`). A v1 server leaves them null. |
| Notification `sessionChanged` | `SessionChangedNotification { SessionId, AttachedClients, Title, Cwd }`. Sent to every v2 client attached to the session when the attached count, the title or the cwd changes. Coalesced (§4). |
| Notification `killed` | `KilledNotification { SessionId, ByClientKind }`. Sent to every v2 subscriber except the killer, **before** that session's `exited`. |
| Error `session_attached` | `MuxErrorCodes.SessionAttached`: an `IfUnattached` attach found another interactive client attached. |

`SessionSummary.Title` was already live: the Phase 1 parser already wires `OnTitleChanged`. Nothing
changes there (see §11).

### 2.1 Fallback matrix

| Client ↔ server | Negotiated | Behaviour |
|---|---|---|
| v2 ↔ v2 | 2 | Everything below. |
| v2 GUI ↔ v1 daemon | 1 | Spawn, attach (shared), detach and kill work as in Phase 2. The factory keeps the client-side `AttachedClients == 0` pre-check. `AttachAsync(IfUnattached / ReadOnly)` throws `MuxProtocolException(version_mismatch)` **on the client, before anything is sent**, because a v1 server would ignore `Mode` and silently share. `SessionChanged` and `KilledElsewhere` never fire, and `AttachedClients` stays null, so the indicator stays hidden. A kill from elsewhere shows the normal exit banner. The close prompt and the picker still work, because both read `listSessions`, which v1 has. |
| v2 text client ↔ v1 daemon | 1 | Shared attach works. `--read-only` exits 2 with "the running multiplexer is too old for --read-only; run 'ntilde mux kill-server' to replace it". |
| v1 client ↔ v2 daemon | 1 | Unchanged. The server never sends `sessionChanged` or `killed` to it (`IMuxFrameSink.WantsSessionEvents` is false below v2). `Mode` is absent, which means `shared`. |

## 3. Attach modes on the parse thread (`HeadlessTerminalSession`, `MuxServerConnection`)

The attach is already a control item on the session's parse thread (`PostAttach` →
`ExecuteAttach`). Every attach to one session, from any connection, runs **serially on that one
thread, between `Process()` calls**. The mode check lives inside that item, so there is no window
between checking and subscribing:

```
ExecuteAttach(sink, requestId, rows, presentation, maxBytes, mode):
  faulted?                       → internal_error (unchanged)
  mode == IfUnattached and some subscriber s != sink with s not read-only
                                 → reply session_attached; nothing changes
  mode != ReadOnly               → ApplyPresentation + ApplyResize (unchanged: latest wins)
  capture snapshot, enqueue      (unchanged)
  subscribe sink; mark it read-only iff mode == ReadOnly; PublishAttachedCount
```

- **Which clients block `IfUnattached`.** Only *interactive* subscribers other than the requester
  count. A read-only observer (the text client's `--read-only`) does not stop a GUI from reclaiming
  its own shell on restart. A re-attach by the sink that already holds the subscription is allowed.
  `AttachedClients` still counts everyone.
- **A read-only attach changes nothing about the session.** It does not resize and does not apply
  its presentation, so its snapshot is at the session's current size.
- **Enforcement is at the connection's reader thread.** It keeps a `_readOnly` set of session ids
  (reader-thread only), added on a read-only attach and removed on any other attach, a detach or a
  kill. `Input` frames and `resize` requests for those sessions are dropped. The resize still gets
  an empty reply, so a client never sees an error for it. The drop is logged once per (session,
  kind). The session also skips geometry for the read-only sink. This is two independent layers,
  but still not a security boundary: any same-user process can open a second, interactive
  connection.
- **The concurrent-attach proof** (`AttachModeTests.IfUnattached_is_decided_on_the_parse_thread`):
  1. An `InvokeAsync` item parks the parse thread on a gate.
  2. Two clients on separate in-memory connections send `IfUnattached` attaches.
  3. The test waits until `QueuedControlCount == 2`, so both requests are queued before either can
     run.
  4. It opens the gate.

  Exactly one attach returns a snapshot, and the other throws `session_attached`. A 50-iteration
  unparked race runs as well.

## 4. `sessionChanged` coalescing without a timer

The parse thread is the only writer of the attached count, the title (`OnTitleChanged`) and the cwd
(`OnWorkingDirectoryChanged`). Each change calls `MarkSessionChanged()`, which only sets
`_sessionChangePending`. `PublishAttachedCount` marks a change only when the count actually changed.

The parse loop flushes. No timer and no thread-pool work are involved:

```
loop:
  if pending:
     wait = lastSentMs + interval - now
     if wait <= 0: flush; continue
     TryTakeFromAny(queues, out item, wait, token); on timeout (-1): flush; continue
  else:
     TakeFromAny(queues, out item, token)
  Execute(item)
  if pending and lastSentMs + interval <= now: flush
```

- **What drives the delayed flush:** the parse loop's own bounded wait (`TryTakeFromAny` with a
  timeout) while a change is pending. An idle session wakes exactly once, at the deadline, and then
  goes back to an unbounded wait.
- **Rate:**
  - The first change after a quiet period is sent at the end of the item that caused it.
  - Later changes within `interval` collapse into one trailing notification that carries the
    latest values.
  - So there is never more than one notification per session per `interval`. The interval is
    `HeadlessSessionOptions.SessionChangedInterval`, default 100 ms.
- **Recipients:** subscribers with `WantsSessionEvents` (a `MuxServerConnection` that negotiated
  v2). A sink that refuses the frame is dropped exactly as `Broadcast` drops one. The test sink
  (`RecordingFrameSink`) defaults to `false`, so every existing exact-frame-sequence assertion in
  `HeadlessTerminalSessionTests` is unchanged.
- **Initial state:** the attach that subscribes a client changes the count, so the attaching v2
  client gets a `sessionChanged` right after its snapshot. The picker gets the same facts from
  `listSessions`.

## 5. `killed` versus `exited`

`MuxServerConnection` now keeps the hello's `ClientKind` (clipped to 64 chars). On `kill`,
`MuxServer.Kill(id, by: this, byClientKind)` calls `HeadlessTerminalSession.Kill(by, kind)`. That
records the killer **before** it disposes the child. `ProcessExit` then runs on the parse thread for
whichever exit item arrives first. That is either the child's own `OnExit` provoked by the dispose,
or the terminal exit item. When a kill was requested, it sends `killed` to every v2 subscriber
except the killer, and then `exited` to everyone, as today.

Client kinds are:

| Client | `ClientKind` |
|---|---|
| GUI | `ntilde` (unchanged default) |
| CLI verbs | `ntilde-cli` |
| Text client | `ntilde-attach` |

## 6. The text client (`Ntilde.Mux/TextClient/`)

### 6.1 Pieces

| Type | Role |
|---|---|
| `IConsoleSurface` | `Size`, `EnterRawMode()`, `RestoreMode()` (idempotent, any thread), `Write(string)`, `Read(char[])` (blocking, 0 = closed), `event Resized`. |
| `UnixConsoleSurface` | libc `tcgetattr` / `cfmakeraw` / `tcsetattr` on an opaque 256-byte `termios` buffer, so it has no struct layout (Linux and macOS differ). `read(0)` with a stateful UTF-8 decoder, and `write(1)`. Size comes from `Console.WindowWidth/Height`: `ioctl` is variadic and not safe to P/Invoke on macOS arm64. `SIGWINCH` comes through `PosixSignalRegistration`. |
| `WindowsConsoleSurface` | Opens `CONIN$` / `CONOUT$` with `CreateFileW`, so it works in a WinExe after `AttachConsole` / `AllocConsole`. Input: clears `LINE`, `ECHO`, `PROCESSED`, `WINDOW` and `MOUSE_INPUT`, and sets `ENABLE_VIRTUAL_TERMINAL_INPUT`. Output: sets `PROCESSED_OUTPUT`, `ENABLE_VIRTUAL_TERMINAL_PROCESSING` and `DISABLE_NEWLINE_AUTO_RETURN`. Uses `ReadConsoleW` / `WriteConsoleW`. A dedicated background thread polls the size every 200 ms: Windows has no `SIGWINCH`. |
| `TextClientModel` | Phase 1's `ClientPaneModel`, generalised. Holds a `TerminalBuffer` and an `AnsiParser(ForceConPtyFiltering from Welcome) { ImageDecoder = null, AllowNativeKittyGraphics = false }`. `OnResponse` is discarded. On `SnapshotReceived` it calls `TerminalStateTransfer.Restore`, on output `Process`, and on `StreamResize` `Buffer.Resize`. All of it runs on the client's delivery thread, and `Changed` fires after each. |
| `TextClientRenderer` | Turns the buffer into terminal output (§6.2). It has no I/O and is fully testable. |
| `DetachChord` | The input state machine (§6.4). |
| `TextClientSession` | Wires the pieces and owns the two dedicated threads, the exit reason and the restore (§6.5). It is not named `TextClient`: a type named like its namespace would bind to the namespace from any code under `Ntilde.Mux.*`. |

`ConsoleSurfaces.Create()` picks the platform surface. It throws `ConsoleUnavailableException` when
stdin/stdout is not a terminal.

### 6.2 Rendering (`TextClientRenderer.Render(consoleCols, consoleRows)`)

1. `CaptureRenderSnapshot` runs over the **whole session grid**, with `ViewportRows = buffer.Rows`,
   `ViewportCols = buffer.Cols` and scroll 0. The renderer is that buffer's only snapshot consumer,
   so the snapshot's dirty-span state is its own.
2. **Status line.** It is shown when the client is read-only, or when the session grid is larger
   than the console. It uses the last console row, in reverse video:
   - `session is 120x40, this terminal is 80x24 — resize to fit`;
   - `read-only · Ctrl+\ d to detach`;
   - both combined, when both apply.

   `visibleRows = consoleRows - (status ? 1 : 0)` and `visibleCols = consoleCols`.
3. **Clip window.**
   - Columns are clipped from the left edge.
   - Rows start at `top = clamp(cursorRow - visibleRows + 1, 0, sessionRows - visibleRows)`, so the
     cursor stays on screen.
   - A wide cell whose second half would fall outside the window is written as a space.
4. **Full repaint.** It happens on the first frame, and whenever any of these changes: the console
   size, the session size, `top`, the inner alt-screen flag, or the status text. The sequence is
   hide cursor, `CSI H`, `SGR 0`, `CSI 2J`, then every visible row, then the status line.
5. **Incremental repaint.** Otherwise only the rows named by `DirtySpans` inside the window are
   repainted. Each is `CUP row;1`, `CSI 2K`, then the row's cells. `CSI 2K` comes before the text,
   never `CSI K` after it: an `EL` at the pending-wrap column would erase the last cell.
6. **Cells** go through `Ntilde.VT.Export.AnsiCellWriter` (§6.3): an SGR only when the style
   changes, graphemes from `RenderCellSnapshot.Text ?? Character`, continuations skipped, and
   `SGR 0` at the end of each row.
7. **Modes**, emitted only when they change against the last *emitted* state (which starts
   unknown):
   - `?1` (application cursor keys, which vim needs) and `?2004` (bracketed paste), from
     `buffer.Modes`.
   - `?1000` / `?1002` / `?1003` / `?1006`, mirrored only while the window is unclipped
     (`top == 0` and the session fits). Mouse reports then carry the right coordinates, and are
     forwarded as ordinary input. Otherwise they are switched off.
   - The cursor goes last: `CUP` to its window position, then `?25h` when `Modes.IsCursorVisible`
     and it is inside the window, else `?25l`.
8. **The outer alternate screen.** The client enters the outer terminal's alternate screen
   (`?1049h`) once, on attach, and leaves it on every exit path. An *inner* alt-screen switch
   (vim starting) is a full repaint, never a nested `?1049` (see §11).
9. **Threading.**
   - The delivery thread only parses and sets a wake event (`AutoResetEvent.Set`, not thread-pool
     work).
   - One dedicated render thread (`MuxAttachRender`) waits on the event, then renders at most once
     per `RenderInterval` (16 ms, about 60 Hz).
   - `Resized` also wakes it. On a size change the render thread requests the new grid
     (`session.Resize`, unless read-only) and invalidates.
   - Only the render thread writes to the console until it has been joined.

### 6.3 `AnsiCellWriter` goes in `Ntilde.VT`, and the exporter stays put

The "Pane: Export Snapshot (ANSI)" core **already lives in `Ntilde.VT`**
(`Ntilde.VT.Export.TerminalExporter.ExportToAnsi`); it was never in `App/Shell`. It does not fit the
text client:

- It walks `TerminalCell`s through `buffer.GetCell`.
- It emits a newline-separated stream with trailing blanks trimmed, then a final `TrimEnd`.
- It has no absolute positioning, clipping or per-row entry point.

Rewriting it to serve both would change the export's exact output for no user benefit. So a new,
small `AnsiCellWriter` goes beside it. It works on `RenderCellSnapshot` spans (what
`CaptureRenderSnapshot` hands out), and `ExportToAnsi` is untouched.

It lives in `Ntilde.VT` because it is pure text generation, with no I/O and no interop, which keeps
VT a leaf. Its SGR mapping:

- Default colour: no code.
- Palette index 0–7 → `30+i` / `40+i`; 8–15 → `90+i-8` / `100+i-8`; above 15 → `38;5;i` / `48;5;i`.
- Otherwise truecolor `38;2;r;g;b` / `48;2;r;g;b`.
- Attributes: 1 bold, 2 faint, 3 italic, 4 underline, 5 blink, 7 inverse, 8 hidden, 9 strike.

A style change emits a full `SGR 0;…` rather than a delta: simpler to get right, and slightly more
bytes.

### 6.4 Input and the detach chord

The input thread (`MuxAttachInput`, dedicated) reads chars and feeds `DetachChord`. Everything it
passes through is sent as one `SendInput` per read, or dropped when read-only.

| State | Input | Action |
|---|---|---|
| Normal | `Ctrl+\` (`\x1c`) | → AfterPrefix. Chars before it are passed through. |
| Normal | anything else | pass through |
| AfterPrefix | `d` or `D` | **detach** |
| AfterPrefix | `\x1c` | pass a literal `\x1c` through → Normal |
| AfterPrefix | any other char `c` | pass `\x1c` + `c` through, so nothing is lost → Normal |

The state survives across reads, so the chord may be split between two reads. A read of 0 (input
closed) counts as a detach.

### 6.5 Lifecycle, exit codes, restore

`TextClientSession.Run(stderr)`:

1. `OpenSession(id, mode)` and wire the events.
2. `EnterRawMode()`, then write `?1049h`, `CSI H`, `CSI 2J`.
3. Attach with the console's grid. The presentation carries `KittyKeyboardEnabled = false`: the
   outer terminal sends legacy keys, and the mux answers queries.
4. Start the render and input threads, then wait for an exit reason.

`finally` does the following on every path, including an exception from the renderer or the
console:

1. Stop and join the render thread.
2. Write the leave sequence: `SGR 0`, `?25h`, and `?1`, `?2004` and the mouse modes switched off,
   then `?1049l`.
3. `RestoreMode()`.
4. Detach, unless the session is gone.

After that, one line is printed on the restored screen.

| Exit reason | Line | Code |
|---|---|---|
| Detach chord, input closed, a signal | `[detached from <id>]` | 0 |
| Session exited | `[session exited with code N]` | 1 |
| Killed elsewhere (`killed` then `exited`) | `[session ended from another window]` | 1 |
| Faulted in the mux | `[session failed in the multiplexer]` | 1 |
| Connection lost mid-session | `[connection to the multiplexer lost]` | 2 |
| Usage, no daemon, unknown session, attach refused, no terminal, `--read-only` on a v1 daemon | `mux: …` on stderr | 2 |

**Signals** (the CLI only; `TextClientOptions.HandleSignals`): `PosixSignalRegistration` for
`SIGINT`, `SIGTERM`, `SIGHUP` and `SIGQUIT` sets `Cancel = true` and requests a stop, so the
`finally` runs. It works on Windows too (Ctrl+Break, console close). In raw mode Ctrl+C is not a
signal; it reaches the shell as `\x03`, which is intended. `AppDomain.ProcessExit` calls
`RestoreMode()` as a backstop.

### 6.6 Mouse

Mouse forwarding is not implemented as a feature. While the window is unclipped, the client mirrors
the inner mouse modes onto the outer terminal. The outer terminal's reports then arrive on stdin
and are forwarded like any other input, with correct coordinates. When the window is clipped, the
modes are off. That is the "optional if cheap" subset.

### 6.7 Getting a console on Windows

The app executable is a `WinExe`, and the self-contained AOT release does **not** ship `Ntilde.Cli`
(`-p:SkipCliShim=true` in `release.yml`).

- `mux attach` from `Ntilde.Cli.exe` (dev and framework-dependent builds) is a normal console
  process.
- From the `WinExe`, `Program.cs` calls a new `CliConsoleBindings.PrepareInteractive()`:
  `AttachConsole(ATTACH_PARENT_PROCESS)`, and if that fails, `AllocConsole()`, so a launch from
  Explorer gets its own window. The surface opens `CONIN$` / `CONOUT$` itself.
- An interactive shell that does not wait for GUI programs (PowerShell, an interactive `cmd`)
  returns to its prompt and competes for the same console input. The documented invocation there is
  `cmd /c ntilde mux attach <id>` (cmd waits in `/c` mode) or
  `Start-Process -Wait -NoNewWindow ntilde 'mux','attach','<id>'`. A console-subsystem launcher in
  the release is open question 1.
- On Linux and macOS the executable is an ordinary process with the terminal on fds 0–2.

## 7. GUI (`Ntilde.App`)

### 7.1 Restore is exclusive by protocol

`MuxTerminalSessionFactory.CreatePersistent` with `ExistingMuxSessionId`:

1. `listSessions` is still called: it detects missing, exited and faulted sessions, and a command
   mismatch (§8).
2. **On v2**, a running, unfaulted, command-matching match → `OpenSession(id, …, IfUnattached)` →
   outcome `Reattached`. There is no client-side count check.
3. The pane attaches, as in Phase 2. If the attach fails with `session_attached`, the pane:
   1. disposes (detaches) that session;
   2. clears its restore id;
   3. sets `_muxAttachedElsewhereNotice`;
   4. calls `Reconnect()`, which spawns fresh.

   After the new session attaches, it raises `PersistenceNotice("Previous shell in use",
   "[Your previous shell is open in another window — started a new shell]")`. The toast path is
   Phase 2's.
4. **On v1**, the Phase 2 pre-check stays. A match that is attached elsewhere now returns the new
   outcome `AttachedElsewhere` (a fresh spawn) instead of `Spawned`, and the pane raises the same
   notice. The outcome is named in the brief and pinned by tests.

Orphan adoption keeps `AttachedClients == 0` as its listing filter. Its panes go through the same
`IfUnattached` restore path, so a second instance racing for an orphan cannot duplicate it.

### 7.2 "Attach to session…"

- **The command.** A palette command, `Session: Attach to Session…` (id `attach_session`),
  registered only while `_muxHost` exists. It also gets a `ShortcutCatalog` entry `attach_session`
  with **no default chord**, which needs the plumbing in §7.6.
- **Listing.** Off the UI thread, `GetClient(5 s)` then `ListSessionsAsync`.
  `MuxSessionPicker.BuildRows` (pure) turns the result into rows: title, command, cwd, `ColsxRows`,
  attached count, running or exited N, and whether the session is *open here*. Faulted sessions are
  left out.
- **The dialog** is a themed `ListBox` with Attach and Cancel. It is reached through the seam
  `internal Func<IReadOnlyList<MuxSessionPickerRow>, Task<Guid?>> PickMuxSession`, so tests choose
  without a modal.
- **Choosing a session that is open here.** It selects that pane's tab. One window has one
  connection, and `MuxClient.OpenSession` refuses a second view of the same id on one connection.
  A same-window duplicate would also add nothing a split does not.
- **Choosing any other session** opens a new tab. Its `TerminalPane` gets
  `MuxSessionIdToRestore = id` and `MuxAttachSharedToRestore = true`. That flows into
  `TerminalSessionRequest.AttachShared = true` (a new trailing optional positional), and the factory
  opens it `Shared` with outcome `Reattached`, running or exited. An exited session attaches and
  shows its final screen and exit banner. A session that is gone or faulted spawns fresh with the
  Phase 2 "previous session lost" notice.
- **A second ntilde instance** works unchanged: same app-data root, same daemon.

### 7.3 The "shared with N" indicator

- **The pane.** It subscribes to `mux.SessionChanged` (delivery thread) and posts
  `ApplyMuxSharing(mux.AttachedClients)`. `N = AttachedClients - 1`. When `N > 0`, an overlay badge
  shows `shared with N`: a dot plus 10 px text, the agent segment's visual pattern.
- **Why an overlay, not the status bar.** The badge sits at the bottom-right of the terminal row, is
  not hit-testable, and sits at ZIndex 60. It is deliberately not in `StatusBar`. That bar sits in
  the pane grid's `Auto` row, so showing it shrinks the terminal by a row. That would send a resize,
  and "latest resize wins" would then take the shared grid away from the window the user is typing
  in, whenever someone else attached.
- **The tab.** The pane raises `MuxSharingChanged`. MainWindow sets `TabRuntimeState.IsShared` (any
  pane in the tab shared). `TabStatusPresentation.ResolveTabMarkers(…, isShared)` feeds a new
  `TabSharedChip` (`⧉`) in vertical headers. The horizontal title suffix gets ` ⧉`, and the
  automation label ` shared`. `TabMarkerSet` gains `bool Shared = false` as a trailing defaulted
  member, so existing constructions still compile and compare equal.
- **Hidden** on v1 (`AttachedClients` null), on disconnect, and when the session is replaced.

### 7.4 Detach and close

- **`PaneDisposition.Detach`** comes back. Its only user is this path. `DisposeControlTree` skips
  the kill for it and still disposes the `MuxClientSession`, which detaches.
- **The detach command.** `Pane: Detach` (id `detach_pane`) goes in the palette and gets a
  `ShortcutCatalog` entry with no default chord, registered only with `_muxHost`. It runs
  `ClosePaneAsync(pane, skipConfirm: true, PaneDisposition.Detach)`, then toasts "Shell detached —
  Shell kept running — Attach to session… to get it back". A pane whose session is not mux-backed
  and connected gets the toast "Only a persistent shell can be detached" instead.
- **The close decision.** `ClosePaneAsync` and `CloseTabAsync` run a new
  `DecidePaneCloseAsync(pane) → Close | Detach | Cancel`:
  - It first runs `mux.RefreshSharingAsync()`, bounded to 1 s. That calls `listSessions`, which
    works on v1 and v2.
  - If `AttachedClients > 1`, it asks `ConfirmSharedClose(others)`. That is a three-button dialog:
    "N other windows are attached to this shell", Close (ends it) / Detach / Cancel. It is a seam
    for tests.
  - Otherwise the Phase 2 `ShouldClosePaneAsync` runs unchanged.
  - A tab close collects the panes the user chose to detach and disposes them with `Detach`.
- **Killing on close.** `KillMuxSessionOnClose` uses `KillAsync` and registers the task with the
  host (§8.1). It skips a session that is already exited or disconnected.

### 7.5 Killed elsewhere, and focus-driven size

- **Killed elsewhere.** `killed` sets `mux.WasKilledElsewhere` before `exited` raises `OnExit`. The
  pane's normal exit path runs `ShellExitPolicy` exactly as for any exit. The kill's exit code is -1,
  so `Graceful` keeps the pane and `Always` closes it. `WriteLocalExitBanner` writes
  `[Shell ended from another window]` / `[Press Enter to restart]` instead of `[Shell exited]` when
  the flag is set.
- **Size arbitration.** Latest resize wins. When another client resizes, this view letterboxes, as
  it does today. The new `TerminalPane.ReassertMuxGrid()` compares the view's grid
  (`TermView.Cols/Rows`) with the buffer's. When they differ it calls `mux.Resize(viewCols,
  viewRows)`. It is called from `TermView.GotFocus` and from `MainWindow.Activated` for the current
  pane, so the window the user is typing in wins.

### 7.6 Unbound shortcut entries

`ShortcutDefinition` throws on an empty default, and `ShortcutBindingResolver` normalises every
binding and would report two empty bindings as a conflict. So "no default chord" needs:

- `ShortcutDefinition` to accept an empty default;
- the resolver to skip unbound definitions (no normalisation, no conflict row);
- the settings row to read `Default none`.

`IsShortcut(e, id, "")` already returns false for an empty binding (`ShortcutMatcher.Matches`), so
key dispatch needs no special case.

## 8. Carry-overs from the PR #489 review

1. **`kill-server` pid fallback.** It lands before the version bump.
   - When the handshake fails with `version_mismatch`, `kill-server` reads the descriptor, then
     verifies that the pid is alive with the recorded process name (`MuxDiscovery.IsProcessAlive`).
   - Without `--force` it exits 1 with "The running multiplexer (pid N) speaks a different protocol
     version. Re-run with --force to terminate it (this ends its sessions)."
   - With `--force` it calls `Process.Kill(entireProcessTree: true)`, waits up to 5 s
     (`MuxDaemonExit.WaitForExit`), deletes the descriptor if it still names that pid, and exits 0.
   - It refuses its own pid.
   - The GUI hint (`MuxVersionMismatchHint`) now names `kill-server --force`.
2. **Input-cap accounting.** `HeadlessTerminalSession.InputLoop` decrements `_queuedInputBytes` in a
   `finally` after `SendInput` returns, not when the item is taken. A write blocked on a wedged
   child keeps counting against the cap.
3. **Startup auto-apply gate.** `Program.ShouldAutoApplyUpdateOnStartup` gets its `liveDaemon`
   delegate from `MuxStartupProbe.IsDaemonLive(descriptorPath, 200 ms)`:
   - no live descriptor → false;
   - an untrusted endpoint → false;
   - `MuxEndpointConnector.Connect(endpoint, 200 ms)` succeeds → true (the stream is disposed at
     once);
   - a refusal or a timeout → the descriptor is deleted if it still names that pid → false.

   A recycled pid therefore no longer disables auto-update.
4. **`ApplyStagedUpdateAsync`.** `PerformAppTeardown()` moves into a `try`. On an exception it
   logs, resets `_teardownDone`, shows the existing "Update could not be applied" toast, and returns
   **without** applying. A test seam, `TeardownFaultForTest`, drives it.
5. **Reattach command check.** `MuxCommandMatch.SameExecutable(daemonCommand, requestCommand)`
   compares file names without extension. It ignores case on Windows, and an empty daemon command
   matches. On a mismatch the factory logs, leaves the daemon session alone, and spawns fresh
   (`Spawned`). It does not apply to `AttachShared`: the picker builds the command from the summary
   itself.
6. **Last-tab kill.**
   - `KillMuxSessionOnClose` calls `KillAsync` (its reply means the kill landed) and
     `MuxConnectionHost.TrackPendingKill(task)`.
   - `MuxConnectionHost.Dispose` first waits for every tracked kill, up to `KillFlushTimeout`
     (3 s), inside `Task.Run`, then runs the existing 1 s ping flush.
   - The kill frame is still enqueued synchronously on the UI thread (`RequestAsync` enqueues before
     its first await), so ordering is unchanged.
7. **The manual checklist.** PR #489's 8 steps plus the four in brief item 6. They are written out
   in the plan's final task, and the log goes into the PR.

## 9. Architecture

- `Ntilde.Mux` still references only Pty, VT, Replay and Mux.Contracts. The text client uses
  libc/kernel32 P/Invokes: `DllImport` with blittable signatures, `ExactSpelling`,
  `DefaultDllImportSearchPaths(System32)` on Windows, and no marshalling of structs with layout
  differences. These are AOT-safe with no reflection.
- New architecture tests:
  - types in `Ntilde.Mux.TextClient` depend on no Avalonia, SkiaSharp, Platform, Rendering, `Shell`
    or `Controls`;
  - `*ConsoleSurface` and `TextClient*` types reside in `Ntilde.Mux.TextClient`;
  - `CliCommandDispatchTests` gains a row: `mux attach` gets an interactive console
    (`MuxCommand.IsAttach` + `CliConsoleBindings.PrepareInteractive` in `App/Program.cs`), and
    `Ntilde.Cli` dispatches `mux`.
- There are no new `TerminalSettings` fields, so the `ApplySettings` whitelist and the MCP drift
  guards are untouched. There is no new test project, so `ci.yml` is untouched.

## 10. Test map (brief §5)

| Brief item | Test |
|---|---|
| `IfUnattached` race | `tests/Ntilde.Mux.Tests/Server/AttachModeTests.cs`: `IfUnattached_is_decided_on_the_parse_thread`, `IfUnattached_races_have_exactly_one_winner` |
| `ReadOnly` drops input and resize | `AttachModeTests.ReadOnly_attach_does_not_resize_and_its_input_and_resize_are_dropped` |
| `sessionChanged` coalescing and delivery | `tests/Ntilde.Mux.Tests/Headless/SessionEventsTests.cs` |
| `killed` vs `exited` | `SessionEventsTests.Kill_sends_killed_before_exited_to_others_only`, `…natural_exit_sends_no_killed` |
| v1 ↔ v2 fallbacks | `tests/Ntilde.Mux.Tests/Client/ProtocolFallbackTests.cs` |
| Text client with a fake console | `tests/Ntilde.Mux.Tests/TextClient/*` (renderer, chord, session loop, restore on every path) |
| Picker attaches shared, both equal the mux | `tests/Ntilde.App.Tests/Core/MainWindowMuxSharingTests.cs` |
| Indicator on `SessionChanged`, killed banner, focus re-request | `tests/Ntilde.App.Tests/Controls/MuxPaneSharingTests.cs` |
| Detach keeps the shell; close with others prompts | `MainWindowMuxSharingTests` |
| Last-tab kill lands | `MuxConnectionHostTests.Dispose_waits_for_a_tracked_kill`, `MainWindowMuxLifecycleTests.Closing_last_tab_with_no_ping_flush_still_kills` |
| Startup gate probe | `tests/Ntilde.App.Tests/Shell/Mux/MuxStartupProbeTests.cs` |
| PtySmoke, two shared clients | `MuxDaemonSmokeTests.Two_clients_attached_shared_to_a_real_shell_see_the_same_marker` |
| Architecture rows | `LayeringTests`, `NamespaceAlignmentTests`, `CliCommandDispatchTests` |

## 11. Differences from the brief

1. **The ANSI exporter is already in `Ntilde.VT`** (`Ntilde.VT.Export.TerminalExporter`), not in
   `App/Shell`. Its core does not fit a positioned, clipped, per-row renderer, so it stays as it is,
   and a new `AnsiCellWriter` over `RenderCellSnapshot` goes beside it (§6.3).
2. **The outer alternate screen is entered once, on attach.** Inner alt-screen switches are full
   repaints, not mirrored `?1049` toggles. Mirroring would paint the session into the user's own
   scrollback while the inner app is on its main screen, and detach would leave it there.
3. **The indicator is an overlay badge, not a `StatusBar` segment.** The status bar's `Auto` row
   resizes the terminal, which would steal the shared grid (§7.3). The visual pattern is the agent
   segment's.
4. **`IfUnattached` ignores read-only observers** (§3). A read-only watcher must not make the GUI
   abandon its own shell on restart.
5. **No same-window duplicate view.** Choosing a session already open in this window focuses its
   tab. One connection cannot hold two views of one id, and the brief's "new tab in the same window"
   is still how every other session opens.
6. **`SessionSummary.Title` was already live** from OSC 0/2 in Phase 1. Only `Cwd` is new.
7. **`AttachedElsewhere` is also produced on v1**, by the kept pre-check, instead of the old
   `Spawned`. On v2 the pane produces it after `session_attached`, because the factory returns
   sessions unattached (Phase 2 §7), so the atomic check can only surface at the pane's attach.
8. **`ShortcutCatalog` cannot hold an unbound entry today.** §7.6 adds that plumbing.
9. **`sessionChanged` and `killed` go only to v2 subscribers.** This keeps v1 peers and the
   existing exact-sequence sink tests untouched.
10. **Existing tests that pin behaviour the brief changes** are updated, and the plan says so where
    it happens:
    - `HeadlessInputWriterTests.Input_beyond_the_byte_cap_is_dropped_not_queued_forever` pinned the
      old decrement-on-take.
    - `MuxTerminalSessionFactoryTests.A_session_attached_by_another_client_is_not_taken_over`
      becomes a v1-pinned test expecting `AttachedElsewhere`, and gets a v2 twin.
    - `MuxServerHandshakeTests.Disjoint_ranges_are_refused_and_the_connection_closed` offered
      `2..3` against the default server, which overlaps after the bump. Its input moves to `3..4`;
      the assertion is unchanged.

    None of these tests runs with persistence off.
11. **Exit code 2 covers every connection problem** for `mux attach`, including "no multiplexer
    running". The other verbs keep exit 1 for that, as specified in Phase 2.
12. **`mux attach` also accepts a unique id prefix** (at least 4 characters) from `listSessions`.

## 12. Open questions for the user

1. **A console launcher for Windows releases.** The AOT bundle has no console-subsystem executable,
   so `ntilde mux attach` from PowerShell needs `cmd /c` or `Start-Process -Wait -NoNewWindow`
   (§6.7). Should Phase 4's install flow ship a tiny console `ntilde-cli.exe`? The alternative is to
   live with the documented workaround.
2. **Should a detached shell be adopted at the next launch?** Phase 2 adopts every unattached,
   unreferenced running session as a background tab at startup. A shell the user deliberately
   detached is exactly that, so it comes back as a tab the next time the GUI starts. The plan keeps
   that ("never lose a shell"). The alternative is a daemon-side "detached on purpose" flag that
   adoption skips.
