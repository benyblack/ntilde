# ntilde multiplexer — Phase 2: local daemon, GUI attach, detach/reattach

**Status:** approved task, design below. **Branch:** `feat/mux-phase2-daemon` off `origin/dev-mux`,
PR targets `dev-mux`. **Predecessors:** Phase 0 (#472), Phase 1 (#474,
`docs/superpowers/specs/2026-09-23-ntilde-mux-phase1.md`).

## 1. Goal

Local shells survive closing the window, an app crash and a restart, and reattach on launch.
Single GUI instance, local (non-SSH) panes only, behind `TerminalSettings.SessionPersistence`
(default `"Off"`). With the setting off no daemon is ever spawned and nothing changes.

### Non-goals (Phase 2)

Multi-client attach of one session from two GUIs, a text client (`ntilde mux attach`), SSH panes
through the mux, inline images in mux-backed panes, surviving a daemon crash, surviving an update.

## 2. Process model

```
 GUI (Ntilde.exe)                          daemon (Ntilde.exe mux serve)
 ├─ MuxConnectionHost ── one MuxClient ──► NamedPipe / UDS ─► MuxDaemonHost
 │    (warm at start, reconnect on demand)                     ├─ MuxServer (Phase 1)
 ├─ MuxTerminalSessionFactory                                  │   └─ HeadlessTerminalSession × N
 │    local → spawn/open MuxClientSession                      │        └─ RustPtySession → shell
 │    SSH   → DefaultTerminalSessionFactory                    ├─ idle-exit + reaper timer
 └─ TerminalPane ← MuxClientSession events                     └─ mux/mux-endpoint.json
```

The daemon is a CLI mode of the app executable (the AOT bundle ships no `Ntilde.Cli`). It never
initialises Avalonia. Daemon death kills its shells (no watchdog, like tmux).

## 3. Endpoint and discovery

- **`MuxDiscovery`** (`Ntilde.Mux.Contracts`, still a zero-reference leaf):
  - `GetRootDirectory()`: `NTILDE_APPDATA_ROOT` (full path) or `LocalApplicationData/ntilde`,
    the `AgentHostDiscovery` shape.
  - `GetDescriptorPath()`: `<root>/mux/mux-endpoint.json`.
  - `GetDefaultEndpoint()`:
    - Windows: pipe `ntilde-mux-<user>-<rootHash8>`, where `<user>` keeps only letters and digits
      of `Environment.UserName`, lowercased, and `<rootHash8>` is the first 8 hex chars of
      SHA-256 over the full root path (upper-cased on Windows). Two app-data roots (tests,
      portable installs) never share a daemon.
    - Unix: `<root>/mux/mux.sock`. If that path's UTF-8 length exceeds the platform `sun_path`
      budget (103 bytes on macOS, 107 on Linux), `<TMPDIR>/ntilde-mux-<user>-<rootHash8>/mux.sock`.
  - `MuxEndpointDescriptor { MinVersion, MaxVersion, Endpoint, Pid, ProcessName }` (source-gen,
    `MuxJsonContext`).
  - `WriteDescriptor` is atomic: a temp file in the same directory, then `File.Move(overwrite)`.
    No `.bak` and no reference to App's `AtomicFile`.
  - `TryReadLiveDescriptor` is true only when the pid is alive **and** its process name equals
    the recorded `ProcessName` (ordinal, ignore case), which guards against pid reuse.
  - `DeleteDescriptorIfOwned(pid)` deletes the file only when it names this pid.
- **Security.** The protocol has no authentication and `spawn` runs arbitrary commands, so:
  - Windows: `NamedPipeServerStream(..., PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly)`
    with `MaxAllowedServerInstances`. The client uses `CurrentUserOnly` too.
  - Unix: the socket directory must be mode `0700` (created that way). If it already exists with
    any other mode, the listener refuses to start and names the path. A `0700` directory owned by
    another user is inaccessible to us, so bind fails and we refuse. That covers ownership without
    `stat` interop. The socket gets `0600` after bind; the `0700` parent closes the window before it.
  - A stale socket file is probe-connected before it is unlinked. If the socket is alive,
    refuse to start.
  - Never TCP.

## 4. Daemon lifecycle (`MuxDaemonHost`, `Ntilde.Mux`)

- `MuxDaemonHost(MuxServer, IMuxListenerFactory, MuxDaemonOptions)`, where `Run(CancellationToken)`
  blocks until shutdown. It starts the listener, writes the descriptor, and ticks a timer every
  `TickInterval` (1 s).
- **Reaping** (`MuxServer.ReapExitedSessions(TimeSpan grace)`): a session is removed and disposed
  when it has exited, has no attached client, and exited at least `grace` ago (default 60 s). An
  attached client keeps an exited session alive, so the pane can still show the exit state.
- **Idle exit** after `IdleExitAfter` (default 10 min, `--idle-exit-minutes`, 0 = never) with zero
  *running* sessions and zero connections. `MuxServer.TryBeginIdleShutdown()` takes the server's
  lifecycle lock, re-checks both counts, sets `_acceptingStopped` and disposes the listener.
  `AcceptConnection` registers connections under the same lock and refuses (disposes the stream)
  once stopped. Then the host deletes the descriptor, unlinks the socket and returns. A client
  racing in gets a refused or broken connection, never a hang, and its launcher starts a new
  daemon.
- **`shutdown` method** (new, additive): the server replies, then raises `ShutdownRequested`. The
  host kills every session and exits. Used by `ntilde mux kill-server` and the update path.
- **Per-session input writer** (resolves PR #474 open question "SendInput head-of-line blocking"):
  - `HeadlessTerminalSession.SendInput` enqueues onto a per-session input thread
    (`MuxInput-{id}`) instead of writing on the connection reader. A child that stops reading
    stdin can no longer stall every pane that shares the GUI's connection.
  - The queue is byte-capped at 16 MiB per session. Over the cap the input is dropped and logged,
    the way a real terminal loses input to a wedged child.
  - Parser replies (`OnResponse`) use the same queue, so their order with user input is kept.
- `Dispose` stays safe against a late accept or spawn (Phase 1). The host catches everything on
  its timer thread.

## 5. `ntilde mux` CLI (`Ntilde.App/Shell/Mux/MuxCommand.cs`)

Dispatched from `src/Ntilde.App/Program.cs` before `AppLogger.Initialize()`, and from
`src/Ntilde.Cli/Program.cs`. Exit codes: 0 ok, 1 the operation failed (incl. "no daemon running"),
2 bad command line.

| Verb | Behaviour |
|---|---|
| `mux serve [--idle-exit-minutes N] [--foreground]` | Daemonises (below), hosts `MuxDaemonHost` with `DefaultTerminalSessionFactory`, logs to `logs/mux.log`. Refuses (exit 1) if a live descriptor names another daemon. |
| `mux ls [--json]` | Table: id, state (running / exited N / faulted), attached, size, title. `--json` = `ListSessionsResult` via `MuxJsonContext`. |
| `mux kill <sessionId>` | `kill`; exit 1 for `unknown_session`. |
| `mux kill-server` | `shutdown`; waits up to 5 s for the descriptor's pid to exit. |

`CliConsoleBindings.Prepare()` runs for every verb except `serve`.

**Daemon start** (skipped with `--foreground`):
- Windows:
  - `FreeConsole()`.
  - `NTILDE_PTY_NO_PASSTHROUGH=1` is set in the process environment before the first spawn,
    because a console-attached host takes the passthrough ConPTY path.
- Unix:
  - `setsid()`. It succeeds because the spawner never makes the daemon a group leader.
  - `PosixSignalRegistration` cancels SIGHUP.
  - `/dev/null` is `dup2`'d over fds 0–2.
- `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException` are logged.
- SIGTERM/SIGINT (`PosixSignalRegistration`, works on Windows for Ctrl+C) trigger a clean
  shutdown: kill the sessions, delete the descriptor.

## 6. Launcher and GUI connection (`Ntilde.App/Shell/Mux/`)

- **`MuxDaemonLauncher`**:
  - `TryConnectExistingAsync` reads the live descriptor and connects within 2 s.
  - `EnsureConnectedAsync` connects to an existing daemon, or spawns one and polls the descriptor
    every 50 ms for up to 10 s, then connects.
  - The spawn uses `UseShellExecute=false`, `CreateNoWindow=true` and redirects all three stdio
    streams, which the spawner closes immediately.
  - On Windows the spawner first clears `HANDLE_FLAG_INHERIT` on its own std handles
    (`SetHandleInformation`). Otherwise `bInheritHandles=TRUE` would hand a captured parent pipe
    to the daemon: the MSBuild hang from `CLAUDE.md`.
  - Executable: `$APPIMAGE` when set and it exists, otherwise `Environment.ProcessPath`.
    Injectable for tests (`dotnet Ntilde.dll`).
  - Windows puts the app in no job object, so the child outlives the parent.
- **`MuxConnectionHost`** keeps one shared `MuxClient` for the GUI.
  - `WarmUp()` starts `EnsureConnectedAsync` on the thread pool at app start (setting on).
  - `GetClient(TimeSpan timeout)` is synchronous for the UI thread. It waits on the warm-up or
    reconnect task inside `Task.Run(...).Wait(timeout)`, so no sync context is captured.
  - A disconnected client is replaced on the next `GetClient`.

## 7. Setting and factory

- `TerminalSettings.SessionPersistence`: `"Off" | "KeepOnClose"`, default `"Off"`, parsed by
  `SessionPersistenceMode.IsKeepOnClose(string?)` (trimmed, ordinal-ignore-case; anything else is
  off).
  - Read by MainWindow only, so it stays out of the `ApplySettings` whitelist (the
    `AgentIndicatorTabRollup` precedent).
  - Registered in `SettingsTools` (schema row, example, `StringFields`, `KnownFields`) and in the
    Settings window (a ComboBox, stored value in `Tag`).
  - Applies to panes created after the change.
- `TerminalSessionRequest` gains `Guid? ExistingMuxSessionId = null` (trailing optional positional).
- `IPersistentSessionFactory : ITerminalSessionFactory` adds
  `PersistentSessionResult CreatePersistent(TerminalSessionRequest)`, which returns
  `(ITerminalSession Session, PersistentSessionOutcome Outcome, string? Endpoint, string? Detail)`.
  Outcomes: `NotPersistent` (SSH), `Spawned`, `Reattached`, `PreviousLost`, `Unavailable`.
- `MuxTerminalSessionFactory : IPersistentSessionFactory` (App):
  - SSH → fallback factory, `NotPersistent`.
  - Local: `GetClient(5 s)`. With `ExistingMuxSessionId`, `ListSessionsAsync`; a running match →
    `OpenSession` → `Reattached`; missing or exited → spawn → `PreviousLost`. Without one → spawn
    (3 s timeout) → `Spawned`.
  - Any failure → fallback session → `Unavailable` + log.
  - The returned `MuxClientSession` is **not attached**.
  - `Create` = `CreatePersistent(...).Session`.
- Pane banners:
  - `Unavailable` → `[Multiplexer unavailable — this session will not persist]`.
  - `PreviousLost` → `[Previous session was lost — started a new shell]`.

## 8. Pane and view wiring

- **`TerminalView.DefersBufferResizeToSession`**: when set, sites A (font change), B (first
  layout / size change) and C (throttled dispatch) raise `OnResize` but do not resize the buffer.
  - They track `_lastRequestedCols/Rows` for "did the grid change", so a request is not repeated.
  - `NotifySessionResizedBuffer()` (UI thread) records the buffer's real size as the dispatched
    grid, resets mouse-motion tracking, clears and resizes the row cache, clamps the scroll offset
    and invalidates.
  - Everything else (selection, hover, scroll offset) already reads `_buffer` under its lock at
    use time.
- **`TerminalPane`**, when `Session is MuxClientSession mux`:
  - `CreateAndWireParser(forceConPtyFiltering: mux.ForceConPtyFiltering, muxBacked: true)` rebuilds
    the parser with `ImageDecoder = null`, `AllowNativeKittyGraphics = false`, `ReadFileBytes = null`.
  - `SnapshotReceived` → `TerminalStateTransfer.Restore` on the delivery thread, then a posted
    `NotifySessionResizedBuffer` + `QueueOutputUiRefresh`. Short-circuit when `_disposed` or
    `Session` is no longer `mux`.
  - `StreamResize` → `Buffer.Resize` on the delivery thread, then the same post.
  - `Disconnected` / `Faulted` → post the banner `[Multiplexer disconnected] [Press Enter to
    reconnect]`, remember `mux.Id` for reattach, and do **not** raise `ProcessExited`.
  - `ShouldReconnectOnEnter` also covers `MuxClientSession { IsConnected: false }` and `IsFaulted`.
  - `Reconnect()` passes the remembered id as `ExistingMuxSessionId`. The snapshot replaces the
    banner-polluted buffer.
  - `BuildMuxPresentation()` is built from the view metrics (cell DIPs × `EffectiveRenderScaling`),
    the effective theme's fg/bg `ToUint()` and `EnableKittyKeyboardProtocol`.
    `UpdatePresentation` runs on `ApplySettings` and `MetricsChanged`.
  - `AttachAsync(Math.Clamp(MaxHistory, 0, 50_000), presentation)` runs after all handlers are
    wired. A failure writes a banner and a log line. Success raises `PersistentSessionAttached`.
- `ClosePaneAsync`'s confirmation refreshes a mux session's child-process state first
  (`RefreshSessionInfoAsync`, 1 s timeout).

## 9. Close semantics, startup reattach, updates (MainWindow)

- `DisposeControlTree(control, PaneDisposition)`. Every existing caller is user-initiated →
  `EndSession`: `mux.Kill()` then `Dispose()`. Window teardown never disposes panes (unchanged).
  With mux sessions open, `PerformAppTeardown` explicitly detaches (`MuxConnectionHost.Dispose()`
  closes the connection; the daemon keeps every session) and logs
  `N sessions kept running; ntilde mux ls`. `PerformAppTeardown` becomes idempotent.
- `SessionManager.BuildPaneTree` writes `MuxSessionId`/`MuxEndpoint` for mux panes.
  `RestorePaneTree` sets `pane.MuxSessionIdToRestore`, and the pane passes it once as
  `ExistingMuxSessionId`. Deferred tabs attach when hydrated. The saved-session file is also
  written (coalesced) after each successful attach.
- **Orphans:** after startup restore, running daemon sessions that no saved pane references and
  that have no attached client open as new tabs, with the toast "Reattached N detached sessions".
- **Updates:** `ApplyStagedUpdate` becomes async. If a daemon is live with N running sessions, it
  confirms "N multiplexed sessions will be closed", then sends `shutdown` before teardown. The
  protocol version range stays the backstop.
- Quake `Hide()`/`Show()` is untouched. Once-per-process mux init is guarded against `OnOpened`
  re-raising.

## 10. Deliberate differences from the task text

1. **Per-session input writer** added (§4). It was not asked for, but PR #474 listed it as open,
   and one shared GUI connection makes head-of-line blocking user-visible.
2. **`shutdown` protocol method** added for `kill-server`. The task named the verb, not its wire.
3. **Snapshot restore and `StreamResize` run on the delivery thread.** PR #474's "marshal events
   off the delivery thread" is resolved the task's way. Only the view bookkeeping is posted.
4. **Window-close summary is a log line**, not a toast: the window is gone when it would show.
5. **The Unix ownership check is implicit** through the `0700` mode check (§3), not a `stat` call.
6. **`Create` may block up to 5 s** on first use while the warm-up is still connecting. After
   that it falls back and says so.
