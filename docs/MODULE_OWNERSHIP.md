# Ntilde – Module Ownership & Invariant Map

Each assembly owns specific invariants. Layering rules are encoded as
[NetArchTest](https://github.com/BenMorris/NetArchTest) facts in
`tests/Ntilde.Architecture.Tests/`. Behavioral invariants are
encoded as unit/integration tests in each module's test suite.

Breaking an invariant is a bug, even if the UI appears correct.

The companion doc is `docs/ARCHITECTURE.md`. Update both when an
invariant changes.

---

## Ntilde.VT (`src/Ntilde.VT/`)

**Namespace:** `Ntilde.VT` (+ `.Export`, `.Links`, `.Storage` sub-namespaces)
**Depends on:** *(leaf — only BCL)*
**Public surface:** `AnsiParser`, `TerminalBuffer`, `TerminalRow`, `TerminalCell`, `BufferSnapshot`, `RenderSnapshots.*`, `ReplayModels.*`, `TerminalTheme`, `UnicodeWidth`, `TerminalStateSnapshot`, `AnsiParserState`, `TerminalStateSerializer`

**Owns**
- VT/ANSI state machine and parser
- Main + alternate screen buffers
- Scrollback (`Buffer/ScrollbackPages.cs`) and lossless reflow (`TerminalBuffer.ReflowEngine.cs`)
- Cell/row semantics, grapheme cluster handling, Unicode width
- Buffer threading contract (`TerminalBuffer.Lock`, `ReaderWriterLockSlim`)
- Resolution of `OSC 133` marks against the buffer: `GridQueryReader` (mark → live command-line
  text) and `ShellMarkAnchorResolver` (mark → viewport row, for Command Assist's overlay anchor).
  Both live here rather than in `Ntilde.CommandAssist` because they are buffer arithmetic
  over VT types, which the layering tests forbid that assembly from referencing.
- `Export/AnsiCellWriter.cs`: render-snapshot rows to positioned ANSI text; no I/O, no cursor
  movement, no erase — the caller positions each row. Used by the mux text client's renderer
  (`Ntilde.Mux.TextClient.TextClientRenderer`, Phase 3 spec §6.3), and kept deliberately separate
  from `Export/TerminalExporter.cs`'s `ExportToAnsi`, whose exact whole-screen dump format the
  "Export Snapshot (ANSI)" command relies on.

**Invariants** (enforced by architecture tests and `tests/Ntilde.VT.Tests/`)
- Deterministic parsing — same byte stream produces the same semantic ops
- Source of truth — renderers/replay/sessions read this, they don't replicate state
- Lossless reflow — resize never silently drops content
- Alternate-screen isolation — main buffer is preserved across alt entries/exits
- All read access requires holding `TerminalBuffer.Lock`; reads without the lock throw via `AssertLockHeld`
- **Lock re-entrancy contract:** `Lock` is a non-recursive `ReaderWriterLockSlim`. `EnterReadLockIfNeeded()` returns `false` (and acquires nothing) when a read *or* write lock is already held. `EnterWriteLockIfNeeded()` returns `false` only when a *write* lock is already held — calling it while holding a read lock throws `LockRecursionException` (upgrading is not supported), it does **not** return `false`. Both return `true` when they actually acquired the lock. The returned `bool` says *whether this call took the lock* — callers must pass it to the matching `Exit…IfNeeded(..., lockTaken)` and must **not** unlock when it is `false`. Treating a `false` return as "lock acquired" double-unlocks (or unlocks a caller's outer lock).
- **`GetRowAbsolute()` null contract:** returns `null` for any absolute row that has no persistent `TerminalRow` — including **paged-out scrollback rows** (scrollback lives in `ScrollbackPages`, not as row objects), out-of-range rows, and negative indices. To read scrollback content use the cell/grapheme accessors (`GetCellAbsolute`, `GetGraphemeAbsolute`), which page it in; callers that assume a non-null row for scrollback indices will NRE.
- No OS, PTY, rendering, or UI logic in this assembly (`Vt_must_be_a_leaf_assembly` arch test)
- All types in `Ntilde.VT.*` namespace (`Leaf_assembly_types_reside_in_its_own_namespace`, `NamespaceAlignmentTests.cs`)

**Test authority**
- Primary: `tests/Ntilde.VT.Tests/`
- Replay/regression coverage: `tests/Ntilde.App.Tests/ReplayTests/`, `tests/Ntilde.App.Tests/AnsiCorpusReplayTests.cs`
- Buffer/reflow coverage: `tests/Ntilde.App.Tests/Buffer/`, `tests/Ntilde.App.Tests/ReflowScenariosTests.cs`, `tests/Ntilde.App.Tests/BufferTests/`

---

## Ntilde.Replay (`src/Ntilde.Replay/`)

**Namespace:** `Ntilde.Replay`
**Depends on:** VT
**Public surface:** `ReplayReader`, `ReplayWriter`, `ReplayRunner`, `ReplayIndex`, `BufferSnapshot`, `GoldenMaster`, `PtyRecorder`

**Owns**
- Replay file format v2 (see `docs/REPLAY_FORMAT_V2.md`)
- Recording-of-bytes pipeline (`ReplayWriter`)
- Playback (`ReplayReader`, `ReplayRunner`) with snapshot virtualization options
- Golden-master harness used by regression suites

**Invariants** (enforced by `Replay_only_depends_on_Vt` and behavioral tests)
- Replay is a pure function of `(byte stream, optional snapshots) → buffer state`
- Cannot reference Pty, Rendering, App, Avalonia, or SkiaSharp
- Snapshot format is forward-compatible within v2

**Test authority**
- `tests/Ntilde.Platform.Tests/Replay/`
- `tests/Ntilde.App.Tests/ReplayTests/`
- `tests/Ntilde.App.Tests/Regressions/` (Midnight Commander, regression suite)

---

## Ntilde.Rendering (`src/Ntilde.Rendering/`)

**Namespace:** `Ntilde.Rendering`
**Depends on:** VT, SkiaSharp 3.119.4
**Public surface:** `PixelGrid`, `GlyphAtlas`, `GlyphCache`, `RowCache`, `ImageRegistry`, `SixelDecoder`, `RenderPerfMetrics`, `RenderPerfWriter`, `RendererStatistics`, `SharedSKFont`, `SharedSKTypeface`

**Owns**
- Skia glyph atlas / cache (dual-atlas system)
- Pixel-grid layout math (`PixelGrid`)
- Sixel image decoding
- Font and typeface wrappers
- Renderer performance metrics

**Invariants** (enforced by `Rendering_only_depends_on_Vt_and_Skia`)
- Rendering is a pure function of `(buffer snapshot, metrics, theme) → pixels`
- No semantic decisions — if the buffer is wrong, the renderer cannot fix it
- No Avalonia in the dependency closure (the Avalonia binding shell is in App)
- Incremental rendering only — no full-redraw fallbacks except on resize/theme change

**Test authority**
- Primary: `tests/Ntilde.Rendering.Tests/` (Skia primitives that don't need a GPU context)
- Renderer metrics: `tests/Ntilde.App.Tests/RenderTests/RendererMetricsTests.cs`
- Golden PNG comparisons: `tests/Ntilde.App.Tests/RenderTests/GoldenSharedPngTests.cs`, `GoldenFontPngTests.cs`

> **Note:** Today the Avalonia renderer composition (`TerminalView`, `TerminalDrawOperation`) lives in `src/Ntilde.App/Shell/` — see `docs/ARCHITECTURE.md` § 14 Known Tech Debt, tracked as #113.

---

## Ntilde.Pty (`src/Ntilde.Pty/`)

**Namespace:** `Ntilde.Pty`
**Depends on:** Replay (for `ReplayWriter` only)
**Public surface:** `ITerminalIO`, `ITerminalLifecycle`, `ITerminalShellMetadata`, `ITerminalRecorder`, `ITerminalSession` (composite), `RustPtySession`, `ShellHelper`, session model DTOs, `ITerminalSessionFactory`, `TerminalSessionRequest`, `SshSessionDescriptor`, `ITerminalSessionCapabilities`, `Utf8ChunkDecoder`

**Owns**
- The rust-PTY adapter (`RustPtySession` + `ConPtyNative` P/Invoke)
- Session contracts: four narrow interfaces composed into `ITerminalSession`
- Raw byte-stream recording lifecycle (no buffer snapshots — those moved out in Phase 5)

**Invariants** (enforced by `Pty_must_not_depend_on_Vt`)
- **Pty does not reference VT.** The session reports raw bytes/strings; parsing into a `TerminalBuffer` is the consumer's responsibility.
- IO is bounded and non-blocking
- Bytes are delivered verbatim — no transformations at this layer
- `ITerminalSession` is a kitchen-sink composite of four narrower interfaces; new code should depend on the narrowest one that fits

**Test authority**
- `tests/Ntilde.App.Tests/PtySmokeTests.cs` (PtySmoke category — filtered out of default CI lane)
- `tests/Ntilde.ExternalSuites/Vttest/` (external scenario driver)
- `tests/Ntilde.App.Tests/Ssh/TerminalPaneRecordingTests.cs`

---

## Ntilde.Platform (`src/Ntilde.Platform/`)

**Namespace:** `Ntilde.Platform` (+ `.Input`, `.Paths`, `.Execution`, plus the SSH sub-tree)
**Depends on:** Pty
**Public surface:** `TerminalInputSender`, path mappers, process abstractions, the SSH stack (`Ssh/{Exec,Interactions,Launch,Models,Native,OpenSsh,Sessions,Storage,Transport}`)

**Owns**
- Input routing primitives (drop router, shell quoters, input sender)
- Path mapping (notably WSL ↔ Windows)
- Process abstraction (`IProcessRunner`)
- The entire SSH stack: native interop with `rusty_ssh.dll`, OpenSSH bridging, session factories, profile storage, transport
- SSH exec channels (`Ssh/Exec/`, namespace `Ntilde.Platform.Ssh.Exec`, multiplexer Phase 4 spec §8.2-§8.3): a command run over SSH with no PTY, which the remote multiplexer (`ntilde-mux proxy --stdio`) and its installer use. `ISshExecTransport` / `ISshExecChannel` (stdout, stdin with EOF on dispose, an 8 KiB stderr tail, the exit code), `OpenSshExecTransport` + `OpenSshExecCommandLine` + `SshAskPassEnvironment` (the `ssh` argv and the askpass/batch-mode environment), `NativeSshExecTransport` over rusty_ssh's exec mode (`nova_ssh_exec`, `nova_ssh_send_eof`), `BoundedChunkQueue` / `BoundedTail`, and `SshExec.RunAsync` (one command with stdin bytes and a deadline)
- Future home of `SessionBufferBinder` and other session-orchestration helpers

**Invariants**
- This is NOT the terminal engine (that's VT). Renamed from `Ntilde.Core` in #76 to end the three-way "Core" name overload.
- No Avalonia or Skia in the dependency closure
- SSH transports must satisfy `IRemoteTerminalTransport` so all SSH session implementations are interchangeable
- **An exec channel's stdout is a clean byte stream.** The OpenSSH exec always runs `-T` with
  `ClearAllForwardings=yes` and `ControlMaster=no`, and drops `-t`/`-tt`/`-T` from the profile's
  extra arguments: a PTY would corrupt the frames, a forward would ride the mux channel, and a
  hidden exec must never become a ControlMaster that a visible tab then depends on
- **Batch mode leaves `ssh` no way to prompt:** `BatchMode=yes`, no `SSH_ASKPASS`,
  `SSH_ASKPASS_REQUIRE=never`, no `DISPLAY` (an OpenSSH older than 8.4 with no tty would otherwise
  fall back to its compiled-in askpass)
- **The native exec channel never answers a prompt its handler declined by throwing:** it closes the
  session instead, so no empty password is ever submitted to a server
- **No thread-pool work on the native exec's stdout path:** one dedicated poll thread per channel
  feeds a bounded byte queue that a reader drains synchronously
- A native exec connection lives exactly as long as its channel; it never shares a pane's connection

**Test authority**
- Primary: `tests/Ntilde.Platform.Tests/` (exec transports: `Ssh/Exec/`)
- Docker-gated E2E: `tests/Ntilde.Platform.Tests/Ssh/NativeSshDockerE2eTests.cs` (skipped without Docker)
- App-side integration: `tests/Ntilde.App.Tests/Ssh/`, `tests/Ntilde.App.Tests/Input/`

---

## Ntilde.AgentHost.Contracts (`src/Ntilde.AgentHost.Contracts/`)

**Namespace:** `Ntilde.AgentHost.Contracts`
**Depends on:** *(leaf — only BCL)*
**Public surface:** `AgentHostProtocol`, `AgentHostDiscovery`, `AgentHostJsonContext`, `Frames.*`, and the contract DTO groups (`ActContracts`, `SessionContracts`, `StatusContracts`, `ReplayContracts`)

**Owns**
- The wire protocol between the app's agent host and any external client (today: the MCP server)
- Frame definitions, discovery, and the source-generated JSON serialization context

**Invariants** (enforced by `AgentHostContracts_must_be_a_leaf_assembly` and `AgentHostContracts_csproj_must_have_no_project_references`)
- Leaf assembly — no project references at all, so both sides of the wire can depend on it without pulling in a dependency graph
- Shared by App and McpServer: a breaking change here breaks the agent integration on both sides at once. Version the protocol rather than redefining a frame in place.

**Test authority**
- `tests/Ntilde.McpServer.Tests/AgentHostClientTests.cs`
- End-to-end over real stdio: `tests/Ntilde.McpServer.Tests/McpServerStdioE2ETests.cs`

---

## Ntilde.Mux.Contracts (`src/Ntilde.Mux.Contracts/`)

**Namespace:** `Ntilde.Mux.Contracts`
**Depends on:** *(leaf — only BCL)*

**Owns**
- The multiplexer wire protocol: frame kinds, framing, binary payload codecs, source-generated JSON DTOs, error codes, version negotiation
- Daemon discovery (`MuxDiscovery.cs`): the app-data root, the `mux/mux-endpoint.json` descriptor (`MuxEndpointDescriptor`), and the per-user, per-root default endpoint name
- `Sha256.cs`: a managed SHA-256 (internal) for the endpoint name's root hash

**Invariants**
- Frame = `u8 kind` + `u32` LE length + payload, payload ≤ `MaxFrameBytes`, header validated before any payload byte is read
- Every JSON type goes through `MuxJsonContext`
- Binary parsers length-check before slicing
- Version negotiation picks the highest common version or refuses (Min 1 / Max 2); Phase 4's additions (`HelloParams.ClientInstanceId`, `InteractiveClients` on `sessionChanged` and `sessionInfo`, an empty `SpawnParams.Command` meaning the daemon's login shell, and `SpawnParams.SessionId`: the id a remote spawn names for its session, refused when malformed (`protocol_error`) or in use (`session_exists`)) are optional members a Phase 3 peer skips
- A descriptor is live only when its pid is alive, **and** runs under the recorded process name, **and** carries the start token the daemon recorded (pid-reuse guard; on Linux the token is the `/proc/<pid>/stat` start time, which a wall-clock step cannot move). A descriptor without a token keeps the name check. Writes are atomic (temp file + move); repair is create-new for a missing file and compare-then-replace for a dead one, so it never overwrites a descriptor another daemon wrote meanwhile
- **No `System.Security.Cryptography`.** `ntilde-mux` runs this assembly and must need nothing but libc, and on Linux .NET loads OpenSSL for every crypto call. The managed `Sha256` is byte-identical to `SHA256.HashData`, so endpoint names still match between the GUI and the daemon on the same host (`LayeringTests.Nothing_ntilde_mux_runs_references_an_OpenSSL_backed_assembly`)

**Test authority**
- `tests/Ntilde.Mux.Tests/`

---

## Ntilde.Mux (`src/Ntilde.Mux/`)

**Namespace:** `Ntilde.Mux` (+ `.Transport`, `.TextClient`, `.Daemon`, `.Cli`)
**Depends on:** Pty, VT, Replay, Mux.Contracts

**Owns**
- Multiplexer core: headless authoritative sessions (one parse thread each), the server, the client (`MuxClientSession : ITerminalSession`), and an in-memory transport
- Local transports (`Transport/`): `NamedPipeMuxListener`, `UnixSocketMuxListener`, `MuxListeners.Create` (the platform's listener) and `MuxEndpointConnector` (the client end)
- The client end of the stdio proxy (`Transport/StdioMuxTransport.cs`, Phase 4 spec §8.1): skips whatever a remote shell prints until the `NTILDE-MUX-PROXY 1 <pid>` preamble (at most 64 KiB, then the captured text is the error) and returns a duplex stream over an exec channel's stdout and stdin
- The daemon host: `MuxDaemonHost` / `MuxDaemonOptions` (lock file, descriptor, reaper, idle exit, `shutdown`), and the `ClientInstanceId` eviction in `MuxServer`: a hello carrying an id closes every other connection with the same id before `welcome` (Phase 4 spec §3)
- The daemon process (`Daemon/`, namespace `Ntilde.Mux.Daemon`, moved from the App in Phase 4): `MuxServeHost` (the `serve` process: listener, `MuxDaemonHost`, `mux.log` through the rotating `MuxLogFile`), `MuxDaemonLauncher` (connect to a live descriptor or spawn the host's serve arguments; `EnsureEndpointStreamAsync` for the proxy, `EnsureConnectedAsync` with the hello), `ProcessMuxDaemonSpawner` / `IMuxDaemonSpawner`, `MuxDaemonExit`, `MuxStartupProbe` (whether a descriptor's daemon is really live, for the App's startup auto-apply gate: only a genuine refusal or a missing socket counts as dead, and a Windows connect timeout falls back to enumerating `\\.\pipe\`), `MuxPaths` (root, descriptor, endpoint, log folder, injected by the executable), and `LocalShellSessionFactory`, the remote daemon's shells (an empty command is the default login shell with `-l`; `~` is `$HOME`; SSH is refused)
- Every `mux` verb (`Cli/`, namespace `Ntilde.Mux.Cli`): `MuxCli.Execute` (serve, ls, kill, kill-server, attach, probe-console, proxy, `--version`, each offered only when the host's `MuxCliVerbs` include it), `MuxCliHost` (what an executable supplies: paths, usage prefix, serve arguments, shell factory, verbs, console binding), `MuxProxyCommand` (`ntilde-mux proxy --stdio`) and `MuxVersionInfo` / `MuxCliJsonContext` (`--version --json`, which the App's installer reads)
- The `ntilde mux attach` text client (`TextClient/`, Phase 3 spec §6): `TextClientSession` (lifecycle,
  threads, exit codes), `TextClientModel` (its own buffer + parser), `TextClientRenderer` (dirty-row
  repaint), `DetachChord` (`Ctrl+\ d`), `IConsoleSurface` + `WindowsConsoleSurface` /
  `UnixConsoleSurface`

**Invariants**
- **One parse thread per session** owns the headless parser and buffer, and control items run only between `Process()` calls
- **Seq semantics:** `Output.seq` = raw offset before the chunk, `ResizeEvent.seq` = offset it applies at, snapshot `StreamSeq + DecoderTail.Length` = offset at capture, and clients drop the connection on any gap
- **The mux answers device queries** and clients never do
- Latest resize wins
- No thread-pool work on the output path
- A slow client is disconnected, never waited for (stream frames against the send budget, snapshots against their own separate bound)
- Attach limits are rejection ceilings, never clamps (#473)
- **Endpoints are current-user only and never TCP:** a `CurrentUserOnly` pipe on Windows (the client checks it too); a `0600` socket in a `0700` directory elsewhere, and the listener refuses a directory with any other mode or a live socket at the path. The protocol has no authentication by design
- Per-session input writer: `SendInput` and parser replies share one byte-capped (16 MiB) queue per session, so a child that stops reading stdin cannot stall the shared connection
- Exited, unattached sessions are reaped after `ReapGrace` (60 s); the daemon exits after `IdleExitAfter` (10 min) with no running session and no connection
- **Attach modes are decided on the parse thread.** `IfUnattached` is checked inside the same
  attach control item that subscribes the sink, on the session's one parse thread — never on the
  connection's reader thread — so two racing attaches resolve to exactly one winner (Phase 3 spec §3)
- **`sessionChanged` coalescing lives in the parse loop, with no timer.** The bounded wait the
  parse loop already does between `Process()` calls drives the trailing flush; no `Timer`,
  `Task.Delay`, `ThreadPool` or `Task.Run` on this path
- **The text client never relays raw output.** It renders every frame from its own
  `CaptureRenderSnapshot`, so a device query (DA, CPR, XTGETTCAP) the shell emits is answered by
  the model's parser and never reaches the terminal the client draws on
- **Console modes are restored on every exit path.** Detach, session exit, kill, disconnect,
  attach failure, a console write that throws, and a signal all reach `IConsoleSurface.RestoreMode()`
- **`Daemon/` and `Cli/` know nothing of the App or Platform** (`LayeringTests.Mux_daemon_and_cli_namespaces_have_no_app_or_platform_dependency`).
  Each executable injects what differs through `MuxCliHost`; the GUI's own daemon keeps the App's
  `DefaultTerminalSessionFactory`, so local behaviour cannot change through the remote daemon's
  factory
- **The proxy never parses a frame.** `ntilde-mux proxy --stdio` writes the preamble, then copies
  bytes on two dedicated threads; its stdout carries nothing but the preamble and frames
  (diagnostics go to stderr, the daemon logs to its own file). Exit codes are a contract the GUI
  classifies a drop by: 0 stdin ended, 3 the daemon side ended and the daemon's process is gone (the
  GUI's `DaemonStopped`), 4 the daemon dropped this connection but runs on, or stdout could not be
  written (a lost link to the GUI), 1 no daemon could be reached or spawned, 2 usage. On Unix it ends
  fds 1 and 2 for real (`UnixChannelStdio`: every copy to `/dev/null`) before it waits to decide 3 or 4
- **A user detach is never lost.** The text client waits (bounded, 1 s) for a ping reply after its
  final detach before it disposes the connection, and a refusal drop before a queued user detach
  keeps `DetachedByUser` (both found and fixed in Phase 4)
- **Nothing here may load OpenSSL** (see Mux.Contracts above; the guard walks every Ntilde assembly
  `ntilde-mux` runs)

**Test authority**
- `tests/Ntilde.Mux.Tests/` (`Daemon/`, `Cli/`, `Transport/StdioMuxTransportTests`, `Server/ClientInstanceIdTests`; + `MuxRealShellSmokeTests` and the real-daemon `MuxDaemonSmokeTests` in App.Tests)

---

## Ntilde.Mux.Daemon (`src/Ntilde.Mux.Daemon/`)

**Assembly:** `ntilde-mux` (published as one NativeAOT file per RID: linux-x64, linux-arm64, osx-arm64)
**Namespace:** `Ntilde.MuxDaemon` (`Ntilde.Mux.Daemon` is `Ntilde.Mux`'s own `Daemon/` namespace)
**Depends on:** Mux (and nothing else: no package references)
**Public surface:** none; `Program` only

**Owns**
- The remote multiplexer executable: its `Program.Main` hands every argument to
  `Ntilde.Mux.Cli.MuxCli.Execute` with an `ntilde-mux` `MuxCliHost` (default paths, usage prefix
  `ntilde-mux`, serve arguments `["serve"]`, `LocalShellSessionFactory`, verbs serve, proxy, ls, kill,
  kill-server, attach, `--version`)
- The static link of `rusty_pty` (`DirectPInvoke` + `NativeLibrary` over `librusty_pty.a` for
  `linux-*`/`osx-*` RIDs) and the single-file publish (referenced projects' `.pdb`s are dropped)

**Invariants**
- **References `Ntilde.Mux` only**, in the csproj (`ProjectFileLayeringTests.MuxDaemon_only_references_Mux`)
  and in IL (`LayeringTests.MuxDaemon_references_only_Mux`; `Ntilde.Pty` appears there only because
  `MuxCliHost.SessionFactory` is a Pty type), and it names no UI, App or Platform type
- **Needs only libc.** Nothing it runs references `System.Security.Cryptography`,
  `System.Net.Security` or `System.Net.Http`, which load OpenSSL at run time on Linux and which a
  remote host may not have (`Nothing_ntilde_mux_runs_references_an_OpenSSL_backed_assembly`). Its only
  dynamic dependencies are libc, libm, libgcc_s and the loader; the highest `GLIBC_` symbol must stay
  at or below 2.35 (asserted by CI's `mux_daemon_aot` and the release's `publish_mux_daemon`)
- **One file per RID**, nothing beside it but `.dbg`/`.dSYM` (asserted by the same jobs)
- Every verb goes through `MuxCli` (`CliCommandDispatchTests.Mux_daemon_dispatches_every_verb_through_MuxCli`);
  its types stay in `Ntilde.MuxDaemon`, and no other assembly uses that namespace
- It may print to the console (`DiagnosticSinkTests.ConsoleToolProjects`)

**Test authority**
- The verbs it hosts: `tests/Ntilde.Mux.Tests/Cli/`, `Daemon/`
- Real binary against a real sshd: `tests/Ntilde.App.Tests/Shell/Mux/Remote/RemoteMuxDockerE2eTests.cs`
  (`Category=DockerE2E`; needs `NTILDE_ENABLE_DOCKER_E2E=1` and `NTILDE_MUX_E2E_BINARY`; build a
  linux-x64 binary locally with `scripts/docker-publish-mux-daemon.sh linux-x64 artifacts/mux-daemon`)
- Layering: `tests/Ntilde.Architecture.Tests/`

---

## Ntilde.Launcher (`src/Ntilde.Launcher/`)

**Assembly:** `Ntilde.Launcher`, shipped as `ntilde.com` next to `Ntilde.exe` (win-x64, NativeAOT)
**Namespace:** `Ntilde.Launcher`
**Depends on:** *(nothing: no project or package references)*
**Public surface:** `LauncherCommandLine` (`Tail`, `Build`, `ReleaseEventVariable`, `TargetMissingExitCode`)

**Owns**
- The Windows console launcher (multiplexer Phase 4 spec §11): find `Ntilde.exe` beside itself
  (exit 9009 when missing), start it in the same console with the raw command-line tail, wait for it
  or for the release event, and forward its exit code
- Its kernel32 P/Invokes (`LauncherNative`) and its console control handler

**Invariants**
- **No references at all** (`ProjectFileLayeringTests.Launcher_has_no_references`,
  `LayeringTests.Launcher_references_no_Ntilde_assembly`): it runs before the App, it is not a second
  entry point into it
- **Arguments pass byte-identical.** The child's command line is the target's quoted path plus the
  raw tail of `GetCommandLineW()` after argv[0] by the CRT's rule; nothing is re-quoted
- **Ctrl+C is never regenerated.** The child shares the console and the process group, so Windows
  delivers Ctrl+C and Ctrl+Break to it; the launcher's handler only keeps the launcher waiting
- **It returns early only when the App says so**, through the `NTILDE_LAUNCHER_RELEASE` event that
  `Ntilde.Shell.LauncherRelease.Signal()` sets on the GUI path; command-line modes are always waited for
- Off Windows it refuses (exit 1)

**Test authority**
- `tests/Ntilde.App.Tests/Launcher/`: `LauncherCommandLine.Tail`/`Build` theories on every OS, the
  wait (`LauncherWaitTests`), end-to-end runs beside a `cmd.exe` stand-in (Windows-gated), and the
  off-Windows refusal; the App's side in `tests/Ntilde.App.Tests/Shell/LauncherReleaseTests.cs`
- Release order in `Program.Main`: `tests/Ntilde.Architecture.Tests/CliCommandDispatchTests.cs`
- CI: `aot_gate` publishes it and runs `ntilde.com /d /c exit 7` beside a `cmd.exe` copy (must exit 7)

---

## Ntilde.VtContract (`src/Ntilde.VtContract/`)

**Namespace:** `Ntilde.VtContract`
**Depends on:** *(leaf — only BCL)*
**Public surface:** `VtCapabilityCatalog`, `VtCapability`, `VtSupport`, `VtCapabilityManifestException`

**Owns**
- The machine-readable catalog of developer-facing VT capability claims
- Strict validation of catalog schema, unique sequence keys, support states, evidence links, and parser contract case names

**Non-responsibilities**
- Parsing terminal input or dispatching escape sequences; `Ntilde.VT` remains the sole owner of runtime VT semantics
- Rendering, application UI, PTY I/O, or MCP transport

**Invariants** (enforced by `VtContract_csproj_has_no_project_references` and the VT capability contract tests)
- Leaf assembly — no project references, so conformance tooling, parser tests, and MCP developer tools can consume the same claims without opening a path into terminal core
- The embedded JSON is the canonical capability source; supported entries require concrete repository evidence and an executable parser contract case

**Test authority**
- Schema and parser contracts: `tests/Ntilde.VT.Tests/VtCapabilityCatalogTests.cs`, `tests/Ntilde.VT.Tests/VtCapabilityContractTests.cs`
- Matrix agreement: `tests/Ntilde.Platform.Tests/Conformance/VtConformanceToolTests.cs`
- Developer-tool agreement: `tests/Ntilde.McpServer.Tests/V2ToolsTests.cs`

---

## Ntilde.Backup (`src/Ntilde.Backup/`)

**Namespace:** `Ntilde.Backup`
**Depends on:** *(leaf — only BCL)*
**Public surface:** `BackupService`, `SnapshotScheduler`, `BackupCatalog`, `BackupCategory`, `BackupManifest`, `BackupOutcome`, `InspectOutcome`, `BundleInspection`, `SnapshotInfo`, `SnapshotReason`, `ImportMode`, `BundleReader`, `BundleWriter`
**Internals exposed to:** `Ntilde.App.Tests` only (`BackupService.ResolveImportStagingRoot`, `SnapshotScheduler.HasPendingChange`/`BeforeSnapshotForTest`/`NotifyFileSystemEvent` — test seams, same shape as `CommandAssist`'s grant below)

**Owns**
- Export/import/snapshot/restore of Ntilde configuration into `.ntildebackup` bundles (a zip with a manifest)
- The category→path catalog (`BackupCatalog`) that decides what a bundle contains
- The debounced snapshot scheduler that watches the backed-up paths and writes an automatic snapshot after changes go quiet

**Non-responsibilities**
- Resolving the app-data root. `BackupService` takes it as a constructor argument rather than
  reading `AppPaths` itself, so both the App and a test can point it at whatever tree they like.
  The one caller that does read `AppPaths` (`BackupCommand`, the `backup` CLI verb) stays in
  `Ntilde.App/Shell/Backup/` rather than moving here — see the invariants below.
- Logging. Nothing here calls a static logger; callers pass an optional `Action<string>? log`
  into `BackupService`'s and `SnapshotScheduler`'s constructors (defaulting to a no-op), mirroring
  the existing `TimeProvider?` injection style.

**Invariants** (enforced by `Backup_csproj_has_no_project_references` and
`McpServer_csproj_only_references_approved_leaf_dependencies`)
- **Leaf assembly — no project references, ever.** This is what makes it safe for
  `Ntilde.McpServer` to reference: a leaf cannot smuggle in App, VT, Pty, or Rendering no
  matter what it depends on, because it depends on nothing. Task 10a's first attempt routed the
  MCP backup tools through `Ntilde.Platform` instead — which itself has no App/VT
  dependency, but does reference `Ntilde.Pty`, so McpServer → Platform → Pty transitively
  broke McpServer's "does not reference Pty" invariant with the reasoning fully intact. Extracting
  this leaf closes that hole by construction, not by remembering to re-check every transitive hop
  Platform might one day pick up.
- No secret material. A bundle carries connection profiles with their `RememberPasswordInVault`
  flag but never password/key material (issue #100) — `BackupService` never reads secret storage.
- `BackupCommand` (the `backup` CLI verb) is the one caller-facing piece that stays behind in
  `Ntilde.App/Shell/Backup/`: it is the only consumer that needs `AppPaths`, and the CLI
  already lives in App (see the Task 7 dispatch guard on `Ntilde.App`, below). Moving it here
  would have bought nothing, since App already references this assembly.

**Test authority**
- `tests/Ntilde.App.Tests/Backup/`
- Palette/scheduler wiring: `tests/Ntilde.App.Tests/Core/MainWindowBackupPaletteTests.cs`
- Settings UI: `tests/Ntilde.App.Tests/Core/SettingsWindowBackupSectionTests.cs`
- Leaf-boundary invariants: `tests/Ntilde.Architecture.Tests/`

> **Note:** extracted from `Ntilde.App/Shell/Backup/` in Task 10a (2026-08-27), then
> re-extracted from a one-round stop at `Ntilde.Platform/Backup/` into its own leaf project
> in Task 10a fix round 1, once review found the Platform routing broke McpServer's
> Pty-independence invariant.

---

## Ntilde.CommandAssist (`src/Ntilde.CommandAssist/`)

**Namespace:** `Ntilde.CommandAssist` (+ `.Application`, `.Domain`, `.Models`, `.Storage`, `.ShellIntegration`, `.ViewModels`)
**Depends on:** *(leaf — only BCL)*
**Public surface:** `CommandAssistController`, `AssistSessionState`, `CommandAssistAnchorCalculator` (+ `AssistRect`/`AssistPoint`/`AssistSize`), `AssistKey`/`AssistModifiers`, `CommandAssistModeRouter`, `CommandAssistKeyRouter`, `CommandAssistInsertionPlanner`, `CommandAssistResultBuilder`, `RecognizedCommandParser`, the `I*Store` / `I*Provider` / `ISuggestionEngine` / `ISecretsFilter` domain contracts and their local implementations, `IShellIntegrationProvider` + the four shell providers and bootstrap builders, `ShellIntegrationRegistry`, `ShellLifecycleTracker`, `JsonlHistoryStore`, `JsonSnippetStore`, `CommandAssistJsonContext` / `CommandAssistJsonLinesContext`, and the assist view-models
**Internals exposed to:** `Ntilde.App.Tests` only. The App is deliberately not granted
`InternalsVisibleTo`: the two helpers `TerminalPane` needs (`CommandAssistKeyRouter`,
`CommandAssistInsertionPlanner`) are public pure-static functions over public types, so the App
consumes this assembly through its published surface. The controller's three collaborators
(`AssistSessionStateMachine`, `AssistSessionContext`, `CapturePipeline`, `SuggestionOrchestrator` +
`SuggestionRefreshOutcome`) and the controller's own `SessionState` accessor are `internal`: only
`CommandAssistController` constructs them, and the only outside readers are the unit tests. The
`AssistSessionState` enum is the one exception that stays public - no public member exposes it, but
the state machine's transition-table tests drive it through `[Theory]` parameters, and xUnit
requires public test classes.

**Owns**
- Assist domain: suggestion ranking, path suggestions, secrets redaction, local docs/recipes, heuristic error insight
- Assist models: suggestions, query/context snapshots, history entries, snippets, failure context
- Assist storage: `history.jsonl` (append-only, in-memory index, periodic compaction, one-time
  migration from the pre-V2 `history.json`) and `snippets.json` (whole-file: low write volume),
  plus their source-generated JSON contexts
- Shell integration: the `OSC 133` contract, the bash/zsh/fish/PowerShell bootstrap builders and providers, provider registry, lifecycle tracking, ordered async event dispatch
- Assist view-models (`INotifyPropertyChanged` only — no toolkit types)
- Application core: controller (a facade over the session state machine, the capture pipeline and
  the suggestion orchestrator, with `AssistSessionContext` carrying the environment they share),
  mode router, insertion planner, result builder, key router, anchor calculator

**Non-responsibilities**
- Rendering the assist surfaces. The Avalonia `UserControl`s stay in the App at
  `src/Ntilde.App/CommandAssist/Views/` under `Ntilde.CommandAssist.Views` — the one
  namespace prefix deliberately shared between two assemblies.
- Resolving application state. Storage paths (`AppPaths`) and settings (`TerminalSettings`) are
  App concerns; they are passed in. `Shell/CommandAssistServices.cs` in the App composes the graph,
  `AppServices.Build` builds the single instance, and `MainWindow` injects it into every pane.

**Invariants**
- **No Avalonia (or Skia) in the dependency closure** — enforced twice: at IL level by
  `CommandAssist_must_not_depend_on_Avalonia_or_the_App` (`LayeringTests.cs`) and at project level by
  `CommandAssist_csproj_must_have_no_project_or_avalonia_references` (`ProjectFileLayeringTests.cs`).
  UI vocabulary crosses the boundary through App-side mappers only: `Avalonia.Input.Key`/
  `KeyModifiers` → `AssistKey`/`AssistModifiers` via `Controls/AssistKeyMapper.cs`, and
  `Avalonia.Rect` → `AssistRect` by construction inside the calculator.
- Leaf assembly — no project references at all, so it stays cheap to reference and cannot drift
  into the UI layer through a transitive edge.
- All types in `Ntilde.CommandAssist.*` (`Leaf_assembly_types_reside_in_its_own_namespace`);
  the App may only use that prefix for Views
  (`App_may_only_use_the_CommandAssist_prefix_for_Views`).

**Test authority**
- `tests/Ntilde.App.Tests/CommandAssist/` (kept there for now — the suite exercises the assist
  assembly and the App's `TerminalPane` wiring together)
- Architecture invariants: `tests/Ntilde.Architecture.Tests/`

> **Note:** extracted from the App in #114 as Phase 0 of the Command Assist V2 rebuild
> (`docs/plans/2026-08-01-command-assist-v2-plan.md`). Phase 0b then replaced the static
> `CommandAssistInfrastructure` locator with the injected `CommandAssistServices`, unified ranking on
> `CommandAssistSuggestionEngine`, and swapped the history store for JSONL. Phase 0c split
> `CommandAssistController` into `AssistSessionStateMachine`, `CapturePipeline` and
> `SuggestionOrchestrator` (plan task 4), completing Phase 0.

---

## Ntilde.McpServer (`src/Ntilde.McpServer/`)

**Namespace:** `Ntilde.McpServer` (+ `.Tools`)
**Depends on:** AgentHost.Contracts, Backup (both zero-reference leaves — see below)
**Public surface:** `Program` (stdio entry point), `AgentHostClient`, `RepoContext`, and the tool groups under `Tools/` (`SessionTools`, `VtTools`, `ThemeTools`, `SettingsTools`, `ConnectionProfileTools`, `ProjectTools`, `WorkflowTools`)

**Owns**
- The opt-in MCP server that lets external agents observe live terminal sessions, and — behind a separate opt-in — drive them
- Tool schemas exposed over MCP, and their validation of caller input
- Talking to the running app through `AgentHostClient` over the AgentHost protocol
- The read-only backup tools (Task 10b) built on `Ntilde.Backup`'s `BackupService`

**Invariants**
- **Does not reference App, VT, Pty, Rendering, or `Ntilde.Platform`.** It is a client of the
  running app over the wire, not an in-process consumer — so it can never reach into terminal state
  directly. `Ntilde.Platform` is named explicitly (not just "the UI layer") because it is the
  one non-obvious way in: Platform itself has no App/VT dependency, but it does reference
  `Ntilde.Pty`, so a McpServer → Platform reference would transitively break "does not
  reference Pty" while looking, at the csproj level, like a small and unrelated addition (Task 10a
  fix round 1 shipped exactly this and caught it in review). `AgentHost.Contracts` and
  `Ntilde.Backup` are the only two references allowed, and both are leaves enforced to have
  zero project references of their own, so neither can ever become a backdoor to the forbidden set.
- Observe and act are separately gated. A tool that mutates session state belongs behind the act opt-in.
- Tool schemas are part of the public contract: `ConnectionProfileDriftGuardTests` exists to catch schema drift against the app's real profile shape.

**Test authority**
- Primary: `tests/Ntilde.McpServer.Tests/`
- Schema-drift guard: `ConnectionProfileDriftGuardTests.cs`
- Stdio end-to-end: `McpServerStdioE2ETests.cs`

> **Note:** the dev companion runs the server from `bin/`, so a connected client
> can hold a lock that blocks repo builds — tracked as #211. See
> `docs/mcp-dev-companion.md`.

---

## Ntilde.App (`src/Ntilde.App/`)

**Namespace:** `Ntilde` (NOT `Ntilde.App` — see test-root-namespace note in `Ntilde.App.Tests`)
**Depends on:** Platform, VT, Rendering, Pty, Replay, AgentHost.Contracts, CommandAssist, Backup, Mux, Mux.Contracts, Avalonia 12.0.4, SkiaSharp 3.119.4
**Public surface:** `App`, `MainWindow`, `TerminalPane`, settings window, theme manager, command palette, command-assist controller, profile importers, startup orchestrator

**Owns**
- Avalonia UI: windows, controls, view-models
- The currently-in-App renderer composition: `Shell/TerminalView.cs`, `Shell/TerminalDrawOperation.cs` (slated to move to Rendering — #113)
- Theme management and bundled fonts
- Profile import/export (Alacritty, iTerm2, Windows Terminal)
- Command palette and shortcuts
- The Command Assist *presentation* layer only: `CommandAssist/Views/` (Avalonia `UserControl`s),
  the `TerminalPane` wiring, and `Shell/CommandAssistServices.cs` as the composition root.
  Everything else moved to `Ntilde.CommandAssist` in #114.
- Startup orchestration (seven `Startup*.cs` files in `Shell/`)
- Workspace and session lifecycle
- SSH UI: connection manager, transfer center, remote files sidebar, vault, sftp service, ssh-askpass
- Persistent sessions (`Shell/Mux/`, namespace `Ntilde.Shell.Mux`):
  - `MuxCommand.cs`: the `ntilde mux serve|ls|kill|kill-server|attach|probe-console` adapter, dispatched from `Program.cs` before `AppLogger` and Avalonia. It strips `mux` and calls `Ntilde.Mux.Cli.MuxCli.Execute` with the App's `MuxCliHost`: the app-data root (`MuxDiscovery.GetRootDirectory()`, or a test's root override), `mux serve` as the serve arguments, `DefaultTerminalSessionFactory`, and `CliConsoleBindings.Prepare`. The daemon process (`MuxServeHost`), the launcher/spawner and the startup probe moved to `Ntilde.Mux.Daemon` in Phase 4, the verb bodies to `Ntilde.Mux.Cli`
  - `MuxConnectionHosts.cs` / `MuxConnectionHost.cs` / `MuxHostPolicy.cs`: one connection host per endpoint, created lazily; each keeps one shared `MuxClient` (warm-up, reconnect, detach on teardown, tracked and queued kills). `local` backs off 30 s after a failed connect; a remote host has the remote policy, the liveness ping and the reconnect loop (Phase 4 spec §5, §7.2-§7.3)
  - `MuxEndpointId.cs`: `PaneNode.MuxEndpoint` parsed and formatted: `local` or `ssh:<profileId>` (null = `local`)
  - `MuxTerminalSessionFactory.cs` / `PersistentSessionFactory.cs` / `SessionPersistenceMode.cs`: local panes go through the mux when `SessionPersistence` is `KeepOnClose`; SSH panes whose profile has `PersistRemoteSessions` go to their `ssh:` host (`RoutesRemote`); other SSH panes and local failures fall back to the default factory. Restore attaches `IfUnattached` on a v2 daemon (server-decided) or falls back to the pane's own `AttachedClients == 0` check on v1; a reattach after a drop opens `Shared`
  - `MuxOrphans.cs` / `PaneDisposition.cs`: orphan adoption on launch, `local` endpoint only (skips sessions with `DetachedByUser`, Phase 3 spec §7.7); a user close kills the session, window teardown detaches
  - `MuxSessionPicker.cs`: the "Attach to Session…" picker's rows (title, command, cwd, size, attached count, running/exited), sorted running-first; local sessions only
  - `MuxCommandMatch.cs`: whether a daemon session runs the program a restoring pane expects, compared by executable file name so a path difference alone does not start a spurious fresh shell (local restores only; a remote session's command is the remote login shell, which the GUI does not know)
  - `SharedCloseChoice.cs`: the shared-close prompt's three-way answer (Cancel / Close / Detach)
  - `RemoteMuxStatusText.cs`: the connection editor's install status line
- Remote persistence (`Shell/Mux/Remote/`, namespace `Ntilde.Shell.Mux.Remote`, Phase 4 spec §7-§9):
  - `RemoteMuxConnector.cs`: one connect attempt: the remote command, the exec transport off the UI thread, the stdio preamble, the hello with the host's `ClientInstanceId`, and the classified failure; `RemoteMuxCommand.cs` builds the remote command lines, `RemoteMuxFailureClassifier.cs` / `RemoteFailureKind.cs` the failure kinds (`NotInstalled`, `Unsupported`, `VersionMismatch`, `SshFailed`, `ProxyFailed`, `NeedsUser`)
  - `RemoteMuxHostFactory.cs`: builds a remote `MuxConnectionHost` from a profile (construction only) and the per-attempt OpenSSH or native exec transport
  - `RemoteMuxInteractionHandler.cs`: a remote host's native prompts: the in-memory secret record, host-key trust for automatic attempts, and the abort that replaces an empty answer
  - `MuxReconnectLoop.cs` / `IMuxTimerScheduler.cs`: the backoff loop and its one-shot timers (a fake scheduler in tests)
  - `RemoteHostProbe.cs`, `MuxDaemonRid.cs`, `IMuxDaemonAssetSource.cs` + `GitHubReleaseMuxAssetSource.cs` / `LocalFileMuxAssetSource.cs`, `RemoteMuxInstallCommands.cs`, `RemoteMuxInstaller.cs`, `RemoteOutputText.cs`: the install flow (probe, verified asset, upload script, version check), UI-free
  - `Views/Ssh/RemoteMuxInstallDialog.cs` (namespace `Ntilde.Views.Ssh`): the code-built install dialog, opened from the connection editor's Reliability tab and from the *Persistent SSH unavailable* notice
- Windows launch support (`Shell/`): `LauncherRelease.cs` (sets the `ntilde.com` release event on the GUI path; discards it in the mux branch) and `UserPathRegistration.cs` (the install folder on the user `PATH`, from Velopack's install, update and uninstall hooks only); `AppVersionInfo.cs` (the app's version, for the About window, the editor and the release download)

**Non-responsibilities**
- VT parsing (delegated to VT)
- Buffer mutation (only via explicit VT APIs)
- Skia primitive logic (delegated to Rendering)

**Invariants**
- App is allowed to depend on all production assemblies; nothing depends on App except Cli and the App.Tests project (and Architecture.Tests, which references everything for inspection)
- Renderer-side bugs ("the pixels look wrong") are diagnosed by chasing back through Rendering → VT, not by patching App
- With `SessionPersistence` off no daemon is ever spawned; the daemon mode never initialises Avalonia; a daemon spawn never inherits the caller's std handles
- **Remote commands are `sh -c` scripts with no single quote inside**, or a recorded absolute path made only of characters no shell treats specially (`RemoteMuxCommand.IsSafeAbsolutePath`). sshd hands them to the user's login shell, which may be bash, fish, tcsh or nushell; both shapes parse alike in all of them, and neither looks anything up on `PATH`. The install scripts (`RemoteMuxInstallCommands.UploadForTrial`, `CommitUpload`, `DiscardUpload`) follow the same rule; an installed `ntilde-mux` is replaced only by the commit, by rename, after a byte-count check, a trial run and the app's check of the version it reported
- **Automatic attempts never prompt.** The reconnect loop's attempts and the kill-delivery attempt are non-interactive: OpenSSH in batch mode with no askpass, native answering only from the host's in-memory secret record and trusting only host keys the known-hosts store already trusts. With nothing to answer, a native attempt aborts (closes the session) rather than submit an empty password, and the failure is `NeedsUser`, which stops the loop. A password is never replayed for a profile with jump hops. Only a user's request (a pane opening, Enter) may show a dialog, and it cancels any automatic attempt in flight
- **No remote connect on the UI thread.** `MuxTerminalSessionFactory.CreatePersistent` for a remote request and a remote host's `GetClient` may wait the 120 s connect timeout behind prompts that themselves need the UI thread; the pane runs them off it, with a generation counter that discards a stale result
- **A remote pane's kill is never lost silently.** Every close of a non-local pane goes through `MuxConnectionHost.KillWhenConnected`: sent now, or queued (kept across a give-up) and sent first on the next connect; dropped only when the daemon stopped (its sessions are gone) or the host is disposed, with a log line
- **The GUI's bootstrap environment never leaves the machine:** a remote `SpawnParams.EnvironmentOverrides` is always null
- **Recording an install changes only the profile's four mux install fields** (`SshConnectionService.RecordRemoteMuxInstall`), on the store's own copy, never through the editor's normalisation
- **`ntilde.com` is released only on the GUI path, and `PATH` is written only from the Velopack hooks** (`CliCommandDispatchTests.The_launcher_is_released_only_on_the_GUI_path`, `The_PATH_is_registered_only_from_the_Velopack_hooks`)

**Test authority**
- `tests/Ntilde.App.Tests/` (the largest suite)
- Remote persistence: `tests/Ntilde.App.Tests/Shell/Mux/Remote/` (connector, classifier, interaction handler, host factory, reconnect loop, installer, probe, asset sources, all over `FakeRemoteHost` and a fake scheduler), `Controls/MuxRemotePaneTests.cs`, `Core/MainWindowMuxRemoteTests.cs`, `Core/RemoteMuxInstallDialogTests.cs`; the Docker E2E `Shell/Mux/Remote/RemoteMuxDockerE2eTests.cs` (`Category=DockerE2E`)
- Windows launch support: `tests/Ntilde.App.Tests/Shell/LauncherReleaseTests.cs`, `Shell/UserPathRegistrationTests.cs`
- xunit.v3 + `Avalonia.Headless.XUnit 12.0.4`; **do not downgrade** the Avalonia stack below 12.0.4 — earlier versions leak the headless dispatcher and hang `dotnet test`

---

## Ntilde.Cli (`src/Ntilde.Cli/`)

**Namespace:** `Ntilde.Cli`
**Depends on:** App
**Public surface:** `Program` (Main entry point)

**Owns**
- Headless CLI entry — used for `vt-report` and automation
- Today the CLI shim is built by the App project via the `BuildCliShim` MSBuild target and copied into App's output as a sidecar
- Reaches into App via `InternalsVisibleTo("Ntilde.Cli")`

**Invariants**
- This dependency direction is **inverted** — see `docs/ARCHITECTURE.md` § 14 Known Tech Debt. A `Ntilde.Bootstrap` library should mediate.

**Test authority**
- `tests/Ntilde.App.Tests/VtReportCliTests.cs`
- Console-output rules for CLI vs GUI assemblies: `tests/Ntilde.Architecture.Tests/DiagnosticSinkTests.cs`

---

## Ntilde.Conformance (`src/Ntilde.Conformance/`)

**Namespace:** `Ntilde.Conformance`
**Depends on:** *(standalone Exe, no project references)*
**Public surface:** `VtConformanceReportTool`, `VtConformanceCli`

**Owns**
- VT conformance matrix parser (reads `docs/vt_coverage_matrix.md`)
- Report generator (writes `src/Ntilde.App/Resources/vt-conformance-report.json`)
- Evidence-link validator (fails CI if a matrix row claims a test file that doesn't exist)

**Invariants**
- Standalone tool — no library dependencies on the rest of the assemblies; consumed via project references from test projects (which run it in-process for validation)
- The shipped `vt-conformance-report.json` artifact's `matrixSha256` must match a fresh re-run on `vt_coverage_matrix.md` — verified by `tests/Ntilde.App.Tests/VtReportCliTests.ShippedArtifact_MatchesFreshToolOutput`

**Test authority**
- `tests/Ntilde.Platform.Tests/Conformance/VtConformanceToolTests.cs`
- `tests/Ntilde.App.Tests/VtReportCliTests.cs`

---

## Tests (First-Class Owners)

### `tests/Ntilde.Architecture.Tests/`

**Owns** the layering and namespace-alignment rules. Adding a new architectural invariant means adding a fact here. See `docs/ARCHITECTURE.md` § 12 for the current enforced rule set.

Four files, by concern:
- `LayeringTests.cs` — assembly-level dependency rules (`Vt_must_be_a_leaf_assembly`, `Pty_must_not_depend_on_Vt`, …)
- `ProjectFileLayeringTests.cs` — the same rules asserted against the `.csproj` files, so a stray `ProjectReference` fails even if no code uses it yet
- `NamespaceAlignmentTests.cs` — one assembly, one namespace prefix
- `DiagnosticSinkTests.cs` — GUI and library code must not write diagnostics to the console; CLI tools may

### `tests/Ntilde.VT.Tests/` + `tests/Ntilde.Rendering.Tests/`

**Own** the fast unit suites for VT and Rendering — designed to run in seconds, no Avalonia in the dependency closure, suitable for tight inner-loop iteration.

### `tests/Ntilde.Platform.Tests/` + `tests/Ntilde.App.Tests/`

**Own** integration coverage. Platform.Tests is the SSH + platform-utilities suite; App.Tests is the full Avalonia-headless integration suite (replay regressions, golden PNGs, command-assist harnesses, shell-integration tests).

### `tests/Ntilde.McpServer.Tests/`

**Owns** the MCP tool surface: tool behaviour, input validation, the connection-profile schema-drift guard, and a stdio end-to-end test that exercises the real protocol rather than a mock.

### `tests/Ntilde.Mux.Tests/`

Scripted-session suite for the mux: contracts, transport, headless session, server, client, the daemon process and the verbs (`Daemon/`, `Cli/`, moved here from App.Tests with the code in Phase 4), and end-to-end scenarios. Shares `TerminalStateAssert` and `ParityCorpus` with VT.Tests by file link.

### `tests/Ntilde.Benchmarks/` + `tests/Ntilde.ExternalSuites/`

Standalone Exes — not test libraries — used for performance benchmarking (BenchmarkDotNet) and external-scenario drivers (Vttest, native SSH transcripts). Not discovered by `dotnet test`.

---

## Guiding Rule

> If tests disagree with code, tests are correct.

> If documentation disagrees with code, **add an architecture test that catches the disagreement**. Then fix whichever side was wrong.

> If code disagrees with the architecture-test layer, the change must un-skip a known violation or add a new rule. Silently changing layering is never the right move.
