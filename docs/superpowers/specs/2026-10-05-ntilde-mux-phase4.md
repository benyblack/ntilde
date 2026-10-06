# ntilde multiplexer — Phase 4: remote daemons over SSH, `ntilde.com`, review carry-overs

**Status:** built; §15 records every place the build departs from this design. **Branch:** `feat/mux-phase4` off `origin/dev-mux` (91d41f1); the PR targets `dev-mux`.
**Predecessors:** Phase 0 (#472), Phase 1 (#474), Phase 2 (#489), Phase 3 (#499,
`docs/superpowers/specs/2026-09-29-ntilde-mux-phase3.md`).

## 1. Goal

An SSH tab can survive a network drop. This is the WezTerm "SSH domain" model:

- the daemon runs on the remote host;
- the GUI reaches it through an SSH exec channel;
- a drop is a reconnect followed by a snapshot plus the tail of the output.

The phase also pays two debts: the ten carry-overs from the PR #499 review, and the Windows
`ntilde.com` console launcher deferred from Phase 3.

It ships as two new AOT binaries:

| Binary | Project | Purpose |
|---|---|---|
| `ntilde-mux` | `src/Ntilde.Mux.Daemon` | Remote daemon, proxy and CLI. linux-x64, linux-arm64 and osx-arm64; one file each. |
| `ntilde.com` | `src/Ntilde.Launcher` | Windows console launcher for `Ntilde.exe`. |

Everything stays behind `TerminalSettings.SessionPersistence` plus a new per-profile flag
`SshMuxOptions.PersistRemoteSessions`. With both off, nothing changes.

### Non-goals

- Windows remote hosts. musl and BSD hosts are refused with a clear message, and the tab falls back to
  plain SSH.
- SFTP sidebar, remote files and port forwards on persisted remote panes (§8.4; follow-up).
- Listing or adopting remote sessions in the "Attach to session…" picker, and orphan adoption for
  remote endpoints (follow-up). A remote shell is reattached by restore and reconnect, never by
  adoption.
- A TCP listener on either side. The remote daemon is reachable only through SSH.
- Authentication beyond SSH. The remote socket is current-user-only, exactly as locally (0600 socket
  in a 0700 directory).

## 2. Decisions the brief left open

1. **The remote binary links `rusty_pty` statically. There is no tarball.**
   - A spike (2026-10-05, `mcr.microsoft.com/dotnet/sdk:10.0`, linux-x64) built `rusty_pty` with
     `crate-type = ["cdylib", "staticlib"]` and linked `librusty_pty.a` into a NativeAOT console app
     through `<DirectPInvoke Include="rusty_pty"/>` + `<NativeLibrary Include="…/librusty_pty.a"/>`.
   - No extra linker arguments were needed. The result is one 1.7 MB executable whose only dynamic
     dependencies are `libc`, `libm`, `libgcc_s` and the loader, and it spawned a PTY child.
   - Only `rusty_pty` is linked; `Ntilde.Mux` never touches `rusty_ssh`. So there is no duplicate
     Rust std, which is the usual failure when two Rust staticlibs meet in one binary.
   - osx-arm64 is proven by the new CI job (§10.2). If it fails there, that RID alone falls back to
     `{exe, librusty_pty.dylib}`, and the PR says so.
2. **The daemon binary is downloaded with checksum verification, not bundled.**
   - Bundling would add three binaries to every desktop package.
   - The install flow (§9) downloads the asset matching the running app version from the GitHub
     release and verifies its `.sha256`.
   - It also accepts a local file, and it can copy a `curl … && sha256sum -c && chmod` one-liner to the
     clipboard for offline hosts.
3. **The native transport does not share a pane's connection.** Neither SFTP nor forwards use it.
   - A native connection lives exactly as long as its PTY shell channel. The Rust worker's main loop
     ends when that channel closes, and only `NativeSshSession`'s poll loop may consume the handle's
     events.
   - SFTP and remote listing already open a fresh connection on every call.
   - So the exec transport is its own connection, in a new exec mode (§8.3), and persisted remote panes
     go without the SFTP sidebar, remote files and port forwards in this phase. That is documented,
     and a follow-up is filed (§14).
4. **The install upload streams over the exec channel instead of SFTP** (a deviation from the brief;
   §9.3). The bytes go through `cat` to a temporary file, then `chmod 755`, then `mv -f` onto the final
   name. One path serves both transports. Compared with `RunSftpTransfer`, it:
   - replaces a running binary atomically, where SFTP's create-with-truncate fails with `ETXTBSY`
     while a daemon runs from that file;
   - can answer auth prompts, where `RunSftpTransfer` authenticates without prompting and refuses an
     untrusted host;
   - sets the mode, which the SFTP upload does not.
5. **A reconnect does not leave a stale client attached.** The new optional `HelloParams.ClientInstanceId` (§3) makes the
   daemon close an older connection that carries the same id. Without it, the half-open connection
   left by a network drop would keep the session "shared with 1". It would also block the
   `IfUnattached` restore rule until TCP noticed the drop. The protocol stays Min 1 / Max 2.
6. **`ntilde.com` does not pin the console for GUI launches** (an addition to the brief; §11.3). In GUI mode
   the app signals an event that the launcher passed in. The launcher then returns at once with exit
   code 0, so typing `ntilde` in PowerShell does not block the prompt until the window closes. CLI
   modes behave exactly as specified: the launcher waits and forwards the exit code.
7. **Ctrl+C is delivered by the console itself; it is not regenerated** (a deviation from the brief;
   §11.2). The child shares the launcher's console and process group, so Windows delivers Ctrl+C and
   Ctrl+Break to it directly. The launcher's handler only swallows the event for itself and keeps
   waiting. `GenerateConsoleCtrlEvent` cannot target a single process group with `CTRL_C_EVENT`, and
   a child started with `CREATE_NEW_PROCESS_GROUP` has Ctrl+C disabled.
8. **Carry-over 6 polls on `EAGAIN` instead of clearing `O_NONBLOCK`** (a deviation from the brief;
   §4.6). `fcntl(F_SETFL)` is variadic. On macOS arm64, variadic arguments go on the stack, so a
   non-variadic P/Invoke hands `fcntl` a garbage flags word; this is the reason the text client
   already avoids `ioctl`. `poll(2)` is not variadic, and polling leaves the inherited file description
   as the parent shell set it.

## 3. Protocol delta (`Ntilde.Mux.Contracts`, additive; Min 1 / Max 2)

| Item | Change |
|---|---|
| `HelloParams.ClientInstanceId` | New `string?` (at most 64 characters; anything longer is ignored). A GUI endpoint host generates one random id and reuses it across reconnects. When a hello carries an id, the server closes every *other* open connection with the same id before it replies `welcome`. Those connections are dead twins left by a dropped link, and closing them detaches their sinks through the normal connection-close path. The text client and the CLI send none. A Phase 3 daemon ignores the field (unknown members are skipped), so a GUI reattaching after a drop always uses `Shared` (§7.4) and never depends on the eviction. |
| `SessionChangedNotification.InteractiveClients` | New `int?`, written only when non-null. Carry-over 9. |
| `SessionInfoResult.InteractiveClients` | New `int?`. Carry-over 9. |
| `SpawnParams.Command` empty | Means "the daemon's default login shell". Only `ntilde-mux` daemons (`LocalShellSessionFactory`, §6.3) give it that meaning. The GUI's own daemon would refuse it (its `DefaultTerminalSessionFactory` hands it to rusty_pty), so it must never be asked: local spawns always carry a command, and `ntilde-mux` serves a root of its own (§15, final review F3), so a remote client never reaches the GUI's daemon, even on a host that runs both. |
| `MuxEndpointDescriptor.StartTime` | New `long?`: the daemon's `Process.StartTime` in UTC ticks. This is the descriptor file, not the wire. Carry-over 1. |

The `JsonSerializerContext` stays source-generated, with `WhenWritingNull`.

## 4. Carry-overs from the PR #499 review

Each carry-over lands first, with a pinning test (§12.1).

1. **`kill-server --force` and recycled pids.**
   - `MuxDaemonHost.CreateDescriptor` records `StartTime`.
   - `MuxDiscovery.IsProcessAlive(pid, name, startTimeTicks?)` checks it. With a recorded time, the
     process's own start time must match within one second, or the pid is treated as dead. A
     descriptor without a time, written by an older daemon, keeps today's check.
   - `MuxCommand.KillByPid` (moved to `MuxCli`, §6) re-reads `process.StartTime` on the
     `Process` object it is about to kill, immediately before `Kill`. On a mismatch it refuses with
     `pid N is no longer the multiplexer; nothing was terminated.` and exits 1.
2. **The Unix startup probe deletes a live daemon's descriptor.** The `IOException` branch of
   `MuxStartupProbe.IsDaemonLive` now deletes only on a genuine refusal, decided by
   `MuxStartupProbe.IsGenuineRefusal(exception, endpoint, fileExists)`, a pure function:
   - an inner `SocketException` with `SocketErrorCode == ConnectionRefused`; or
   - an absent socket file, off Windows; or
   - a `FileNotFoundException`, on Windows.

   Everything else (`EACCES`, `EAGAIN`, a too-long path, a timeout off Windows) counts as live. The
   descriptor is kept, and the gate stays closed.
3. **Shared tabs restore as owned.** `PaneNode` gains `[JsonIgnore(WhenWritingDefault)] bool MuxShared`.
   - `SessionManager.WriteMuxIds` sets it from `TerminalPane.MuxSessionIsShare`.
   - `ApplyRestoredMuxId` sets `MuxAttachSharedToRestore = node.MuxShared`.
   - A restored share therefore reattaches `Shared` with outcome `Reattached`: no fresh shell, no
     "previous shell in use" toast.
   - If the session has gone, the restored share takes the existing `ShareEnded` path.
4. **The text client leaves outer autowrap on.**
   - `TextClientRenderer.EnterSequence` gains `CSI ?7l`, and `LeaveSequence` gains `CSI ?7h`.
   - Without this, painting the last column of the last row scrolls the outer screen by one line.
5. **A shared text attach downgrades the GUI's kitty keyboard.** `HeadlessTerminalSession` keeps each
   interactive sink's last presented `KittyKeyboardEnabled`. The parser's flag becomes the AND over
   the interactive subscribers.
   - It is recomputed on every attach, presentation-carrying resize, detach and sink drop.
   - Read-only sinks do not take part.
   - With no interactive subscriber, the last value stays.
   - So a text client (which sends `false`) turns kitty off only while it is attached, and its
     detach turns kitty back on for the GUI.
6. **Unix `read(0)` `EAGAIN` means "input closed".** `UnixConsoleSurface.Read` handles
   `EAGAIN`/`EWOULDBLOCK` (11 and 35 on Linux; 35 on macOS) by calling
   `poll({fd 0, POLLIN}, 1, -1)` (retrying on `EINTR`) and then retrying the read. Only `n == 0`,
   or an error other than `EINTR`/`EAGAIN`, ends input (§2 decision 8).
7. **Descriptor repair TOCTOU** (`MuxDaemonHost.EnsureDescriptor`).
   - **Missing descriptor:** the host writes the temp file, then `File.Move(temp, path,
     overwrite: false)`. That is create-new: if another daemon wrote the file first, the move fails
     and the host leaves the newer file alone.
   - **Foreign descriptor naming a dead pid:** the host re-reads the file immediately before
     replacing it. It replaces the file only if the content is byte-for-byte what it judged dead.
     Otherwise it gives up for this tick.
   - Both paths log what they decided.
8. **`WirePane` never unsubscribes `MuxAdoptionLost`.** The leading unsubscribe block in
   `MainWindow.WirePane` gains `pane.MuxAdoptionLost -= OnPaneMuxAdoptionLost;`. Toggling persistence
   re-wires every pane, so without this one loss closes the pane twice.
9. **`InteractiveClients` is missing from `sessionChanged` and `sessionInfo`.** §3 adds the field.
   `HeadlessTerminalSession.PublishAttachedCount` then marks a change when either member of the pair
   `(attached, interactive)` changes, so a ReadOnly → Shared re-attach of the same sink is now a
   change. On the client:
   - `MuxClientSession.InteractiveClients` is fed by the notification, by `RefreshSessionInfoAsync`
     and by `RefreshSharingAsync` (v2 listSessions).
   - The badge ("shared with N", N = interactive others) and `DecidePaneCloseAsync` both use
     `InteractiveOthers`. That is `InteractiveClients - 1`, or `AttachedClients - 1` when the daemon
     did not send `InteractiveClients`.
   - The skipConfirm (agent/MCP) close's `Leave` decision uses it as well.
   - So a read-only peek neither raises the badge nor turns a close into a detach.
10. **The picker and shared-close dialogs ignore Enter and Escape.**
    - Picker: Attach is `IsDefault`, Cancel is `IsCancel`.
    - Shared-close: Detach is `IsDefault` (the non-destructive choice), and Cancel is `IsCancel`.
      "Close (ends it)" is never the default.

## 5. Endpoint identity and connection hosts (`Ntilde.App`)

- **`PaneNode.MuxEndpoint`** is now defined. Its value is `local`, or `ssh:<sshProfileId>` where the
  id is a Guid in `N` format. Null, written before Phase 4, means `local`.
  - `MuxEndpointId` (App, `Shell/Mux/MuxEndpointId.cs`) parses it and formats it. It is a value type
    with `IsLocal`, `SshProfileId` and `ToString()`.
- **`MuxConnectionHosts`** is a registry with one `MuxConnectionHost` per endpoint. It is created
  lazily by `GetOrCreate(MuxEndpointId)`.
  - `local` is the existing host, unchanged: same connect function, same 30 s cooldown, and lazy
    reconnect on Enter.
  - A remote host is built by `RemoteMuxHostFactory` from the profile. Its policy differs from local:

    | | Local | Remote |
    |---|---|---|
    | `ConnectTimeout` | 5 s | 120 s, so an askpass prompt can be answered |
    | `FailureCooldown` | 30 s | 0, so a user retry is never swallowed |
    | `RpcTimeout` | 3 s | 10 s |
    | Liveness ping | none | §7.2 |
    | Reconnect loop | none | §7.3 |
- **The kill and teardown paths go through the pane's host.**
  - `KillMuxSessionOnClose` uses `Hosts.Get(pane.MuxEndpoint).TrackPendingKill`.
  - `PerformAppTeardown` disposes every host. The local host goes last, and each host flushes its own
    tracked kills.
- **Restore and dedupe key on `(endpoint, id)`.** `SessionManager.DedupeMuxIds` and
  `MuxOrphans.CollectReferencedIds` do this, and orphan adoption and its counts consider only
  `local` ids.
- **A never-spawned pane keeps its endpoint.** `TerminalPane.MuxEndpoint` gets an internal setter,
  and `ApplyRestoredMuxId` restores it, so a pane whose restore is still pending writes the endpoint
  back unchanged.

## 6. The daemon core moves into `Ntilde.Mux`

### 6.1 Layout

| New namespace (dir) | Types moved from `Ntilde.App/Shell/Mux` | Notes |
|---|---|---|
| `Ntilde.Mux.Daemon` (`Daemon/`) | `MuxDaemonLauncher`, `MuxUnavailableException`, `IMuxDaemonSpawner`, `ProcessMuxDaemonSpawner`, `MuxDaemonExit`, `MuxStartupProbe` | `MuxDaemonProcess` becomes `MuxServeHost`; new: `MuxPaths`, `LocalShellSessionFactory`, `MuxLogFile` |
| `Ntilde.Mux.Cli` (`Cli/`) | the verb bodies of `MuxCommand` | `MuxCli`, `MuxCliHost`, `MuxProxyCommand`, `MuxVersionInfo` |

`Ntilde.Mux` still references only Pty, VT, Replay and Mux.Contracts.

### 6.2 Injected paths and hosting

```csharp
public sealed record MuxPaths(string Root)           // Root = MuxDiscovery.GetRootDirectory() unless overridden
{
    public string DescriptorPath => MuxDiscovery.GetDescriptorPath(Root);
    public string Endpoint => MuxDiscovery.GetDefaultEndpoint(Root);
    public string LogDirectory { get; init; } = Path.Combine(Root, "logs");
}

public sealed class MuxCliHost                     // what an executable supplies
{
    public required MuxPaths Paths { get; init; }
    public required string ExecutableDisplayName { get; init; }   // "ntilde" or "ntilde-mux" in usage text
    public required IReadOnlyList<string> ServeArguments { get; init; } // ["mux","serve"] or ["serve"]
    public required Func<ITerminalSessionFactory> SessionFactory { get; init; }
    public required MuxCliVerbs Verbs { get; init; }               // flags: which verbs this exe offers
    public Action? PrepareForegroundConsole { get; init; }         // App: CliConsoleBindings.Prepare
    public bool AttachedToParentConsole { get; init; }             // App: from PrepareInteractive
    public string Version { get; init; } = "";
}
```

- **`MuxCli.Execute(string[] verbArgs, TextWriter stdout, TextWriter stderr, MuxCliHost host)`** holds
  every verb. It covers the same exit codes, usage text, the `$APPIMAGE` rule (in
  `ProcessMuxDaemonSpawner`), the trust check (`MuxDaemonLauncher.IsTrustedEndpoint`) and the
  nested-attach guard as today.
- **`MuxServeHost.Run`** is today's `MuxDaemonProcess.Run`. It has three changes:
  - it takes `MuxPaths`, `ITerminalSessionFactory` and the log directory;
  - it writes `mux.log` through `MuxLogFile`, a small rotating writer (16 MiB rotation, 8192-entry
    queue) copied from `RotatingFileLogWriter` with its tests;
  - it no longer touches `AppPaths`.
- **`ProcessMuxDaemonSpawner`** takes the serve arguments instead of hard-coding `mux serve`.
- **`MuxDaemonLauncher`** gains `EnsureEndpointStreamAsync(ct)`. It returns the raw connected
  `Stream`, spawning the daemon on demand, but does not do the hello. `EnsureConnectedAsync` is that
  stream plus `MuxClient.ConnectAsync`. The proxy (§8.1) uses the raw stream.

### 6.3 `LocalShellSessionFactory` (`Ntilde.Mux.Daemon`)

This is the remote daemon's `ITerminalSessionFactory`: `RustPtySession` over the request.

- **Empty command.** It resolves `ShellHelper.GetDefaultShell()` and, for `bash`, `zsh`, `fish`,
  `ksh`, `sh` and `dash`, adds `-l`, so the shell gets the same login environment `sshd` would give
  it.
- **Working directory.** A `~` or `~/…` working directory expands against `$HOME`. A directory that
  does not exist falls back to `$HOME`.
- **SSH.** It refuses an SSH request (`NotSupportedException`). The daemon never spawns SSH:
  `MuxServer` always passes `Ssh: null`.

The GUI's own daemon (`ntilde mux serve`) keeps `DefaultTerminalSessionFactory`, injected by the App
adapter, so local behaviour cannot change.

### 6.4 The App adapter

`Ntilde.App`'s `MuxCommand` stays in `Ntilde.Shell.Mux`, with the same public static surface:
`IsSupportedCliMode`, `IsServe`, `IsAttach`, `NeedsInteractiveConsole`, `Execute` and
`AttachedToParentConsole`. That keeps `CliCommandDispatchTests` and `Program.cs` byte-identical in
their mux lines. (`AttachConsoleHint` is removed with the `cmd /c` hint, §11.4.)

`Execute` strips the leading `mux` and calls `MuxCli.Execute` with:

- `Paths = new MuxPaths(rootOverride ?? MuxDiscovery.GetRootDirectory())`;
- `ServeArguments = ["mux", "serve"]`;
- `SessionFactory = DefaultTerminalSessionFactory.Instance`;
- `PrepareForegroundConsole = CliConsoleBindings.Prepare`;
- `Verbs = serve | ls | kill | kill-server | attach | probe-console`.

The Phase 2/3 tests for moved code move to `Ntilde.Mux.Tests` (`Daemon/`, `Cli/`). The adapter keeps a
small `MuxCommandTests` in App.Tests (dispatch, console hint, the root override reaching `serve`).

## 7. Remote connection, reconnect and restore (`Ntilde.App`, `Ntilde.Platform`, `Ntilde.Mux`)

### 7.1 Connecting

`RemoteMuxConnector.ConnectAsync(ct)` (App, `Shell/Mux/Remote/`):

1. Build the remote command: `<path> proxy --stdio`. `<path>` is `SshMuxOptions.RemoteDaemonPath`
   when the install flow recorded one. Otherwise it is `"$HOME/.local/share/ntilde/bin/ntilde-mux"`,
   which the remote shell expands to an absolute path. Either way there is no PATH lookup.
2. `ISshExecTransport.Start(command)`, through the profile's backend: OpenSSH (§8.2) or native
   (§8.3). It runs off the UI thread; prompts reach the user through askpass or the native
   interaction handler.
3. `StdioMuxTransport.ConnectAsync(channel.Stdout, channel.Stdin, ct)` (§8.1), then
   `MuxClient.ConnectAsync(stream, options with ClientInstanceId)`.
4. Failures are classified by `RemoteMuxFailureClassifier.Classify(exitCode, captured, stderr)`, a pure
   function, into these kinds:

   | Kind | When |
   |---|---|
   | `NotInstalled` | exit 127, or "not found" / "No such file" for the path |
   | `Unsupported` | exit 126, "Exec format error", "cannot execute binary file", or `GLIBC_… not found` (reason text kept) |
   | `VersionMismatch` | the handshake's `version_mismatch` |
   | `SshFailed` | OpenSSH exit 255, or a native connect error |
   | `ProxyFailed` | anything else |

   The result is a `MuxUnavailableException` carrying a `RemoteFailureKind` and the text the user
   should see.

### 7.2 Liveness

A dropped link is silent until TCP notices, which can take tens of minutes. Every remote host
therefore pings its client every 15 s with a 10 s timeout, from one `System.Threading.Timer`:

- A ping failure disposes the client. That raises `Disconnected` with reason `ping timeout`.
- The ping is skipped while a request is in flight. Any reply resets the clock.

### 7.3 Reconnect loop

On `Disconnected`, the remote host classifies the loss:

- **The proxy exited with code 3 (the daemon's process is gone; §8.1).** The host raises
  `DaemonStopped`, and there is no automatic retry.
- **Anything else** (ping timeout, SSH exit 255, the proxy's exit 4 for a connection a live daemon
  dropped, channel EOF, native disconnect). The host raises `ConnectionLost` and starts the loop.

The loop:

- Runs on one `System.Threading.Timer`, rescheduled per attempt and never on a delivery thread.
- Backs off 1, 2, 4, 8, 16 then 30 s. Each delay is jittered by ±20%, with a 30 s ceiling.
- Each attempt calls the host's normal connect, which joins an in-flight one.
- On success it raises `Reconnected(MuxClient)`.
- After `ReconnectBudget` (10 minutes from the loss; a constant, not a setting) it stops and raises
  `ReconnectAbandoned`.
- A user-initiated `GetClient` (Enter in a pane) while the loop is sleeping attempts at once and
  resets the backoff.

### 7.4 Pane wiring (`TerminalPane`)

- **Routing.** A pane whose profile is SSH, and whose SSH profile has `PersistRemoteSessions` while
  `SessionPersistence` is on, routes through `IPersistentSessionFactory.CreatePersistent`. This
  replaces the plain `SessionFactory.Create`. The predicate is
  `MuxTerminalSessionFactory.RoutesRemote(request)`, and `TakeMuxSessionIdToRestore` /
  `TakeMuxAttachShared` stop skipping such panes.
- **The factory call runs off the UI thread** (`Task.Run`). Meanwhile:
  - the pane shows `[Connecting to <host>…]`;
  - a generation counter discards a stale result;
  - a pane disposed meanwhile disposes the result's session.

  The result is applied on the UI thread through the same code that handles local results
  (`ApplyPersistentResult`, extracted from `CreateLocalSession`).
- **Connection lost** (the host's `ConnectionLost` while this pane's session is current):
  1. The pane writes `[Connection to <host> lost — reconnecting…]` and keeps its last screen.
  2. It sets `_muxReconnecting`.
  3. It calls `TermView.SetSession(null)`, so keys reach the pane.
  4. **Input typed while reconnecting is dropped, never queued.** The first dropped key of each episode
     writes `[Input is not sent while reconnecting]`.
- **`Reconnected`.**
  - Each reconnecting pane re-runs the factory off the UI thread, with
    `ExistingMuxSessionId = <its id>` and `ReattachAfterDrop = true`. That is a new trailing optional
    on `TerminalSessionRequest`, and it makes the factory open the session `Shared`.
  - The pane attaches. The snapshot replaces the screen, so the banners vanish.
  - A session that is gone takes the existing `PreviousLost` path: a fresh shell and the "previous
    session was lost" notice.
- **`ReconnectAbandoned`.** The pane writes `[Connection to <host> lost] [Press Enter to reconnect]`
  and behaves like today's lost-connection state. Enter runs a reattach attempt.
- **`DaemonStopped`.** The pane writes `[ntilde-mux on <host> stopped] [Press Enter to reconnect]`.
  Enter reconnects (the proxy spawns a new daemon), and the missing session gives `PreviousLost`, so
  a fresh shell starts with the notice.
- **Bootstrap environment.** The GUI-host bootstrap environment (`_shellIntegrationEnvOverrides`) is
  never sent: a remote `SpawnParams.EnvironmentOverrides` is always null. OSC 133 comes from the
  remote shell-integration installer, as for plain SSH, and the daemon sets `NTILDE_MUX_SESSION`
  itself.

### 7.5 Factory outcomes for a remote request

`MuxTerminalSessionFactory.CreatePersistent`, for a remote request:

1. **Ensure the connection.** It calls `Hosts.GetOrCreate(ssh:<id>).GetClient(120 s)`.
2. **Then one of three paths:**
   - **No existing id:** `spawn` with `SpawnParams { Command = "", StartingDirectory =
     profile.WorkingDirectory, EnvironmentOverrides = null, Title = profile.Name }`.
   - **Existing id:** `listSessions` + `OpenSession(id, IfUnattached)`, with exactly the local rules:
     `Reattached`, `PreviousLost`, `AttachedElsewhere` (pane-side, after `session_attached`), and no
     command-mismatch check. A remote daemon session's command is the remote default shell, which the
     GUI does not know.
   - **`ReattachAfterDrop`:** `OpenSession(id, Shared)`.
3. **Failures:**
   - **No existing id:** `Unavailable`. The session is the fallback factory's **plain SSH** session,
     and the pane raises the notice `Persistent SSH unavailable` /
     `[<host>: <reason> — this tab will not survive a disconnect]`.
     - For `NotInstalled`, `VersionMismatch` and `Unsupported`, the toast carries an action, *Install
       ntilde-mux…* or *Update ntilde-mux…*, which opens the install flow (§9) for the profile.
     - `Unsupported` carries no action. Its reason is the exact text, for example
       `musl libc is not supported`.
   - **Existing id:** `DaemonUnreachable`. The id is kept, the pane shows
     `[<host> not reachable — press Enter to retry]`, and the toast carries the same action.
4. `PersistentSessionResult.Endpoint` is `ssh:<id>`. It gains `RemoteFailure`
   (`RemoteFailureKind?`) and `HostDisplayName`.

The toast gets one generic action button: `ShowRecordingToast(…, actionLabel, action)`, an additive
change to the toast that recordings and notices already share.

### 7.6 Restore

- `SessionManager.ApplyRestoredMuxId` sets `pane.MuxEndpoint` from `node.MuxEndpoint`.
- An SSH pane restoring an `ssh:` endpoint whose profile still has the flag reattaches through §7.5:
  `IfUnattached`, the same exclusivity as local.
- If the profile no longer has the flag, or the profile is gone, the pane ignores the id and opens
  plain SSH. The id is kept for one save, then dropped.
- Several panes of one profile share one connection, and so one set of prompts.

## 8. Transports

### 8.1 `ntilde-mux proxy --stdio` and `StdioMuxTransport` (`Ntilde.Mux`)

**Proxy** (`MuxProxyCommand`):

1. `MuxDaemonLauncher.EnsureEndpointStreamAsync` connects to the host-local daemon, or spawns one on
   demand: `ntilde-mux serve`, detached, with the lock and the descriptor. The UDS is under the
   remote `MuxDiscovery` root, with the existing `$XDG_RUNTIME_DIR` fallback for long paths.
2. It writes one preamble line to stdout, `NTILDE-MUX-PROXY 1 <daemonPid>\n`, and flushes.
3. It pumps bytes on two dedicated threads: stdin → socket and socket → stdout, using raw
   `Console.OpenStandardInput/Output` streams with 64 KiB buffers. The proxy never parses a frame.
4. **When either side closes:**
   - **stdin EOF:** shut down the socket's send side, close the socket and exit **0**;
   - **socket EOF or error:** flush and close stdout and the socket. Then exit **3** once the daemon's
     process is gone (`MuxDiscovery.IsProcessAlive` with the descriptor's pid, process name and start
     token, so a recycled pid does not count), asked every 50 ms for up to 1.5 s, since a daemon that
     stops closes its connections first and exits a moment later; exit **4** when it still runs at the
     end of that wait (it dropped this connection: a newer one with the same client instance id, or a
     client too slow to keep up);
   - **stdout cannot be written** (the preamble or a frame): close both, and exit **4**, never 3;
   - **could not reach or spawn the daemon:** `mux: …` on stderr, exit **1**;
   - **usage error:** exit **2**.
5. Stdout carries nothing but the preamble and frames. Diagnostics go to stderr, and the daemon logs
   to its own file.

**Client** (`StdioMuxTransport.ConnectAsync(Stream stdout, Stream stdin, CancellationToken)`):

- It reads stdout until it finds a line `NTILDE-MUX-PROXY <v> <pid>` with `v == 1`, discarding
  everything before it (rc files and the MOTD can print).
- The scan is bounded to 64 KiB. Past that, or on EOF or a timeout before the preamble, it throws
  `MuxProxyHandshakeException`. The exception carries the captured text (control characters
  escaped, the last 2 KiB at most), so the user sees what the shell printed.
- It returns a `DuplexStdioStream`. That stream reads leftover bytes and then stdout, and writes to
  stdin. Its `Dispose` closes stdin first, which half-closes the channel and makes the proxy exit,
  and then stdout.

### 8.2 OpenSSH exec transport (`Ntilde.Platform/Ssh/Exec/`)

`OpenSshExecTransport.Start(command)`:

- **Arguments.** It spawns `ssh` from `SshLaunchPlanner.Plan(profile)`, which gives
  `-F <generated cfg> <alias> [ExtraSshArgs]`. It adds:
  - `-T -o ClearAllForwardings=yes`, so the profile's forwards do not ride the mux channel, matching
    native;
  - `-o BatchMode=no`;
  - `-- <command>`.
- **Process.** It runs as a `Process` with redirected stdin, stdout and stderr, `CreateNoWindow`, and
  no shell.
- **Askpass.** The askpass contract from 009b1e3, removed in #59, is restored here only:
  - `SSH_ASKPASS=<ntilde exe>`, `SSH_ASKPASS_REQUIRE=force`, `DISPLAY=ntilde`, `NTILDE_SSH_ASKPASS=1`;
  - `NTILDE_SSH_ASKPASS_PROFILE_{ID,NAME,USER,HOST,PORT}`.

  The helper path comes from `ISshAskPassLocator`. The App supplies `Environment.ProcessPath`; dev
  builds supply `Ntilde.Cli` when present.
- **Old OpenSSH.** `SSH_ASKPASS_REQUIRE` needs OpenSSH 8.4 or later. Older clients still use askpass
  when they have no tty and `DISPLAY` is set, and the process has no tty.
- **Channel.** `Stdout`, `Stdin` and `StderrTail` (the last 8 KiB, kept by a dedicated reader
  thread); `Completion` is the exit code.

### 8.3 Native exec transport (`rusty_ssh`, `Ntilde.Platform`)

- **Rust.** `nova_ssh_exec(args, command) -> handle` reuses `NativeConnectArgs` and
  `establish_session`'s hop and auth path, including the prompts surfaced as poll events. The new
  `SessionMode::Exec(command)` changes the session as follows:
  - **Channel:** no shell detection and no PTY. The session does `channel_open_session` and then
    `exec(true, command)`.
  - **Output:** stdout arrives as the existing `Data` events, stderr as the new `ExtendedData`, and
    the exit status as the new `ExitStatus { code }`, before `Closed`.
  - **Input:** the existing `nova_ssh_write` writes stdin. The new `nova_ssh_send_eof(handle)` sends
    EOF on the main channel.
  - **Lifetime:** the main loop ends when the exec channel closes.

  The private `detect_login_shell` helper is rewritten over the same exec primitive, so there is one
  exec implementation.
- **C#.**
  - `INativeSshInterop` gains `Exec(options, command)` and `SendEof(handle)`.
  - `NativeSshEvent` gains the new kinds.
  - `NativeSshExecTransport` owns the handle and **one dedicated poll thread**, which routes each
    event:
    - data → a bounded pipe that backs `Stdout`;
    - stderr → `StderrTail`;
    - prompts → `ISshInteractionHandler`;
    - exit and close → `Completion`.
  - `Stdin` writes through `Write`, and its `Dispose` sends EOF.
  - Options come from the existing `NativeSshSession` option builder: jump hops, identity, agent,
    known hosts, keepalive, and the vault password.

### 8.4 What persisted remote panes do not get (this phase)

A persisted remote pane is not an `ActiveSshSessionRegistry` native session. So:

- the SFTP sidebar and remote files stay disabled for it, showing "Not available on a persistent
  remote tab";
- the profile's port forwards are not established (`ClearAllForwardings`; the native exec mode has no
  forward router).

The follow-up is to run forwards on the exec connection, and register the pane for the sidebar.

## 9. Install flow (`Ntilde.App/Shell/Mux/Remote/RemoteMuxInstaller.cs`)

Entry points:

- *Install ntilde-mux on this host* in the connection editor's Reliability tab. It sits next to the
  new *Keep remote sessions running (ntilde-mux)* checkbox, with a status line beside it:
  `ntilde-mux 0.11.0 installed` / `not installed` / `0.10.0 installed — this app is 0.11.0`.
- The toast action from §7.5.

Every step runs through `ISshExecTransport`, with the same askpass and prompts as the connection.

1. **Probe.** One exec runs `uname -sm; (ldd --version 2>&1 || getconf GNU_LIBC_VERSION 2>&1) | head -n 1;
   printf 'HOME=%s\n' "$HOME"`, with stderr and the exit code captured. `RemoteHostProbe.Parse`, a
   pure function, maps the result:

   | Host | Result |
   |---|---|
   | `Linux x86_64` | `linux-x64` |
   | `Linux aarch64` or `arm64` | `linux-arm64` |
   | `Darwin arm64` | `osx-arm64` |
   | `Darwin x86_64` | refused: "Intel Macs are not supported" |
   | musl (`musl libc` in the ldd line) | refused, with the reason |
   | glibc below 2.35 | refused: "glibc 2.31 is older than 2.35" |
   | FreeBSD, OpenBSD, anything else | refused, with the reason |
2. **Get the asset** (`IMuxDaemonAssetSource`):
   - **(a) GitHub release.** `https://github.com/benyblack/ntilde/releases/download/v<appVersion>/ntilde-mux-<rid>`
     and `…/ntilde-mux-<rid>.sha256`, with `HttpClient`. The SHA-256 must match, and the file is cached
     under `<root>/cache/ntilde-mux/<version>/<rid>`.
   - **(b) A local file** the user picks. Its own SHA-256 is shown, and its ELF or Mach-O header must
     name the probed RID, or it is refused before the upload.
   - **(c) Offline.** *Copy install command* puts the one-liner on the clipboard:
     `mkdir -p ~/.local/share/ntilde/bin && cd ~/.local/share/ntilde/bin && curl -fsSLo ntilde-mux.new <url> && curl -fsSL <url>.sha256 | sed 's/ .*/  ntilde-mux.new/' | sha256sum -c - && chmod 755 ntilde-mux.new && mv -f ntilde-mux.new ntilde-mux`.
     On macOS it uses `shasum -a 256 -c`.
   - A version with no release (a dev build) ends up with (b) only: once (a) reports the release
     missing, the dialog hides (c), whose one-liner would point at the same missing release, and
     disables (a). A version that is not a plain release name (an empty one) offers only (b) from
     the start (§15).
3. **Upload** (§2 decision 4). One exec runs, with `<N>` the binary's size in bytes,
   `sh -c 'set -e; trap "" PIPE; trap "exit 1" HUP TERM; d="$HOME/.local/share/ntilde/bin"; mkdir -p "$d"; t="$d/.ntilde-mux.$$"; trap "rm -f \"\$t\"" EXIT; cat > "$t"; n=$(wc -c < "$t"); [ $n -eq <N> ] || { echo "ntilde-mux upload incomplete: expected <N> bytes" >&2; exit 1; }; chmod 755 "$t"; "$t" --version --json > /dev/null; mv -f "$t" "$d/ntilde-mux"; trap - EXIT; exec "$d/ntilde-mux" --version --json'`.
   The binary goes to its stdin, then EOF, and a progress bar tracks the bytes written.
   Nothing may replace a working binary with one that cannot run, hence the extras: a cancelled or
   timed-out upload ends with stdin's EOF, so the byte count catches the short read `cat` would accept;
   the trial run catches a file the host cannot execute; the trap expands `$t` only when it fires, so an
   odd `$HOME` is neither a syntax error nor run; and dash runs no EXIT trap on a signal, so SIGPIPE (a
   dropped connection's) is ignored and HUP/TERM exit through the trap.
4. **Verify.** The upload exec's stdout is `--version --json`:
   `{"version":"…","protocolMin":1,"protocolMax":2,"rid":"…","path":"/abs/…/ntilde-mux"}`.
   - The protocol range must overlap this app's.
   - A binary that reports another RID than the probe's stays installed (it ran), with a warning.
   - The flow records `RemoteDaemonPath`, `RemoteDaemonVersion` and `RemoteDaemonRid` in the
     profile's `SshMuxOptions`, and shows the result.
   - The flag `PersistRemoteSessions` is not changed by the flow. The dialog offers to turn it on.

The dialog is `RemoteMuxInstallDialog`, code-built like the picker. It has a step list, a log box,
Cancel, and the copy and choose-file buttons. The installer and the probe parser are UI-free and
tested with a fake exec and a fake asset source.

`RemoteDaemonVersion` drives the editor's status line and the toast wording ("Update" when it differs
from the app). Compatibility itself is the handshake.

## 10. Builds, release and CI

### 10.1 Projects

- **`src/Ntilde.Mux.Daemon/Ntilde.Mux.Daemon.csproj`**:
  - `OutputType Exe`, `AssemblyName ntilde-mux`, `RootNamespace Ntilde.MuxDaemon`;
  - `PublishAot`, `IsAotCompatible`, `TreatWarningsAsErrors`, `WarningsAsErrors IL2026;IL3050`,
    `StripSymbols`, `InvariantGlobalization`;
  - references `Ntilde.Mux` only;
  - for `RuntimeIdentifier` `linux-*`/`osx-*`: `<DirectPInvoke Include="rusty_pty"/>` and
    `<NativeLibrary Include="$(RustyPtyStaticLib)"/>`. The default is `native/target/release/librusty_pty.a`,
    which a property overrides;
  - non-AOT builds copy the shared `rusty_pty` next to the exe, as the App does, so `dotnet run` works.
  - `Program.cs` holds one type, `Ntilde.MuxDaemon.Program`.
- **`src/Ntilde.Launcher/Ntilde.Launcher.csproj`**: `OutputType Exe`, `PublishAot`, no project or
  package references, and `kernel32` P/Invokes only.
- **`native/Cargo.toml`:** `crate-type = ["cdylib", "staticlib"]`.

### 10.2 CI (`ci.yml`)

- **New job `mux_daemon_aot`.** It runs on PRs whose paths touch `src/`, the props files or the
  workflow, with the same detect regex as `aot_gate`. The matrix:
  - `linux-x64`: an ubuntu:22.04 container on ubuntu-latest;
  - `linux-arm64`: ubuntu:22.04 on ubuntu-24.04-arm;
  - `osx-arm64`: macos-latest.

  Each leg:
  - builds `librusty_pty.a` with `cargo build --release --locked`;
  - publishes `ntilde-mux`, where any IL2026/IL3050 fails the publish;
  - asserts one file plus nothing but `.dbg`/`.dSYM`;
  - runs `--version --json` and `serve`/`ls` smoke;
  - on Linux, asserts that the highest `GLIBC_` symbol version in `objdump -T` is ≤ 2.35;
  - prints the size, and uploads `ntilde-mux-<rid>` as an artifact.
- **`aot_gate` (win-x64)** also publishes `Ntilde.Launcher` and asserts the exe exists.
- **`native_ssh_docker_e2e`** needs `mux_daemon_aot`, downloads the linux-x64 artifact, and also runs
  `tests/Ntilde.App.Tests --filter "Category=DockerE2E"` with `NTILDE_MUX_E2E_BINARY`.
- No new test project, so the artifact list is untouched.

### 10.3 Release (`release.yml`)

- **New job `publish_mux_daemon`.** It uses the same three-leg matrix, with `-p:Version`, and the Linux
  legs reuse the `ubuntu:22.04` container recipe of `publish_linux`. Each leg:
  - writes `ntilde-mux-<rid>` and `ntilde-mux-<rid>.sha256` (`sha256sum`/`shasum -a 256` format);
  - uploads them to the release with `contents: write`, after a `test -f` assert of both files.
- **`publish_aot` win-x64**:
  - publishes `Ntilde.Launcher` to `artifacts/launcher`;
  - copies it as `artifacts/publish/win-x64/ntilde.com` **before** the smoke gate, the zip and
    `vpk pack`, so both the zip and the Velopack package carry it;
  - asserts it.

## 11. `ntilde.com` (Windows)

### 11.1 Launch

`Ntilde.Launcher` (published as `ntilde.com`):

1. Finds `Ntilde.exe` in its own directory. If it is missing: `ntilde: Ntilde.exe not found next to
   ntilde.com`, exit 9009.
2. Builds the child command line as `"<full path>\Ntilde.exe"` plus the **raw tail** of
   `GetCommandLineW()` after the launcher's own argv[0]. `LauncherCommandLine.Tail` is pure and
   follows the CRT's argv[0] rules: quoted or unquoted, up to the first space or tab outside quotes.
   The arguments are byte-identical; there is no re-quoting.
3. Calls `CreateProcessW` with `bInheritHandles = TRUE`, no `CREATE_NEW_CONSOLE` and no
   `CREATE_NEW_PROCESS_GROUP`, and the environment plus `NTILDE_LAUNCHER_RELEASE=<event name>`.
4. Waits on the process **or** the release event (§11.3), then forwards `GetExitCodeProcess`.

### 11.2 Ctrl+C and Ctrl+Break

`SetConsoleCtrlHandler(handler, TRUE)`:

- The handler returns TRUE for `CTRL_C_EVENT` and `CTRL_BREAK_EVENT`, so the launcher keeps waiting.
  The child receives the same event from the console because it shares the console and the group.
- `CTRL_CLOSE_EVENT` waits up to 4 s for the child, then returns.

`mux attach` puts the console in raw mode (no `ENABLE_PROCESSED_INPUT`), so Ctrl+C reaches the remote
or local shell as `0x03`.

### 11.3 GUI launches

`Program.Main` calls `LauncherRelease.Signal()` (App) on the GUI path, once it knows it is not a CLI
mode. It opens the event named by `NTILDE_LAUNCHER_RELEASE` with `EVENT_MODIFY_STATE`, sets it, and
clears the variable, so child shells do not inherit it. The launcher then exits 0 and the prompt
returns.

### 11.4 PATH

The installer did not put anything on PATH. Velopack hooks are added in `Program.cs`:

- `OnAfterInstallFastCallback` and `OnAfterUpdateFastCallback` call `UserPathRegistration.Ensure(dir)`;
- `OnBeforeUninstallFastCallback` calls `Remove(dir)`.

`dir` is the directory of `Environment.ProcessPath`, Velopack's stable `current` folder.

- The merge, `UserPathRegistration.Merge(existing, dir, add)`, is pure. It is case-insensitive,
  tolerates trailing separators, and preserves order and `%VAR%` entries.
- The write is HKCU `Environment\Path` as `REG_EXPAND_SZ`, followed by
  `SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, "Environment")`.

The Phase 3 `cmd /c` hint, `AttachConsoleHint` and its docs are removed. `mux attach --help` no
longer names `cmd /c`.

## 12. Tests

### 12.1 `tests/Ntilde.Mux.Tests`

- **Carry-overs:**
  - 1: `MuxDiscoveryTests.A_descriptor_whose_start_time_differs_is_not_alive`,
    `MuxCliKillServerTests.Force_refuses_a_pid_whose_start_time_changed`.
  - 2: `MuxStartupProbeTests.Only_a_refusal_or_a_missing_socket_deletes_the_descriptor` (theory over
    the error codes).
  - 4: `TextClientRendererTests.Enter_turns_autowrap_off_and_leave_turns_it_on`.
  - 5: `HeadlessKittyPresentationTests.Kitty_is_the_and_over_interactive_clients`, with detach
    restoring it and read-only ignored.
  - 6: `UnixConsoleSurfaceTests.Eagain_waits_instead_of_closing_input`, Unix-gated, with a
    non-blocking pipe.
  - 7: `MuxDaemonHostTests.Repair_does_not_overwrite_a_descriptor_written_meanwhile` (both cases).
  - 9: `SessionEventsTests.ReadOnly_to_shared_reattach_is_a_change` and
    `…InteractiveClients_is_sent`.
- **Moved Phase 2/3 tests:** under `Daemon/` and `Cli/`.
- **`Transport/StdioMuxTransportTests`:**
  - preamble after noise;
  - no preamble (EOF, and the 64 KiB bound) with the captured text in the message;
  - leftover bytes after the preamble reach the client;
  - half-close in each direction.
- **`Cli/MuxProxyTests`:**
  - proxy ↔ daemon in-process over a real UDS (Unix) and named pipe (Windows), with
    `MuxClient` → frames → equality;
  - the daemon spawned on demand through a fake spawner;
  - exit codes 0, 3 and 1.
- **`Cli/MuxVersionTests`:** `--version` and `--version --json`.
- **`Server/ClientInstanceIdTests`:** a second hello with the same id evicts the first connection's
  sinks; other ids are untouched.

### 12.2 `tests/Ntilde.App.Tests`

- **Carry-overs:** 3 (`SessionManagerMuxTests.Shared_panes_restore_shared`), 8 (`WirePane` twice → one
  close), 9 (badge and close decision with a read-only peer), 10 (headless dialogs: Enter → default,
  Escape → cancel).
- **Remote factory** against an in-memory "remote" `MuxServer` behind a fake `ISshExecTransport`. The
  fake pairs an `InMemoryDuplexPipe` with a proxy pump running in-process:
  - spawn → attach → buffer equality with the daemon;
  - restore by `ssh:<profileId>` (`IfUnattached`);
  - version-mismatch, not-installed and unsupported outcomes, each with its toast action;
  - the `NotPersistent` fallback for a profile without the flag;
  - no environment overrides sent.
- **Reconnect:**
  - drop the fake link → banner → `Reconnected` → reattach → equality;
  - input during reconnect is dropped with one hint;
  - retries stop after the budget, driven by a fake clock and scheduler;
  - `DaemonStopped` gives the Enter banner.
- **Install flow:** probe parsing (a theory over real `uname`/`ldd` outputs: glibc, musl, Darwin,
  BSD), the asset source (checksum match and mismatch), the upload command and stdin bytes, and the
  version recording, all with a fake exec and fake HTTP.
- **Launcher** (Windows-gated):
  - `LauncherCommandLine.Tail` theory;
  - end to end: the launcher exe copied beside a fake `Ntilde.exe` (a copy of `cmd.exe`): argument
    passthrough, the exit code forwarded, the release event returning 0;
  - `UserPathRegistration.Merge` theory.

### 12.3 Docker E2E (`tests/Ntilde.App.Tests/Mux/Remote/RemoteMuxDockerE2eTests.cs`)

- **Gating.** `[DockerFact]`, `Category=DockerE2E`, which needs `NTILDE_ENABLE_DOCKER_E2E=1` and
  `NTILDE_MUX_E2E_BINARY`.
- **Fixture.** It links `DockerSshFixture` from Platform.Tests. The image goes to v5, which adds
  `procps` (`top`) and `iproute2`.
- **Per transport (OpenSSH and native):**
  1. Install `ntilde-mux` through the real `RemoteMuxInstaller`, with the local-file asset source.
  2. Open a persisted profile through `MuxTerminalSessionFactory`, and run `top -d 1`, with the
     output mirrored into a `TerminalBuffer`.
  3. Run `docker network disconnect bridge <ctr>`, wait for `ConnectionLost`, then
     `docker network connect bridge <ctr>`. If the published port is lost, fall back to
     `docker pause`/`unpause`. Wait for `Reconnected` and the reattach, then assert `top`'s pid is
     unchanged and its header is on screen.
  4. `kill -9` the proxy only: the loop reattaches, and the daemon pid is unchanged.
  5. `kill -9` the remote daemon: `DaemonStopped`, then Enter → a fresh shell and `PreviousLost`.
- **Latency.** The same suite measures attach latency (`OpenSession` + `AttachAsync` → snapshot) for
  an empty session and for one with 10 000 lines of scrollback, and logs both.

### 12.4 Architecture

- **New rows:**
  - `Ntilde.Mux.Daemon` references `Ntilde.Mux` only (csproj and IL);
  - `Ntilde.Launcher` has no project or package references;
  - `Ntilde.Mux` keeps no App or Avalonia reference;
  - types in `Ntilde.Mux.Daemon` and `Ntilde.Mux.Cli` depend on no `Ntilde.Shell` or Platform.
- **Lists the new projects join:** `DiagnosticSinkTests.ConsoleToolProjects` takes both new projects.
  The Velopack allowlist is unchanged.
- **`CliCommandDispatchTests`:** keeps the App `MuxCommand` rows, and gains one: `ntilde-mux`'s
  `Program` dispatches through `MuxCli`.

### 12.5 AOT

- **App, all four RIDs:** 0 IL2026/IL3050. The `aot_publish` dispatch covers win-x64, linux-x64 and
  osx-arm64, and the release's linux-arm64 leg is unchanged.
- **`ntilde-mux`:** three RIDs (`mux_daemon_aot`).
- **`Ntilde.Launcher`:** win-x64 (`aot_gate`).

## 13. Docs and roadmap

- **`docs/USER_MANUAL.md` §3.3**, a new remote subsection:
  - install;
  - the flag;
  - what survives a disconnect;
  - limitations: SFTP, forwards, no Windows remote hosts, the glibc floor, systemd
    `KillUserProcesses` / `loginctl enable-linger`, `SSH_AUTH_SOCK` in long-lived shells;
  - `ntilde.com`.
- **`docs/ARCHITECTURE.md`:** the process diagram with the remote daemon and the proxy, endpoint
  identity, and the two AOT binaries.
- **`docs/MODULE_OWNERSHIP.md`:** the new rows.
- **`docs/SSH_ROADMAP.md`:** the remote persistence section.
- **`docs/ROADMAP.md:246`** ("without becoming a full multiplexer") and **`:411`** ("Full tmux clone"):
  - Phase 2 now reads "Win remote workflows; sessions persist locally and on SSH hosts through
    ntilde's own multiplexer".
  - The non-goal becomes "tmux compatibility: no prefix-key mode, no tmux configuration or command
    language, no tmux protocol". Ntilde has a multiplexer of its own (a daemon, shared attach, and
    remote persistence), not a tmux clone.

## 14. Follow-ups (Phase 5 candidates)

- Flip the `SessionPersistence` default.
- Agent-host `read_screen` for windowless sessions.
- Update hand-off between daemon builds: a running daemon replaced without losing sessions.
- SFTP sidebar, remote files and port forwards on persisted remote panes (§8.4).
- Remote endpoints in the "Attach to session…" picker; adoption of remote orphans.
- Embed the release's `ntilde-mux` SHA-256s in the app at build time, instead of trusting the
  `.sha256` next to the asset.
- Refresh `SSH_AUTH_SOCK` in long-lived remote shells (tmux's `update-environment`).
- A winget alias for `ntilde.com`.

Recorded during the build:

- **Native exec latency floor.** `NativeSshExecTransport`'s poll thread sleeps 10 ms when idle, which
  puts about 15 ms under every request round trip (attach of an empty session: 15 ms native against
  2 ms over OpenSSH, §15). An event-driven wakeup from rusty_ssh would remove it; plain native tabs
  poll at 25 ms.
- **Password memory per (kind, host, user).** Add the host and user to the native `PasswordPrompt`
  payload and key `RemoteMuxInteractionHandler`'s remembered secrets by them, so a profile with jump
  hops and passwords can reconnect on its own (today it reconnects on Enter, §15).
- **Vault password to a jump host (pre-existing, plain native tabs too).** `NativeSshPromptResponder`
  allows vault reuse on the *first* Password prompt, and with jump hops that may be a jump host's
  prompt, so the target's vault password can be sent to the jump host.
- **Liveness knobs.** `RemoteMuxHostFactory.Create` does not expose `LivenessInterval` /
  `LivenessTimeout` (`init` on `MuxConnectionHost`), so the Docker E2E runs at the production
  15 s + 10 s.
- **The probe's glibc floor is higher than the binary's.** `RemoteHostProbe` refuses glibc below 2.35,
  but both Linux binaries' highest symbol is `GLIBC_2.34`, so RHEL 9 and its rebuilds (glibc 2.34)
  are refused although the binary would run. Lowering the floor to 2.34 means the probe's constant
  and the CI/release ceiling (§10.2) together.
- **The probe needs `ldd`.** `(ldd --version || getconf GNU_LIBC_VERSION) | head -n 1` keeps ldd's
  "not found" line on a glibc host without `ldd` and refuses it; ask `getconf` first.

## 15. As built

Where the build departs from §1-§14, one entry each, with the reason. Most were rulings made during
review; the section they change is named first.

### Carry-overs and protocol

- **§4.1 Start token, not wall-clock start time.** On Linux the descriptor's `StartTime` holds the
  `/proc/<pid>/stat` start time (clock ticks since boot) and must match exactly; elsewhere it is the
  UTC start time within 1 s (`MuxDiscovery.GetProcessStartToken`, `MuxDaemonOptions.StartToken`).
  .NET's Linux `Process.StartTime` is derived from the wall clock, so an NTP or VM clock step of more
  than a second made a live daemon look dead and sent the launcher into its lock.
- **§4.1 `kill-server --force`** reads the descriptor with the name-only check and leaves the start
  check to the guard on the `Process` it is about to kill, so a recycled pid gets the specified
  "pid N is no longer the multiplexer" message rather than a generic one.
- **§4.9 `RefreshSessionInfoAsync` writes no sharing counts on a v2 daemon.** A thread-pool reply must
  not overwrite a newer `sessionChanged`; `RefreshSharingAsync` (listSessions) carries
  `InteractiveClients` instead.
- **§4.10 The shared-close dialog focuses Detach.** A focused Cancel consumes Enter before `IsDefault`
  is consulted, so Enter would cancel.
- **§3 `ClientInstanceId` eviction** aborts each twin through the normal connection-close path, and a
  connection records its own id only after evicting, so two simultaneous hellos with one id cannot
  evict each other (both may survive; a reconnect is sequential). An id over 64 characters is
  ignored and logged.

### The daemon core and the binaries

- **§6.2 `EnsureConnectedAsync` does the hello inside the connect-or-spawn loop, per attempt.** A daemon
  idling out between accept and hello would otherwise surface as an `IOException` and a 30 s GUI
  cooldown.
- **§6.2 The spawner hands its `MuxPaths.Root` down** when the spawned executable's own rule would not
  resolve it: through `NTILDE_MUX_ROOT` for `ntilde-mux`, `NTILDE_APPDATA_ROOT` for the GUI's
  executable (`MuxPaths.RootHandDown`, final review F3). The proxy spawns with its host's paths, and a
  non-default root would otherwise start the daemon at the default one.
- **§6.2 Surface:** `MuxCliHost.UsagePrefix` (`"ntilde mux"` / `"ntilde-mux"`) stands for
  `ExecutableDisplayName`, and `MuxPaths.Default()` is added. The usage text lists only the host's
  verbs and is normalised to `Environment.NewLine` (the CRLF checkout leaked CRLF into `ntilde-mux`
  on Linux), and the version-mismatch hint names the host's own `kill-server --force`.
- **`ntilde-mux` serves its own root** (final review F3): `MuxPaths.Standalone()`, which is
  `NTILDE_MUX_ROOT` when set, else `<app-data root>/ntilde-mux` (`~/.local/share/ntilde/ntilde-mux` on
  Linux, `~/Library/Application Support/ntilde/ntilde-mux` on macOS; `NTILDE_APPDATA_ROOT` moves it
  with the app-data root). Every `ntilde-mux` verb uses it; the App's `ntilde mux` adapter keeps the
  app-data root (`MuxPaths.Default()`). With one root for both, a host that also ran the GUI shared
  descriptor, socket, lock and log: a remote proxy reached the GUI's daemon, whose factory refuses an
  empty command, so every persistent tab fell back to plain SSH; or the GUI found `ntilde-mux`'s daemon,
  adopted a remote client's sessions as orphans, and its update flow's `shutdown` ended them. The
  socket stays 0600 in a 0700 directory; the `sun_path` fallback (`$XDG_RUNTIME_DIR` or the temp
  directory) is named by the root's hash, so it differs as well; a typical macOS `$HOME` keeps the
  socket in the root (about 84 of 103 bytes). A proxy hands a root other than its environment's to the
  daemon it spawns through `NTILDE_MUX_ROOT` (the GUI's executable still through
  `NTILDE_APPDATA_ROOT`): `MuxPaths.IsStandalone` picks the variable. The binary's install path,
  `~/.local/share/ntilde/bin/ntilde-mux`, is unchanged; nothing migrates, since no `ntilde-mux` had
  shipped. `MuxPathsTests` and `CliCommandDispatchTests.Mux_daemon_serves_its_own_root_and_the_App_adapter_the_GUIs`
  pin it; the CI smoke and the Docker E2E read the descriptor there.
- **§6.4 `MuxCliHost.AttachedToParentConsole` stays** (listed in §6.2, set by `Program.cs`), but no verb
  reads it since the `cmd /c` hint went (§11.4).
- **§2 decision 1 Managed SHA-256.** `MuxDiscovery`'s root hash uses `Ntilde.Mux.Contracts.Sha256`,
  byte-identical to `SHA256.HashData`. On Linux every crypto call loads OpenSSL at run time, which
  `ldd` does not show and the probe cannot check: `serve` aborted on debian:12-slim without libssl.
  `LayeringTests.Nothing_ntilde_mux_runs_references_an_OpenSSL_backed_assembly` keeps
  System.Security.Cryptography, System.Net.Security and System.Net.Http out of everything
  `ntilde-mux` runs.
- **§12.4 `ntilde-mux`'s IL references `Ntilde.Pty` as well as `Ntilde.Mux`.**
  `MuxCliHost.SessionFactory` is a Pty type in Mux's public API; the csproj still references Mux only.
- **§10.1 The csproj drops referenced projects' `.pdb`s from the publish**, which the one-file
  assertion needs. The release job also asserts the binary reports the release version (the
  installer reads it back), and `publish_mux_daemon` waits for `release_tests`.
- **§10.3 The App's uploads wait for `publish_mux_daemon`** (final review I4): `publish_aot` (the Windows
  and macOS assets and feeds) and `release_linux` (the Linux ones) need it, so a failed ntilde-mux leg
  keeps the App back too; the installer downloads only its own version's `ntilde-mux-<rid>`, so an App
  published without them could never install it on that platform. `create_release` cannot wait (every
  upload, ntilde-mux's included, goes into the release it creates). `ReleaseWorkflowTests` parses
  `release.yml` and checks every uploading job.
- **§10.2 osx-arm64 links `librusty_pty.a` statically too**, so no RID needed the `{exe, dylib}`
  fallback. First CI run: linux-x64 6,199,568 B, linux-arm64 6,285,528 B, osx-arm64 5,805,608 B;
  highest symbol `GLIBC_2.34` on both Linux legs.

### Transports

- **§7.1 The remote command is `sh -c 'exec "$HOME/.local/share/ntilde/bin/ntilde-mux" proxy --stdio'`**
  unless the recorded path passes `RemoteMuxCommand.IsSafeAbsolutePath`, in which case it is
  `<path> proxy --stdio`. sshd hands the command to the login shell, and fish, tcsh and nushell do not
  all parse a bare `"$HOME/…"` alike; a single-quoted `sh` script with no single quote inside does.
- **§7.1 The preamble wait is the host's connect timeout (120 s).** Native `Start` returns before
  authentication, so the wait includes the user answering prompts.
- **§7.1 Classifier order.** Loader and format errors (including musl's "Error loading shared
  library") are checked before exit 127, which the dynamic loader also returns next to "not found" /
  "No such file"; a native transport error is checked right after `VersionMismatch`, because the
  native stderr tail holds the transport's message. A sixth kind, `NeedsUser`, is added (below).
- **§8.2 `-o ControlMaster=no` comes before the plan's arguments, and what would break the channel
  is dropped from `ExtraSshArgs`.** OpenSSH takes the first value, so the hidden exec would otherwise
  become a master that a visible tab rides on; a PTY corrupts the binary stream; `-N` runs no remote
  command and `-f` backgrounds ssh, so the proxy would never run on the channel.
- **§8.2 The plan is parsed with ssh's own getopt rules** (codex C3). Matching whole tokens let
  clusters through: OpenSSH 9.6 reads `-tv` as `requesttty true`, `-Nv` as `sessiontype none`, and
  `-fN` backgrounds ssh. `OpenSshExecCommandLine` now walks the plan with ssh.c's option string
  (`1246ab:c:e:fgi:kl:m:no:p:qstvxAB:CD:E:F:GI:J:KL:MNO:P:Q:R:S:TVw:W:XYy`): letters cluster; a letter
  that takes an argument takes the rest of its token or the next one, never scanned (`-p2tN` is port
  `2tN`); options are read before the destination and again after it, up to `--` or the next word,
  after which nothing is an option (kept as written, and logged: ssh would read it as the remote
  command). Dropped from a cluster, the rest kept (`-tv` becomes `-v`): `t`, `T` (we pass `-T`), `N`,
  `f`, `n` (stdin from `/dev/null` starves the proxy), `s` (a subsystem), `G` and `V` (print and
  exit), and `M`, which `ssh -G` shows overrides the `-o ControlMaster=no` before it (a ruling past the
  review's list). Dropped with their argument: `-W`, `-O`, `-Q`, and an `-o` whose keyword - read as
  ssh's readconf reads it (`strdelim`: leading whitespace, one optional `=` with whitespace around it,
  a quoted keyword unquoted), compared case-insensitively - is `RequestTTY`, `SessionType`,
  `ForkAfterAuthentication`, `StdinNull`, `RemoteCommand` or `PermitLocalCommand` (a `LocalCommand`
  writes to ssh's stdout, the mux stream); every other `-o` is kept whole, its value included. Each
  dropped piece is logged by its letter or keyword, never its value. `OpenSshExecCommandLineSshTests`
  runs `ssh -G` on the built argv (an empty `-F` config, no network; skipped without `ssh`) and checks
  `requesttty false`, `sessiontype default`, `forkafterauthentication no`, `stdinnull no` and
  `controlmaster false` for each tricky input.
- **§8.2 The askpass helper fills in the vault password only for a prompt that names the target's
  `user@host`.** A ProxyJump hop's prompt would otherwise receive the target's password.
- **§8.2 No `ISshAskPassLocator`.** The App passes the helper path (`SshAskPassCommand.LocateHelper()`).
- **`ISshExecChannel.Abort()`** (final review F4): synchronous, idempotent, no grace period. OpenSSH
  kills ssh's process tree (an askpass helper and its dialog with it); native closes the session; a
  `Dispose` still in its grace period is cut short. The connector aborts a channel nobody was handed
  yet when its attempt is cancelled, inside the cancel itself, so the host's `Dispose` (app exit) or a
  user's request superseding an automatic attempt has stopped ssh before it returns. Ending it
  gracefully on the pool left ssh and its askpass dialog running after the app exited.
- **§8.3 Native stdout is a bounded byte queue** (`BoundedChunkQueue`, `Monitor` wait/pulse) that the
  poll thread feeds directly, not a `Pipe`. `Pipe`'s default schedulers put thread-pool work on the
  remote output path, against the multiplexer's no-thread-pool-on-the-output-path rule.
- **§8.3 ABI details.** Handles are `usize` with 0 for a rejected call; the exit status reuses event
  kind 7 and stderr is the new kind 14; a refused exec gives `Connected`, `Error`, `Closed`; a process
  killed by a signal sends no exit status, so `Completion` is null (as for a killed OpenSSH channel).
  `detect_login_shell` runs over `run_exec_collect`, and the exec session repeats its open-and-exec
  pair: two exec paths, not the one §8.3 asked for.

### Reconnect

- **Automatic attempts are non-interactive** (§7.3 had every attempt call the normal connect).
  `MuxConnectAttempt { Interactive }`: a user's request (a pane opening, Enter) is interactive; the
  loop's attempts and the kill-delivery attempt are not. A network drop must not pop a dialog every
  few seconds, nor send failed logins that fail2ban and lockouts count. Cost: password-only OpenSSH
  profiles, and native profiles with jump hops and passwords, reconnect on Enter.
- **OpenSSH automatic attempts** run with `BatchMode=yes`, no `SSH_ASKPASS`,
  `SSH_ASKPASS_REQUIRE=never` and no `DISPLAY`. A ProxyJump hop may not inherit `BatchMode`, and an
  OpenSSH before 8.4 with no tty uses askpass whenever `DISPLAY` is set.
- **Native automatic attempts** (`RemoteMuxInteractionHandler`) never reach the window's handler, so
  neither dialogs nor the vault: a host key is accepted only when the app's native known-hosts store
  already trusts it (rusty_ssh asks about the key on every connect); a password or passphrase is
  offered only from the host's in-memory record of one that got an earlier attempt in (forgotten on
  host dispose, and as soon as an attempt that offered it fails SSH or meets another auth prompt);
  a password is never remembered or replayed for a profile with jump hops, since the prompt does not
  say which hop asks.
- **No empty password.** With nothing to answer, a native automatic attempt aborts by closing the
  session instead of cancelling the prompt: rusty_ssh submits a cancel as an empty password (or empty
  keyboard-interactive answers), which a server logs as a failed login on every retry.
- **`NeedsUser` stops the loop at once** with `ReconnectAbandoned`. It is the native abort above, and
  an automatic OpenSSH attempt refused with `Permission denied` (exit 255). Spending the 10-minute
  budget would be about 25 pre-auth connections that fail2ban counts. The factory treats it like
  `SshFailed`.
- **§7.3 A user's request never joins an automatic attempt.** It cancels the automatic attempt in
  flight and starts an interactive one (then resets the backoff); a joined automatic attempt would
  fail for want of a prompt.
- **§7.2 Liveness counts byte progress.** The link is dead only when no inbound byte arrived within
  10 s of the ping (a read-stream wrapper stamps every read), and a tick is skipped while the
  previous ping is unanswered. A ping queued behind a large snapshot on a slow link would otherwise
  cut a healthy link into a reattach loop.
- **§7.3 A loss is classified by the lost client's own channel**, waiting at most 1 s for its exit
  code, and a host clears its last failure when a new attempt starts, so a stale `NotInstalled`
  cannot label a later timeout.
- **§8.1, §7.3 The proxy exits 3 only when the daemon's process is gone; a connection a live daemon
  drops exits 4** (codex D1). A live daemon ends single connections too and keeps their sessions: it
  drops a client too slow to keep up (`client_too_slow`, plausible on a slow link with heavy output),
  and a hello with the same `ClientInstanceId` evicts the older connection. The proxy exited 3 for
  those as well, and the host read `DaemonStopped`: no automatic reconnect, the wrong banner, and its
  queued kills dropped, so a later close orphaned the remote shell. Now, once the daemon side ends, the
  proxy closes stdout at once (the client sees the end without delay), then asks whether the
  descriptor's daemon still runs (`MuxDiscovery.IsProcessAlive` with its pid, process name and start
  token), every 50 ms for up to 1.5 s: gone is 3, still running is `MuxProxyExitCodes.ConnectionClosed`
  (4). A stdout that cannot be written, at the preamble or later, is 4 too: nobody reads the code, and
  it says nothing about the daemon. The host counts every code but 3 as a lost link, as before. The
  1.5 s fits inside the 2 s the exec channels give a command to exit once their stdin closed; the
  host's 1 s classification cap is unchanged, so a daemon that takes longer than that to exit after
  closing its connections is read as a lost link, and the reconnect starts a new daemon, where each
  pane's reattach finds its session gone (`PreviousLost`). `MuxProxyCommand.Run` takes the liveness
  check as an optional parameter, for tests whose daemon runs in their own process.
- **Queued kills** survive `ReconnectAbandoned` (budget or `NeedsUser`) and are sent, before
  `Reconnected` is raised, on any later connect, a user's Enter included: the user's intent to end
  those shells still stands. They are dropped, with a log line, on `DaemonStopped` (the daemon's
  sessions ended with it, and delivering them would start a new daemon) and on host dispose.
- **§5 A remote host is released when no pane needs its endpoint** (final review F1). After a remote
  pane closes (`DisposeControlTree`, once the close is done), the window releases every remote host no
  pane needs: a pane needs its endpoint while its session is on it, while it keeps a pending restore
  id on it (a hydrated tab never shown, a reattach waiting for Enter), and while its connect is in
  flight (`TerminalPane.RemoteMuxEndpointInUse`). `MuxConnectionHosts.Release` closes the host once no
  kill is queued and none is sent but unsettled (`MuxConnectionHost.WhenKillsDrained`): a sent kill counts
  until its continuation has run, answered or queued again because its connection closed, so a kill that
  fails at once on a connection already dead is never read as delivered (residual round). Kills
  `DaemonStopped` dropped count as settled; a host that gave up (`ReconnectAbandoned`) with kills queued stays
  registered, idle (no loop, no pings), and its kills go out on the next use of the endpoint. Reuse:
  `GetOrCreate` for an endpoint whose release is pending takes that host back and cancels the release;
  each release has a token its drained callback must still find pending, so a callback that runs late
  (the host handed it out before a take-back and a new release that waits for a new kill) closes
  nothing (codex residual round);
  the host is forgotten under the registry's lock just before its dispose, so nobody is handed a host
  about to close. A host disposed by any path is forgotten (`MuxConnectionHost.Closed`), so the next ask
  builds a new one; the dispose forgets the remembered secret. Local hosts are unchanged. Before, a
  remote host lived until the window closed: the hidden ssh, the remote proxy and daemon stayed, pings
  went out every 15 s, the daemon never idled out, and a connection the user had closed came back
  after sleep.
- **§5 Every close of a non-local pane goes through `KillWhenConnected`,** whatever the session's
  `IsConnected` says, and through `GetOrCreate` even for a never-spawned pending id; an idle remote
  host starts one non-interactive attempt to deliver the kill. A kill sent into a silently dead link
  was lost, and a never-shown restored tab had no host at all.
- **§5 A closed pane's remote kill gets a host whatever `PersistRemoteSessions` says** (codex C2). The
  flag routes tabs (`MuxTerminalSessionFactory.RoutesRemote`, read again in `CreatePersistent` before
  any host is asked for), so a tab of a profile with the flag off still opens plain SSH; it does not
  decide whether a shell the user closed ends. `RemoteMuxHostFactory.Create` now declines only a
  missing profile. A pane that kept its pending `ssh:` id after the flag went off (§7.6 below) has
  that shell killed on close through a host built for it: one automatic attempt, which never prompts,
  then released once the kill is delivered. That attempt signs in only with what needs no answer (keys,
  the agent; a host built fresh remembers no password), so for a password-only OpenSSH profile, or a
  native one without a remembered password, it fails and the kill stays queued on an idle host: sent
  on the next connect to that endpoint in the window (a tab opened once the flag is back on), tried
  again by another such close, and dropped, logged, when the window closes (USER_MANUAL §3.3 says
  so). A plain SSH pane no longer counts as needing its endpoint
  (`TerminalPane.RemoteMuxEndpointInUse`), pending id or not: its close or next spawn asks for the
  connection again, and counting it kept a host built for another pane's kill connected for as long
  as it stayed open. A deleted profile still gets no host, and the kill that cannot be sent is logged,
  naming the shell. Before, the creator declined a flag-off profile and the kill was lost without a
  word; nothing adopts a remote orphan.
- **§7.4 A stale remote result's shell is killed through its host too** (codex C1). A result that comes
  back to a pane that was closed or restarted meanwhile, and that started a shell (any outcome but
  `Reattached`), has that shell killed with `KillWhenConnected` on its endpoint's host
  (`TerminalPane.KillStaleRemoteSession`, through `GetOrCreate`, so a host released meanwhile is taken
  back or built again), on the UI thread before the session is let go; then the pane asks the window
  for its release pass (`TerminalPane.RemoteMuxReleaseCheck`, wired per pane and kept after the pane
  is unwired), since its close's pass may have run before the result came back. A host kept or built
  only for that kill is released once it is delivered. No host at all (the window closing, the profile
  gone) is logged. Before, the session's own fire-and-forget `Kill()` was dropped on a link already
  down, or once the release had closed the host, and the shell ran on with no pane able to reach it. A
  `Reattached` result is let go unkilled: it did not start that shell.

### Factory and pane

- **§7.5 A new tab whose connect fails (`SshFailed`, `NeedsUser`, a timeout, or a host that cannot be
  set up) gets `DaemonUnreachable`, no session, no id and the retry banner; no plain-SSH fallback.**
  Plain SSH would only repeat the SSH failure and its prompts. A failure after the client connected
  (a spawn RPC) still falls back to plain SSH, as do `NotInstalled`, `Unsupported`,
  `VersionMismatch` and `ProxyFailed`.
- **§7.5 Notice actions.** Only `NotInstalled` (*Install ntilde-mux…*) and `VersionMismatch`
  (*Update ntilde-mux…*) carry one; `Unsupported` and `ProxyFailed` carry none (§7.5 listed
  `Unsupported` both ways). A toast with an action does not auto-hide, remote notices merge by title
  and message (so an action sits next to its own host's line), and an unreachable notice is raised
  only when SSH itself worked. The button names its host, *Install ntilde-mux on <user@host>…*
  (final review I1): a merged toast offers only the last action raised, under several hosts' lines.
- **§7.6 A pane whose profile lost the flag keeps its pending `ssh:` id for its whole life**, not for
  one save. Turning the flag back on can still reattach the shell; dropping the id would orphan a
  shell nothing can adopt.
- **§8.4 The palette's SFTP upload and download** are refused on a persistent remote tab with the
  sidebar's notice (final review I3). They opened the transfer dialog with the mux session's id, which
  names no SSH session of the app, and the job then made an SSH connection of its own.
- **§7.4 Keys are swallowed while a remote connect is in flight.** The connect may be waiting on a
  prompt, and a second Enter would only join it.
- **Pane: Detach on a remote pane** (final review F2) detaches as on a local one, but its toast reads
  "Shell kept running on `<user@host>` — run 'ntilde-mux attach `<id>`' on that host to get it back",
  with the full id `ntilde-mux ls` lists: "Attach to session…" lists local shells only, and adoption
  is local-only. A local pane's toast is unchanged.
- **§7.4 A pane at the daemon-stopped banner waits for its own Enter** (a later `Reconnected` does not
  revive it, since its session went with the daemon), and a pane whose Enter is armed ignores a later
  `ConnectionLost`.

### Install flow

- **§9 step 3 The upload script** (the text in §9 is final): a byte count after `cat`, a trial
  `--version --json` before `mv -f`, a cleanup trap that expands `$t` only when it fires, `SIGPIPE`
  ignored and `HUP`/`TERM` exiting through the trap. A cancelled or timed-out upload ends with stdin's
  EOF, which `cat` takes as the whole file; an odd `$HOME` broke or ran the eager trap; dash runs no
  EXIT trap on a signal. Verified under dash and bash, with fish and tcsh as login shells.
- **§9 step 2(b)/4 Platform checks.** A local file's ELF or Mach-O header must name the probed RID
  (a universal macOS binary is refused), and an installed binary that reports another RID stays
  installed with a warning.
- **§9 step 2(c) Copy install command** uses the profile's recorded RID, or else one probe exec, and is
  hidden (with Install disabled) after the release download reports the release missing.
- **§9 step 4 Recording an install** goes through `SshConnectionService.RecordRemoteMuxInstall`, which
  changes only the four `SshMuxOptions` fields on the store's own copy. The editor view-model's
  normalisation dropped `ControlPath` and zeroed `ControlPersistSeconds`.
- **§9 Surface:** the dialog's installer factory also receives the dialog's `report` and `progress`;
  `RemoteMuxInstaller.ProbeAsync` and `RemoteMuxInstallResult.ReleaseMissing` are added.

### `ntilde.com`

- **§11.3 `LauncherRelease.Discard()` is the first statement of the mux branch.** A daemon started by
  `mux attach` would otherwise pass `NTILDE_LAUNCHER_RELEASE` to its shells, and a GUI started from
  one would release the attach's launcher.
- **§11.1 The child gets the launcher's own `STARTUPINFO`** (`GetStartupInfoW`), so redirected stdio
  flows through.
- **§11.4 The PATH hooks are registered on Windows only** and log to Velopack's log inside a hook
  process; a `Path` value that is not a string is refused rather than read as empty, and the registry
  is written only when the value changes.

### Phase 3 defects found and fixed

- **A user detach could lose `DetachedByUser`.** When a broadcast dropped a just-closed connection's
  sink (its frames are refused) before that connection's queued user detach ran on the parse thread,
  the detach found no sink and decided nothing, so the deliberately detached shell was re-adopted at
  the next launch. A refusal drop that leaves no interactive subscriber now records the dropped
  sink, and that sink's own detach, when it arrives, decides as it would have. Found through an
  arm64-only `AttachModeTests` failure.
- **`mux attach` could lose its own detach.** `MuxClient.Dispose` closes without draining, and the
  attach verb disposed right after posting the user detach; it now awaits a bounded ping (at most
  1 s) first, the pattern `MuxConnectionHost.Dispose` already used.

### Tests

- **§12.3** The Docker E2E is `tests/Ntilde.App.Tests/Shell/Mux/Remote/RemoteMuxDockerE2eTests.cs`,
  with `Category=DockerE2E` and no `Lane` trait, in its own non-parallel collection, and with its own
  known-hosts store passed through `RemoteMuxHostFactory.Create(…, isTrustedHostKey)`. Both transports
  pass against a real sshd: the drop was noticed 25.1-25.3 s after the disconnect (the production
  15 s + 10 s liveness), `top` kept its pid through the drop and a killed proxy, and a killed daemon
  gave `DaemonStopped`, then `PreviousLost`. Attach latency, median of 5 (Docker Desktop, same
  machine): OpenSSH 2.0 ms empty, 123 ms with 10 000 lines (a 12.9 MB snapshot); native 15.2 ms and
  123 ms.
- **§12.1** The kill-server start-time test lives in `MuxCliTests`, not a separate
  `MuxCliKillServerTests`.
