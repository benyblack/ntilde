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

Ntilde is structured as fifteen focused .NET assemblies. The dependency graph is acyclic. Each assembly's namespace matches its assembly name (enforced by `tests/Ntilde.Architecture.Tests/NamespaceAlignmentTests`).

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
```

Five of those fifteen are **leaves with no project references at all**:
`CommandAssist`, `Backup`, `VtContract`, `AgentHost.Contracts`, `Mux.Contracts`. The empty
reference list is the point — it is what lets `McpServer` share real code with
the app without acquiring a transitive path into `App`, `VT`, `Pty` or
`Rendering`. An earlier attempt routed the MCP backup tools through
`Ntilde.Platform`, which has no App or VT dependency but does reference
`Pty`, and that broke `McpServer`'s Pty-independence invariant with the
reasoning fully intact. Each empty list is asserted individually by the
architecture tests.

Concretely, from the `.csproj` graph:

| Assembly | Depends on | Owns |
|---|---|---|
| `Ntilde.VT` | (leaf) | VT/ANSI parser, terminal buffer state, scrollback, reflow, transferable terminal/parser state (TerminalStateSnapshot) |
| `Ntilde.Replay` | VT | Session recording/playback, snapshots, replay format v2 |
| `Ntilde.Rendering` | VT, SkiaSharp | Skia glyph atlas/cache, pixel grid, sixel decoder |
| `Ntilde.Pty` | Replay | PTY transport: rust-PTY adapter, session contracts, session factory contract, UTF-8 chunk decoding. **Does not depend on VT** — see arch test `Pty_must_not_depend_on_Vt` |
| `Ntilde.Platform` | Pty | Platform-utilities: input routing, path mapping, SSH transport/sessions, credential vault |
| `Ntilde.CommandAssist` | (leaf) | Command Assist domain, models, storage, shell integration, view-models and application core. **No Avalonia** — see arch test `CommandAssist_must_not_depend_on_Avalonia_or_the_App` |
| `Ntilde.Backup` | (leaf) | `.ntildebackup` bundle format, export/import/restore, the category-to-path catalogue, the debounced snapshot scheduler. Never reads secret storage |
| `Ntilde.VtContract` | (leaf) | The machine-readable VT capability catalogue (`vt-capabilities.json`) and its strict schema validation |
| `Ntilde.AgentHost.Contracts` | (leaf) | Wire protocol between the app's agent host and any external client: frames, discovery, source-generated JSON context |
| `Ntilde.Mux.Contracts` | (leaf) | Multiplexer wire protocol: framing, frame kinds, binary payload codecs, source-generated JSON DTOs, error codes, version negotiation |
| `Ntilde.Mux` | Pty, VT, Replay, Mux.Contracts | Multiplexer core: headless authoritative sessions (one parse thread each), server, client + `MuxClientSession : ITerminalSession`, in-memory and local (named pipe / Unix socket) transports, the daemon host (`MuxDaemonHost`). **Must not reference Platform, App, Avalonia or SkiaSharp** |
| `Ntilde.App` | Platform, VT, Rendering, Pty, Replay, CommandAssist, Backup, AgentHost.Contracts, Mux, Mux.Contracts | Avalonia UI shell: windows, controls, command palette, settings, themes, command-assist views, Agent Output panel. Also the `mux serve` daemon mode and the GUI's mux connection (`Shell/Mux/`, section 8.1) |
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

This is **not** the terminal engine. It's a platform-utilities library: input routing (`Input/`), path mapping (`Paths/WslPathMapper`), the SSH stack (`Ssh/{Native,OpenSsh,Sessions,Storage,Transport}`), and the credential vault. It was renamed from `Ntilde.Core` to `Ntilde.Platform` (issue #76) to end the three-way "Core" name overload; the original "Core" terminal engine had earlier been renamed to `Ntilde.VT` during the namespace-alignment work.

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
shells in a separate daemon process and survive window close, an app crash and a restart. The
daemon is **a CLI mode of the app executable** (`Ntilde mux serve`), not a separate binary: the
AOT bundle ships no `Ntilde.Cli`. `Program.cs` dispatches `mux` verbs before `AppLogger` and long
before Avalonia, so the daemon never initialises a UI. The edge is App → `Ntilde.Mux` →
`Ntilde.Mux.Contracts`; `Ntilde.Mux` still references nothing above Pty/VT/Replay.

```
 GUI (Ntilde.exe)                          daemon (Ntilde.exe mux serve)
 ├─ MuxConnectionHost ── one MuxClient ──► NamedPipe / UDS ─► MuxDaemonHost
 │    (warm at start, reconnect on demand)                     ├─ MuxServer (Phase 1)
 ├─ MuxTerminalSessionFactory                                  │   └─ HeadlessTerminalSession × N
 │    local → spawn/open MuxClientSession                      │        └─ RustPtySession → shell
 │    SSH   → DefaultTerminalSessionFactory                    ├─ idle-exit + reaper timer
 └─ TerminalPane ← MuxClientSession events                     └─ mux/mux-endpoint.json
```

- **Discovery.** `MuxDiscovery` (Mux.Contracts) resolves `<root>/mux/mux-endpoint.json` under
  `NTILDE_APPDATA_ROOT` or the local app-data folder. The descriptor names the endpoint, pid and
  process name; it counts as live only when that pid is alive under that name (pid-reuse guard).
  The endpoint name carries a hash of the root, so two app-data roots (tests, portable installs)
  never share a daemon.
- **Lifecycle.** `MuxDaemonHost` holds a lock file, writes the descriptor, reaps exited sessions
  that no client is attached to 60 s after exit, and exits 10 minutes after its last running
  session and last connection are gone (`--idle-exit-minutes`, 0 = never). The `shutdown` method
  (`ntilde mux kill-server`, the update path) kills every session and exits. Daemon death kills
  its shells: there is no watchdog, as in tmux. The daemon logs to `logs/mux.log`.
- **Launch.** `MuxDaemonLauncher` connects to a live descriptor or spawns `mux serve` fully
  detached (all three stdio streams redirected and closed, inheritable std handles cleared on
  Windows so a captured parent pipe never reaches the daemon), then polls the descriptor.
  `MuxConnectionHost` keeps the GUI's single shared `MuxClient`; after a failed connect it backs
  off for 30 s and panes fall back to a normal shell with a banner.
- **Close semantics.** A user closing a pane or tab kills its session. Window teardown only
  detaches (closes the connection). Saved sessions record each pane's mux session id, so the next
  launch reattaches; running sessions nobody references are adopted as background tabs.
- **Endpoint security.** The protocol has no authentication by design, and `spawn` runs arbitrary
  commands, so the endpoint is local and same-user only:
  - Windows: a named pipe created with `PipeOptions.CurrentUserOnly` (current-user ACL); the
    client also connects with `CurrentUserOnly`, so a pipe squatted by another account is rejected.
  - Unix: a socket in a directory that must be mode `0700` (created that way; an existing
    directory with any other mode makes the listener refuse to start and name the path), and the
    socket itself is `0600`. A stale socket is probe-connected before it is unlinked; a live one
    means refuse.
  - Never TCP.

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
- `Mux_references_only_approved_ntilde_assemblies`
- `No_production_assembly_references_test_assemblies`

**`NamespaceAlignmentTests`**
- `Leaf_assembly_types_reside_in_its_own_namespace` (Theory: VT, Replay, Rendering, Pty, Platform, AgentHost.Contracts, CommandAssist, Mux.Contracts, Mux)
- `No_two_assemblies_share_a_namespace_prefix`
- `App_may_only_use_the_CommandAssist_prefix_for_Views`
- `Mux_does_not_use_the_MuxContracts_namespace`

**`ProjectFileLayeringTests`**
- `Pty_csproj_must_not_reference_Vt`
- `Replay_csproj_only_references_Vt`
- `Rendering_csproj_only_references_Vt`
- `Vt_csproj_must_have_no_project_references`
- `CommandAssist_csproj_must_have_no_project_or_avalonia_references`
- `MuxContracts_csproj_must_have_no_project_references`
- `Mux_only_references_Pty_Vt_Replay_and_MuxContracts`

Adding a new layering invariant means adding a new fact. Reverting one of these accidentally fails CI.

---

## 13. Test Layout

| Project | What it tests |
|---|---|
| `Ntilde.VT.Tests` | Fast unit suite for parser/buffer in isolation (no Avalonia, no Skia) |
| `Ntilde.Rendering.Tests` | Skia primitives that don't need a GPU context |
| `Ntilde.Platform.Tests` | Platform utilities + SSH; includes Docker-gated E2E (skipped without Docker) |
| `Ntilde.App.Tests` | App-level integration — Avalonia-headless tests, replay regressions, golden PNG comparisons, command-assist |
| `Ntilde.Architecture.Tests` | Layering and namespace rules (Section 12) |
| `Ntilde.Mux.Tests` | Multiplexer contracts, transports, headless sessions, server/client, daemon host and scenario suites (scripted sessions; the real-daemon PtySmoke test lives in App.Tests) |
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
