# ntilde multiplexer — Phase 4: remote daemons over SSH, `ntilde.com`, review carry-overs

**Status:** design. **Branch:** `feat/mux-phase4` off `origin/dev-mux` (91d41f1); the PR targets `dev-mux`.
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
| `SpawnParams.Command` empty | Means "the daemon's default login shell". Only `ntilde-mux` daemons (`LocalShellSessionFactory`, §6.3) give it that meaning. The GUI's own daemon is never asked for it: local spawns always carry a command. |
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

- **The proxy exited with code 3 (the daemon closed the connection).** The host raises
  `DaemonStopped`, and there is no automatic retry.
- **Anything else** (ping timeout, SSH exit 255, channel EOF, native disconnect). The host raises
  `ConnectionLost` and starts the loop.

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
   - **socket EOF or error:** flush and close stdout, and exit **3**;
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
   - A dev build, whose version has no release, offers (b) and (c) only.
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
