# Ntilde – Architecture & Design Rationale

This document describes the **internal architecture**, **invariants**, and **design trade-offs**
of Ntilde.

It is authoritative for contributors and automated agents.

---

## 1. Architectural Goals

Ntilde is designed to satisfy four non-negotiable goals:

1. **Deterministic terminal semantics**
2. **Cross-platform behavioral parity**
3. **Incremental, flicker-free rendering**
4. **Test-enforced correctness**

All architectural decisions follow from these goals.

---

## 2. Assembly Graph

Ntilde is structured as seventeen focused .NET assemblies. The dependency graph is acyclic. Each assembly's namespace matches its assembly name (enforced by `tests/Ntilde.Architecture.Tests/NamespaceAlignmentTests`). The newest exception is pinned by the same tests: `src/Ntilde.Mux.Daemon` builds the assembly `ntilde-mux` in the namespace `Ntilde.MuxDaemon`, because `Ntilde.Mux.Daemon` is the namespace of `Ntilde.Mux`'s own `Daemon/` folder.

```
Cli ──► App ──► Platform ──► Pty ──► Replay ──► VT
            ├─► Rendering ─────────────────────► VT
            ├─► VT
            ├─► Pty
            ├─► Replay
            ├─► Mux ──► Mux.Contracts  (leaf)
            ├─► Mux.Contracts          (leaf)
            ├─► CommandAssist          (leaf)
            ├─► Backup                 (leaf)
            └─► AgentHost.Contracts    (leaf)

McpServer  ──► AgentHost.Contracts     (leaf)
           ├─► Backup                  (leaf)
           └─► VtContract              (leaf)

Conformance ─► VtContract              (leaf)

Mux  ──► Pty, VT, Replay, Mux.Contracts
Mux.Contracts                           (leaf)

Mux.Daemon (ntilde-mux) ──► Mux        (and nothing else)
Launcher   (ntilde.com)                 (no references at all)
```

Five of those seventeen are **leaves with no project references at all**:
`CommandAssist`, `Backup`, `VtContract`, `AgentHost.Contracts`, `Mux.Contracts`. The empty
reference list is the point — it is what lets `McpServer` share real code with
the app without acquiring a transitive path into `App`, `VT`, `Pty` or
`Rendering`. An earlier attempt routed the MCP backup tools through
`Ntilde.Platform`, which has no App or VT dependency but does reference
`Pty`, and that broke `McpServer`'s Pty-independence invariant with the
reasoning fully intact. Each empty list is asserted individually by the
architecture tests. (`Ntilde.Launcher` references nothing either, but it is a
standalone Windows executable, not a library anything else references: section 8.3.)

Concretely, from the `.csproj` graph:

| Assembly | Depends on | Owns |
|---|---|---|
| `Ntilde.VT` | (leaf) | VT/ANSI parser, terminal buffer state, scrollback, reflow, transferable terminal/parser state (TerminalStateSnapshot) |
| `Ntilde.Replay` | VT | Session recording/playback, snapshots, replay format v2 |
| `Ntilde.Rendering` | VT, SkiaSharp | Skia glyph atlas/cache, pixel grid, sixel decoder |
| `Ntilde.Pty` | Replay | PTY transport: rust-PTY adapter, session contracts, session factory contract, UTF-8 chunk decoding. **Does not depend on VT** — see arch test `Pty_must_not_depend_on_Vt` |
| `Ntilde.Platform` | Pty | Platform-utilities: input routing, path mapping, SSH transport/sessions, SSH exec channels (`Ssh/Exec/`, section 8.2), credential vault |
| `Ntilde.CommandAssist` | (leaf) | Command Assist domain, models, storage, shell integration, view-models and application core. **No Avalonia** — see arch test `CommandAssist_must_not_depend_on_Avalonia_or_the_App` |
| `Ntilde.Backup` | (leaf) | `.ntildebackup` bundle format, export/import/restore, the category-to-path catalogue, the debounced snapshot scheduler. Never reads secret storage |
| `Ntilde.VtContract` | (leaf) | The machine-readable VT capability catalogue (`vt-capabilities.json`) and its strict schema validation |
| `Ntilde.AgentHost.Contracts` | (leaf) | Wire protocol between the app's agent host and any external client: frames, discovery, source-generated JSON context |
| `Ntilde.Mux.Contracts` | (leaf) | Multiplexer wire protocol: framing, frame kinds, binary payload codecs, source-generated JSON DTOs, error codes, version negotiation |
| `Ntilde.Mux` | Pty, VT, Replay, Mux.Contracts | Multiplexer core: headless authoritative sessions (one parse thread each), server, client + `MuxClientSession : ITerminalSession`, in-memory, local (named pipe / Unix socket) and stdio-proxy (`StdioMuxTransport`) transports, the daemon host (`MuxDaemonHost`), the daemon process around it (`Daemon/`: serve, launch-or-spawn, paths, log, the remote daemon's shell factory), every `mux` verb (`Cli/`: `MuxCli`, including `proxy --stdio` and `--version`), and the `ntilde mux attach` text client (`TextClient/`, section 8.1). **Must not reference Platform, App, Avalonia or SkiaSharp** |
| `Ntilde.Mux.Daemon` | Mux | `ntilde-mux`: the remote daemon, its stdio proxy and its CLI, one NativeAOT file per RID (linux-x64, linux-arm64, osx-arm64) with `rusty_pty` linked statically. Its `Program` only hosts `Ntilde.Mux.Cli`. Runs on libc alone: nothing it runs may load OpenSSL (section 8.2) |
| `Ntilde.Launcher` | *(none)* | `ntilde.com`: the Windows console launcher for `Ntilde.exe` (kernel32 P/Invokes only, NativeAOT, section 8.3) |
| `Ntilde.App` | Platform, VT, Rendering, Pty, Replay, CommandAssist, Backup, AgentHost.Contracts, Mux, Mux.Contracts | Avalonia UI shell: windows, controls, command palette, settings, themes, command-assist views, Agent Output panel. Also the `ntilde mux` CLI adapter over `Ntilde.Mux.Cli`, the GUI's mux connections, local and remote (`Shell/Mux/`, `Shell/Mux/Remote/`, sections 8.1 and 8.2), and the launcher release and user-PATH registration on Windows (section 8.3) |
| `Ntilde.McpServer` | AgentHost.Contracts, Backup, VtContract | stdio MCP server: repo/dev-companion tools, config validators, and the opt-in observe/act channel into live sessions. **Must not reference App, VT, Pty or Rendering** |
| `Ntilde.Cli` | App | Headless CLI shim (`vt-report`, `--replay`, askpass, `backup` verbs) |
| `Ntilde.Conformance` | VtContract | VT conformance matrix tool used by tests and CI |

### Hard Rule

> **No OS-specific logic in `Ntilde.VT`.** VT is the leaf — no Avalonia, no Skia, no native interop, no I/O. Enforced by `LayeringTests.Vt_must_be_a_leaf_assembly`.

---

## 3. Terminal Engine — `Ntilde.VT`

The terminal engine is the **single source of truth** for terminal semantics. The Avalonia UI is downstream — if the rendered pixels look wrong, the bug is in the VT engine, not the renderer.

### 3.1 Responsibilities
- Parse VT / ANSI escape sequences (`src/Ntilde.VT/AnsiParser.cs`)
- Maintain deterministic screen state in `TerminalBuffer` (`src/Ntilde.VT/TerminalBuffer.cs` + partial files)
- Handle alternate-screen transitions
- Manage scrollback (`src/Ntilde.VT/Buffer/ScrollbackPages.cs`)
- Perform lossless reflow on resize (`src/Ntilde.VT/TerminalBuffer.ReflowEngine.cs`)
- Provide read-only snapshots for rendering and replay

### 3.2 Components

#### `AnsiParser`

State machine for ESC, CSI, OSC, DEC-private modes. Emits **semantic operations** against `TerminalBuffer`, not rendering actions.

**Key invariant:** Same byte stream → same sequence of semantic operations.

#### `TerminalBuffer`

Applies semantic operations to terminal state. Maintains cursor, modes, tab stops, margins. Owns main + alternate screen buffers and scrollback.

**Key invariants**
- Alternate screen is fully isolated from main screen
- Scrollback is immutable once flushed
- Buffer state is renderer-agnostic — no Skia, no Avalonia in the type surface
- All read access requires holding `TerminalBuffer.Lock` (`ReaderWriterLockSlim`); reads without the lock throw `AssertLockHeld`

#### `TerminalRow` / `TerminalCell`

Row/cell semantics. Cell equality is defined only on render-affecting state — no renderer fields allowed.

### 3.3 Determinism

The VT engine is **purely deterministic**:

- No timers
- No threading assumptions beyond the documented lock
- No OS calls
- No UI interactions

This enables deterministic replay, cross-platform parity testing, and safe refactoring.

---

## 4. Recording / Playback — `Ntilde.Replay`

A focused module that owns the record/replay file format and the snapshot/byte-stream coupling. References VT for snapshot types (`BufferSnapshot`), but is independent of Pty, Rendering, and App.

`PtyRecorder`, `ReplayWriter`, `ReplayReader`, `ReplayRunner` live here. Goldens and regression suites consume `ReplayRunner`.

---

## 5. Renderer Primitives — `Ntilde.Rendering`

Skia-only helpers: glyph atlas (`GlyphAtlas`), glyph cache (`GlyphCache`), pixel grid math (`PixelGrid`), font wrappers (`SharedSKFont`, `SharedSKTypeface`), image registry, sixel decoder, render performance metrics.

This module is intentionally **Avalonia-agnostic**. The actual Avalonia `DrawOperation` and viewport (`TerminalDrawOperation`, `TerminalView`) live in `src/Ntilde.App/Shell/` today — see Known Tech Debt below for the planned extraction.

**Invariants** (enforced by `LayeringTests.Rendering_only_depends_on_Vt_and_Skia`)
- Rendering is a pure function of (buffer snapshot, metrics, theme).
- No semantic decisions — if the buffer is wrong, the renderer cannot fix it.
- No Avalonia in the dependency closure.

---

## 6. PTY Transport — `Ntilde.Pty`

Native OS integration via the Rust PTY library (`rusty_pty.dll`/`.so`/`.dylib`). Defines the session contracts and `RustPtySession` as the canonical implementation.

`ITerminalSession` is a composite of four narrow interfaces:

| Interface | Concern |
|---|---|
| `ITerminalIO` | `SendInput(string)`, `OnOutputReceived` event |
| `ITerminalLifecycle` | `Id`, `Resize`, process state, `OnExit`, `IDisposable` |
| `ITerminalShellMetadata` | `ShellCommand`, `ShellArguments` |
| `ITerminalRecorder` | `StartRecording(filePath)`, `StopRecording`, `IsRecording` |

**Invariants** (enforced by `LayeringTests.Pty_must_not_depend_on_Vt`)
- Pty does not reference VT. The session reports raw byte/string output; parsing it into a `TerminalBuffer` is the consumer's job.
- IO is bounded and non-blocking.
- Recording in this layer captures the raw byte stream only — buffer-snapshot recording is an orchestration-layer concern and is not currently wired up.

---

## 7. Platform / SSH — `Ntilde.Platform`

This is **not** the terminal engine. It's a platform-utilities library: input routing (`Input/`), path mapping (`Paths/WslPathMapper`), the SSH stack (`Ssh/{Exec,Native,OpenSsh,Sessions,Storage,Transport}`; `Exec/` is the non-PTY command channel the remote multiplexer and its installer run over, section 8.2), and the credential vault. It was renamed from `Ntilde.Core` to `Ntilde.Platform` (issue #76) to end the three-way "Core" name overload; the original "Core" terminal engine had earlier been renamed to `Ntilde.VT` during the namespace-alignment work.

This is also where session-orchestration helpers (such as a future `SessionBufferBinder`) belong.

---

## 8. UI Shell — `Ntilde.App`

Avalonia 12.0.4 application. Hosts windows, controls (`TerminalPane`, `MainWindow`, `SettingsWindow`), view-models, themes, and command-palette. Composes everything below.

Shell composition glue (startup orchestration, app paths/logging/services, session & workspace managers, theme manager, command registry, profiles, the Avalonia view host) lives in `src/Ntilde.App/Shell/`, namespace `Ntilde.Shell` — renamed from `App/Core/` + `Ntilde.Core` (issue #76).

### Responsibilities
- Window and pane management
- Input routing (keyboard, mouse, drag-and-drop)
- Selection handling
- Settings UI, theming, profiles
- Command palette + command-assist (in-process shell-integration helpers)
- The Agent Output panel (`AgentOutput/`): tracks the current command's output region and renders it as markdown into an Avalonia control tree. Parsing is Markdig; the control-tree rendering is this assembly's own `MarkdownRenderer`
- Pane resizing (pixel → row/col calculation)

### Non-Responsibilities
- VT parsing (delegated to VT)
- Buffer mutation (except via explicit APIs)
- Rendering logic (Skia primitives in Rendering; Avalonia binding shell here is intentionally thin — though see Known Tech Debt)

### 8.1 Persistent sessions: the mux daemon

With `TerminalSettings.SessionPersistence = "KeepOnClose"` (default `"Off"`), local panes run their
shells in a separate daemon process and survive window close, an app crash and a restart. SSH panes
whose profile sets `SshMuxOptions.PersistRemoteSessions` do the same on the remote host (section
8.2). The local daemon is **a CLI mode of the app executable** (`Ntilde mux serve`), not a separate
binary: the AOT bundle ships no `Ntilde.Cli`. `Program.cs` dispatches `mux` verbs before `AppLogger`
and long before Avalonia, so the daemon never initialises a UI. The edge is App → `Ntilde.Mux` →
`Ntilde.Mux.Contracts`; `Ntilde.Mux` still references nothing above Pty/VT/Replay.

The local and the remote daemon share one implementation. `Ntilde.Mux.Cli.MuxCli.Execute`
holds every verb, and `Ntilde.Mux.Daemon.MuxServeHost` is the `serve` process around
`MuxDaemonHost`. An executable hosts them through a `MuxCliHost` that supplies what differs:

| `MuxCliHost` | `ntilde mux` (App, `Shell/Mux/MuxCommand.cs`) | `ntilde-mux` (remote, `src/Ntilde.Mux.Daemon`) |
|---|---|---|
| `Paths` (`MuxPaths`: root, descriptor, endpoint, log folder) | the app-data root, or `NTILDE_APPDATA_ROOT` (`MuxPaths.Default`); a daemon spawned for another root is handed it through `NTILDE_APPDATA_ROOT` | its own root beneath it, `<app-data root>/ntilde-mux`, or `NTILDE_MUX_ROOT` (`MuxPaths.Standalone`); a daemon spawned for another root is handed it through `NTILDE_MUX_ROOT`. On a host that also runs the GUI, the two daemons share no descriptor, socket, lock or log |
| `ServeArguments` | `mux serve` | `serve` |
| `SessionFactory` | `DefaultTerminalSessionFactory` (the GUI's shells, unchanged) | `LocalShellSessionFactory`: an empty command is the user's login shell with `-l`; `~` is `$HOME`; SSH is refused |
| `Verbs` | serve, ls, kill, kill-server, attach, probe-console | serve, proxy, ls, kill, kill-server, attach, `--version` |
| `PrepareForegroundConsole` | `CliConsoleBindings.Prepare` | none |

```
 GUI (Ntilde.exe)                          daemon (Ntilde.exe mux serve: MuxServeHost)
 ├─ MuxConnectionHost ── one MuxClient ──► NamedPipe / UDS ─► MuxDaemonHost
 │    "local": warm at start, reconnect on demand              ├─ MuxServer (Phase 1/2)
 │    (MuxConnectionHosts: one host per endpoint;              │   └─ HeadlessTerminalSession × N
 │     the "ssh:<profileId>" hosts are section 8.2)            │        └─ RustPtySession → shell
 ├─ MuxTerminalSessionFactory                                  ├─ idle-exit + reaper timer
 │    local      → spawn/open MuxClientSession                 └─ mux/mux-endpoint.json
 │    SSH, opted → the ssh:<profileId> host (8.2)                    ▲
 │    other SSH  → DefaultTerminalSessionFactory                     │
 └─ TerminalPane ← MuxClientSession events                           │
                                                                     │
 ntilde mux attach <id> (Ntilde.exe mux attach)                      │
 └─ TextClientSession ── its own MuxClient ──────────────────────────┘
      (mode: shared, or readOnly with --read-only; renders from its own buffer, spec §6)
```

- **Discovery.** `MuxDiscovery` (Mux.Contracts) resolves `<root>/mux/mux-endpoint.json` under
  the daemon's root: for the GUI's daemon `NTILDE_APPDATA_ROOT` or the local app-data folder, for
  `ntilde-mux` its own root beneath that (section 8.2). The descriptor names the endpoint, pid and
  process name; it counts as live only when that pid is alive under that name and with the start
  token the daemon recorded (pid-reuse guard; on Linux the token is the `/proc/<pid>/stat` start
  time in clock ticks since boot, which a wall-clock step cannot move, elsewhere the UTC start time
  within 1 s; a descriptor without a token keeps the name check). The endpoint name carries a hash
  of the root, so two app-data roots (tests, portable installs) never share a daemon. The hash is a
  managed SHA-256 (`Ntilde.Mux.Contracts`' `Sha256`), byte-identical to the BCL's, so that
  `ntilde-mux` never loads OpenSSL (section 8.2).
- **Lifecycle.** `MuxDaemonHost` holds a lock file, writes the descriptor, reaps exited sessions
  that no client is attached to 60 s after exit, and exits 10 minutes after its last running
  session and last connection are gone (`--idle-exit-minutes`, 0 = never). The `shutdown` method
  (`ntilde mux kill-server`, the update path) kills every session and exits. The accept loop
  never gives up on a failing listener (capped backoff, rate-limited log); the host stops
  (`accept-failed`) only after `AcceptFailureStopAfter` (60 s) of continuous accept failure with
  zero connections, so a connected client's shells are never killed by an endpoint fault, and the
  lock file is left in place (never unlinked). Daemon death kills
  its shells: there is no watchdog, as in tmux. The daemon logs to `logs/mux.log`.
- **Launch.** `MuxDaemonLauncher` connects to a live descriptor or spawns the host's serve
  arguments fully detached (all three stdio streams redirected and closed, inheritable std handles
  cleared on Windows so a captured parent pipe never reaches the daemon, and the launcher's root
  handed down only when the spawned executable would not resolve it on its own:
  `NTILDE_APPDATA_ROOT` for the GUI's `mux serve`, `NTILDE_MUX_ROOT` for `ntilde-mux serve`
  (`MuxPaths.RootHandDown`)), then polls the descriptor. `EnsureEndpointStreamAsync` stops at the
  connected stream (the proxy's path, section 8.2); `EnsureConnectedAsync` adds the hello, inside the
  connect-or-spawn loop, so a daemon that idles out between accept and hello is retried rather than
  reported. `MuxConnectionHosts` keeps one `MuxConnectionHost` per endpoint, each with one shared
  `MuxClient`; the `local` host, after a failed connect, backs off for 30 s and panes fall back to a
  normal shell with a banner.
- **Close semantics.** A user closing a pane or tab kills its session, through the pane's own host
  (section 8.2 for a remote one). Window teardown only detaches (closes the connection), disposing
  every remote host first and the local one last; each flushes its own tracked kills. Saved sessions
  record each pane's mux session id and endpoint (`PaneNode.MuxEndpoint`), so the next launch
  reattaches; restore and dedupe key on `(endpoint, id)`. Running sessions nobody references are
  adopted as background tabs, for the `local` endpoint only.
- **Endpoint security.** The protocol has no authentication by design, and `spawn` runs arbitrary
  commands, so the endpoint is local and same-user only:
  - Windows: a named pipe created with `PipeOptions.CurrentUserOnly` (current-user ACL); the
    client also connects with `CurrentUserOnly`, so a pipe squatted by another account is rejected.
  - Unix: a socket in a directory that must be mode `0700` (created that way; an existing
    directory with any other mode makes the listener refuse to start and name the path), and the
    socket itself is `0600`. A stale socket is probe-connected before it is unlinked; a live one
    means refuse.
  - Never TCP.

#### Protocol v2

Negotiated range 1..2 (`MuxProtocol.MinSupportedVersion` / `MaxSupportedVersion`);
`MuxProtocol.SessionEventsVersion = 2` is the one feature gate every v2 behaviour checks. Every
change is additive, and the source-generated JSON context (`WhenWritingNull` / `WhenWritingDefault`)
keeps a v1 peer's wire shape exactly the v1 shape.

| Item | v2 addition |
|---|---|
| `AttachParams.Mode` | `string?`: null/absent = `"shared"`, or `"ifUnattached"`, `"readOnly"` (`MuxAttachMode` enum, `MuxAttachModes.ToWire`/`TryParse`) |
| `SessionSummary.Cwd` | the last OSC 7 directory the mux parser saw; null on a v1 daemon |
| `SessionSummary.DetachedByUser` | true while the session's last interactive detach was deliberate; serialized only when true, so a v1 peer sees exactly the v1 shape |
| `SessionSummary.InteractiveClients` | `AttachedClients` without read-only observers; serialized only when non-zero. Startup adoption reads it on v2, so a crash orphan someone peeks at with `--read-only` is still adopted; on v1 it falls back to `AttachedClients` |
| `DetachParams.UserDetached` | `bool?`: true on a deliberate detach ("Pane: Detach", the shared-close prompt's Detach, the text client's `Ctrl+\ d` chord); absent/null is an ordinary detach |
| Notification `sessionChanged` | attached-count, title or cwd changed; coalesced to at most one per session per 100 ms (§4 below); sent to v2 clients only |
| Notification `killed` | sent to every v2 subscriber except the killer, before that session's `exited` |
| Error `session_attached` | `MuxErrorCodes.SessionAttached`: an `IfUnattached` attach refused because another interactive client is already attached |

Fallback matrix (spec §2.1):

| Client ↔ server | Negotiated | Behaviour |
|---|---|---|
| v2 ↔ v2 | 2 | Everything above |
| v2 GUI ↔ v1 daemon | 1 | Spawn, attach (shared), detach and kill work as in Phase 2. `UserDetached` is never sent and the daemon has no `DetachedByUser`, so a deliberately detached shell is re-adopted as a background tab at the next launch until the daemon is replaced. The factory keeps its client-side `AttachedClients == 0` pre-check, and refuses `IfUnattached`/`ReadOnly` on the client before sending anything (`version_mismatch`), rather than let a v1 server silently share |
| v2 text client ↔ v1 daemon | 1 | Shared attach works; `--read-only` exits 2 ("too old for --read-only") |
| v1 client ↔ v2 daemon | 1 | Unchanged: the server never sends `sessionChanged` or `killed` to a client that didn't negotiate v2 |

#### Attach modes

`IfUnattached` is decided inside the attach control item, on the session's own parse thread
(`HeadlessTerminalSession.ExecuteAttach`) — the same thread every attach to that session runs on,
serially, between `Process()` calls. Two attaches racing in from different connections still
resolve to exactly one winner; there is no window between checking and subscribing. Read-only
observers (the text client's `--read-only`) never count against `IfUnattached`, so a GUI can
always reclaim its own shell on restart even while something is peeking at it; `AttachedClients`
still counts everyone. A `ReadOnly` attach changes nothing about the session: no resize, no
presentation applied, and its snapshot is taken at the session's current size. The parse thread is
also the sole writer of each connection's read-only status, reported on every attach/detach exit
path through `IMuxFrameSink.OnSubscriptionState(sessionId, subscribed, readOnly)`; the connection's
reader thread only reads that state, to drop `Input` and `resize` requests from a read-only sink
(the resize still gets an empty reply, so the client never sees an error). None of this is a
security boundary: any same-user process can open a second, interactive connection.

#### The text client (`Ntilde.Mux.TextClient`)

`ntilde mux attach <id|prefix>` (spec §6) renders a session into whatever terminal it was run
from, entirely from its own copy of the buffer — it never relays the raw byte stream:

- `TextClientModel` holds its own `TerminalBuffer` and `AnsiParser`, fed by the same
  `MuxClientSession` output stream a GUI pane would use.
- `TextClientRenderer.Render(consoleCols, consoleRows)` does a dirty-row repaint over
  `CaptureRenderSnapshot`, composing cursor moves, SGR and cell text itself; `AnsiCellWriter`
  (`Ntilde.VT.Export`, deliberately separate from `TerminalExporter.ExportToAnsi`) turns snapshot
  rows into positioned ANSI text with no I/O of its own.
- A dedicated render thread and a dedicated input thread, besides the client's own delivery
  (parse) thread — no thread-pool work on the output path.
- `IConsoleSurface` abstracts the real terminal — `WindowsConsoleSurface` and `UnixConsoleSurface`
  (tests use a fake) — and every exit path (detach, session exit, kill, disconnect, attach
  failure, a console write that throws, a signal) runs `RestoreMode()`.
- **It never relays device queries.** Because it renders from its own buffer, a DA, CPR or
  XTGETTCAP request the shell emits is consumed by the model's parser and never reaches the
  terminal the client is drawing on — the mux, not that outer terminal, answers it.
- The outer alternate screen is entered once on attach (`TextClientRenderer.EnterSequence`) and
  left once on every exit (`LeaveSequence`); an inner screen switch (main ↔ alt) repaints inside
  that one outer alternate screen rather than toggling it again.
- Getting a console on Windows: `CliConsoleBindings.PrepareInteractive` (called from `Program.cs`
  before `mux attach` runs) attaches to the parent console if there is one, else allocates a new
  one, before the text client opens `CONIN$`/`CONOUT$` itself. `Ntilde.exe` is a GUI-subsystem
  program, so a shell does not wait for it and would read the same keyboard as the attached text
  client. `ntilde.com` (section 8.3), a console-subsystem launcher next to it, closes that gap:
  PATHEXT tries `.com` before `.exe`, so `ntilde mux attach <id>` typed in PowerShell or cmd starts
  the launcher, which the shell waits for, and the launcher runs `Ntilde.exe` in the same console
  and waits for it in turn. `mux attach --help` therefore names no workaround, and nothing is
  printed before the console goes raw.

### 8.2 Remote persistence: `ntilde-mux` over SSH

An SSH pane whose profile sets `SshMuxOptions.PersistRemoteSessions`, while `SessionPersistence` is
on, runs its shell in `ntilde-mux` on the remote host. This is WezTerm's "SSH domain" model: the
daemon runs on the host, the GUI reaches it through an SSH exec channel, and a drop is a reconnect
followed by a snapshot plus the tail of the output. Neither side listens on TCP, and there is no
authentication beyond SSH's: the remote socket is current-user only, exactly as locally. The spec is
`docs/superpowers/specs/2026-10-05-ntilde-mux-phase4.md` (its §15 lists where the build departs from
it).

```
 GUI (Ntilde.exe)                                 remote host
 MuxConnectionHost "ssh:<profileId>"
 │  liveness ping, reconnect loop
 └─ RemoteMuxConnector, one attempt at a time
    ├─ ISshExecTransport ── SSH exec ───────────► sshd
    │    OpenSSH: ssh -T … -- <command>           └─ sh -c 'exec "$HOME/…/ntilde-mux" proxy --stdio'
    │    native:  rusty_ssh exec mode                ntilde-mux proxy: copies bytes between
    ├─ StdioMuxTransport: preamble, then frames      stdin/stdout and the socket, unparsed
    └─ MuxClient (hello: ClientInstanceId)           │  UDS: 0600 socket in a 0700 directory
         └─ MuxClientSession × N → TerminalPane      ▼
                                                  ntilde-mux serve (spawned on demand by a proxy)
                                                  ├─ MuxServer, MuxDaemonHost (lock, descriptor)
                                                  └─ HeadlessTerminalSession × N
                                                       └─ RustPtySession → login shell (-l)
```

- **Endpoint identity.** `PaneNode.MuxEndpoint` is `local` or `ssh:<sshProfileId>` (the Guid in `N`
  format); null, written before Phase 4, means `local`. `MuxEndpointId` (`Shell/Mux/`) parses and
  formats it. A pane whose restore is still pending keeps its endpoint, so a save writes it back
  unchanged, and a plain-SSH pane keeps a pending `ssh:` id for its whole life, so turning the
  profile's flag back on can still reattach that shell.
- **One connection host per endpoint.** `MuxConnectionHosts.GetOrCreate(endpoint)` creates hosts
  lazily. `local` is section 8.1's host. A remote host is built by `RemoteMuxHostFactory` from the
  SSH profile; the factory only constructs (the registry calls it outside its lock and keeps the
  first of two racing creations), and declines only a profile that is gone: the flag decides where
  tabs go, not whether a closed pane's shell ends, so a pane that kept its pending id after the flag
  went off still gets a host to deliver its kill. All
  panes of one profile share the host: one SSH connection, one set of prompts, one `MuxClient`. When
  a remote pane closes, the window releases every remote host no pane needs any more (a pane needs
  its endpoint while its session is there, while it keeps a session id pending there - unless it runs
  plain SSH - or while its connect runs): `MuxConnectionHosts.Release` closes the host once its kills are delivered
  (`MuxConnectionHost.WhenKillsDrained`), and a host that gave up with kills queued waits idle until
  the endpoint is used again. Asking for the endpoint meanwhile takes the host back; after it closed,
  the next ask builds a new one. The policies differ:

  | `MuxHostPolicy` | `local` | `ssh:<profileId>` |
  |---|---|---|
  | `ConnectTimeout` | 5 s | 120 s, so a prompt can be answered |
  | `FailureCooldown` | 30 s | 0, so a user's retry is never swallowed |
  | `RpcTimeout` | 3 s | 10 s |
  | Liveness ping | none | every 15 s, 10 s to answer |
  | Reconnect loop | none | 10 minutes |
- **Connecting** (`Shell/Mux/Remote/RemoteMuxConnector`). The remote command is `<path> proxy --stdio`
  when the install flow recorded a path made only of characters no shell treats specially
  (`RemoteMuxCommand.IsSafeAbsolutePath`), and otherwise
  `sh -c 'exec "$HOME/.local/share/ntilde/bin/ntilde-mux" proxy --stdio'`: one single-quoted script
  with no single quote inside, which every login shell (bash, zsh, fish, tcsh, nushell) hands to `sh`
  untouched. Neither form looks anything up on `PATH`. The transport starts off the UI thread;
  `StdioMuxTransport.ConnectAsync` discards whatever rc files and the MOTD print until the line
  `NTILDE-MUX-PROXY 1 <pid>` (bounded at 64 KiB and by the 120 s connect timeout, after which the
  captured text is the error), and `MuxClient.ConnectAsync` sends the host's `ClientInstanceId`.
  `RemoteMuxFailureClassifier` sorts a failure into `NotInstalled`, `Unsupported`, `VersionMismatch`,
  `SshFailed`, `ProxyFailed` or `NeedsUser`, and the pane's notice and action follow from the kind.
  Each attempt reads the profile until one connects; from then on the host keeps that attempt's SSH
  target, refreshing only the install metadata, so an edited profile cannot send the reconnects and
  kills of shells on one host to another (OpenSSH plans the pinned copy itself, with `-F none` and its
  block as `-o` options once the store's profile differs: `SshLaunchPlanner.PlanFor`).
- **The proxy** (`ntilde-mux proxy --stdio`, `Ntilde.Mux.Cli.MuxProxyCommand`) connects to the
  host-local daemon or spawns one (`MuxDaemonLauncher.EnsureEndpointStreamAsync`), writes the
  preamble, then copies bytes on two dedicated threads. It exits 0 when stdin ends; 3 when the
  daemon side ended and the daemon's process is gone (pid, name and start token, waited for up to
  1.5 s, since a stopping daemon closes its connections before it exits); 4 when the daemon dropped
  this connection but runs on (a client too slow, or evicted by its twin) or stdout could not be
  written; 1 when it could not reach or spawn a daemon; and 2 on a usage error. Its stdout carries
  nothing but the preamble and frames. Every `ntilde-mux` verb serves and
  looks under `ntilde-mux`'s own root (`MuxPaths.Standalone`: `~/.local/share/ntilde/ntilde-mux` on
  Linux, `~/Library/Application Support/ntilde/ntilde-mux` on macOS), never the GUI's: on a host that
  also runs the GUI, a remote client would otherwise reach the GUI's daemon (whose session factory
  refuses an empty command, so every persistent tab fell back to plain SSH), and the GUI would adopt
  the remote client's sessions as orphans and end them with its update flow's `shutdown`. The
  sun_path fallback (`$XDG_RUNTIME_DIR` or the temp directory) is named by a hash of the root, so it
  stays apart too.
- **Dead twins.** A host's `MuxClient` sends the same random `ClientInstanceId` in every hello for
  the host's life. The server closes every other connection carrying that id before it replies, so
  the half-open connection a drop leaves behind neither keeps the session "shared with 1" nor blocks
  an `IfUnattached` restore. Ids over 64 characters are ignored; a Phase 3 daemon ignores the field,
  which is why a reattach after a drop always opens `Shared`.
- **Liveness.** A dropped link is silent until TCP notices, which can take tens of minutes. Every 15 s
  a remote host pings; the link is dead when no inbound byte arrives within 10 s of the ping. Any
  byte counts, so a ping queued behind a large snapshot on a slow link does not cut a healthy
  connection. The host then disposes the client (`ping timeout`).
- **Reconnect loop** (`MuxReconnectLoop`, one timer through `IMuxTimerScheduler`). On `Disconnected`
  the host classifies the loss by the lost channel's exit code, waiting at most 2 s for it: 3 raises
  `DaemonStopped` and no retry (the daemon's sessions ended with it, so queued kills are dropped);
  anything else raises `ConnectionLost` and starts the loop. It waits 1, 2, 4, 8, 16, then 30 s, each
  jittered by ±20%, for 10 minutes from the loss, then raises `ReconnectAbandoned`. Success raises
  `Reconnected(MuxClient)`. Events are raised on the pool, one at a time, in order, never under the
  host's lock.
- **Automatic attempts never prompt.** `MuxConnectAttempt.Interactive` is true for a user's request
  (a pane opening, Enter) and false for the loop's attempts and the kill-delivery attempt below. A
  user's request cancels an automatic attempt in flight and starts an interactive one. An automatic
  OpenSSH attempt runs with `BatchMode=yes`, no `SSH_ASKPASS`, `SSH_ASKPASS_REQUIRE=never` and no
  `DISPLAY`. An automatic native attempt goes through `RemoteMuxInteractionHandler`: it accepts a
  host key only when the app's native known-hosts store already trusts it; it offers a password or
  passphrase only from the host's in-memory record of one that got an earlier attempt in (forgotten
  when an attempt that offered it fails SSH, and never kept for a password on a profile with jump
  hops, since a native prompt does not say which hop asks); and for anything else it aborts the
  attempt by closing the session, so no empty password ever reaches the server. Such an attempt
  fails `NeedsUser` (as does an automatic OpenSSH attempt refused with `Permission denied`), which
  stops the loop at once with `ReconnectAbandoned`: retrying would only feed fail2ban. A key's
  passphrase with nothing remembered is cancelled instead (it sends the server nothing, and the agent
  or another key may still get in), but recorded: if the attempt then fails SSH, it is `NeedsUser`
  too.
- **Kills while down** (`MuxConnectionHost.KillWhenConnected`). Every close of a remote pane goes
  through it, and so does the kill of a shell a stale result started (a result that came back to a pane
  closed or restarted meanwhile; the pane then asks the window for its release pass). On a live client the kill is sent at once; otherwise it is queued, kept across
  `ReconnectAbandoned`, and sent before anything else on the next successful connect of any kind. An
  idle host (no client, no attempt, no loop) starts one automatic attempt to deliver it. Queued kills
  are dropped, with a log line, on `DaemonStopped` and on dispose.
- **The pane** (`TerminalPane`) calls `MuxTerminalSessionFactory.CreatePersistent` off the UI thread
  (it may wait 120 s behind prompts that the UI thread shows), with a generation counter that
  discards a stale result, and follows the host's four events: on `ConnectionLost` it keeps its last
  screen, drops typed input with one hint per episode, and on `Reconnected` reopens its session
  `Shared` (`TerminalSessionRequest.ReattachAfterDrop`), whose snapshot replaces the screen.
- **Factory outcomes.** A new tab spawns with an empty command (the daemon's login shell), the
  profile's working directory and name, and no environment overrides: the GUI's bootstrap
  environment never leaves the machine, OSC 133 comes from the remote shell's own integration, and
  the daemon sets `NTILDE_MUX_SESSION`. A restore opens `IfUnattached` with the local rules but no
  command check (the GUI does not know the remote login shell). A failure while connecting
  (`SshFailed`, `NeedsUser`, a timeout) gives `DaemonUnreachable`, a retry banner and no plain-SSH
  stand-in; `NotInstalled`, `Unsupported`, `VersionMismatch` and `ProxyFailed` on a new tab give the
  plain SSH session with a notice, which offers the install flow for `NotInstalled` and
  `VersionMismatch`.
- **Not on remote panes, this phase.** A persisted remote pane is not an `ActiveSshSessionRegistry`
  session, so the SFTP sidebar, remote files and the palette's SFTP transfers are disabled for it, and the profile's port forwards
  are not set up (`ClearAllForwardings=yes`; the native exec mode has no forward router).

#### Exec transports (`Ntilde.Platform/Ssh/Exec/`)

`ISshExecTransport.Start(command)` returns an `ISshExecChannel`: `Stdout`, `Stdin` (its dispose
sends EOF), `StderrTail` (the last 8 KiB) and `Completion` (the exit code, or null when the channel
was killed or the transport failed). `SshExec.RunAsync` runs one command with stdin bytes and a
deadline; the installer uses it.

- **OpenSSH** (`OpenSshExecTransport`): `ssh` from `SshLaunchPlanner.PlanFor(profile)`
  (`-F <generated config> <alias>`, or `-F none <alias>` with the profile's block as `-o` options for
  a pinned copy the store no longer holds as it is), plus `-o ControlMaster=no` (the hidden exec never becomes a
  master a visible tab then rides on), `-T -o ClearAllForwardings=yes`, `-o BatchMode=no|yes` and
  `-- <command>`. The profile's extra arguments are read with ssh's own getopt rules
  (`OpenSshExecCommandLine.SshOptionLetters`: clusters, an option's argument never scanned, options on
  both sides of the destination), and what would break the channel is dropped and logged: a PTY
  (`-t`, `-T`, `-o RequestTTY`), which would corrupt the binary stream; and whatever keeps the proxy from
  running on it (`-N`, `-f`, `-n`, `-s`, `-G`, `-V`, `-W`, `-O`, `-Q`, `-o SessionType`,
  `-o ForkAfterAuthentication`, `-o StdinNull`, `-o RemoteCommand`, `-o PermitLocalCommand`), or would
  make the hidden ssh a master (`-M`). An interactive attempt gets the app as `SSH_ASKPASS`
  (`SSH_ASKPASS_REQUIRE=force`, `DISPLAY=ntilde`, `NTILDE_SSH_ASKPASS_PROFILE_*`); the helper fills in
  the vault password only for a prompt that names the target's `user@host`, never a jump host's.
- **Native** (`NativeSshExecTransport`): `nova_ssh_exec(args, command)` runs rusty_ssh's exec mode,
  which takes the same hop, auth and prompt path as a shell session but opens no PTY and detects no
  shell: `channel_open_session`, then `exec`. Stdout arrives as `Data` events, stderr as
  `ExtendedData` (kind 14), the exit status (kind 7) before `Closed`, and `nova_ssh_send_eof` ends
  stdin. One dedicated poll thread per channel routes the events: stdout into a bounded byte queue
  read without the thread pool, stderr into the tail, prompts to the handler. A native connection
  lives exactly as long as its channel, so an exec never shares a pane's connection.

#### Install flow (`Shell/Mux/Remote/RemoteMuxInstaller.cs`, `Views/Ssh/RemoteMuxInstallDialog.cs`)

Each step is an exec over the same transports, with the same prompts as a connection:

1. **Probe**: `uname -sm`, the libc line and `$HOME`. `RemoteHostProbe.Parse` (pure) maps the host to
   linux-x64, linux-arm64 or osx-arm64, and refuses musl, glibc older than 2.35, Intel Macs and
   anything else, with the reason.
2. **Asset** (`IMuxDaemonAssetSource`): the GitHub release's `ntilde-mux-<rid>`, verified against its
   `.sha256` and cached at `<app data>/cache/ntilde-mux/<version>/<rid>`; or a local file, whose ELF
   or Mach-O header must name the probed RID. `MuxDaemonRid` reads the header.
3. **Upload** (`RemoteMuxInstallCommands.Upload`): one `sh -c` script with the binary on stdin. It
   writes a temp file beside the target, checks the byte count, `chmod 755`s it, runs it once, then
   `mv -f`s it over `ntilde-mux` (a rename, so a running daemon keeps its inode), and execs the
   installed binary's `--version --json`, which is the verification. A short or failed upload never
   replaces a working binary.
4. **Record**: `SshConnectionService.RecordRemoteMuxInstall` writes only `RemoteDaemonPath`,
   `RemoteDaemonVersion`, `RemoteDaemonRid` and, when the user ticks it, `PersistRemoteSessions`, on
   the store's own copy of the profile. Compatibility is decided by the handshake;
   `RemoteDaemonVersion` only drives the editor's status line and the notice's wording.

#### The two AOT binaries

| Binary | Project | Built and shipped |
|---|---|---|
| `ntilde-mux` | `src/Ntilde.Mux.Daemon` | NativeAOT, one file per RID: linux-x64, linux-arm64, osx-arm64 (about 6 MB). `librusty_pty.a` is linked statically (`DirectPInvoke` + `NativeLibrary`; `native/Cargo.toml` builds `cdylib` and `staticlib`), so the only dynamic dependencies are libc, libm, libgcc_s and the loader; the highest `GLIBC_` symbol is 2.34. CI's `mux_daemon_aot` builds the three RIDs (ubuntu:22.04 containers, macos-latest), asserts one file and the glibc ceiling, and smokes `--version --json`, `serve`, `ls` and `kill-server`. The release's `publish_mux_daemon` uploads `ntilde-mux-<rid>` and `ntilde-mux-<rid>.sha256`. Not bundled with the app: the install flow downloads it. |
| `ntilde.com` | `src/Ntilde.Launcher` | NativeAOT win-x64 (about 0.9 MB), no project or package references, kernel32 P/Invokes only. The release copies it into the win-x64 bundle beside `Ntilde.exe` before the zip and `vpk pack`; CI's `aot_gate` and the release smoke it with a `cmd.exe` stand-in (`/d /c exit 7` must exit 7). Section 8.3. |

`ntilde-mux` must run on libc alone. On Linux, .NET loads OpenSSL at run time for
System.Security.Cryptography (hashes included), System.Net.Security and System.Net.Http; `ldd` does
not show it, and a remote host may have no libssl (`serve` aborted on debian:12-slim until the
endpoint hash moved to a managed SHA-256). `LayeringTests.Nothing_ntilde_mux_runs_references_an_OpenSSL_backed_assembly`
walks everything it runs.

### 8.3 `ntilde.com`: the Windows console launcher

`Ntilde.exe` is a GUI-subsystem program, so a console shell neither waits for it nor stops reading
the keyboard. `ntilde.com` is a console-subsystem program next to it (the `devenv.com` pattern), and
PATHEXT makes `ntilde` resolve to it first. `Ntilde.Launcher`'s `Program`:

1. finds `Ntilde.exe` in its own directory (exit 9009 when it is missing);
2. builds the child's command line from `"<path>\Ntilde.exe"` and the raw tail of
   `GetCommandLineW()` after its own argv[0] (`LauncherCommandLine.Tail`, the CRT's argv[0] rule), so
   the arguments pass byte-identical, never re-quoted;
3. calls `CreateProcessW` with inherited handles, its own `STARTUPINFO` (so redirected stdio flows
   through), no new console and no new process group, and `NTILDE_LAUNCHER_RELEASE=<event name>` in
   the environment;
4. waits on the child or that event, and returns the child's exit code (0 for the event).

- **Ctrl+C and Ctrl+Break.** The child shares the console and the process group, so Windows
  delivers both to it directly; the launcher's handler only returns TRUE so the launcher keeps
  waiting. `CTRL_CLOSE_EVENT` waits up to 4 s for the child. `mux attach` turns off processed input,
  so Ctrl+C reaches the attached shell as `0x03`.
- **GUI launches return at once.** `Program.Main` calls `LauncherRelease.Signal()` on the GUI path,
  after every CLI-mode check: it sets the event and clears the variable, and the launcher exits 0.
  The mux branch calls `LauncherRelease.Discard()` first, so a daemon that `mux attach` starts never
  passes the variable to its shells, where a GUI started from one would release the attach's
  launcher. `CliCommandDispatchTests.The_launcher_is_released_only_on_the_GUI_path` pins this order
  in `Main`'s IL.
- **PATH.** The installer put nothing on `PATH` before. Velopack's `OnAfterInstall` and
  `OnAfterUpdate` fast callbacks call `UserPathRegistration.Ensure(<install dir>)`, and
  `OnBeforeUninstall` calls `Remove`, where the install dir is the directory of
  `Environment.ProcessPath` (Velopack's stable `current` folder). `Merge` is pure: case-insensitive,
  tolerant of trailing separators, and it keeps order and `%VAR%` spellings. The write is HKCU
  `Environment\Path` as `REG_EXPAND_SZ`, only when the value changed, followed by a
  `WM_SETTINGCHANGE` broadcast. `CliCommandDispatchTests.The_PATH_is_registered_only_from_the_Velopack_hooks`
  pins that a normal start never touches `PATH`.

---

## 9. CLI Shim — `Ntilde.Cli`

Headless tooling entry point used for `vt-report` and other automation use cases. Currently references `Ntilde.App`; a `Ntilde.Bootstrap` library should mediate so neither side reaches into the other (see Known Tech Debt).

---

## 10. Deterministic Replay (Architectural Feature)

Replay is a **core architectural feature**, not a debug tool.

```
PTY byte stream
   ↓
[Recorder]                  -- ReplayWriter (Ntilde.Replay)
   ↓
Replay file
   ↓
AnsiParser                  -- Ntilde.VT
   ↓
TerminalBuffer              -- Ntilde.VT
   ↓
Buffer snapshot             -- compared in CI
```

Snapshots capture visible screen content, attributes, cursor state, alt/main flag, and minimal scrollback.

---

## 11. Cross-Platform Parity

Ntilde enforces **behavioral parity** across OSes:

| Aspect | Must match across OSes |
|---|---|
| VT parsing | Yes |
| Buffer state | Yes |
| Wrapping & reflow | Yes |
| Search semantics | Yes |
| Rendering semantics | Yes |

Allowed differences: window chrome, hotkeys, blur/transparency, credential storage backend.

---

## 12. Architecture-Tests Safety Net

`tests/Ntilde.Architecture.Tests/` uses [NetArchTest.Rules](https://github.com/BenMorris/NetArchTest) to encode layering and namespace rules as xUnit facts. Today's enforced rules:

**`LayeringTests`**
- `Vt_must_be_a_leaf_assembly`
- `Replay_only_depends_on_Vt`
- `Rendering_only_depends_on_Vt_and_Skia`
- `Pty_must_not_depend_on_Vt`
- `CommandAssist_must_not_depend_on_Avalonia_or_the_App`
- `MuxContracts_must_be_a_leaf_assembly`
- `Mux_must_not_depend_on_ui_platform_or_app`
- `Mux_daemon_and_cli_namespaces_have_no_app_or_platform_dependency`
- `Mux_references_only_approved_ntilde_assemblies`
- `MuxDaemon_references_only_Mux` (IL: `Ntilde.Mux`, plus `Ntilde.Pty` because `MuxCliHost.SessionFactory` is a Pty type)
- `Nothing_ntilde_mux_runs_references_an_OpenSSL_backed_assembly`
- `Launcher_references_no_Ntilde_assembly`
- `No_production_assembly_references_test_assemblies`

**`NamespaceAlignmentTests`**
- `Leaf_assembly_types_reside_in_its_own_namespace` (Theory: VT, Replay, Rendering, Pty, Platform, AgentHost.Contracts, CommandAssist, Mux.Contracts, Mux, Launcher)
- `No_two_assemblies_share_a_namespace_prefix`
- `App_may_only_use_the_CommandAssist_prefix_for_Views`
- `Mux_does_not_use_the_MuxContracts_namespace`
- `MuxDaemon_types_reside_in_the_MuxDaemon_namespace`, `No_other_assembly_uses_the_MuxDaemon_namespace`
- `Launcher_types_reside_in_the_Launcher_namespace`

**`ProjectFileLayeringTests`**
- `Pty_csproj_must_not_reference_Vt`
- `Replay_csproj_only_references_Vt`
- `Rendering_csproj_only_references_Vt`
- `Vt_csproj_must_have_no_project_references`
- `CommandAssist_csproj_must_have_no_project_or_avalonia_references`
- `MuxContracts_csproj_must_have_no_project_references`
- `Mux_only_references_Pty_Vt_Replay_and_MuxContracts`
- `MuxDaemon_only_references_Mux` (and no package references)
- `Launcher_has_no_references`

**`CliCommandDispatchTests`** (entry points, read from `Program.Main`'s IL)
- `Mux_daemon_dispatches_every_verb_through_MuxCli`
- `The_launcher_is_released_only_on_the_GUI_path`
- `The_PATH_is_registered_only_from_the_Velopack_hooks`

Adding a new layering invariant means adding a new fact. Reverting one of these accidentally fails CI.

---

## 13. Test Layout

| Project | What it tests |
|---|---|
| `Ntilde.VT.Tests` | Fast unit suite for parser/buffer in isolation (no Avalonia, no Skia) |
| `Ntilde.Rendering.Tests` | Skia primitives that don't need a GPU context |
| `Ntilde.Platform.Tests` | Platform utilities + SSH, including the exec transports (`Ssh/Exec/`); includes Docker-gated E2E (skipped without Docker) |
| `Ntilde.App.Tests` | App-level integration — Avalonia-headless tests, replay regressions, golden PNG comparisons, command-assist. Remote persistence (`Shell/Mux/Remote/`) runs against an in-memory "remote" daemon behind a fake exec transport (`FakeRemoteHost`) and a fake timer scheduler; `RemoteMuxDockerE2eTests` (`Category=DockerE2E`, needs `NTILDE_ENABLE_DOCKER_E2E=1` and `NTILDE_MUX_E2E_BINARY`) drops a real link to a real sshd over both backends. The launcher's end-to-end tests are Windows-gated |
| `Ntilde.Architecture.Tests` | Layering and namespace rules (Section 12) |
| `Ntilde.Mux.Tests` | Multiplexer contracts, transports (including `StdioMuxTransport`), headless sessions, server/client, attach modes, `sessionChanged`/`killed` protocol v2 events, the text client (`TextClient/`), daemon host, the daemon process and verbs (`Daemon/`, `Cli/`: proxy, `--version`), and scenario suites (scripted sessions; the real-daemon PtySmoke test lives in App.Tests) |
| `Ntilde.Benchmarks` | BenchmarkDotNet perf benchmarks (Exe, not auto-discovered by `dotnet test`) |
| `Ntilde.ExternalSuites` | Vttest and Native-SSH external scenario drivers (Exe) |

App-side tests use xunit.v3 + `Avalonia.Headless.XUnit 12.0.4` (which carries the dispatcher-cleanup fix; **do not downgrade** below 12.0.4 — earlier versions leak headless dispatcher threads and hang `dotnet test` under captured-stdout runners).

---

## 14. Known Tech Debt

These are tracked in follow-up plans under `docs/plans/`:

- **Renderer composition still lives in App.** `src/Ntilde.App/Shell/TerminalView.cs` (2,604 LOC) and `TerminalDrawOperation.cs` (2,935 LOC) implement the Skia-backed Avalonia renderer. They belong in `Ntilde.Rendering` behind a thin Avalonia binding shell. Planned extraction: `2026-MM-DD-renderer-composition-extraction-plan.md`.
- **SSH is fragmented across Platform and App.** `Platform/Ssh/` holds the transport, while `App/Services/Ssh/`, `App/ViewModels/Ssh/`, `App/Views/Ssh/`, and `App/Shell/{SftpService,VaultService,SshAskPassCommand}.cs` hold the user-facing surface. A `Ntilde.Ssh` (or `.Remote`) assembly would consolidate the non-UI portion.
- **CommandAssist Phase 0 has one item left.** Tasks 1–2 and 7 of `docs/plans/2026-08-01-command-assist-v2-plan.md` Phase 0 (the `Ntilde.CommandAssist` assembly extraction and its Avalonia-free key/geometry abstractions, #114) landed first; tasks 3, 5 and 6 followed (`CommandAssistServices` composed at the App root and injected into `TerminalPane`, a single ranking path with the history store reduced to a recall gate, and the append-only JSONL history store with in-memory index and compaction). What remains is task 4: `CommandAssistController` is ~940 LOC doing session state, capture, and suggestion orchestration at once, and splits into `AssistSessionStateMachine` / `CapturePipeline` / `SuggestionOrchestrator`. (`JsonSnippetStore` deliberately stays whole-file JSON — it writes only on pin/unpin/delete.)
- **CLI ↔ App reference direction.** `Cli` currently references `App` and is built via a nested MSBuild target (`BuildCliShim` in `App.csproj`). A `Ntilde.Bootstrap` library should mediate so `Cli → Bootstrap ← App` replaces `Cli → App`.
- **Byte vs string at the PTY boundary.** `ITerminalIO.SendInput(string)` and `OnOutputReceived(Action<string>)` lose information at the byte-vs-codepoint boundary (UTF-8 split across reads, embedded NULs, lone surrogates). Migrating to `ReadOnlySpan<byte>` / `Action<ReadOnlyMemory<byte>>` is the planned follow-up to Phase 5.
- **Buffer-snapshot recording.** Phase 5 removed `ITerminalSession.AttachBuffer` / `TakeSnapshot`. The byte-stream is still recorded; buffer snapshots at recording start/stop are gone. Re-introducing them as an orchestration helper (likely in `Replay` or `Platform`) is a small follow-up.
- **`MainWindow.axaml.cs` is 8,755 LOC, `TerminalPane.axaml.cs` 5,029 LOC, `SettingsWindow.axaml.cs` 3,312 LOC** (measured 2026-09-03; each has grown 60-95% since this item was written, so the trend is the finding as much as the number). These code-behinds contain business logic that should live in services and view-models.
- **`TerminalBuffer` is split across 10 partial files (~5K LOC).** Several of the partials (`WritePath`, `ReflowEngine`, `ThreadingAndInvalidation`, `TabStops`) want to be collaborators rather than partials.
- **Mux snapshot capture holds the buffer read lock (~27 ms at 10k rows);** the mux parser has no image decoder, so Phase 2 turns inline images off in mux-backed panes (no image decoder, no native kitty graphics) rather than show images a reattach would lose.

The 2026-05-28 architecture review (`docs/plans/2026-05-28-architecture-module-boundaries-review.md`) catalogs all of the above and ranks them by leverage.

---

## 15. Failure Modes Designed Against

- "Looks fine on my machine"
- Resize-induced corruption
- Alternate-screen leakage
- Renderer-driven semantic drift
- Platform-specific behavior divergence
- Silent assembly-boundary refactors (caught by Section 12 arch tests)

---

## 16. Why This Architecture

This architecture is intentionally **boring**. That is a feature.

- Ghostty and WezTerm succeed because they are predictable.
- Users forgive missing features. They do not forgive broken terminals.
- Ntilde optimizes for **trust first**, features second.

---

## 17. Summary

Ntilde is:
- deterministic by design
- cross-platform by construction
- test-gated by policy (arch tests + replay regression suite)
- incremental by default

> UI attracts users. Correctness keeps them.

Read this before making architectural changes. Add an arch test before reaching for documentation as the enforcement mechanism — docs rot, tests don't.
