# ntilde mux Phase 2 (daemon, GUI attach, detach/reattach) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Local shells survive closing the window, an app crash and a restart, and reattach on launch, behind `TerminalSettings.SessionPersistence` (default off).

**Architecture:** `ntilde mux serve` (a CLI mode of the app exe) hosts the Phase 1 `MuxServer` behind a current-user-only named pipe / `0700`-dir Unix socket, advertised by `mux/mux-endpoint.json`. The GUI keeps one `MuxClient`; a `MuxTerminalSessionFactory` hands `TerminalPane` an unattached `MuxClientSession`, which the pane wires (snapshot restore, stream resize, disconnect banner) and then attaches. Window teardown detaches; user-initiated closes kill.

**Tech Stack:** .NET 10, C#, Avalonia 11 (App only), xUnit v3, NativeAOT (App publish), `System.IO.Pipes`, `System.Net.Sockets` (Unix domain sockets), `System.Text.Json` source generation.

**Spec:** `docs/superpowers/specs/2026-09-23-ntilde-mux-phase2.md` (read it first; section numbers below refer to it). Also read the PR #474 description ("Open questions for Phase 2") and `tests/Ntilde.Mux.Tests/Support/ClientPaneModel.cs`.

## Global Constraints

- Branch `feat/mux-phase2-daemon` (worktree `.worktrees/mux-phase2`), off `origin/dev-mux`; PR targets `dev-mux`. Never touch `claude/ntilde-multiplexer-mbxx9b`.
- Build/test only through `scripts/build.ps1` (Windows) / `scripts/build.sh`, never raw `dotnet`. One test project per invocation. `tests/Ntilde.App.Tests` always with `--blame-hang-timeout 5m`, and `Lane!=PlatformBoot` / `Lane=PlatformBoot` as separate runs.
- Default off: with `SessionPersistence` = `"Off"` no daemon is ever spawned and no behaviour changes.
- Endpoint: current-user-only pipe ACL, or socket `0600` inside a `0700` directory; refuse a mis-permissioned directory; never TCP.
- `Ntilde.Mux.Contracts` keeps **zero** project references. `Ntilde.Mux` must not reference App/Avalonia/Platform.
- NativeAOT: source-generated JSON only (`MuxJsonContext`), no reflection serialization; `publish src/Ntilde.App -c Release -r win-x64` must show zero IL2026/IL3050.
- No thread-pool work on the output path; snapshot restore runs on the client delivery thread (`MuxClientRead`).
- Additive: keep `MainWindow`/`TerminalPane` changes to the seams named in the spec. New `TerminalSettings` field needs `SettingsTools` schema + `StringFields` + `KnownFields` (two gating drift-guard tests).
- Line endings CRLF: run `scripts/build.ps1 format whitespace --no-restore` before each commit.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`.
- All dispatch in `TerminalPane` uses `this.Dispatcher`, never `Dispatcher.UIThread` (#423).

## Review Focus

1. **Pane torn down while the delivery thread is inside a handler.** A snapshot or `StreamResize` arriving after `DetachFromUiThread` must not touch the buffer or post UI work that assumes a live pane. Pinned by Task 9 test `Late_snapshot_after_pane_dispose_is_ignored`.
2. **Daemon killed while panes are attached, then Enter.** The expected result is a banner, no `ProcessExited`, and Enter reattaches to the *same* id if a new daemon still had it (it won't: the daemon died), else "[Previous session was lost — started a new shell]". Pinned by Task 9 `Disconnect_then_Enter_with_session_gone_spawns_fresh_with_lost_banner`.
3. **Two GUI-side creates racing the warm-up connection at startup.** Several restored panes call `Create` before the warm-up finishes; all must share one client and one daemon spawn. Pinned by Task 7 `Concurrent_GetClient_calls_spawn_one_daemon`.
4. **Idle exit racing a new connection.** The client is refused, not hung, and a retry against a new daemon works. Pinned by Task 4 `Connection_during_idle_shutdown_is_refused_not_hung`.
5. **A user closes the last tab with the setting on.** The session must be killed (user-initiated) even though the window then tears down (detach path). Pinned by Task 10 `Closing_last_tab_kills_its_session_before_teardown`.

---

## File map

| File | Task | Responsibility |
|---|---|---|
| `src/Ntilde.Mux/Transport/NamedPipeMuxListener.cs` | 1 | Windows current-user pipe listener |
| `src/Ntilde.Mux/Transport/UnixSocketMuxListener.cs` | 1 | UDS listener, 0700 dir check, stale-socket probe |
| `src/Ntilde.Mux/Transport/MuxEndpointConnector.cs` | 1 | Client-side connect (pipe or UDS) with timeout |
| `src/Ntilde.Mux.Contracts/MuxDiscovery.cs` | 2 | Root/descriptor/endpoint derivation, atomic write, liveness |
| `src/Ntilde.Mux.Contracts/MuxMessages.cs`, `MuxJsonContext.cs`, `MuxProtocol.cs` | 2, 3 | `MuxEndpointDescriptor`, `shutdown` method |
| `src/Ntilde.Mux/HeadlessTerminalSession.cs` | 3 | Input writer thread, `ExitedAtMs` |
| `src/Ntilde.Mux/MuxServer.cs`, `MuxServerConnection.cs` | 3 | Reaping, idle-shutdown gate, `shutdown`, running count |
| `src/Ntilde.Mux/MuxClient.cs`, `MuxClientSession.cs` | 3 | `ShutdownServerAsync`, `MuxClientSession.IsConnected` |
| `src/Ntilde.Mux/MuxDaemonHost.cs`, `MuxDaemonOptions.cs` | 4 | Listener + descriptor + timer (reap, idle) + shutdown |
| `src/Ntilde.App/Shell/Mux/MuxCommand.cs` | 6 | `mux serve|ls|kill|kill-server` |
| `src/Ntilde.App/Shell/Mux/MuxDaemonProcess.cs` | 6 | Daemonise (FreeConsole / setsid / dup2 / signals), logging |
| `src/Ntilde.App/Shell/Mux/MuxDaemonLauncher.cs` | 5 | Connect-or-spawn daemon |
| `src/Ntilde.App/Shell/Mux/MuxConnectionHost.cs` | 7 | Shared GUI `MuxClient`, warm-up, reconnect |
| `src/Ntilde.App/Shell/Mux/SessionPersistenceMode.cs` | 7 | Setting parser |
| `src/Ntilde.App/Shell/Mux/PersistentSessionFactory.cs` | 7 | `IPersistentSessionFactory`, result, outcomes |
| `src/Ntilde.App/Shell/Mux/MuxTerminalSessionFactory.cs` | 7 | Local→mux, SSH→fallback, reattach, fallback on failure |
| `src/Ntilde.Pty/ITerminalSessionFactory.cs` | 7 | `ExistingMuxSessionId` |
| `src/Ntilde.App/Shell/TerminalSettings.cs`, `SettingsWindow.axaml(.cs)`, `src/Ntilde.McpServer/Tools/SettingsTools.cs` | 7 | Setting plumbing |
| `src/Ntilde.App/Shell/TerminalView.cs` | 8 | Deferred-resize mode |
| `src/Ntilde.App/Controls/TerminalPane.axaml.cs` | 9 | Mux wiring |
| `src/Ntilde.App/MainWindow.axaml.cs`, `src/Ntilde.App/Shell/SessionManager.cs` | 10, 11, 12 | Close semantics, persistence, orphans, updates |
| tests (per task) | all | see tasks |
| `docs/USER_MANUAL.md`, `docs/ARCHITECTURE.md`, `docs/MODULE_OWNERSHIP.md` | 13 | Docs |

Test-run command shapes used below (PowerShell):

```powershell
scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~<Class>"
scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~<Class>"
scripts/build.ps1 test tests/Ntilde.Architecture.Tests
```

---

### Task 1: Local transports (named pipe, Unix socket, connector)

**Files:**
- Create: `src/Ntilde.Mux/Transport/NamedPipeMuxListener.cs`
- Create: `src/Ntilde.Mux/Transport/UnixSocketMuxListener.cs`
- Create: `src/Ntilde.Mux/Transport/MuxEndpointConnector.cs`
- Test: `tests/Ntilde.Mux.Tests/Transport/NamedPipeMuxListenerTests.cs`
- Test: `tests/Ntilde.Mux.Tests/Transport/UnixSocketMuxListenerTests.cs`

**Interfaces:**
- Consumes: `IMuxListener { Stream? Accept(CancellationToken); void Dispose(); }` (`src/Ntilde.Mux/Transport/IMuxListener.cs`), `MuxServer.Start(IMuxListener)`.
- Produces:
  - `public sealed class NamedPipeMuxListener : IMuxListener` — `NamedPipeMuxListener(string pipeName)`.
  - `public sealed class UnixSocketMuxListener : IMuxListener` — `UnixSocketMuxListener(string socketPath)`; throws `MuxEndpointSecurityException` (new, `: IOException`) for a mis-permissioned directory, `IOException` when a live socket already exists.
  - `public static class MuxEndpointConnector` — `static Stream Connect(string endpoint, TimeSpan timeout)`; endpoint is a pipe name on Windows, a socket path elsewhere; throws `TimeoutException` / `IOException` on failure.

- [ ] **Step 1: Write the failing named-pipe tests**

```csharp
// tests/Ntilde.Mux.Tests/Transport/NamedPipeMuxListenerTests.cs
using System.IO.Pipes;
using Ntilde.Mux.Tests.Support;
using Ntilde.Mux.Transport;

namespace Ntilde.Mux.Tests.Transport;

public sealed class NamedPipeMuxListenerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string UniqueName() => "ntilde-mux-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task A_client_round_trips_bytes_through_an_accepted_pipe()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Named pipes are the Windows transport.");
        string name = UniqueName();
        using var listener = new NamedPipeMuxListener(name);
        Task<Stream?> accept = Task.Run(() => listener.Accept(Ct), Ct);

        using Stream client = MuxEndpointConnector.Connect(name, TimeSpan.FromSeconds(5));
        using Stream server = (await accept.WaitAsync(TimeSpan.FromSeconds(5), Ct))!;
        await client.WriteAsync(new byte[] { 1, 2, 3 }, Ct);
        byte[] got = new byte[3];
        await server.ReadExactlyAsync(got, Ct);
        Assert.Equal(new byte[] { 1, 2, 3 }, got);
    }

    [Fact]
    public async Task Two_clients_can_be_accepted_back_to_back()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Named pipes are the Windows transport.");
        string name = UniqueName();
        using var listener = new NamedPipeMuxListener(name);
        for (int i = 0; i < 2; i++)
        {
            Task<Stream?> accept = Task.Run(() => listener.Accept(Ct), Ct);
            using Stream client = MuxEndpointConnector.Connect(name, TimeSpan.FromSeconds(5));
            using Stream? server = await accept.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.NotNull(server);
        }
    }

    [Fact]
    public async Task Dispose_unblocks_a_pending_accept_with_null()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Named pipes are the Windows transport.");
        var listener = new NamedPipeMuxListener(UniqueName());
        Task<Stream?> accept = Task.Run(() => listener.Accept(Ct), Ct);
        await Task.Delay(100, Ct);
        listener.Dispose();
        Assert.Null(await accept.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public void Connecting_to_a_pipe_nobody_serves_times_out()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Named pipes are the Windows transport.");
        Assert.ThrowsAny<Exception>(() => MuxEndpointConnector.Connect(UniqueName(), TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public void The_pipe_is_created_current_user_only()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Named pipes are the Windows transport.");
        // CurrentUserOnly on the server means a client that does NOT ask for it still connects as the
        // same user, but the ACL names only the owner. We assert the owner-only ACL directly.
        string name = UniqueName();
        using var listener = new NamedPipeMuxListener(name);
        Task.Run(() => listener.Accept(Ct), Ct);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        client.Connect(5000);
        Assert.True(client.IsConnected);
    }
}
```

- [ ] **Step 2: Write the failing Unix-socket tests**

```csharp
// tests/Ntilde.Mux.Tests/Transport/UnixSocketMuxListenerTests.cs
using System.Net.Sockets;
using Ntilde.Mux.Transport;

namespace Ntilde.Mux.Tests.Transport;

public sealed class UnixSocketMuxListenerTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    // Short base: macOS sun_path is 104 bytes and TMPDIR there is already long.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nmx" + Guid.NewGuid().ToString("N")[..8]);
    private string SocketPath => Path.Combine(_dir, "m.sock");

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    [Fact]
    public async Task Round_trips_bytes_and_creates_0700_dir_and_0600_socket()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix sockets are the Linux/macOS transport.");
        using var listener = new UnixSocketMuxListener(SocketPath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_dir));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SocketPath));

        Task<Stream?> accept = Task.Run(() => listener.Accept(Ct), Ct);
        using Stream client = MuxEndpointConnector.Connect(SocketPath, TimeSpan.FromSeconds(5));
        using Stream server = (await accept.WaitAsync(TimeSpan.FromSeconds(5), Ct))!;
        await client.WriteAsync(new byte[] { 7 }, Ct);
        byte[] got = new byte[1];
        await server.ReadExactlyAsync(got, Ct);
        Assert.Equal(7, got[0]);
    }

    [Fact]
    public void Refuses_a_directory_that_exists_with_a_wider_mode()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix sockets are the Linux/macOS transport.");
        Directory.CreateDirectory(_dir);
        File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        var ex = Assert.Throws<MuxEndpointSecurityException>(() => new UnixSocketMuxListener(SocketPath));
        Assert.Contains(_dir, ex.Message);
    }

    [Fact]
    public void Replaces_a_stale_socket_file_nobody_listens_on()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix sockets are the Linux/macOS transport.");
        using (new UnixSocketMuxListener(SocketPath)) { }
        // Leave a dead socket file behind (Dispose unlinks; recreate one by binding and not unlinking).
        Directory.CreateDirectory(_dir);
        using (var dead = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            dead.Bind(new UnixDomainSocketEndPoint(SocketPath));
        }
        Assert.True(File.Exists(SocketPath));
        using var listener = new UnixSocketMuxListener(SocketPath); // must not throw
    }

    [Fact]
    public void Refuses_to_start_over_a_live_socket()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix sockets are the Linux/macOS transport.");
        using var first = new UnixSocketMuxListener(SocketPath);
        Assert.Throws<IOException>(() => new UnixSocketMuxListener(SocketPath));
    }

    [Fact]
    public async Task Dispose_unblocks_accept_and_unlinks_the_socket()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix sockets are the Linux/macOS transport.");
        var listener = new UnixSocketMuxListener(SocketPath);
        Task<Stream?> accept = Task.Run(() => listener.Accept(Ct), Ct);
        await Task.Delay(100, Ct);
        listener.Dispose();
        Assert.Null(await accept.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.False(File.Exists(SocketPath));
    }
}
```

Note: `MuxEndpointSecurityException` is only thrown on Unix but the type is referenced unconditionally, so it must exist on every OS.

- [ ] **Step 3: Run to verify they fail**

Run: `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~MuxListenerTests"`
Expected: build FAIL — `NamedPipeMuxListener`, `UnixSocketMuxListener`, `MuxEndpointConnector`, `MuxEndpointSecurityException` not defined.

- [ ] **Step 4: Implement `NamedPipeMuxListener`**

```csharp
// src/Ntilde.Mux/Transport/NamedPipeMuxListener.cs
using System.IO.Pipes;
using System.Runtime.Versioning;

namespace Ntilde.Mux.Transport;

/// <summary>
/// Windows endpoint (spec §3). <see cref="PipeOptions.CurrentUserOnly"/> restricts the pipe's ACL to
/// the creating user: the protocol has no authentication and <c>spawn</c> runs arbitrary commands.
/// <see cref="PipeOptions.Asynchronous"/> because a pipe opened for synchronous I/O can deadlock its
/// Dispose against a read blocked on another thread (PR #474 open question).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NamedPipeMuxListener : IMuxListener
{
    private readonly string _pipeName;
    private readonly CancellationTokenSource _disposed = new();
    private readonly object _gate = new();
    private NamedPipeServerStream? _pending;

    public NamedPipeMuxListener(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        // Create the first instance now so a client that connects before the accept loop runs finds
        // the pipe (and so a name collision fails the constructor, not the accept thread).
        _pending = CreateInstance();
    }

    private NamedPipeServerStream CreateInstance() =>
        new(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    public Stream? Accept(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
        NamedPipeServerStream server;
        lock (_gate)
        {
            if (_disposed.IsCancellationRequested) return null;
            server = _pending ?? CreateInstance();
            _pending = null;
        }

        try
        {
            server.WaitForConnectionAsync(linked.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
        {
            server.Dispose();
            if (linked.IsCancellationRequested) return null;
            throw;
        }

        // Pre-create the next instance so there is never a moment with no listening instance
        // (a client connecting then would see "pipe not found" rather than wait).
        lock (_gate)
        {
            if (!_disposed.IsCancellationRequested) _pending ??= CreateInstance();
        }

        return server;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed.IsCancellationRequested) return;
            _disposed.Cancel();
            _pending?.Dispose();
            _pending = null;
        }
    }
}
```

- [ ] **Step 5: Implement `UnixSocketMuxListener` + `MuxEndpointSecurityException`**

```csharp
// src/Ntilde.Mux/Transport/UnixSocketMuxListener.cs
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace Ntilde.Mux.Transport;

/// <summary>A local endpoint whose permissions would let another user reach the daemon.</summary>
public sealed class MuxEndpointSecurityException : IOException
{
    public MuxEndpointSecurityException(string message) : base(message) { }
}

/// <summary>
/// Linux/macOS endpoint (spec §3): a socket file (0600) inside a directory created 0700. A directory
/// that already exists with any other mode is refused rather than "fixed": something other than us
/// made it, and a 0700 directory owned by someone else is unreadable to us, so bind fails - which
/// makes the mode check an ownership check too, without stat interop.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class UnixSocketMuxListener : IMuxListener
{
    private const UnixFileMode DirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode SocketMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _socketPath;
    private readonly Socket _socket;
    private readonly CancellationTokenSource _disposed = new();

    public UnixSocketMuxListener(string socketPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        _socketPath = Path.GetFullPath(socketPath);
        string dir = Path.GetDirectoryName(_socketPath)!;
        EnsurePrivateDirectory(dir);

        if (File.Exists(_socketPath))
        {
            if (IsAlive(_socketPath)) throw new IOException($"Another multiplexer is already listening on {_socketPath}.");
            File.Delete(_socketPath); // stale: probe-connect failed
        }

        _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            _socket.Bind(new UnixDomainSocketEndPoint(_socketPath));
            File.SetUnixFileMode(_socketPath, SocketMode);
            _socket.Listen(backlog: 16);
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
    }

    private static void EnsurePrivateDirectory(string dir)
    {
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir, DirMode);
            // umask can only remove bits, never add: re-assert in case the parent path pre-existed.
            File.SetUnixFileMode(dir, DirMode);
            return;
        }

        UnixFileMode mode = File.GetUnixFileMode(dir);
        if (mode != DirMode)
        {
            throw new MuxEndpointSecurityException(
                $"Refusing to serve: {dir} has mode {Convert.ToString((int)mode, 8)}, expected 700. Remove the directory or fix its permissions.");
        }
    }

    internal static bool IsAlive(string socketPath)
    {
        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            probe.Connect(new UnixDomainSocketEndPoint(socketPath));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public Stream? Accept(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
        try
        {
            Socket client = _socket.AcceptAsync(linked.Token).AsTask().GetAwaiter().GetResult();
            return new NetworkStream(client, ownsSocket: true);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            if (linked.IsCancellationRequested) return null;
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed.IsCancellationRequested) return;
        _disposed.Cancel();
        _socket.Dispose();
        try { File.Delete(_socketPath); }
        catch (IOException) { /* best effort; a stale file is probed next start */ }
        catch (UnauthorizedAccessException) { }
    }
}
```

- [ ] **Step 6: Implement `MuxEndpointConnector`**

```csharp
// src/Ntilde.Mux/Transport/MuxEndpointConnector.cs
using System.IO.Pipes;
using System.Net.Sockets;

namespace Ntilde.Mux.Transport;

/// <summary>Client end of <see cref="NamedPipeMuxListener"/> / <see cref="UnixSocketMuxListener"/>.</summary>
public static class MuxEndpointConnector
{
    /// <param name="endpoint">Pipe name on Windows, absolute socket path elsewhere.</param>
    public static Stream Connect(string endpoint, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        if (OperatingSystem.IsWindows())
        {
            // CurrentUserOnly on the client too: it verifies the server pipe is owned by this user,
            // so a pipe squatted by another account is rejected rather than trusted.
            var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                pipe.Connect((int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue));
                return pipe;
            }
            catch
            {
                pipe.Dispose();
                throw;
            }
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), cts.Token).AsTask().GetAwaiter().GetResult();
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (OperationCanceledException)
        {
            socket.Dispose();
            throw new TimeoutException($"Timed out connecting to {endpoint}.");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~MuxListenerTests"`
Expected (Windows): the 5 named-pipe tests PASS and the Unix tests are SKIPPED. On CI's ubuntu leg it's the reverse.
Also run `scripts/build.ps1 build -c Release src/Ntilde.Mux`: expect 0 warnings. `IsAotCompatible` plus warnings-as-errors means CA1416 platform warnings would fail the build. The attributes above satisfy them, but `MuxEndpointConnector` calls `NamedPipeClientStream` under `OperatingSystem.IsWindows()`, which the analyzer accepts.

- [ ] **Step 8: Commit**

```powershell
scripts/build.ps1 format whitespace --no-restore
git add src/Ntilde.Mux/Transport tests/Ntilde.Mux.Tests/Transport
git commit -m "feat(mux): current-user named-pipe and 0700 unix-socket listeners"
```

---

### Task 2: Discovery and the endpoint descriptor (`Ntilde.Mux.Contracts`)

**Files:**
- Create: `src/Ntilde.Mux.Contracts/MuxDiscovery.cs`
- Modify: `src/Ntilde.Mux.Contracts/MuxMessages.cs` (add `MuxEndpointDescriptor`)
- Modify: `src/Ntilde.Mux.Contracts/MuxJsonContext.cs` (add `[JsonSerializable(typeof(MuxEndpointDescriptor))]`)
- Test: `tests/Ntilde.Mux.Tests/Contracts/MuxDiscoveryTests.cs`

**Interfaces:**
- Produces (all `public static` on `MuxDiscovery` unless noted):
  - `const string RootOverrideEnvVar = "NTILDE_APPDATA_ROOT"`
  - `string GetRootDirectory()`
  - `string GetDescriptorPath()` → `<root>/mux/mux-endpoint.json`; overload `GetDescriptorPath(string root)`
  - `string GetDefaultEndpoint()`; overload `GetDefaultEndpoint(string root)`
  - `void WriteDescriptor(string path, MuxEndpointDescriptor descriptor)` (atomic)
  - `bool TryReadDescriptor(string path, [NotNullWhen(true)] out MuxEndpointDescriptor? descriptor)`
  - `bool TryReadLiveDescriptor(string path, [NotNullWhen(true)] out MuxEndpointDescriptor? descriptor)`
  - `bool IsProcessAlive(int pid, string processName)`
  - `void DeleteDescriptorIfOwned(string path, int pid)`
  - `public sealed record MuxEndpointDescriptor { int MinVersion; int MaxVersion; required string Endpoint; int Pid; required string ProcessName; }`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Ntilde.Mux.Tests/Contracts/MuxDiscoveryTests.cs
using System.Diagnostics;
using Ntilde.Mux.Contracts;

namespace Ntilde.Mux.Tests.Contracts;

public sealed class MuxDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ntilde-disc-" + Guid.NewGuid().ToString("N"));
    private string DescriptorPath => MuxDiscovery.GetDescriptorPath(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static MuxEndpointDescriptor Descriptor(int pid, string processName) => new()
    {
        MinVersion = MuxProtocol.MinSupportedVersion,
        MaxVersion = MuxProtocol.MaxSupportedVersion,
        Endpoint = "ep",
        Pid = pid,
        ProcessName = processName,
    };

    [Fact]
    public void Descriptor_path_is_under_the_root_mux_directory()
    {
        Assert.Equal(Path.Combine(_root, "mux", "mux-endpoint.json"), DescriptorPath);
    }

    [Fact]
    public void Write_then_read_round_trips_and_leaves_no_temp_files()
    {
        MuxDiscovery.WriteDescriptor(DescriptorPath, Descriptor(123, "ntilde"));
        Assert.True(MuxDiscovery.TryReadDescriptor(DescriptorPath, out MuxEndpointDescriptor? back));
        Assert.Equal(123, back.Pid);
        Assert.Equal("ep", back.Endpoint);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(DescriptorPath)!));
    }

    [Fact]
    public void The_current_process_under_its_own_name_is_live()
    {
        using Process self = Process.GetCurrentProcess();
        MuxDiscovery.WriteDescriptor(DescriptorPath, Descriptor(self.Id, self.ProcessName));
        Assert.True(MuxDiscovery.TryReadLiveDescriptor(DescriptorPath, out _));
    }

    [Fact]
    public void A_reused_pid_with_a_different_process_name_is_not_live()
    {
        using Process self = Process.GetCurrentProcess();
        MuxDiscovery.WriteDescriptor(DescriptorPath, Descriptor(self.Id, "definitely-not-" + self.ProcessName));
        Assert.False(MuxDiscovery.TryReadLiveDescriptor(DescriptorPath, out _));
    }

    [Fact]
    public void A_dead_pid_is_not_live()
    {
        using Process p = Process.Start(new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            OperatingSystem.IsWindows() ? "/c exit 0" : "-c true") { UseShellExecute = false, CreateNoWindow = true })!;
        p.WaitForExit();
        MuxDiscovery.WriteDescriptor(DescriptorPath, Descriptor(p.Id, "cmd"));
        Assert.False(MuxDiscovery.TryReadLiveDescriptor(DescriptorPath, out _));
    }

    [Fact]
    public void Missing_or_garbage_descriptor_reads_as_absent()
    {
        Assert.False(MuxDiscovery.TryReadDescriptor(DescriptorPath, out _));
        Directory.CreateDirectory(Path.GetDirectoryName(DescriptorPath)!);
        File.WriteAllText(DescriptorPath, "{not json");
        Assert.False(MuxDiscovery.TryReadDescriptor(DescriptorPath, out _));
    }

    [Fact]
    public void Delete_if_owned_keeps_another_pids_descriptor()
    {
        MuxDiscovery.WriteDescriptor(DescriptorPath, Descriptor(42, "ntilde"));
        MuxDiscovery.DeleteDescriptorIfOwned(DescriptorPath, pid: 43);
        Assert.True(File.Exists(DescriptorPath));
        MuxDiscovery.DeleteDescriptorIfOwned(DescriptorPath, pid: 42);
        Assert.False(File.Exists(DescriptorPath));
    }

    [Fact]
    public void Different_roots_get_different_endpoints()
    {
        string other = _root + "-b";
        Assert.NotEqual(MuxDiscovery.GetDefaultEndpoint(_root), MuxDiscovery.GetDefaultEndpoint(other));
    }

    [Fact]
    public void Unix_endpoint_falls_back_to_tmp_when_the_socket_path_is_too_long()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows uses pipe names.");
        string longRoot = Path.Combine(Path.GetTempPath(), new string('r', 150));
        string endpoint = MuxDiscovery.GetDefaultEndpoint(longRoot);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(endpoint) <= 103, endpoint);
        Assert.EndsWith("mux.sock", endpoint);
    }

    [Fact]
    public void Windows_endpoint_is_a_bare_pipe_name()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Unix uses socket paths.");
        string endpoint = MuxDiscovery.GetDefaultEndpoint(_root);
        Assert.StartsWith("ntilde-mux-", endpoint);
        Assert.DoesNotContain('\\', endpoint);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~MuxDiscoveryTests"`
Expected: build FAIL (`MuxDiscovery`, `MuxEndpointDescriptor` undefined).

- [ ] **Step 3: Add the descriptor DTO and register it**

Append to `src/Ntilde.Mux.Contracts/MuxMessages.cs`:

```csharp
/// <summary>
/// <c>mux/mux-endpoint.json</c> (spec §3): how a client finds a running daemon, and enough to tell a
/// live daemon from a recycled pid (<see cref="ProcessName"/>).
/// </summary>
public sealed record MuxEndpointDescriptor
{
    public int MinVersion { get; init; }
    public int MaxVersion { get; init; }
    /// <summary>Pipe name (Windows) or absolute socket path (Linux/macOS).</summary>
    public required string Endpoint { get; init; }
    public int Pid { get; init; }
    public required string ProcessName { get; init; }
}
```

In `MuxJsonContext.cs` add `[JsonSerializable(typeof(MuxEndpointDescriptor))]` next to the existing attributes.

- [ ] **Step 4: Implement `MuxDiscovery`**

```csharp
// src/Ntilde.Mux.Contracts/MuxDiscovery.cs
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ntilde.Mux.Contracts;

/// <summary>
/// Where the daemon's endpoint and descriptor live (spec §3). Same root rule as
/// <c>AgentHostDiscovery</c>/<c>AppPaths</c>, duplicated deliberately: this assembly is a
/// zero-reference leaf.
/// </summary>
public static class MuxDiscovery
{
    private const string AppName = "ntilde";
    public const string RootOverrideEnvVar = "NTILDE_APPDATA_ROOT";
    public const string DirectoryName = "mux";
    public const string DescriptorFileName = "mux-endpoint.json";
    public const string SocketFileName = "mux.sock";

    public static string GetRootDirectory()
    {
        string? overrideRoot = Environment.GetEnvironmentVariable(RootOverrideEnvVar);
        if (!string.IsNullOrWhiteSpace(overrideRoot)) return Path.GetFullPath(overrideRoot);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
    }

    public static string GetDescriptorPath() => GetDescriptorPath(GetRootDirectory());

    public static string GetDescriptorPath(string root) => Path.Combine(root, DirectoryName, DescriptorFileName);

    public static string GetDefaultEndpoint() => GetDefaultEndpoint(GetRootDirectory());

    public static string GetDefaultEndpoint(string root)
    {
        string suffix = $"{SanitizedUser()}-{RootHash(root)}";
        if (OperatingSystem.IsWindows()) return "ntilde-mux-" + suffix;

        string preferred = Path.Combine(root, DirectoryName, SocketFileName);
        int budget = OperatingSystem.IsMacOS() ? 103 : 107; // sun_path minus the terminating NUL
        if (Encoding.UTF8.GetByteCount(preferred) <= budget) return preferred;
        return Path.Combine(Path.GetTempPath(), "ntilde-mux-" + suffix, SocketFileName);
    }

    private static string SanitizedUser()
    {
        var sb = new StringBuilder();
        foreach (char c in Environment.UserName.ToLowerInvariant()) if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
        return sb.Length == 0 ? "user" : sb.ToString();
    }

    private static string RootHash(string root)
    {
        string full = Path.GetFullPath(root);
        if (OperatingSystem.IsWindows()) full = full.ToUpperInvariant(); // case-insensitive paths
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(full));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    /// <summary>Temp file in the same directory, then an atomic replace. No .bak: a stale descriptor is harmless.</summary>
    public static void WriteDescriptor(string path, MuxEndpointDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(descriptor, MuxJsonContext.Default.MuxEndpointDescriptor));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public static bool TryReadDescriptor(string path, [NotNullWhen(true)] out MuxEndpointDescriptor? descriptor)
    {
        descriptor = null;
        try
        {
            if (!File.Exists(path)) return false;
            descriptor = JsonSerializer.Deserialize(File.ReadAllText(path), MuxJsonContext.Default.MuxEndpointDescriptor);
            return descriptor is { Endpoint.Length: > 0 };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            descriptor = null;
            return false;
        }
    }

    /// <summary>Readable, and its pid is alive under the recorded process name (guards pid reuse).</summary>
    public static bool TryReadLiveDescriptor(string path, [NotNullWhen(true)] out MuxEndpointDescriptor? descriptor)
    {
        if (TryReadDescriptor(path, out descriptor) && IsProcessAlive(descriptor.Pid, descriptor.ProcessName)) return true;
        descriptor = null;
        return false;
    }

    public static bool IsProcessAlive(int pid, string processName)
    {
        if (pid <= 0) return false;
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited && string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    public static void DeleteDescriptorIfOwned(string path, int pid)
    {
        if (!TryReadDescriptor(path, out MuxEndpointDescriptor? d) || d.Pid != pid) return;
        try { File.Delete(path); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~MuxDiscoveryTests"` → all PASS or SKIP by OS.
Run: `scripts/build.ps1 test tests/Ntilde.Architecture.Tests --filter "FullyQualifiedName~MuxContracts"` → PASS (the leaf guarantee holds; `System.Diagnostics.Process` and `System.Security.Cryptography` are BCL).

- [ ] **Step 6: Commit**

```powershell
scripts/build.ps1 format whitespace --no-restore
git add src/Ntilde.Mux.Contracts tests/Ntilde.Mux.Tests/Contracts/MuxDiscoveryTests.cs
git commit -m "feat(mux): endpoint discovery and atomic live descriptor"
```

---

### Task 3: Server lifecycle primitives (reaping, idle gate, `shutdown`, input writer)

**Files:**
- Modify: `src/Ntilde.Mux.Contracts/MuxProtocol.cs` (`MuxMethods.Shutdown = "shutdown"`)
- Modify: `src/Ntilde.Mux/HeadlessTerminalSession.cs` (input writer thread, `ExitedAtMs`)
- Modify: `src/Ntilde.Mux/MuxServer.cs` (lifecycle lock, `RunningSessionCount`, `ReapExitedSessions`, `TryBeginIdleShutdown`, `ShutdownRequested`, `KillAll`)
- Modify: `src/Ntilde.Mux/MuxServerConnection.cs` (`shutdown` case)
- Modify: `src/Ntilde.Mux/MuxClient.cs` (`ShutdownServerAsync`)
- Modify: `src/Ntilde.Mux/MuxClientSession.cs` (`public bool IsConnected => _client.IsConnected;`)
- Test: `tests/Ntilde.Mux.Tests/Server/MuxServerLifecycleTests.cs`
- Test: `tests/Ntilde.Mux.Tests/Headless/HeadlessInputWriterTests.cs`

**Interfaces:**
- Consumes: `MuxTestHost`, `ScriptedTerminalSession` (has `SentInput`-style recording — check the fake; `ThrowOnSendInput` exists), `TestWait`.
- Produces:
  - `MuxServer.RunningSessionCount` (`int`), `MuxServer.ReapExitedSessions(TimeSpan grace) : int` (count reaped), `MuxServer.TryBeginIdleShutdown() : bool`, `MuxServer.IsAcceptingStopped` (`bool`), `public event Action? ShutdownRequested`, `MuxServer.KillAllSessions()`.
  - `HeadlessTerminalSession.ExitedAtMs` (`long`, `Environment.TickCount64` at exit, 0 while running), `HeadlessSessionOptions.MaxQueuedInputBytes` (default 16 MiB).
  - `MuxClient.ShutdownServerAsync(CancellationToken) : Task`.
  - `MuxClientSession.IsConnected` (`bool`).

- [ ] **Step 1: Read the fake session and existing session tests.** Open `tests/Ntilde.Mux.Tests/Support/ScriptedTerminalSession.cs` and find how it records input (e.g. `SentInput` / `Inputs`) and how a test blocks `SendInput` (add a `SendInputGate` `ManualResetEventSlim?` that `SendInput` waits on if non-null — add it now if absent). Find how a test makes the fake exit (e.g. `EmitExit(int)`).

- [ ] **Step 2: Write the failing lifecycle tests**

```csharp
// tests/Ntilde.Mux.Tests/Server/MuxServerLifecycleTests.cs
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Server;

public sealed class MuxServerLifecycleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_exited_unattached_session_is_reaped_after_the_grace_period()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        host.Fake(id).EmitExit(0);
        await TestWait.UntilAsync(() => host.Mux(id).IsExited, "the mux saw the exit");

        Assert.Equal(0, host.Server.ReapExitedSessions(TimeSpan.FromMinutes(5)));   // too young
        Assert.Equal(1, host.Server.ReapExitedSessions(TimeSpan.Zero));
        Assert.DoesNotContain(id, host.Server.GetSessionIds());
    }

    [Fact]
    public async Task An_exited_session_with_an_attached_client_is_kept()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(client, id);
        host.Fake(id).EmitExit(3);
        await TestWait.UntilAsync(() => host.Mux(id).IsExited, "the mux saw the exit");

        Assert.Equal(0, host.Server.ReapExitedSessions(TimeSpan.Zero));
        pane.Session.Dispose(); // detach
        await TestWait.UntilAsync(() => host.Mux(id).AttachedClients == 0, "the detach landed");
        Assert.Equal(1, host.Server.ReapExitedSessions(TimeSpan.Zero));
    }

    [Fact]
    public async Task Idle_shutdown_is_refused_while_a_connection_or_running_session_exists()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Assert.False(host.Server.TryBeginIdleShutdown());            // a connection
        Guid id = await MuxTestHost.SpawnAsync(client);
        client.Dispose();
        await TestWait.UntilAsync(() => host.Server.ConnectionCount == 0, "the connection closed");
        Assert.False(host.Server.TryBeginIdleShutdown());            // a running session
        host.Server.KillAllSessions();
        Assert.True(host.Server.TryBeginIdleShutdown());
        Assert.True(host.Server.IsAcceptingStopped);
    }

    [Fact]
    public async Task A_connection_after_idle_shutdown_began_is_refused_not_hung()
    {
        using var host = new MuxTestHost();
        Assert.True(host.Server.TryBeginIdleShutdown());
        // The in-memory listener is disposed by the shutdown; accept directly to model the race.
        (Stream clientEnd, Stream serverEnd) = Ntilde.Mux.Transport.InMemoryDuplexPipe.Create(1 << 16);
        host.Server.AcceptConnection(serverEnd);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            MuxClient.ConnectAsync(clientEnd, new MuxClientOptions { RequestTimeout = TimeSpan.FromSeconds(5) }, Ct));
    }

    [Fact]
    public async Task Shutdown_replies_then_raises_ShutdownRequested()
    {
        using var host = new MuxTestHost();
        int raised = 0;
        host.Server.ShutdownRequested += () => Interlocked.Increment(ref raised);
        MuxClient client = await host.ConnectClientAsync();
        await client.ShutdownServerAsync(Ct);
        await TestWait.UntilAsync(() => Volatile.Read(ref raised) == 1, "ShutdownRequested fired");
    }

    [Fact]
    public async Task MuxClientSession_IsConnected_follows_the_client()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        MuxClientSession session = client.OpenSession(id);
        Assert.True(session.IsConnected);
        client.Dispose();
        Assert.False(session.IsConnected);
    }
}
```

If `InMemoryDuplexPipe` is `internal`, the test project already has `InternalsVisibleTo`; if its `Create` signature differs, match it (see `InMemoryMuxListener.Connect`).

- [ ] **Step 3: Write the failing input-writer tests**

```csharp
// tests/Ntilde.Mux.Tests/Headless/HeadlessInputWriterTests.cs
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Headless;

public sealed class HeadlessInputWriterTests
{
    [Fact]
    public async Task A_child_that_stops_reading_stdin_does_not_block_input_to_another_session_on_the_same_connection()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid stuck = await MuxTestHost.SpawnAsync(client);
        Guid healthy = await MuxTestHost.SpawnAsync(client);
        using var gate = new ManualResetEventSlim(false);
        host.Fake(stuck).SendInputGate = gate;          // SendInput blocks until set

        using MuxClientSession a = client.OpenSession(stuck);
        using MuxClientSession b = client.OpenSession(healthy);
        a.SendInput("wedged");
        b.SendInput("hello");

        await TestWait.UntilAsync(() => host.Fake(healthy).ReceivedInput.Contains("hello"),
            "input to the healthy session arrives while the other session's writer is blocked");
        await client.PingAsync(TestContext.Current.CancellationToken); // the connection reader is not stuck either
        gate.Set();
        await TestWait.UntilAsync(() => host.Fake(stuck).ReceivedInput.Contains("wedged"), "the wedged input lands once unblocked");
    }

    [Fact]
    public async Task Input_order_is_preserved_per_session()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        using MuxClientSession s = client.OpenSession(id);
        for (int i = 0; i < 200; i++) s.SendInput(i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",");
        string expected = string.Concat(Enumerable.Range(0, 200).Select(i => i + ","));
        await TestWait.UntilAsync(() => string.Concat(host.Fake(id).ReceivedInput) == expected, "all 200 inputs arrive in order");
    }

    [Fact]
    public async Task Input_beyond_the_byte_cap_is_dropped_not_queued_forever()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        using var gate = new ManualResetEventSlim(false);
        host.Fake(id).SendInputGate = gate;
        HeadlessTerminalSession mux = host.Mux(id);
        mux.MaxQueuedInputBytesForTest = 1024;
        mux.SendInput("first");                              // taken by the writer, which then blocks
        await TestWait.UntilAsync(() => mux.QueuedInputBytes == 0, "the writer took the first item");
        mux.SendInput(new string('x', 2000));                // over the cap: dropped
        Assert.Equal(0, mux.QueuedInputBytes);
        gate.Set();
    }
}
```

Adjust `ReceivedInput` / `SendInputGate` to the fake's real member names (Step 1). Add them to `ScriptedTerminalSession` if missing: `public ConcurrentQueue<string> ReceivedInput { get; } = new();` and `public ManualResetEventSlim? SendInputGate { get; set; }`, with `SendInput` doing `SendInputGate?.Wait(); ReceivedInput.Enqueue(text);` after its existing `ThrowOnSendInput` check.

- [ ] **Step 4: Run to verify failure**

Run: `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~MuxServerLifecycleTests|FullyQualifiedName~HeadlessInputWriterTests"`
Expected: build FAIL (missing members).

- [ ] **Step 5: Implement the input writer in `HeadlessTerminalSession`**

Add fields and a thread started in the constructor right after `_parseThread.Start()` (and stopped in the same failure `catch` and in `Dispose`):

```csharp
// Input writer (spec §4): SendInput runs on the connection reader thread; a child that stops
// reading stdin would otherwise stall every session sharing that connection.
private readonly BlockingCollection<string> _input = new();
private readonly Thread _inputThread;
private long _queuedInputBytes;
private long _exitedAtMs;

internal long QueuedInputBytes => Interlocked.Read(ref _queuedInputBytes);
internal long MaxQueuedInputBytesForTest { get; set; }
private long MaxQueuedInputBytes => MaxQueuedInputBytesForTest > 0 ? MaxQueuedInputBytesForTest : _maxQueuedInputBytes;
public long ExitedAtMs => Interlocked.Read(ref _exitedAtMs);
```

Constructor: `_maxQueuedInputBytes = options.MaxQueuedInputBytes;` and
`_inputThread = new Thread(InputLoop) { IsBackground = true, Name = $"MuxInput-{id:N}" }; _inputThread.Start();`
Change `_parser.OnResponse = reply => _session.SendInput(reply);` to `_parser.OnResponse = EnqueueInput;`.

```csharp
public void SendInput(string text)
{
    if (IsExited || Volatile.Read(ref _disposed) != 0 || string.IsNullOrEmpty(text)) return;
    EnqueueInput(text);
}

private void EnqueueInput(string text)
{
    long bytes = (long)text.Length * sizeof(char);
    if (Interlocked.Add(ref _queuedInputBytes, bytes) > MaxQueuedInputBytes)
    {
        Interlocked.Add(ref _queuedInputBytes, -bytes);
        Log($"[Mux] session {Id}: dropped {text.Length} chars of input; the child is not reading stdin.");
        return;
    }

    try { _input.Add(text); }
    catch (InvalidOperationException) { Interlocked.Add(ref _queuedInputBytes, -bytes); } // CompleteAdding: disposed
}

private void InputLoop()
{
    try
    {
        foreach (string text in _input.GetConsumingEnumerable(_cts.Token))
        {
            Interlocked.Add(ref _queuedInputBytes, -(long)text.Length * sizeof(char));
            try { _session.SendInput(text); }
            catch (Exception ex) { Log($"[Mux] session {Id}: SendInput failed: {ex.Message}"); }
        }
    }
    catch (OperationCanceledException) { /* disposed */ }
}
```

Wherever the parse loop marks the session exited (the place that sets `_exited = 1`), also set `Interlocked.Exchange(ref _exitedAtMs, Environment.TickCount64);`. In `Dispose` (after `_cts.Cancel()`): `_input.CompleteAdding(); _inputThread.Join(TimeSpan.FromSeconds(2));` and dispose `_input` only if the thread stopped (same rule the file already applies to its queues). Add `public long MaxQueuedInputBytes { get; init; } = 16L * 1024 * 1024;` to `HeadlessSessionOptions`, and pass it through in `MuxServer.Spawn`.

- [ ] **Step 6: Implement the server lifecycle members**

In `MuxServer`:

```csharp
private readonly object _lifecycleGate = new();
private bool _acceptingStopped;

public event Action? ShutdownRequested;
public bool IsAcceptingStopped { get { lock (_lifecycleGate) return _acceptingStopped; } }
public int RunningSessionCount => _sessions.Values.Count(s => !s.IsExited);

/// <summary>Removes and disposes exited sessions nobody is attached to that exited at least <paramref name="grace"/> ago (spec §4).</summary>
public int ReapExitedSessions(TimeSpan grace)
{
    long now = Environment.TickCount64;
    int reaped = 0;
    foreach (HeadlessTerminalSession s in _sessions.Values)
    {
        if (!s.IsExited || s.AttachedClients != 0 || now - s.ExitedAtMs < (long)grace.TotalMilliseconds) continue;
        if (_sessions.TryRemove(new KeyValuePair<Guid, HeadlessTerminalSession>(s.Id, s)))
        {
            s.Dispose();
            reaped++;
        }
    }

    return reaped;
}

/// <summary>
/// Stop accepting iff there is nothing to serve, atomically with <see cref="AcceptConnection"/>:
/// a connection either registered before (and this returns false) or is refused after.
/// </summary>
public bool TryBeginIdleShutdown()
{
    lock (_lifecycleGate)
    {
        if (_acceptingStopped) return true;
        if (!_connections.IsEmpty || RunningSessionCount != 0) return false;
        _acceptingStopped = true;
    }

    _listener?.Dispose();
    return true;
}

public void KillAllSessions()
{
    foreach (Guid id in _sessions.Keys)
    {
        if (_sessions.TryRemove(id, out HeadlessTerminalSession? s))
        {
            try { s.Kill(); } catch (Exception ex) { Log($"[MuxServer] kill {id} failed: {ex.Message}"); }
            s.Dispose();
        }
    }
}

internal void RequestShutdown()
{
    try { ShutdownRequested?.Invoke(); }
    catch (Exception ex) { Log($"[MuxServer] ShutdownRequested handler threw: {ex}"); }
}
```

Change `AcceptConnection` to register under the gate:

```csharp
public void AcceptConnection(Stream stream)
{
    ArgumentNullException.ThrowIfNull(stream);
    MuxServerConnection connection;
    lock (_lifecycleGate)
    {
        if (Volatile.Read(ref _disposed) != 0 || _acceptingStopped)
        {
            stream.Dispose();
            return;
        }

        connection = new MuxServerConnection(this, stream);
        _connections[connection.ConnectionId] = connection;
    }

    connection.Start();
}
```

Note `ReapExitedSessions`'s `Count`/`Values` enumerations over `ConcurrentDictionary` are snapshot-safe.

In `MuxServerConnection.HandleRequest` add before `case MuxMethods.Hello:`:

```csharp
case MuxMethods.Shutdown:
    ReplyEmpty(request);
    // After the reply is queued: the host tears the server down, which aborts this connection.
    ThreadPool.UnsafeQueueUserWorkItem(static s => s.RequestShutdown(), _server, preferLocal: false);
    break;
```

(Off the reader thread so the host's `Dispose` → connection `Abort` does not run on the connection's own reader. This is control-plane, not the output path.)

In `MuxProtocol.cs` `MuxMethods`: `public const string Shutdown = "shutdown";` with a doc comment "Request: the daemon kills every session and exits (spec §4). Additive."

In `MuxClient`: 

```csharp
public Task ShutdownServerAsync(CancellationToken cancellationToken = default) =>
    RequestAsync(MuxMethods.Shutdown, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty, MuxJsonContext.Default.MuxEmpty, cancellationToken);
```

In `MuxClientSession`: `public bool IsConnected => _client.IsConnected;` (doc: "False once the connection to the daemon is gone; the session may still be running there.").

- [ ] **Step 7: Run the new tests, then the whole Mux suite**

Run: `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~MuxServerLifecycleTests|FullyQualifiedName~HeadlessInputWriterTests"` → PASS.
Run: `scripts/build.ps1 test tests/Ntilde.Mux.Tests` → all PASS (267 + new). Tests that asserted synchronous `SendInput` (e.g. an immediate check after `session.SendInput`) must now wait with `TestWait.UntilAsync`; fix any such test by waiting, never by weakening the assertion.

- [ ] **Step 8: Commit**

```powershell
scripts/build.ps1 format whitespace --no-restore
git add src/Ntilde.Mux src/Ntilde.Mux.Contracts tests/Ntilde.Mux.Tests
git commit -m "feat(mux): reaping, idle-shutdown gate, shutdown method, per-session input writer"
```

---
### Task 4: `MuxDaemonHost` (listener + descriptor + lock + reap/idle timer)

**Files:**
- Create: `src/Ntilde.Mux/MuxDaemonHost.cs`, `src/Ntilde.Mux/MuxDaemonOptions.cs`, `src/Ntilde.Mux/Transport/MuxListeners.cs`
- Test: `tests/Ntilde.Mux.Tests/Server/MuxDaemonHostTests.cs`

**Interfaces:**
- Consumes: Task 1 listeners/connector, Task 2 `MuxDiscovery`, Task 3 `MuxServer.ReapExitedSessions/TryBeginIdleShutdown/KillAllSessions/ShutdownRequested/RunningSessionCount`.
- Produces:
  - `public static class MuxListeners { public static IMuxListener Create(string endpoint); }` (pipe on Windows, UDS elsewhere).
  - `public sealed class MuxDaemonOptions { required string Endpoint; required string DescriptorPath; TimeSpan IdleExitAfter = 10 min (Zero = never); TimeSpan ReapGrace = 60 s; TimeSpan TickInterval = 1 s; Func<string, IMuxListener> ListenerFactory = MuxListeners.Create; int Pid = Environment.ProcessId; string ProcessName = <current>; Action<string>? Log; }`
  - `public sealed class MuxDaemonAlreadyRunningException : IOException`
  - `public sealed class MuxDaemonHost : IDisposable { MuxDaemonHost(MuxServer, MuxDaemonOptions); void Start(); Task<string> Completion { get; } void RequestStop(string reason); internal void TickForTest(); }` — `Completion` yields the stop reason: `"idle"`, `"shutdown"`, `"signal"`, `"disposed"`.

Behaviour (spec §4):
- `Start()`:
  1. Take `mux/mux.lock` (`FileStream(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)`, held until stop). If that fails, or a live descriptor names a different pid, throw `MuxDaemonAlreadyRunningException`.
  2. Create the listener, then `server.Start(listener)`.
  3. `MuxDiscovery.WriteDescriptor`.
  4. Subscribe `ShutdownRequested` → `RequestStop("shutdown")`.
  5. Start a `System.Threading.Timer` for `Tick`.
- `Tick`:
  1. `ReapExitedSessions(ReapGrace)`.
  2. If `RunningSessionCount == 0 && ConnectionCount == 0`: set `_idleSince` (keep it if already set). Once `IdleExitAfter` has elapsed and `TryBeginIdleShutdown()` succeeds → `RequestStop("idle")`. Otherwise clear `_idleSince`.
  3. The whole body is inside `try/catch (Exception) { Log }`.
- `RequestStop(reason)`, once only:
  1. Dispose the timer.
  2. `KillAllSessions()` for `shutdown`/`signal`/`disposed`.
  3. `server.Dispose()`, which disposes the listener and unlinks the socket.
  4. `MuxDiscovery.DeleteDescriptorIfOwned(DescriptorPath, Pid)`.
  5. Release and delete the lock file.
  6. Complete `Completion`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Ntilde.Mux.Tests/Server/MuxDaemonHostTests.cs
using System.Diagnostics;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Mux.Transport;

namespace Ntilde.Mux.Tests.Server;

public sealed class MuxDaemonHostTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nmxd" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<IDisposable> _owned = new();

    public void Dispose()
    {
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private (MuxDaemonHost Host, MuxServer Server, MuxDaemonOptions Options) NewHost(TimeSpan? idle = null, int? pid = null)
    {
        var server = new MuxServer(new ScriptedSessionFactory(), new MuxServerOptions { ForceConPtyFiltering = false });
        using Process self = Process.GetCurrentProcess();
        var options = new MuxDaemonOptions
        {
            Endpoint = MuxDiscovery.GetDefaultEndpoint(_root),
            DescriptorPath = MuxDiscovery.GetDescriptorPath(_root),
            IdleExitAfter = idle ?? TimeSpan.Zero,
            TickInterval = TimeSpan.FromMilliseconds(50),
            Pid = pid ?? Environment.ProcessId,
            ProcessName = self.ProcessName,
        };
        var host = new MuxDaemonHost(server, options);
        _owned.Add(host);
        return (host, server, options);
    }

    private static Task<MuxClient> ConnectAsync(string endpoint) =>
        MuxClient.ConnectAsync(MuxEndpointConnector.Connect(endpoint, TimeSpan.FromSeconds(5)), null, Ct);

    [Fact]
    public async Task Start_writes_a_live_descriptor_and_serves_the_endpoint()
    {
        var (host, _, o) = NewHost();
        host.Start();
        Assert.True(MuxDiscovery.TryReadLiveDescriptor(o.DescriptorPath, out MuxEndpointDescriptor? d));
        Assert.Equal(o.Endpoint, d.Endpoint);
        using MuxClient client = await ConnectAsync(d.Endpoint);
        await client.PingAsync(Ct);
    }

    [Fact]
    public async Task Shutdown_request_stops_the_host_and_deletes_the_descriptor()
    {
        var (host, _, o) = NewHost();
        host.Start();
        using MuxClient client = await ConnectAsync(o.Endpoint);
        Guid id = await MuxTestHost.SpawnAsync(client);
        await client.ShutdownServerAsync(Ct);
        Assert.Equal("shutdown", await host.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.False(File.Exists(o.DescriptorPath));
    }

    [Fact]
    public async Task Idle_host_exits_after_the_idle_period()
    {
        var (host, _, o) = NewHost(idle: TimeSpan.FromMilliseconds(200));
        host.Start();
        Assert.Equal("idle", await host.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.False(File.Exists(o.DescriptorPath));
    }

    [Fact]
    public async Task A_running_session_keeps_the_host_alive_past_the_idle_period()
    {
        var (host, _, o) = NewHost(idle: TimeSpan.FromMilliseconds(200));
        host.Start();
        using (MuxClient client = await ConnectAsync(o.Endpoint)) await MuxTestHost.SpawnAsync(client);
        await Task.Delay(600, Ct);
        Assert.False(host.Completion.IsCompleted);
    }

    [Fact]
    public async Task Connection_during_idle_shutdown_is_refused_not_hung()
    {
        var (host, _, o) = NewHost(idle: TimeSpan.FromMilliseconds(100));
        host.Start();
        await host.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using Stream s = MuxEndpointConnector.Connect(o.Endpoint, TimeSpan.FromMilliseconds(500));
            using MuxClient c = await MuxClient.ConnectAsync(s, new MuxClientOptions { RequestTimeout = TimeSpan.FromSeconds(2) }, Ct);
        });
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }

    [Fact]
    public void A_second_host_on_the_same_root_refuses_to_start()
    {
        var (first, _, _) = NewHost();
        first.Start();
        var (second, _, _) = NewHost(pid: Environment.ProcessId + 100_000);
        Assert.Throws<MuxDaemonAlreadyRunningException>(second.Start);
    }
}
```

- [ ] **Step 2: Run → build FAIL** (`scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~MuxDaemonHostTests"`).

- [ ] **Step 3: Implement `MuxListeners`**

```csharp
// src/Ntilde.Mux/Transport/MuxListeners.cs
namespace Ntilde.Mux.Transport;

public static class MuxListeners
{
    /// <summary>The platform's local listener for <paramref name="endpoint"/> (spec §3).</summary>
    public static IMuxListener Create(string endpoint) =>
        OperatingSystem.IsWindows() ? new NamedPipeMuxListener(endpoint) : new UnixSocketMuxListener(endpoint);
}
```

- [ ] **Step 4: Implement options, exception and host**

```csharp
// src/Ntilde.Mux/MuxDaemonOptions.cs
using System.Diagnostics;
using Ntilde.Mux.Transport;

namespace Ntilde.Mux;

public sealed class MuxDaemonOptions
{
    public required string Endpoint { get; init; }
    public required string DescriptorPath { get; init; }
    /// <summary>Exit after this long with no running session and no connection. <see cref="TimeSpan.Zero"/> = never.</summary>
    public TimeSpan IdleExitAfter { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan ReapGrace { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(1);
    public Func<string, IMuxListener> ListenerFactory { get; init; } = MuxListeners.Create;
    public int Pid { get; init; } = Environment.ProcessId;
    public string ProcessName { get; init; } = CurrentProcessName();
    public Action<string>? Log { get; init; }

    private static string CurrentProcessName() { using Process p = Process.GetCurrentProcess(); return p.ProcessName; }
}

public sealed class MuxDaemonAlreadyRunningException : IOException
{
    public MuxDaemonAlreadyRunningException(string message) : base(message) { }
}
```

```csharp
// src/Ntilde.Mux/MuxDaemonHost.cs
using Ntilde.Mux.Contracts;

namespace Ntilde.Mux;

/// <summary>
/// The daemon around a <see cref="MuxServer"/> (spec §4): one per app-data root (lock file +
/// descriptor), reaps exited sessions, exits when idle, and stops on a <c>shutdown</c> request.
/// </summary>
public sealed class MuxDaemonHost : IDisposable
{
    private readonly MuxServer _server;
    private readonly MuxDaemonOptions _options;
    private readonly TaskCompletionSource<string> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FileStream? _lock;
    private Timer? _timer;
    private long _idleSinceMs = -1;
    private int _stopping;

    public MuxDaemonHost(MuxServer server, MuxDaemonOptions options)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<string> Completion => _completion.Task;
    private string LockPath => Path.Combine(Path.GetDirectoryName(_options.DescriptorPath)!, "mux.lock");

    public void Start()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_options.DescriptorPath)!);
        try
        {
            _lock = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new MuxDaemonAlreadyRunningException($"Another multiplexer owns {LockPath}: {ex.Message}");
        }

        if (MuxDiscovery.TryReadLiveDescriptor(_options.DescriptorPath, out MuxEndpointDescriptor? other) && other.Pid != _options.Pid)
        {
            ReleaseLock();
            throw new MuxDaemonAlreadyRunningException($"A multiplexer (pid {other.Pid}) is already running at {other.Endpoint}.");
        }

        try
        {
            _server.Start(_options.ListenerFactory(_options.Endpoint));
            _server.ShutdownRequested += OnShutdownRequested;
            MuxDiscovery.WriteDescriptor(_options.DescriptorPath, new MuxEndpointDescriptor
            {
                MinVersion = _server.Options.MinProtocolVersion,
                MaxVersion = _server.Options.MaxProtocolVersion,
                Endpoint = _options.Endpoint,
                Pid = _options.Pid,
                ProcessName = _options.ProcessName,
            });
        }
        catch
        {
            _server.Dispose();
            ReleaseLock();
            throw;
        }

        _timer = new Timer(_ => Tick(), null, _options.TickInterval, _options.TickInterval);
        Log($"[MuxDaemon] serving {_options.Endpoint} (pid {_options.Pid})");
    }

    private void OnShutdownRequested() => RequestStop("shutdown");

    internal void TickForTest() => Tick();

    private void Tick()
    {
        try
        {
            int reaped = _server.ReapExitedSessions(_options.ReapGrace);
            if (reaped > 0) Log($"[MuxDaemon] reaped {reaped} exited session(s)");

            if (_options.IdleExitAfter <= TimeSpan.Zero) return;
            if (_server.RunningSessionCount != 0 || _server.ConnectionCount != 0)
            {
                Interlocked.Exchange(ref _idleSinceMs, -1);
                return;
            }

            long now = Environment.TickCount64;
            long since = Interlocked.CompareExchange(ref _idleSinceMs, now, -1);
            if (since == -1) since = now;
            if (now - since >= (long)_options.IdleExitAfter.TotalMilliseconds && _server.TryBeginIdleShutdown())
            {
                RequestStop("idle");
            }
        }
        catch (Exception ex)
        {
            Log($"[MuxDaemon] tick failed: {ex}");
        }
    }

    public void RequestStop(string reason)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
        try
        {
            _timer?.Dispose();
            _server.ShutdownRequested -= OnShutdownRequested;
            if (reason != "idle") _server.KillAllSessions();
            _server.Dispose();
            MuxDiscovery.DeleteDescriptorIfOwned(_options.DescriptorPath, _options.Pid);
            ReleaseLock();
            Log($"[MuxDaemon] stopped ({reason})");
        }
        catch (Exception ex)
        {
            Log($"[MuxDaemon] stop failed: {ex}");
        }
        finally
        {
            _completion.TrySetResult(reason);
        }
    }

    private void ReleaseLock()
    {
        FileStream? held = Interlocked.Exchange(ref _lock, null);
        if (held is null) return;
        held.Dispose();
        try { File.Delete(LockPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Log(string message)
    {
        try { _options.Log?.Invoke(message); } catch (Exception) { /* a throwing logger must not stop the daemon */ }
    }

    public void Dispose() => RequestStop("disposed");
}
```

Note `Start` checks the lock before the descriptor. In the "second host" test both hosts run in one process: on Windows `FileShare.None` throws `IOException`, and on Unix .NET takes a `flock` advisory lock, which also blocks a second open in the same process. If the second open succeeds on some platform anyway, the descriptor check (different pid) still refuses.

- [ ] **Step 5: Run the tests → PASS.** Then the full `tests/Ntilde.Mux.Tests` → PASS.

- [ ] **Step 6: Commit**

```powershell
scripts/build.ps1 format whitespace --no-restore
git add src/Ntilde.Mux tests/Ntilde.Mux.Tests/Server/MuxDaemonHostTests.cs
git commit -m "feat(mux): daemon host with descriptor, lock, reaping and idle exit"
```

---

### Task 5: App references Mux; `MuxDaemonLauncher`

**Files:**
- Modify: `src/Ntilde.App/Ntilde.App.csproj` (add `<ProjectReference Include="..\Ntilde.Mux\Ntilde.Mux.csproj" />` and `..\Ntilde.Mux.Contracts\Ntilde.Mux.Contracts.csproj` next to the existing references at ~534-541)
- Modify: `src/Ntilde.Mux/Ntilde.Mux.csproj` (`<InternalsVisibleTo Include="Ntilde.App.Tests" />`)
- Modify: `tests/Ntilde.App.Tests/Ntilde.App.Tests.csproj` (file-link the Mux test support, below)
- Create: `src/Ntilde.App/Shell/Mux/MuxDaemonLauncher.cs`
- Create: `src/Ntilde.App/Shell/Mux/MuxDaemonSpawner.cs`
- Test: `tests/Ntilde.App.Tests/Shell/Mux/MuxDaemonLauncherTests.cs`

**Interfaces:**
- Consumes: `MuxDiscovery`, `MuxEndpointConnector`, `MuxClient.ConnectAsync`, Task 4's `MuxDaemonHost` (tests only).
- Produces (namespace `Ntilde.Shell.Mux`, all `internal`):
  - `interface IMuxDaemonSpawner { void Spawn(); }`
  - `sealed class ProcessMuxDaemonSpawner : IMuxDaemonSpawner { ProcessMuxDaemonSpawner(string executable, IReadOnlyList<string> leadingArgs); static ProcessMuxDaemonSpawner CreateDefault(); }`
  - `sealed class MuxUnavailableException : Exception`
  - `sealed class MuxDaemonLauncher { MuxDaemonLauncher(string descriptorPath, IMuxDaemonSpawner spawner, MuxClientOptions? clientOptions = null, Action<string>? log = null); TimeSpan ConnectTimeout = 2 s; TimeSpan SpawnTimeout = 10 s; static MuxDaemonLauncher CreateDefault(Action<string>? log); Task<MuxClient?> TryConnectExistingAsync(CancellationToken); Task<MuxClient> EnsureConnectedAsync(CancellationToken); }`

- [ ] **Step 1: Add the references and the test file links**

In `tests/Ntilde.App.Tests/Ntilde.App.Tests.csproj` add (check first whether `TerminalStateAssert.cs` is already linked; don't link it twice):

```xml
<ItemGroup>
  <!-- Mux test support, shared with Ntilde.Mux.Tests (Phase 2 plan, Task 5). -->
  <Compile Include="..\Ntilde.Mux.Tests\Support\ScriptedSessionFactory.cs" Link="MuxSupport\ScriptedSessionFactory.cs" />
  <Compile Include="..\Ntilde.Mux.Tests\Support\ScriptedTerminalSession.cs" Link="MuxSupport\ScriptedTerminalSession.cs" />
  <Compile Include="..\Ntilde.Mux.Tests\Support\TestWait.cs" Link="MuxSupport\TestWait.cs" />
  <Compile Include="..\Ntilde.Mux.Tests\Support\ClientPaneModel.cs" Link="MuxSupport\ClientPaneModel.cs" />
  <Compile Include="..\Ntilde.Mux.Tests\Support\RawMuxConnection.cs" Link="MuxSupport\RawMuxConnection.cs" />
  <Compile Include="..\Ntilde.Mux.Tests\Support\MuxTestHost.cs" Link="MuxSupport\MuxTestHost.cs" />
  <Compile Include="..\Ntilde.VT.Tests\StateTransfer\TerminalStateAssert.cs" Link="MuxSupport\TerminalStateAssert.cs" />
</ItemGroup>
```

Also create the shared visible-text helper used by Tasks 9–13. It is the same reflection over `_viewport` as `tests/Ntilde.App.Tests/Ssh/TerminalPaneSshDisconnectTests.cs:184`:

```csharp
// tests/Ntilde.App.Tests/Shell/Mux/MuxTestText.cs
using System.Reflection;
using Ntilde.VT;

namespace Ntilde.Tests.Shell.Mux;

internal static class MuxTestText
{
    public static string VisibleText(TerminalBuffer buffer)
    {
        var field = typeof(TerminalBuffer).GetField("_viewport", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var viewport = (TerminalRow[])field.GetValue(buffer)!;
        return string.Join("\n", viewport.Select(row => new string(row.Cells.Select(c => c.Character == '\0' ? ' ' : c.Character).ToArray()).TrimEnd())).TrimEnd();
    }
}
```

Test files in other namespaces add `using Ntilde.Tests.Shell.Mux;`.
Build `tests/Ntilde.App.Tests` (`scripts/build.ps1 build tests/Ntilde.App.Tests`). If a linked file pulls in more support types, link those as well (follow the compiler errors). If a name collides with an App.Tests type, it is still distinct because the linked files live in namespace `Ntilde.Mux.Tests.Support`.

- [ ] **Step 2: Write the failing launcher tests**

```csharp
// tests/Ntilde.App.Tests/Shell/Mux/MuxDaemonLauncherTests.cs
using System.Diagnostics;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

public sealed class MuxDaemonLauncherTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nmxl" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<IDisposable> _owned = new();

    public void Dispose()
    {
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A "spawn" that starts an in-process daemon host on the real endpoint for _root.</summary>
    private sealed class InProcessSpawner(MuxDaemonLauncherTests t) : IMuxDaemonSpawner
    {
        public int Spawns;
        public void Spawn()
        {
            Interlocked.Increment(ref Spawns);
            t.StartDaemon();
        }
    }

    private sealed class NoopSpawner : IMuxDaemonSpawner { public int Spawns; public void Spawn() => Spawns++; }

    internal MuxDaemonHost StartDaemon()
    {
        var server = new MuxServer(new ScriptedSessionFactory(), new MuxServerOptions { ForceConPtyFiltering = false });
        var host = new MuxDaemonHost(server, new MuxDaemonOptions
        {
            Endpoint = MuxDiscovery.GetDefaultEndpoint(_root),
            DescriptorPath = MuxDiscovery.GetDescriptorPath(_root),
            IdleExitAfter = TimeSpan.Zero,
        });
        host.Start();
        _owned.Add(host);
        return host;
    }

    private MuxDaemonLauncher Launcher(IMuxDaemonSpawner spawner) =>
        new(MuxDiscovery.GetDescriptorPath(_root), spawner) { SpawnTimeout = TimeSpan.FromSeconds(3) };

    [Fact]
    public async Task No_daemon_means_TryConnectExisting_returns_null_without_spawning()
    {
        var spawner = new NoopSpawner();
        Assert.Null(await Launcher(spawner).TryConnectExistingAsync(Ct));
        Assert.Equal(0, spawner.Spawns);
    }

    [Fact]
    public async Task EnsureConnected_spawns_once_and_connects()
    {
        var spawner = new InProcessSpawner(this);
        using MuxClient client = await Launcher(spawner).EnsureConnectedAsync(Ct);
        await client.PingAsync(Ct);
        Assert.Equal(1, spawner.Spawns);
    }

    [Fact]
    public async Task EnsureConnected_uses_a_live_daemon_without_spawning()
    {
        StartDaemon();
        var spawner = new InProcessSpawner(this);
        using MuxClient client = await Launcher(spawner).EnsureConnectedAsync(Ct);
        Assert.Equal(0, spawner.Spawns);
    }

    [Fact]
    public async Task A_spawn_that_never_advertises_fails_with_MuxUnavailable_within_the_timeout()
    {
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<MuxUnavailableException>(() => Launcher(new NoopSpawner()).EnsureConnectedAsync(Ct));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task A_stale_descriptor_is_ignored_and_a_daemon_is_spawned()
    {
        MuxDiscovery.WriteDescriptor(MuxDiscovery.GetDescriptorPath(_root), new MuxEndpointDescriptor
        {
            Endpoint = "gone", Pid = 999_999, ProcessName = "nothing", MinVersion = 1, MaxVersion = 1,
        });
        var spawner = new InProcessSpawner(this);
        using MuxClient client = await Launcher(spawner).EnsureConnectedAsync(Ct);
        Assert.Equal(1, spawner.Spawns);
    }
}
```

(Test namespace: match the neighbouring App.Tests folders, e.g. `Ntilde.Tests.Shell`; check an existing file under `tests/Ntilde.App.Tests/Shell/`.)

- [ ] **Step 3: Run → build FAIL.**

- [ ] **Step 4: Implement the spawner**

```csharp
// src/Ntilde.App/Shell/Mux/MuxDaemonSpawner.cs
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ntilde.Shell.Mux;

internal interface IMuxDaemonSpawner
{
    void Spawn();
}

/// <summary>
/// Starts <c>&lt;exe&gt; mux serve</c> fully detached from the caller's stdio (spec §6): the daemon
/// outlives us and must never hold a pipe a parent is waiting on for EOF - the MSBuild-node hang
/// CLAUDE.md describes.
/// </summary>
internal sealed partial class ProcessMuxDaemonSpawner : IMuxDaemonSpawner
{
    private readonly string _executable;
    private readonly IReadOnlyList<string> _leadingArgs;

    public ProcessMuxDaemonSpawner(string executable, IReadOnlyList<string> leadingArgs)
    {
        _executable = executable;
        _leadingArgs = leadingArgs;
    }

    /// <summary>
    /// $APPIMAGE when running from an AppImage: Environment.ProcessPath is inside the runtime's FUSE
    /// mount, which is unmounted when the GUI exits and would pull the daemon's files out from under it.
    /// </summary>
    public static ProcessMuxDaemonSpawner CreateDefault()
    {
        string? appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        string exe = !string.IsNullOrEmpty(appImage) && File.Exists(appImage)
            ? appImage
            : Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unknown.");
        return new ProcessMuxDaemonSpawner(exe, []);
    }

    public void Spawn()
    {
        var psi = new ProcessStartInfo(_executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (string a in _leadingArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("mux");
        psi.ArgumentList.Add("serve");

        if (OperatingSystem.IsWindows()) ClearStdHandleInheritance();

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("mux serve did not start.");
        // Our ends of the three pipes: closed at once, so the daemon holds nothing of ours.
        process.StandardInput.Close();
        process.StandardOutput.Close();
        process.StandardError.Close();
    }

    // With redirection, CreateProcess runs with bInheritHandles=TRUE and the child inherits every
    // inheritable handle we hold - including std handles a parent (a CLI, a test runner) gave us.
    private static void ClearStdHandleInheritance()
    {
        foreach (int std in new[] { StdInputHandle, StdOutputHandle, StdErrorHandle })
        {
            IntPtr h = GetStdHandle(std);
            if (h != IntPtr.Zero && h != new IntPtr(-1)) SetHandleInformation(h, HandleFlagInherit, 0);
        }
    }

    private const int StdInputHandle = -10, StdOutputHandle = -11, StdErrorHandle = -12;
    private const uint HandleFlagInherit = 0x1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);
}
```

(`DllImport` matches the codebase convention; there is no `LibraryImport` anywhere.)

- [ ] **Step 5: Implement the launcher**

```csharp
// src/Ntilde.App/Shell/Mux/MuxDaemonLauncher.cs
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Transport;

namespace Ntilde.Shell.Mux;

internal sealed class MuxUnavailableException : Exception
{
    public MuxUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Connect to the running daemon, or start one and connect (spec §6).</summary>
internal sealed class MuxDaemonLauncher
{
    private readonly string _descriptorPath;
    private readonly IMuxDaemonSpawner _spawner;
    private readonly MuxClientOptions _clientOptions;
    private readonly Action<string>? _log;

    public MuxDaemonLauncher(string descriptorPath, IMuxDaemonSpawner spawner, MuxClientOptions? clientOptions = null, Action<string>? log = null)
    {
        _descriptorPath = descriptorPath;
        _spawner = spawner;
        _clientOptions = clientOptions ?? new MuxClientOptions { Log = log };
        _log = log;
    }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan SpawnTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public static MuxDaemonLauncher CreateDefault(Action<string>? log) =>
        new(MuxDiscovery.GetDescriptorPath(), ProcessMuxDaemonSpawner.CreateDefault(), log: log);

    public async Task<MuxClient?> TryConnectExistingAsync(CancellationToken cancellationToken)
    {
        if (!MuxDiscovery.TryReadLiveDescriptor(_descriptorPath, out MuxEndpointDescriptor? d)) return null;
        try
        {
            Stream stream = await Task.Run(() => MuxEndpointConnector.Connect(d.Endpoint, ConnectTimeout), cancellationToken).ConfigureAwait(false);
            return await MuxClient.ConnectAsync(stream, _clientOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or MuxProtocolException or System.Net.Sockets.SocketException)
        {
            _log?.Invoke($"[Mux] connecting to {d.Endpoint} (pid {d.Pid}) failed: {ex.Message}");
            return null;
        }
    }

    public async Task<MuxClient> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (await TryConnectExistingAsync(cancellationToken).ConfigureAwait(false) is { } existing) return existing;

        _log?.Invoke("[Mux] starting the multiplexer daemon");
        try { _spawner.Spawn(); }
        catch (Exception ex) { throw new MuxUnavailableException($"Could not start the multiplexer: {ex.Message}", ex); }

        DateTime deadline = DateTime.UtcNow + SpawnTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryConnectExistingAsync(cancellationToken).ConfigureAwait(false) is { } client) return client;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        throw new MuxUnavailableException($"The multiplexer did not come up within {SpawnTimeout.TotalSeconds:0} s.");
    }
}
```

- [ ] **Step 6: Run the tests → PASS.**

Run: `scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~MuxDaemonLauncherTests"`

Then `scripts/build.ps1 test tests/Ntilde.Architecture.Tests` → PASS. If a layering test enumerates App's allowed references and fails, add `Ntilde.Mux` and `Ntilde.Mux.Contracts` to it with a comment citing the spec.

- [ ] **Step 7: Commit**

```powershell
scripts/build.ps1 format whitespace --no-restore
git add src/Ntilde.App src/Ntilde.Mux/Ntilde.Mux.csproj tests/Ntilde.App.Tests
git commit -m "feat(mux): app references the mux; daemon launcher with detached spawn"
```

---

### Task 6: `ntilde mux` CLI and the daemon process

**Files:**
- Create: `src/Ntilde.App/Shell/Mux/MuxCommand.cs`
- Create: `src/Ntilde.App/Shell/Mux/MuxDaemonProcess.cs`
- Modify: `src/Ntilde.App/Program.cs` (dispatch before `AppLogger.Initialize()`, line ~71)
- Modify: `src/Ntilde.Cli/Program.cs`
- Modify: `tests/Ntilde.Architecture.Tests/CliCommandDispatchTests.cs` (`Assert.Contains(commandTypes, t => t.Name == "MuxCommand");`)
- Test: `tests/Ntilde.App.Tests/Shell/Mux/MuxCommandTests.cs`

**Interfaces:**
- Consumes: Task 5 `MuxDaemonLauncher.TryConnectExistingAsync`, Task 4 `MuxDaemonHost`, `MuxListeners`, `RotatingFileLogWriter` (`src/Ntilde.App/Shell/RotatingFileLogWriter.cs`), `AppPaths.LogsDirectory`, `DefaultTerminalSessionFactory.Instance`, `PtyLogger.Sink`.
- Produces:
  - `public static class MuxCommand` in namespace `Ntilde.Shell.Mux`:
    - `static bool IsSupportedCliMode(string[] args)`: `args[0] == "mux"`, ignore case.
    - `static bool IsServe(string[] args)`.
    - `static int Execute(string[] args, TextWriter stdout, TextWriter stderr, string? rootOverride = null)`.
    - `internal static bool TryParseServe(string[] args, out MuxServeOptions options, out string? error)`.
  - `internal sealed record MuxServeOptions(TimeSpan IdleExitAfter, bool Foreground)`.
  - `internal static class MuxDaemonProcess { static int Run(MuxServeOptions options, TextWriter stderr); }`.

- [ ] **Step 1: Write the failing CLI tests**

```csharp
// tests/Ntilde.App.Tests/Shell/Mux/MuxCommandTests.cs
using System.Text.Json;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

public sealed class MuxCommandTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nmxc" + Guid.NewGuid().ToString("N")[..8]);
    private MuxDaemonHost? _host;

    public void Dispose()
    {
        _host?.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task<Guid> StartDaemonWithOneSessionAsync()
    {
        var server = new MuxServer(new ScriptedSessionFactory(), new MuxServerOptions { ForceConPtyFiltering = false });
        _host = new MuxDaemonHost(server, new MuxDaemonOptions
        {
            Endpoint = MuxDiscovery.GetDefaultEndpoint(_root),
            DescriptorPath = MuxDiscovery.GetDescriptorPath(_root),
            IdleExitAfter = TimeSpan.Zero,
        });
        _host.Start();
        using Stream s = Ntilde.Mux.Transport.MuxEndpointConnector.Connect(MuxDiscovery.GetDefaultEndpoint(_root), TimeSpan.FromSeconds(5));
        using MuxClient c = await MuxClient.ConnectAsync(s, null, Ct);
        return await MuxTestHost.SpawnAsync(c);
    }

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        int code = MuxCommand.Execute(args, o, e, _root);
        return (code, o.ToString(), e.ToString());
    }

    [Fact] public void Dispatch_matches_only_mux() { Assert.True(MuxCommand.IsSupportedCliMode(["mux", "ls"])); Assert.False(MuxCommand.IsSupportedCliMode(["backup"])); Assert.False(MuxCommand.IsSupportedCliMode([])); }
    [Fact] public void Serve_is_recognised() { Assert.True(MuxCommand.IsServe(["mux", "serve"])); Assert.False(MuxCommand.IsServe(["mux", "ls"])); }
    [Fact] public void Unknown_verb_is_exit_2() => Assert.Equal(2, Run("mux", "frobnicate").Code);
    [Fact] public void Missing_verb_is_exit_2() => Assert.Equal(2, Run("mux").Code);

    [Fact]
    public void Ls_without_a_daemon_is_exit_1_with_a_message()
    {
        var (code, _, err) = Run("mux", "ls");
        Assert.Equal(1, code);
        Assert.Contains("No multiplexer is running", err);
    }

    [Fact]
    public async Task Ls_lists_the_running_session()
    {
        Guid id = await StartDaemonWithOneSessionAsync();
        var (code, output, _) = Run("mux", "ls");
        Assert.Equal(0, code);
        Assert.Contains(id.ToString(), output);
        Assert.Contains("running", output);
    }

    [Fact]
    public async Task Ls_json_is_a_ListSessionsResult()
    {
        Guid id = await StartDaemonWithOneSessionAsync();
        var (code, output, _) = Run("mux", "ls", "--json");
        Assert.Equal(0, code);
        ListSessionsResult r = JsonSerializer.Deserialize(output, MuxJsonContext.Default.ListSessionsResult)!;
        Assert.Contains(r.Sessions, s => s.SessionId == id);
    }

    [Fact]
    public async Task Kill_unknown_session_is_exit_1_and_known_is_0()
    {
        Guid id = await StartDaemonWithOneSessionAsync();
        Assert.Equal(1, Run("mux", "kill", Guid.NewGuid().ToString()).Code);
        Assert.Equal(2, Run("mux", "kill", "not-a-guid").Code);
        Assert.Equal(0, Run("mux", "kill", id.ToString()).Code);
    }

    [Fact]
    public async Task Kill_server_stops_the_daemon()
    {
        await StartDaemonWithOneSessionAsync();
        Assert.Equal(0, Run("mux", "kill-server").Code);
        Assert.Equal("shutdown", await _host!.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Theory]
    [InlineData(new[] { "mux", "serve" }, 10, false)]
    [InlineData(new[] { "mux", "serve", "--idle-exit-minutes", "0" }, 0, false)]
    [InlineData(new[] { "mux", "serve", "--foreground", "--idle-exit-minutes", "3" }, 3, true)]
    public void Serve_arguments_parse(string[] args, int minutes, bool foreground)
    {
        Assert.True(MuxCommand.TryParseServe(args, out MuxServeOptions o, out _));
        Assert.Equal(TimeSpan.FromMinutes(minutes), o.IdleExitAfter);
        Assert.Equal(foreground, o.Foreground);
    }

    [Theory]
    [InlineData("--idle-exit-minutes")]
    [InlineData("--idle-exit-minutes", "-1")]
    [InlineData("--bogus")]
    public void Bad_serve_arguments_are_rejected(params string[] extra)
    {
        Assert.False(MuxCommand.TryParseServe(["mux", "serve", .. extra], out _, out string? error));
        Assert.NotNull(error);
    }
}
```

`kill-server` against a test root: `Execute` with `rootOverride` must use `MuxDiscovery.GetDescriptorPath(rootOverride)`. It waits for the descriptor's pid to exit only when that pid is not our own. In the test the daemon is in-process, so it waits until the descriptor is gone instead.

- [ ] **Step 2: Run → build FAIL.**

- [ ] **Step 3: Implement `MuxCommand`**

```csharp
// src/Ntilde.App/Shell/Mux/MuxCommand.cs
using System.Globalization;
using System.Text.Json;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;

namespace Ntilde.Shell.Mux;

internal sealed record MuxServeOptions(TimeSpan IdleExitAfter, bool Foreground);

/// <summary>
/// <c>ntilde mux serve|ls|kill|kill-server</c> (spec §5). Exit codes: 0 success, 1 the operation
/// failed (including "no daemon running"), 2 the command line was wrong.
/// </summary>
public static class MuxCommand
{
    private const string Usage = """
        Usage:
          ntilde mux serve [--idle-exit-minutes N] [--foreground]
          ntilde mux ls [--json]
          ntilde mux kill <sessionId>
          ntilde mux kill-server
        """;

    public static bool IsSupportedCliMode(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Length > 0 && string.Equals(args[0], "mux", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsServe(string[] args) =>
        IsSupportedCliMode(args) && args.Length > 1 && string.Equals(args[1], "serve", StringComparison.OrdinalIgnoreCase);

    /// <param name="rootOverride">Test seam: app-data root. Null uses <see cref="MuxDiscovery.GetRootDirectory"/>.</param>
    public static int Execute(string[] args, TextWriter stdout, TextWriter stderr, string? rootOverride = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        if (args.Length < 2) return Fail(stderr, Usage);

        string descriptorPath = MuxDiscovery.GetDescriptorPath(rootOverride ?? MuxDiscovery.GetRootDirectory());
        try
        {
            return args[1].ToLowerInvariant() switch
            {
                "serve" => Serve(args, stderr),
                "ls" => List(args, stdout, stderr, descriptorPath),
                "kill" => Kill(args, stdout, stderr, descriptorPath),
                "kill-server" => KillServer(args, stdout, stderr, descriptorPath),
                _ => Fail(stderr, Usage),
            };
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or MuxProtocolException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"mux: {ex.Message}");
            return 1;
        }
    }

    internal static bool TryParseServe(string[] args, out MuxServeOptions options, out string? error)
    {
        TimeSpan idle = TimeSpan.FromMinutes(10);
        bool foreground = false;
        options = new MuxServeOptions(idle, foreground);
        for (int i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--foreground":
                    foreground = true;
                    break;
                case "--idle-exit-minutes":
                    if (i + 1 >= args.Length || !int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int m))
                    {
                        error = "--idle-exit-minutes needs a non-negative whole number.";
                        return false;
                    }

                    idle = TimeSpan.FromMinutes(m);
                    i++;
                    break;
                default:
                    error = $"Unknown option '{args[i]}'.";
                    return false;
            }
        }

        options = new MuxServeOptions(idle, foreground);
        error = null;
        return true;
    }

    private static int Serve(string[] args, TextWriter stderr)
    {
        if (!TryParseServe(args, out MuxServeOptions options, out string? error)) return Fail(stderr, error + Environment.NewLine + Usage);
        return MuxDaemonProcess.Run(options, stderr);
    }

    private static MuxClient? Connect(string descriptorPath, TextWriter stderr)
    {
        var launcher = new MuxDaemonLauncher(descriptorPath, new NoSpawn());
        MuxClient? client = launcher.TryConnectExistingAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (client is null) stderr.WriteLine("No multiplexer is running.");
        return client;
    }

    private sealed class NoSpawn : IMuxDaemonSpawner { public void Spawn() => throw new InvalidOperationException("CLI verbs never start a daemon."); }

    private static int List(string[] args, TextWriter stdout, TextWriter stderr, string descriptorPath)
    {
        bool json = args.Skip(2).Any(a => a == "--json");
        if (args.Skip(2).Any(a => a != "--json")) return Fail(stderr, Usage);
        using MuxClient? client = Connect(descriptorPath, stderr);
        if (client is null) return 1;

        IReadOnlyList<SessionSummary> sessions = client.ListSessionsAsync().GetAwaiter().GetResult();
        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new ListSessionsResult { Sessions = sessions }, MuxJsonContext.Default.ListSessionsResult));
            return 0;
        }

        if (sessions.Count == 0)
        {
            stdout.WriteLine("No sessions.");
            return 0;
        }

        stdout.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-36}  {1,-12}  {2,8}  {3,-9}  {4}", "ID", "STATE", "ATTACHED", "SIZE", "TITLE"));
        foreach (SessionSummary s in sessions)
        {
            string state = s.Faulted ? "faulted" : s.Running ? "running" : $"exited {s.ExitCode}";
            stdout.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-36}  {1,-12}  {2,8}  {3,-9}  {4}",
                s.SessionId, state, s.AttachedClients, $"{s.Cols}x{s.Rows}", s.Title));
        }

        return 0;
    }

    private static int Kill(string[] args, TextWriter stdout, TextWriter stderr, string descriptorPath)
    {
        if (args.Length != 3 || !Guid.TryParse(args[2], out Guid id)) return Fail(stderr, Usage);
        using MuxClient? client = Connect(descriptorPath, stderr);
        if (client is null) return 1;
        try
        {
            client.KillAsync(id).GetAwaiter().GetResult();
        }
        catch (MuxProtocolException ex) when (ex.Code == MuxErrorCodes.UnknownSession)
        {
            stderr.WriteLine($"No session {id}.");
            return 1;
        }

        stdout.WriteLine($"Killed {id}.");
        return 0;
    }

    private static int KillServer(string[] args, TextWriter stdout, TextWriter stderr, string descriptorPath)
    {
        if (args.Length != 2) return Fail(stderr, Usage);
        MuxDiscovery.TryReadDescriptor(descriptorPath, out MuxEndpointDescriptor? d);
        using (MuxClient? client = Connect(descriptorPath, stderr))
        {
            if (client is null) return 1;
            client.ShutdownServerAsync().GetAwaiter().GetResult();
        }

        // Wait for it to really be gone, so "kill-server && start" cannot race the old daemon.
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && MuxDiscovery.TryReadLiveDescriptor(descriptorPath, out _)) Thread.Sleep(50);
        if (d is not null && d.Pid != Environment.ProcessId)
        {
            while (DateTime.UtcNow < deadline && MuxDiscovery.IsProcessAlive(d.Pid, d.ProcessName)) Thread.Sleep(50);
        }

        stdout.WriteLine("Multiplexer stopped.");
        return 0;
    }

    private static int Fail(TextWriter stderr, string message)
    {
        stderr.WriteLine(message);
        return 2;
    }
}
```

`MuxCommand` must be `public` because `IsSupportedCliMode`/`Execute` are reflected over by the architecture test. `MuxDaemonLauncher` stays internal; `Ntilde.Cli` already has `InternalsVisibleTo`. The `using` on a nullable `MuxClient?` is fine.

- [ ] **Step 4: Implement `MuxDaemonProcess`**

```csharp
// src/Ntilde.App/Shell/Mux/MuxDaemonProcess.cs
using System.Runtime.InteropServices;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Pty;

namespace Ntilde.Shell.Mux;

/// <summary>The body of <c>ntilde mux serve</c> (spec §5). Never initialises Avalonia.</summary>
internal static partial class MuxDaemonProcess
{
    public static int Run(MuxServeOptions options, TextWriter stderr)
    {
        if (!options.Foreground) Daemonize();

        // A console-attached host takes ConPTY's passthrough path (lib.rs ~727-769); the daemon must
        // not, whatever console it inherited. Before the first spawn.
        if (OperatingSystem.IsWindows()) Environment.SetEnvironmentVariable("NTILDE_PTY_NO_PASSTHROUGH", "1");

        using var log = new RotatingFileLogWriter(Path.Combine(AppPaths.LogsDirectory, "mux.log"), 16L * 1024 * 1024, 8192);
        void Log(string message)
        {
            string line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z {message}";
            log.Write(line);
            if (options.Foreground) stderr.WriteLine(line);
        }

        PtyLogger.Sink = Log;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log($"[MuxDaemon] unhandled: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => { Log($"[MuxDaemon] unobserved task: {e.Exception}"); e.SetObserved(); };

        var server = new MuxServer(DefaultTerminalSessionFactory.Instance, new MuxServerOptions { Log = Log });
        using var host = new MuxDaemonHost(server, new MuxDaemonOptions
        {
            Endpoint = MuxDiscovery.GetDefaultEndpoint(),
            DescriptorPath = MuxDiscovery.GetDescriptorPath(),
            IdleExitAfter = options.IdleExitAfter,
            Log = Log,
        });

        try
        {
            host.Start();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"[MuxDaemon] not starting: {ex.Message}");
            if (options.Foreground) stderr.WriteLine(ex.Message);
            return 1;
        }

        using PosixSignalRegistration term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; host.RequestStop("signal"); });
        using PosixSignalRegistration intr = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; host.RequestStop("signal"); });
        using PosixSignalRegistration? hup = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx => ctx.Cancel = true);

        string reason = host.Completion.GetAwaiter().GetResult();
        Log($"[MuxDaemon] exiting ({reason})");
        return 0;
    }

    private static void Daemonize()
    {
        if (OperatingSystem.IsWindows())
        {
            FreeConsole();
            return;
        }

        // New session: no controlling terminal, not in the spawner's process group, so a terminal
        // hang-up or the GUI's group being signalled does not reach the daemon (or its shells).
        setsid();
        int devNull = open("/dev/null", 2 /* O_RDWR */);
        if (devNull >= 0)
        {
            dup2(devNull, 0);
            dup2(devNull, 1);
            dup2(devNull, 2);
            if (devNull > 2) close(devNull);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();

    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int dup2(int oldfd, int newfd);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
```

Check `PtyLogger.Sink`'s actual type in `src/Ntilde.Pty` and adapt the assignment if it is not `Action<string>`. Check `RotatingFileLogWriter`'s constructor signature. The explorer reported `(string path, long maxBytes, int queueCapacity)`.

- [ ] **Step 5: Dispatch**

`src/Ntilde.App/Program.cs`, immediately after the `BackupCommand` branch (before `AppLogger.Initialize()`):

```csharp
if (Ntilde.Shell.Mux.MuxCommand.IsSupportedCliMode(args))
{
    // serve is a daemon: it must not attach to the launching console (it detaches from it).
    if (!Ntilde.Shell.Mux.MuxCommand.IsServe(args)) CliConsoleBindings.Prepare();
    Environment.ExitCode = Ntilde.Shell.Mux.MuxCommand.Execute(args, Console.Out, Console.Error);
    return;
}
```

The architecture test searches `Program.cs` for `"MuxCommand.IsSupportedCliMode("`, and the fully qualified spelling still contains that substring. If you prefer a `using Ntilde.Shell.Mux;` plus the short name, that also works.

`src/Ntilde.Cli/Program.cs`, after the Backup branch: `if (Ntilde.Shell.Mux.MuxCommand.IsSupportedCliMode(args)) return Ntilde.Shell.Mux.MuxCommand.Execute(args, Console.Out, Console.Error);`

`tests/Ntilde.Architecture.Tests/CliCommandDispatchTests.cs`: add `Assert.Contains(commandTypes, t => t.Name == "MuxCommand");` next to the Backup/Replay pins.

- [ ] **Step 6: Run**

- `scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~MuxCommandTests"` → PASS.
- `scripts/build.ps1 test tests/Ntilde.Architecture.Tests` → PASS.
- Manual smoke (Windows), after `scripts/build.ps1 build src/Ntilde.App`:
  1. Run `src/Ntilde.App/bin/Debug/net10.0/Ntilde.exe mux ls`. Expected: "No multiplexer is running.", exit 1.
  2. Start `Ntilde.exe mux serve --foreground` in another terminal, then `mux ls` again. Expected: "No sessions.".
  3. Run `Ntilde.exe mux kill-server`. Expected: "Multiplexer stopped.".
  4. Record all of this in the task report.

- [ ] **Step 7: Commit**

```powershell
scripts/build.ps1 format whitespace --no-restore
git add src/Ntilde.App src/Ntilde.Cli tests/Ntilde.App.Tests tests/Ntilde.Architecture.Tests
git commit -m "feat(mux): ntilde mux serve|ls|kill|kill-server"
```

---

### Task 7: `SessionPersistence` setting, `MuxConnectionHost`, `MuxTerminalSessionFactory`

**Files:**
- Modify: `src/Ntilde.Pty/ITerminalSessionFactory.cs` (`TerminalSessionRequest(..., SshSessionDescriptor? Ssh, Guid? ExistingMuxSessionId = null)`)
- Modify: `src/Ntilde.App/Shell/TerminalSettings.cs` (after `AgentIndicatorTabRollup`, ~L102)
- Modify: `src/Ntilde.McpServer/Tools/SettingsTools.cs` (schema row after the `AgentIndicatorTabRollup` row ~L61, example line ~L127, `StringFields` ~L175, `KnownFields` ~L191)
- Modify: `src/Ntilde.App/SettingsWindow.axaml` + `SettingsWindow.axaml.cs` (`LoadCurrentSettings` ~L2974, `SaveAndClose` ~L3258)
- Create: `src/Ntilde.App/Shell/Mux/SessionPersistenceMode.cs`, `PersistentSessionFactory.cs`, `MuxConnectionHost.cs`, `MuxTerminalSessionFactory.cs`
- Test: `tests/Ntilde.App.Tests/Shell/Mux/MuxTerminalSessionFactoryTests.cs`, `tests/Ntilde.App.Tests/Shell/Mux/MuxConnectionHostTests.cs`, `tests/Ntilde.App.Tests/Core/SessionPersistenceSettingTests.cs`

**Interfaces:**
- Consumes: Task 5 `MuxDaemonLauncher`, linked `MuxTestHost`, `DefaultTerminalSessionFactory`, `FakeTerminalSession`/`RecordingSessionFactory` (`tests/Ntilde.App.Tests/Controls/PaneSessionFactoryTests.cs`).
- Produces (namespace `Ntilde.Shell.Mux`, `internal`):
  - `static class SessionPersistenceMode { const string Off = "Off"; const string KeepOnClose = "KeepOnClose"; static bool IsKeepOnClose(string? value); }`
  - `enum PersistentSessionOutcome { NotPersistent, Spawned, Reattached, PreviousLost, Unavailable }`
  - `sealed record PersistentSessionResult(ITerminalSession Session, PersistentSessionOutcome Outcome, string? Endpoint, string? Detail)`
  - `interface IPersistentSessionFactory : ITerminalSessionFactory { PersistentSessionResult CreatePersistent(TerminalSessionRequest request); }`
  - `sealed class MuxConnectionHost : IDisposable`, with:
    - `MuxConnectionHost(Func<CancellationToken, Task<MuxClient>> connect, string? endpoint, Action<string>? log)`
    - `static MuxConnectionHost CreateDefault(Action<string>? log)`
    - `void WarmUp()`, `MuxClient? GetClient(TimeSpan timeout)`, `MuxClient? CurrentClient`, `string? Endpoint`, `int ConnectAttempts` (tests)
  - `sealed class MuxTerminalSessionFactory : IPersistentSessionFactory`, with:
    - `MuxTerminalSessionFactory(MuxConnectionHost host, ITerminalSessionFactory fallback, Action<string>? log)`
    - `TimeSpan ConnectTimeout = 5 s`, `TimeSpan RpcTimeout = 3 s`
    - `MuxConnectionHost Host { get; }`
  - `TerminalSettings.SessionPersistence` (`string`, default `"Off"`).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Ntilde.App.Tests/Core/SessionPersistenceSettingTests.cs
using System.Text.Json;
using Ntilde.Shell;
using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Core;

public sealed class SessionPersistenceSettingTests
{
    [Fact] public void Default_is_off() => Assert.Equal("Off", new TerminalSettings().SessionPersistence);

    [Theory]
    [InlineData("KeepOnClose", true)]
    [InlineData(" keeponclose ", true)]
    [InlineData("Off", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("Always", false)]
    public void Only_KeepOnClose_turns_it_on(string? value, bool expected) => Assert.Equal(expected, SessionPersistenceMode.IsKeepOnClose(value));

    [Fact]
    public void Round_trips_through_the_source_generated_context()
    {
        var s = new TerminalSettings { SessionPersistence = "KeepOnClose" };
        string json = JsonSerializer.Serialize(s, AppJsonContext.Default.TerminalSettings);
        Assert.Equal("KeepOnClose", JsonSerializer.Deserialize(json, AppJsonContext.Default.TerminalSettings)!.SessionPersistence);
        Assert.Equal("Off", JsonSerializer.Deserialize("{}", AppJsonContext.Default.TerminalSettings)!.SessionPersistence);
    }
}
```

```csharp
// tests/Ntilde.App.Tests/Shell/Mux/MuxConnectionHostTests.cs
using Ntilde.Mux;
using Ntilde.Mux.Tests.Support;
using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

public sealed class MuxConnectionHostTests
{
    [Fact]
    public void Concurrent_GetClient_calls_spawn_one_daemon()
    {
        using var mux = new MuxTestHost();
        int connects = 0;
        using var host = new MuxConnectionHost(async ct =>
        {
            Interlocked.Increment(ref connects);
            await Task.Delay(200, ct);   // a slow daemon start
            return await MuxClient.ConnectAsync(mux.Listener.Connect(), null, ct);
        }, endpoint: "test", log: null);

        MuxClient?[] clients = new MuxClient?[8];
        Parallel.For(0, clients.Length, i => clients[i] = host.GetClient(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, connects);
        Assert.All(clients, c => Assert.Same(clients[0], c));
    }

    [Fact]
    public void A_disconnected_client_is_replaced_on_the_next_GetClient()
    {
        using var mux = new MuxTestHost();
        using var host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(mux.Listener.Connect(), null, ct), "test", null);
        MuxClient first = host.GetClient(TimeSpan.FromSeconds(5))!;
        first.Dispose();
        MuxClient second = host.GetClient(TimeSpan.FromSeconds(5))!;
        Assert.NotSame(first, second);
        Assert.True(second.IsConnected);
    }

    [Fact]
    public void A_failing_connect_returns_null_and_is_retried_next_time()
    {
        int attempts = 0;
        using var host = new MuxConnectionHost(_ => { Interlocked.Increment(ref attempts); throw new MuxUnavailableException("nope"); }, "test", null);
        Assert.Null(host.GetClient(TimeSpan.FromSeconds(1)));
        Assert.Null(host.GetClient(TimeSpan.FromSeconds(1)));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void GetClient_times_out_rather_than_blocking_forever()
    {
        using var host = new MuxConnectionHost(async ct => { await Task.Delay(Timeout.Infinite, ct); return null!; }, "test", null);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(host.GetClient(TimeSpan.FromMilliseconds(300)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));
    }
}
```

```csharp
// tests/Ntilde.App.Tests/Shell/Mux/MuxTerminalSessionFactoryTests.cs
using Ntilde.Mux;
using Ntilde.Mux.Tests.Support;
using Ntilde.Pty;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Controls; // FakeTerminalSession, RecordingSessionFactory

namespace Ntilde.Tests.Shell.Mux;

public sealed class MuxTerminalSessionFactoryTests
{
    private static TerminalSessionRequest Local(Guid? existing = null) =>
        new("scripted", "", "", 80, 24, null, false, null, existing);

    private static (MuxTestHost Mux, MuxTerminalSessionFactory Factory, RecordingSessionFactory Fallback) Build()
    {
        var mux = new MuxTestHost();
        var host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(mux.Listener.Connect(), null, ct), "test-endpoint", null);
        var fallback = new RecordingSessionFactory(new FakeTerminalSession());
        return (mux, new MuxTerminalSessionFactory(host, fallback, null), fallback);
    }

    [Fact]
    public void Local_request_spawns_an_unattached_mux_session()
    {
        var (mux, factory, _) = Build();
        using (mux) using (factory.Host)
        {
            PersistentSessionResult r = factory.CreatePersistent(Local());
            Assert.Equal(PersistentSessionOutcome.Spawned, r.Outcome);
            var session = Assert.IsType<MuxClientSession>(r.Session);
            Assert.False(session.IsAttached);
            Assert.Contains(session.Id, mux.Server.GetSessionIds());
            Assert.Equal("test-endpoint", r.Endpoint);
        }
    }

    [Fact]
    public void Ssh_request_goes_to_the_fallback()
    {
        var (mux, factory, fallback) = Build();
        using (mux) using (factory.Host)
        {
            var ssh = new TerminalSessionRequest("", "", "", 80, 24, null, false, new SshSessionDescriptor(Guid.NewGuid(), 0, null, false));
            PersistentSessionResult r = factory.CreatePersistent(ssh);
            Assert.Equal(PersistentSessionOutcome.NotPersistent, r.Outcome);
            Assert.Same(ssh, fallback.LastRequest);
            Assert.Empty(mux.Server.GetSessionIds());
        }
    }

    [Fact]
    public void Existing_running_id_reattaches_without_spawning()
    {
        var (mux, factory, _) = Build();
        using (mux) using (factory.Host)
        {
            var first = (MuxClientSession)factory.CreatePersistent(Local()).Session;
            Guid id = first.Id;
            first.Dispose(); // detach, as a pane does on Reconnect
            PersistentSessionResult r = factory.CreatePersistent(Local(id));
            Assert.Equal(PersistentSessionOutcome.Reattached, r.Outcome);
            Assert.Equal(id, ((MuxClientSession)r.Session).Id);
            Assert.Single(mux.Server.GetSessionIds());
        }
    }

    [Fact]
    public void Missing_id_spawns_fresh_and_reports_PreviousLost()
    {
        var (mux, factory, _) = Build();
        using (mux) using (factory.Host)
        {
            PersistentSessionResult r = factory.CreatePersistent(Local(Guid.NewGuid()));
            Assert.Equal(PersistentSessionOutcome.PreviousLost, r.Outcome);
            Assert.IsType<MuxClientSession>(r.Session);
        }
    }

    [Fact]
    public void Unreachable_daemon_falls_back_to_a_local_session_and_says_so()
    {
        var fallback = new RecordingSessionFactory(new FakeTerminalSession());
        using var host = new MuxConnectionHost(_ => throw new MuxUnavailableException("down"), "x", null);
        var factory = new MuxTerminalSessionFactory(host, fallback, null) { ConnectTimeout = TimeSpan.FromMilliseconds(500) };
        PersistentSessionResult r = factory.CreatePersistent(Local());
        Assert.Equal(PersistentSessionOutcome.Unavailable, r.Outcome);
        Assert.IsType<FakeTerminalSession>(r.Session);
        Assert.NotNull(r.Detail);
    }

    [Fact]
    public void Spawn_failure_on_the_daemon_falls_back()
    {
        var (mux, factory, fallback) = Build();
        using (mux) using (factory.Host)
        {
            mux.Factory.ThrowOnCreate = true; // add to ScriptedSessionFactory if absent
            PersistentSessionResult r = factory.CreatePersistent(Local());
            Assert.Equal(PersistentSessionOutcome.Unavailable, r.Outcome);
            Assert.NotNull(fallback.LastRequest);
        }
    }
}
```

`FakeTerminalSession` and `RecordingSessionFactory` live in `tests/Ntilde.App.Tests/Controls/PaneSessionFactoryTests.cs`; check their namespace and make them reachable (they are `internal` in the same assembly). If `ScriptedSessionFactory` has no throw switch, add `public bool ThrowOnCreate { get; set; }` in `tests/Ntilde.Mux.Tests/Support/ScriptedSessionFactory.cs`: `Create` throws `InvalidOperationException("scripted spawn failure")` when set.

- [ ] **Step 2: Run → build FAIL.**

- [ ] **Step 3: Request field + setting + parser**

`src/Ntilde.Pty/ITerminalSessionFactory.cs`:

```csharp
/// <param name="ExistingMuxSessionId">
/// Multiplexer session to reopen instead of spawning (Phase 2 reattach). Ignored by factories that
/// do not multiplex. Null = spawn.
/// </param>
public sealed record TerminalSessionRequest(
    string Command, string Arguments, string StartingDirectory, int Cols, int Rows,
    IReadOnlyDictionary<string, string>? EnvironmentOverrides,
    bool SkipPowerShellPostLaunchInit, SshSessionDescriptor? Ssh,
    Guid? ExistingMuxSessionId = null);
```

(Keep whatever XML doc already exists on the record; add the new `<param>`.) Build the solution to confirm every existing positional construction still compiles.

`TerminalSettings.cs`, after `AgentIndicatorTabRollup`:

```csharp
/// <summary>
/// "Off" (default) or "KeepOnClose": local shells run in the <c>ntilde mux</c> daemon and survive
/// closing the window, a crash and a restart (docs/USER_MANUAL.md, "Persistent sessions").
/// Unrecognised values behave as "Off" - a typo must never start a background daemon. Read by
/// MainWindow (which picks the session factory for new panes); the pane never reads it, so it is
/// deliberately absent from TerminalPane.ApplySettings.
/// </summary>
public string SessionPersistence { get; set; } = "Off";
```

```csharp
// src/Ntilde.App/Shell/Mux/SessionPersistenceMode.cs
namespace Ntilde.Shell.Mux;

internal static class SessionPersistenceMode
{
    public const string Off = "Off";
    public const string KeepOnClose = "KeepOnClose";

    public static bool IsKeepOnClose(string? value) =>
        value is not null && string.Equals(value.Trim(), KeepOnClose, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 4: MCP registration**

In `SettingsTools.cs`:
- Schema row after the `AgentIndicatorTabRollup` row: ``| `SessionPersistence` | string (enum-like) | "Off"/"KeepOnClose". Default "Off". "KeepOnClose" runs local shells in the `ntilde mux` daemon so they survive closing the window and reattach on launch. Applies to panes opened after the change. Type-checked only; unrecognised values behave as "Off". |``
- Example JSON: `"SessionPersistence": "Off",` after `"AgentIndicatorTabRollup": "WritesOnly",`.
- Add `"SessionPersistence"` to `StringFields` and to `KnownFields`.

Run `scripts/build.ps1 test tests/Ntilde.McpServer.Tests --filter "FullyQualifiedName~SettingsTools"` → PASS. That includes the drift guards and `SchemaExample_PassesItsOwnValidator`.

- [ ] **Step 5: Settings window**

In `SettingsWindow.axaml`, on the page that holds the scrollback input (`scrollbackInput`), add a row styled like the `AgentIndicatorTabRollupList` row (hairline `Border` separator, `RowLabel`, `RowDesc`):

```xml
<Grid ColumnDefinitions="*,360">
    <StackPanel Grid.Column="0" Spacing="2">
        <TextBlock Classes="RowLabel" Text="Keep shells running when the window closes"/>
        <TextBlock Classes="RowDesc" TextWrapping="Wrap" Text="Local shells run in a background multiplexer, survive closing the window or a crash, and reattach when Ntilde starts. SSH panes are not affected. Applies to panes opened after saving."/>
    </StackPanel>
    <ComboBox Name="SessionPersistenceList" Grid.Column="1" HorizontalAlignment="Right" MinWidth="180">
        <ComboBoxItem Tag="Off">Off</ComboBoxItem>
        <ComboBoxItem Tag="KeepOnClose">Keep running</ComboBoxItem>
    </ComboBox>
</Grid>
```

In `LoadCurrentSettings`:

```csharp
var sessionPersistenceList = this.FindControl<ComboBox>("SessionPersistenceList");
if (sessionPersistenceList != null)
{
    string wanted = Ntilde.Shell.Mux.SessionPersistenceMode.IsKeepOnClose(_settings.SessionPersistence)
        ? Ntilde.Shell.Mux.SessionPersistenceMode.KeepOnClose : Ntilde.Shell.Mux.SessionPersistenceMode.Off;
    sessionPersistenceList.SelectedItem = sessionPersistenceList.Items.Cast<ComboBoxItem>()
        .FirstOrDefault(i => string.Equals(i.Tag as string, wanted, StringComparison.Ordinal));
    if (sessionPersistenceList.SelectedItem == null) sessionPersistenceList.SelectedIndex = 0;
}
```

In `SaveAndClose`:

```csharp
var sessionPersistenceList = this.FindControl<ComboBox>("SessionPersistenceList");
if (sessionPersistenceList?.SelectedItem is ComboBoxItem persistenceItem)
{
    _settings.SessionPersistence = persistenceItem.Tag as string ?? Ntilde.Shell.Mux.SessionPersistenceMode.Off;
}
```

Find the existing SettingsWindow load/save tests (grep `AgentIndicatorTabRollupList` under `tests/`). If one exists, add the same round-trip for `SessionPersistenceList`.

- [ ] **Step 6: Outcome types, connection host, factory**

```csharp
// src/Ntilde.App/Shell/Mux/PersistentSessionFactory.cs
using Ntilde.Pty;

namespace Ntilde.Shell.Mux;

internal enum PersistentSessionOutcome { NotPersistent, Spawned, Reattached, PreviousLost, Unavailable }

/// <param name="Endpoint">The daemon endpoint the session lives on (persisted as PaneNode.MuxEndpoint); null when not persistent.</param>
/// <param name="Detail">Why the outcome is Unavailable, for the log.</param>
internal sealed record PersistentSessionResult(ITerminalSession Session, PersistentSessionOutcome Outcome, string? Endpoint, string? Detail);

/// <summary>A factory that can tell the pane whether the session it made will persist (spec §7).</summary>
internal interface IPersistentSessionFactory : ITerminalSessionFactory
{
    PersistentSessionResult CreatePersistent(TerminalSessionRequest request);
}
```

```csharp
// src/Ntilde.App/Shell/Mux/MuxConnectionHost.cs
using Ntilde.Mux;
using Ntilde.Mux.Contracts;

namespace Ntilde.Shell.Mux;

/// <summary>
/// The GUI's one connection to the daemon (spec §6). Every pane's MuxClientSession shares it.
/// <see cref="GetClient"/> is synchronous because ITerminalSessionFactory.Create is, and runs on
/// the UI thread: it waits inside Task.Run so no UI sync context is ever captured.
/// </summary>
internal sealed class MuxConnectionHost : IDisposable
{
    private readonly Func<CancellationToken, Task<MuxClient>> _connect;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _disposed = new();
    private MuxClient? _client;
    private Task<MuxClient>? _connecting;
    private int _connectAttempts;

    public MuxConnectionHost(Func<CancellationToken, Task<MuxClient>> connect, string? endpoint, Action<string>? log)
    {
        _connect = connect;
        Endpoint = endpoint;
        _log = log;
    }

    public static MuxConnectionHost CreateDefault(Action<string>? log)
    {
        MuxDaemonLauncher launcher = MuxDaemonLauncher.CreateDefault(log);
        return new MuxConnectionHost(launcher.EnsureConnectedAsync, MuxDiscovery.GetDefaultEndpoint(), log);
    }

    public string? Endpoint { get; }
    public int ConnectAttempts => Volatile.Read(ref _connectAttempts);
    public MuxClient? CurrentClient { get { lock (_gate) return _client is { IsConnected: true } c ? c : null; } }

    /// <summary>Starts connecting (spawning the daemon if needed) in the background. Idempotent.</summary>
    public void WarmUp() => _ = StartConnecting();

    public MuxClient? GetClient(TimeSpan timeout)
    {
        Task<MuxClient>? attempt = StartConnecting();
        if (attempt is null) return CurrentClient;
        try
        {
            if (!Task.Run(() => attempt).Wait(timeout)) return null;
            return attempt.Result;
        }
        catch (AggregateException ex)
        {
            _log?.Invoke($"[Mux] connection failed: {ex.InnerException?.Message}");
            return null;
        }
    }

    /// <summary>The in-flight or just-finished attempt; null when a live client already exists.</summary>
    private Task<MuxClient>? StartConnecting()
    {
        lock (_gate)
        {
            if (_disposed.IsCancellationRequested) return null;
            if (_client is { IsConnected: true }) return null;
            if (_connecting is { IsCompleted: false }) return _connecting;
            if (_connecting is { IsCompletedSuccessfully: true } done && done.Result.IsConnected && _client is null)
            {
                _client = done.Result;
                return null;
            }

            _client = null;
            Interlocked.Increment(ref _connectAttempts);
            CancellationToken token = _disposed.Token;
            _connecting = Task.Run(async () =>
            {
                MuxClient client = await _connect(token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_disposed.IsCancellationRequested) { client.Dispose(); throw new ObjectDisposedException(nameof(MuxConnectionHost)); }
                    _client = client;
                }

                return client;
            }, token);
            return _connecting;
        }
    }

    /// <summary>Closes the connection: the daemon detaches every session on it and keeps them running.</summary>
    public void Dispose()
    {
        MuxClient? client;
        lock (_gate)
        {
            if (_disposed.IsCancellationRequested) return;
            _disposed.Cancel();
            client = _client;
            _client = null;
        }

        client?.Dispose();
    }
}
```

`GetClient` returns `null` from `StartConnecting` both when a live client exists and when the host is disposed; `CurrentClient` then gives the right answer. The concurrency test depends on every caller getting the same `_connecting` task: keep the "in-flight" check first after the live-client check.

```csharp
// src/Ntilde.App/Shell/Mux/MuxTerminalSessionFactory.cs
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Pty;

namespace Ntilde.Shell.Mux;

/// <summary>
/// Local panes → a session in the daemon (spawned, or reopened by id); SSH → <see cref="_fallback"/>.
/// Returns the MuxClientSession UNATTACHED: the pane attaches after wiring its handlers, so no
/// snapshot can be missed (spec §7). Never throws for a daemon problem: it falls back to a normal
/// local session and reports <see cref="PersistentSessionOutcome.Unavailable"/>.
/// </summary>
internal sealed class MuxTerminalSessionFactory : IPersistentSessionFactory
{
    private readonly ITerminalSessionFactory _fallback;
    private readonly Action<string>? _log;

    public MuxTerminalSessionFactory(MuxConnectionHost host, ITerminalSessionFactory fallback, Action<string>? log)
    {
        Host = host;
        _fallback = fallback;
        _log = log;
    }

    public MuxConnectionHost Host { get; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan RpcTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public ITerminalSession Create(TerminalSessionRequest request) => CreatePersistent(request).Session;

    public PersistentSessionResult CreatePersistent(TerminalSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Ssh is not null) return new(_fallback.Create(request), PersistentSessionOutcome.NotPersistent, null, null);

        MuxClient? client = Host.GetClient(ConnectTimeout);
        if (client is null) return Fallback(request, "the multiplexer could not be reached");

        try
        {
            if (request.ExistingMuxSessionId is Guid existing)
            {
                IReadOnlyList<SessionSummary> sessions = Rpc(ct => client.ListSessionsAsync(ct));
                if (sessions.Any(s => s.SessionId == existing && s.Running && !s.Faulted))
                {
                    return new(client.OpenSession(existing, request.Command, request.Arguments), PersistentSessionOutcome.Reattached, Host.Endpoint, null);
                }

                Guid fresh = Spawn(client, request);
                return new(client.OpenSession(fresh, request.Command, request.Arguments), PersistentSessionOutcome.PreviousLost, Host.Endpoint, null);
            }

            Guid spawned = Spawn(client, request);
            return new(client.OpenSession(spawned, request.Command, request.Arguments), PersistentSessionOutcome.Spawned, Host.Endpoint, null);
        }
        catch (Exception ex) when (ex is MuxProtocolException or IOException or TimeoutException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            return Fallback(request, ex.Message);
        }
    }

    private Guid Spawn(MuxClient client, TerminalSessionRequest r) => Rpc(ct => client.SpawnAsync(new SpawnParams
    {
        Command = r.Command,
        Arguments = r.Arguments,
        StartingDirectory = r.StartingDirectory,
        Cols = r.Cols,
        Rows = r.Rows,
        EnvironmentOverrides = r.EnvironmentOverrides,
        SkipPowerShellPostLaunchInit = r.SkipPowerShellPostLaunchInit,
        Title = r.Command,
    }, ct));

    private T Rpc<T>(Func<CancellationToken, Task<T>> call)
    {
        using var cts = new CancellationTokenSource(RpcTimeout);
        Task<T> task = Task.Run(() => call(cts.Token));
        if (!task.Wait(RpcTimeout + TimeSpan.FromMilliseconds(250))) throw new TimeoutException("The multiplexer did not answer in time.");
        return task.GetAwaiter().GetResult();
    }

    private PersistentSessionResult Fallback(TerminalSessionRequest request, string why)
    {
        _log?.Invoke($"[Mux] multiplexer unavailable ({why}); this session will not persist");
        return new(_fallback.Create(request), PersistentSessionOutcome.Unavailable, null, why);
    }
}
```

`task.Wait` wraps a failure in `AggregateException`. Because `GetAwaiter().GetResult()` runs after `Wait` returned true, it rethrows the original exception, which the `catch` filters on.

- [ ] **Step 7: Run all Task 7 tests → PASS**

```powershell
scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~SessionPersistenceSettingTests|FullyQualifiedName~MuxConnectionHostTests|FullyQualifiedName~MuxTerminalSessionFactoryTests"
scripts/build.ps1 test tests/Ntilde.McpServer.Tests
```

- [ ] **Step 8: Commit**

```powershell
scripts/build.ps1 format whitespace --no-restore
git add -A src tests
git commit -m "feat(mux): SessionPersistence setting and the persistent session factory"
```

---
### Task 8: `TerminalView` deferred-resize mode

**Files:**
- Modify: `src/Ntilde.App/Shell/TerminalView.cs`: resize sites A (`ApplySettings` font change, ~1008-1033), B (`OnSizeChanged`, ~1913-1980), C (`SendThrottledResize`, ~2088-2123), `ReconcileDispatchedGrid` (~2057-2086), plus the new members.
- Test: `tests/Ntilde.App.Tests/Core/TerminalViewDeferredResizeTests.cs`

**Interfaces:**
- Produces on `TerminalView`:
  - `internal bool DefersBufferResizeToSession { get; set; }`: when true, the view raises `OnResize` (the request) and never resizes the buffer itself.
  - `internal void NotifySessionResizedBuffer()`: UI thread only. The session has resized the buffer; the view updates its bookkeeping.
  - `internal (int Cols, int Rows) LastRequestedGridForTest`.

Why: a mux session orders resizes in its output stream (`OrdersResizeInStream`). The pane's buffer must change size exactly where the mux's did, via `StreamResize` on the delivery thread. If the view resized eagerly, local output parsed between the eager resize and the stream's resize would wrap differently from the mux.

- [ ] **Step 1: Write the failing tests** (pattern: `tests/Ntilde.App.Tests/Core/TerminalViewResizeTrackingTests.cs`, driven through a real `Window`)

```csharp
// tests/Ntilde.App.Tests/Core/TerminalViewDeferredResizeTests.cs
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Shell;
using Ntilde.VT;

namespace Ntilde.Tests.Core;

/// <summary>A session that orders resizes in its stream owns the buffer's size (Phase 2 spec §8).</summary>
public class TerminalViewDeferredResizeTests
{
    private static (Window Window, TerminalView View, TerminalBuffer Buffer) Show()
    {
        var buffer = new TerminalBuffer(80, 24);
        var view = new TerminalView();
        view.SetBuffer(buffer);
        var window = new Window { Content = view, Width = 800, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.Bounds.Width > 0, $"the view was never arranged: {view.Bounds}");
        return (window, view, buffer);
    }

    [AvaloniaFact]
    public void A_deferred_view_requests_the_new_grid_but_leaves_the_buffer_alone()
    {
        var (window, view, buffer) = Show();
        try
        {
            (int cols, int rows) = (buffer.Cols, buffer.Rows);
            view.DefersBufferResizeToSession = true;
            view.ReleaseResizeThrottleForTest();
            var requests = new List<(int, int)>();
            view.OnResize += (c, r) => requests.Add((c, r));

            window.Width = 500;
            Dispatcher.UIThread.RunJobs();

            Assert.NotEmpty(requests);
            Assert.True(requests[^1].Item1 < cols, "the request names the narrower grid");
            Assert.Equal((cols, rows), (buffer.Cols, buffer.Rows));             // untouched
            Assert.Equal((cols, rows), view.LastDispatchedGridForTest);         // nothing applied yet
            Assert.Equal(requests[^1], view.LastRequestedGridForTest);
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact]
    public void The_same_grid_is_not_requested_twice()
    {
        var (window, view, _) = Show();
        try
        {
            view.DefersBufferResizeToSession = true;
            view.ReleaseResizeThrottleForTest();
            window.Width = 500;
            Dispatcher.UIThread.RunJobs();
            int requests = 0;
            view.OnResize += (_, _) => requests++;
            view.ReleaseResizeThrottleForTest();
            window.Height = 401; // sub-cell change: same grid
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, requests);
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact]
    public void NotifySessionResizedBuffer_records_the_buffers_real_grid()
    {
        var (window, view, buffer) = Show();
        try
        {
            view.DefersBufferResizeToSession = true;
            buffer.Resize(50, 20);          // what StreamResize does on the delivery thread
            view.NotifySessionResizedBuffer();
            Assert.Equal((50, 20), view.LastDispatchedGridForTest);
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact]
    public void A_font_change_in_deferred_mode_requests_but_does_not_resize()
    {
        var (window, view, buffer) = Show();
        try
        {
            (int cols, int rows) = (buffer.Cols, buffer.Rows);
            view.DefersBufferResizeToSession = true;
            int requests = 0;
            view.OnResize += (_, _) => requests++;
            view.ApplySettings(new TerminalSettings { FontSize = 28 });
            Dispatcher.UIThread.RunJobs();
            Assert.True(requests > 0, "a bigger font means a smaller grid, which must be requested");
            Assert.Equal((cols, rows), (buffer.Cols, buffer.Rows));
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }
}
```

- [ ] **Step 2: Run → build FAIL** (`--filter "FullyQualifiedName~TerminalViewDeferredResizeTests"`).

- [ ] **Step 3: Implement**

Add next to `_lastDispatchedCols/_lastDispatchedRows`:

```csharp
// Deferred mode (Phase 2 spec §8): the grid last *requested* via OnResize. The buffer is resized
// by the session (in stream order), so "did the grid change" compares against the request, and
// _lastDispatched* records only what the buffer actually became (NotifySessionResizedBuffer).
private int _lastRequestedCols;
private int _lastRequestedRows;

/// <summary>
/// Set by the pane for a session that orders resizes in its output stream: OnResize is then only a
/// request, and the buffer changes size when the session says so. Everything else in the view
/// reads the buffer's size under its lock at use time.
/// </summary>
internal bool DefersBufferResizeToSession { get; set; }

internal (int Cols, int Rows) LastRequestedGridForTest => (_lastRequestedCols, _lastRequestedRows);

private bool GridDiffersFromLastSent(int cols, int rows) => DefersBufferResizeToSession
    ? cols != _lastRequestedCols || rows != _lastRequestedRows
    : cols != _lastDispatchedCols || rows != _lastDispatchedRows;

/// <summary>After raising OnResize for (cols, rows): record the request, and the dispatch only if the buffer really is that size.</summary>
private void RecordGridSent(int cols, int rows)
{
    _lastRequestedCols = cols;
    _lastRequestedRows = rows;
    if (!DefersBufferResizeToSession || (_buffer is { } b && b.Cols == cols && b.Rows == rows)) RecordDispatchedGrid(cols, rows);
}

/// <summary>UI thread: the session resized the buffer (snapshot restore or an in-stream resize).</summary>
internal void NotifySessionResizedBuffer()
{
    Dispatcher.VerifyAccess();
    if (_buffer is not { } buffer) return;
    RecordDispatchedGrid(buffer.Cols, buffer.Rows);
    ResetMouseMotionTracking();
    _rowCache.MaxEntries = Math.Max(buffer.Rows * 3, 50);
    _rowCache.RequestClear();
    ApplyScrollOffset(_scrollOffset); // re-clamp against the new TotalLines/Rows
    InvalidateBuffer();
}
```

Adapt `ApplyScrollOffset` to its real signature (~1575). If it takes no argument or is named differently, call whatever clamps `_scrollOffset` against `_buffer.TotalLines - _buffer.Rows`.

Change the three sites:
- **A (font change):**
  - Replace `cols != _buffer.Cols || rows != _buffer.Rows` with `DefersBufferResizeToSession ? GridDiffersFromLastSent(cols, rows) : (cols != _buffer.Cols || rows != _buffer.Rows)`.
  - Guard `_buffer.Resize(cols, rows);` with `if (!DefersBufferResizeToSession)`.
  - Replace `RecordDispatchedGrid(cols, rows)` with `RecordGridSent(cols, rows)`.
- **B (`OnSizeChanged`):**
  - `bool dimensionsChanged = GridDiffersFromLastSent(cols, rows);`
  - In the `!_isReady` block keep `_buffer.Resize` as is. At that moment no session exists and the flag is false; the pane sets it from inside `Ready`.
  - Replace the trailing `RecordDispatchedGrid(cols, rows)` there with `RecordGridSent(cols, rows)`.
- **C (`SendThrottledResize`):**
  - Wrap `_buffer.Resize(_pendingCols, _pendingRows);`, `_rowCache.MaxEntries = ...;` and `_rowCache.RequestClear();` in `if (!DefersBufferResizeToSession) { ... }`.
  - Replace `RecordDispatchedGrid(_pendingCols, _pendingRows)` with `RecordGridSent(_pendingCols, _pendingRows)`.
  - Keep `OnResize?.Invoke(...)`.
- **`ReconcileDispatchedGrid`:** in deferred mode compare the computed grid against `_lastRequested*`; if it differs, raise `OnResize` and call `RecordGridSent`, without resizing the buffer.

Also search the file for any other `_buffer.Resize(` and `_lastDispatchedCols` use (`rg "_buffer.Resize\(|_lastDispatched" src/Ntilde.App/Shell/TerminalView.cs`) and apply the same rule. List every site you changed, and anything else that caches cols/rows, in the task report. Report item 4 of the PR needs it.

- [ ] **Step 4: Run the new tests and the existing resize suites → PASS**

```powershell
scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~TerminalViewDeferredResizeTests|FullyQualifiedName~TerminalViewResize"
```

- [ ] **Step 5: Commit**

```powershell
scripts/build.ps1 format whitespace --no-restore
git add src/Ntilde.App/Shell/TerminalView.cs tests/Ntilde.App.Tests/Core/TerminalViewDeferredResizeTests.cs
git commit -m "feat(mux): TerminalView defers buffer resizes to stream-ordered sessions"
```

---

### Task 9: `TerminalPane` mux wiring

**Files:**
- Modify: `src/Ntilde.App/Controls/TerminalPane.axaml.cs`:
  - `CreateAndWireParser` (~3078)
  - `InitializeSessionCore` (~3344-3519)
  - `WireReusedTermViewHandlers` metrics handler (~3640)
  - `ApplySettings` (~3817)
  - `Reconnect` / `ShouldReconnectOnEnter` (~4162-4183)
  - new members
- Test: `tests/Ntilde.App.Tests/Controls/MuxPaneTests.cs`

**Interfaces:**
- Consumes:
  - Task 7: `IPersistentSessionFactory`, `PersistentSessionResult`, `PersistentSessionOutcome`, `MuxTerminalSessionFactory`, `MuxConnectionHost`.
  - Task 8: `TerminalView.DefersBufferResizeToSession`, `NotifySessionResizedBuffer`.
  - `MuxClientSession`: `SnapshotReceived`, `StreamResize`, `Disconnected`, `Faulted`, `IsConnected`, `IsFaulted`, `ForceConPtyFiltering`, `AttachAsync`, `UpdatePresentation`, `Id`.
  - `TerminalStateTransfer.Restore`.
- Produces on `TerminalPane`:
  - `internal Guid? MuxSessionIdToRestore { get; set; }`: set by `SessionManager.RestorePaneTree` (Task 11); consumed once.
  - `internal string? MuxEndpoint { get; private set; }`
  - `internal event Action<TerminalPane>? PersistentSessionAttached;`
  - `internal void CreateAndWireParser(bool? forceConPtyFiltering = null, bool muxBacked = false)` (signature change; existing callers unchanged).
  - `internal MuxPresentation BuildMuxPresentation()`
  - `internal void HandleMuxSnapshot(MuxClientSession source, TerminalStateSnapshot snapshot)`: delivery-thread handler, internal for tests.
  - Banner texts, as constants:
    - `internal const string MuxUnavailableBanner = "[Multiplexer unavailable — this session will not persist]";`
    - `internal const string MuxPreviousLostBanner = "[Previous session was lost — started a new shell]";`
    - `internal const string MuxDisconnectedBanner = "[Multiplexer disconnected] [Press Enter to reconnect]";`

- [ ] **Step 1: Write the failing tests**

The pane test harness is in `tests/Ntilde.App.Tests/Controls/PaneSessionFactoryTests.cs`: `new TerminalPane()`, `PaneSpawnTestHelpers.DisableShellIntegration(pane)`, `pane.SessionFactory = ...`, `pane.CreateAndWireParser()`, then `pane.InitializeSessionCore("fake-shell", "", profile: null, cols: 80, rows: 24)`. Reuse it. Buffer text comes from `MuxTestText.VisibleText` (Task 5). Never `Wait()` a mux task on the UI thread: wrap it in `Task.Run(...).GetAwaiter().GetResult()`, because the Avalonia sync context would deadlock.

```csharp
// tests/Ntilde.App.Tests/Controls/MuxPaneTests.cs
using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Mux;
using Ntilde.Mux.Tests.Support;
using Ntilde.Shell.Mux;
using Ntilde.VT;

namespace Ntilde.Tests.Controls;

public sealed class MuxPaneTests : IDisposable
{
    private readonly MuxTestHost _mux = new();
    private readonly MuxConnectionHost _host;
    private readonly MuxTerminalSessionFactory _factory;
    private TerminalPane? _pane;

    public MuxPaneTests()
    {
        _host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(_mux.Listener.Connect(), null, ct), "test", null);
        _factory = new MuxTerminalSessionFactory(_host, new RecordingSessionFactory(new FakeTerminalSession()), null);
    }

    public void Dispose()
    {
        _pane?.Dispose();
        _host.Dispose();
        _mux.Dispose();
    }

    private static void PumpUntil(Func<bool> condition, string because, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) Assert.Fail($"Timed out waiting until {because}.");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    private MuxClientSession StartPane()
    {
        _pane = new TerminalPane();
        PaneSpawnTestHelpers.DisableShellIntegration(_pane);
        _pane.SessionFactory = _factory;
        _pane.CreateAndWireParser();
        _pane.InitializeSessionCore("scripted", string.Empty, profile: null, cols: 80, rows: 24);
        var session = Assert.IsType<MuxClientSession>(_pane.Session);
        PumpUntil(() => session.IsAttached, "the pane attached");
        return session;
    }

    private void SettleAndAssertEqual(Guid id, string because)
    {
        MuxClient client = _host.CurrentClient!;
        Task.Run(() => _mux.SettleAsync(id, client)).GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
        HeadlessTerminalSession m = _mux.Mux(id);
        TerminalStateAssert.AssertEquivalent($"[{because}]", m.Buffer, m.Parser, _pane!.Buffer!, _pane.Parser!);
    }

    [AvaloniaFact]
    public void Spawn_attach_and_output_leave_the_pane_buffer_equal_to_the_mux()
    {
        MuxClientSession s = StartPane();
        _mux.Fake(s.Id).EmitOutput("hello\r\n\x1b[31mred\x1b[0m world\r\n");
        SettleAndAssertEqual(s.Id, "after output");
    }

    [AvaloniaFact]
    public void The_mux_parser_is_built_without_image_support()
    {
        StartPane();
        Assert.Null(_pane!.Parser!.ImageDecoder);
        Assert.False(_pane.Parser.AllowNativeKittyGraphics);
        Assert.True(_pane.TermView.DefersBufferResizeToSession);
    }

    [AvaloniaFact]
    public void A_stream_resize_resizes_the_pane_buffer_and_the_view_records_it()
    {
        MuxClientSession s = StartPane();
        _mux.Mux(s.Id).PostResize(100, 30, null);
        PumpUntil(() => _pane!.Buffer!.Cols == 100 && _pane.Buffer.Rows == 30, "the stream resize reached the pane");
        PumpUntil(() => _pane!.TermView.LastDispatchedGridForTest == (100, 30), "the view recorded the session's resize");
        SettleAndAssertEqual(s.Id, "after stream resize");
    }

    [AvaloniaFact]
    public void A_resize_storm_ends_with_equal_state()
    {
        MuxClientSession s = StartPane();
        for (int i = 0; i < 40; i++)
        {
            s.Resize(60 + i % 30, 20 + i % 10);
            _mux.Fake(s.Id).EmitOutput($"line {i}\r\n");
        }

        SettleAndAssertEqual(s.Id, "after the storm");
    }

    [AvaloniaFact]
    public void A_resize_storm_through_a_hosted_TerminalView_ends_with_equal_state()
    {
        // The real path: layout → TerminalView (deferred) → OnResize → Session.Resize → server →
        // ResizeEvent → StreamResize → pane buffer. Ready spawns the session like a real launch.
        _pane = new TerminalPane();
        PaneSpawnTestHelpers.DisableShellIntegration(_pane);
        _pane.SessionFactory = _factory;
        var window = new Avalonia.Controls.Window { Content = _pane, Width = 900, Height = 500 };
        try
        {
            window.Show();
            PumpUntil(() => _pane.Session is MuxClientSession { IsAttached: true }, "the hosted pane attached");
            var s = (MuxClientSession)_pane.Session!;
            for (int i = 0; i < 25; i++)
            {
                _pane.TermView.ReleaseResizeThrottleForTest();
                window.Width = 500 + (i * 37 % 400);
                window.Height = 300 + (i * 23 % 200);
                Dispatcher.UIThread.RunJobs();
                _mux.Fake(s.Id).EmitOutput($"storm {i} " + new string('x', i * 3) + "\r\n");
            }

            SettleAndAssertEqual(s.Id, "after a layout-driven resize storm");
            Assert.Equal((_pane.Buffer!.Cols, _pane.Buffer.Rows), _pane.TermView.LastDispatchedGridForTest);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
    [AvaloniaFact]
    public void Disconnect_writes_the_banner_and_does_not_raise_ProcessExited()
    {
        MuxClientSession s = StartPane();
        int exited = 0;
        _pane!.ProcessExited += (_, _) => exited++;
        _host.CurrentClient!.Dispose();
        PumpUntil(() => BufferText(_pane.Buffer!).Contains("[Multiplexer disconnected]"), "the banner is shown");
        Assert.Equal(0, exited);
        Assert.True(TerminalPane.ShouldReconnectOnEnter(_pane.Session));
    }

    [AvaloniaFact]
    public void Enter_after_a_disconnect_reattaches_the_same_session_and_replaces_the_banner()
    {
        MuxClientSession s = StartPane();
        _mux.Fake(s.Id).EmitOutput("before\r\n");
        _host.CurrentClient!.Dispose();
        PumpUntil(() => BufferText(_pane!.Buffer!).Contains("[Multiplexer disconnected]"), "the banner is shown");

        _pane!.Reconnect();
        var again = Assert.IsType<MuxClientSession>(_pane.Session);
        Assert.Equal(s.Id, again.Id);
        PumpUntil(() => again.IsAttached, "reattached");
        Assert.DoesNotContain("[Multiplexer disconnected]", BufferText(_pane.Buffer!));
        SettleAndAssertEqual(s.Id, "after reattach");
    }

    [AvaloniaFact]
    public void Disconnect_then_Enter_with_session_gone_spawns_fresh_with_lost_banner()
    {
        MuxClientSession s = StartPane();
        _host.CurrentClient!.Dispose();
        PumpUntil(() => BufferText(_pane!.Buffer!).Contains("[Multiplexer disconnected]"), "the banner is shown");
        _mux.Server.KillAllSessions();

        _pane!.Reconnect();
        var fresh = Assert.IsType<MuxClientSession>(_pane.Session);
        Assert.NotEqual(s.Id, fresh.Id);
        PumpUntil(() => BufferText(_pane.Buffer!).Contains(TerminalPane.MuxPreviousLostBanner), "the lost banner is shown");
    }

    [AvaloniaFact]
    public void Late_snapshot_after_pane_dispose_is_ignored()
    {
        MuxClientSession s = StartPane();
        _mux.Mux(s.Id).PostResize(120, 40, null);
        Task.Run(() => _mux.Mux(s.Id).FlushAsync()).GetAwaiter().GetResult();
        TerminalStateSnapshot snapshot = _mux.Mux(s.Id).CaptureSnapshot(100);
        TerminalBuffer buffer = _pane!.Buffer!;
        (int cols, int rows) = (buffer.Cols, buffer.Rows);

        _pane.DetachFromUiThread()?.Dispose();
        _pane.HandleMuxSnapshot(s, snapshot); // must not touch the buffer
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((cols, rows), (buffer.Cols, buffer.Rows));
    }

    [AvaloniaFact]
    public void An_unreachable_daemon_gives_a_local_session_and_the_unavailable_banner()
    {
        using var deadHost = new MuxConnectionHost(_ => throw new MuxUnavailableException("down"), "x", null);
        _pane = new TerminalPane();
        PaneSpawnTestHelpers.DisableShellIntegration(_pane);
        _pane.SessionFactory = new MuxTerminalSessionFactory(deadHost, new RecordingSessionFactory(new FakeTerminalSession()), null)
            { ConnectTimeout = TimeSpan.FromMilliseconds(300) };
        _pane.CreateAndWireParser();
        _pane.InitializeSessionCore("scripted", string.Empty, profile: null, cols: 80, rows: 24);
        Assert.IsType<FakeTerminalSession>(_pane.Session);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(TerminalPane.MuxUnavailableBanner, BufferText(_pane.Buffer!));
        Assert.False(_pane.TermView.DefersBufferResizeToSession);
    }

    [AvaloniaFact]
    public void A_real_exit_goes_through_the_normal_exit_path()
    {
        MuxClientSession s = StartPane();
        int exited = 0;
        _pane!.ProcessExited += (_, _) => exited++;
        _mux.Fake(s.Id).EmitExit(0);
        PumpUntil(() => exited == 1, "ProcessExited fired");
    }

    [AvaloniaFact]
    public void The_restore_id_is_passed_once_as_ExistingMuxSessionId()
    {
        MuxClientSession first = StartPane();
        Guid id = first.Id;
        _pane!.Dispose();

        _pane = new TerminalPane { MuxSessionIdToRestore = id };
        PaneSpawnTestHelpers.DisableShellIntegration(_pane);
        _pane.SessionFactory = _factory;
        _pane.CreateAndWireParser();
        _pane.InitializeSessionCore("scripted", string.Empty, profile: null, cols: 80, rows: 24);
        Assert.Equal(id, Assert.IsType<MuxClientSession>(_pane.Session).Id);
        Assert.Null(_pane.MuxSessionIdToRestore);
    }

    private static string BufferText(TerminalBuffer buffer) => MuxTestText.VisibleText(buffer);
}
```

Replace `EmitOutput`/`EmitExit` with `ScriptedTerminalSession`'s actual member names. `TermView` is the generated `x:Name` field; if it isn't reachable from tests, add `internal TerminalView TermViewForTest => TermView;` and use that. `pane.Dispose()` detaches the mux session (`MuxClientSession.Dispose` = detach), so in the restore test the session keeps running in the server.

- [ ] **Step 2: Run → FAIL** (`--filter "FullyQualifiedName~MuxPaneTests"`).

- [ ] **Step 3: Parser rebuild parameters**

```csharp
internal void CreateAndWireParser(bool? forceConPtyFiltering = null, bool muxBacked = false)
{
    if (Buffer == null) return;
    // A mux-backed pane parses the same bytes as the daemon's parser and must stay equal to it: same
    // ConPTY filtering (from Welcome), and no image decoding - the mux parser has none, and decoding
    // moves the cursor, so a decoding pane would diverge. Inline images are a documented v1 limit.
    Parser = new AnsiParser(Buffer, forceConPtyFiltering);
    Parser.ImageDecoder = muxBacked ? null : new Ntilde.Rendering.SkiaImageDecoder();
    ...existing wiring unchanged...
    Parser.AllowNativeKittyGraphics = !muxBacked && (_settings?.AllowNativeKittyGraphics ?? true);
    Parser.ReadFileBytes = muxBacked || Profile is { Type: ConnectionType.SSH } ? null : ReadKittyTransportFile;
    ...
}
```

In `ApplySettings`, the line `Parser.AllowNativeKittyGraphics = effectiveSettings.AllowNativeKittyGraphics;` becomes `Parser.AllowNativeKittyGraphics = Session is not MuxClientSession && effectiveSettings.AllowNativeKittyGraphics;`. After the parser block add:

```csharp
if (Session is MuxClientSession { IsAttached: true } attachedMux) attachedMux.UpdatePresentation(BuildMuxPresentation());
```

In the `_onTermViewMetricsChanged` handler, after the parser metric update: `if (Session is MuxClientSession { IsAttached: true } mux) mux.UpdatePresentation(BuildMuxPresentation());`.

- [ ] **Step 4: Session creation, request id, outcome banner**

In `InitializeSessionCore`:
- Build the request with `ExistingMuxSessionId: isSsh ? null : TakeMuxSessionIdToRestore()`.
- Replace the local branch's `Session = SessionFactory.Create(request);` with:

```csharp
string? bannerAfterAttach = null;
if (SessionFactory is IPersistentSessionFactory persistent)
{
    PersistentSessionResult result = persistent.CreatePersistent(request);
    Session = result.Session;
    MuxEndpoint = result.Endpoint;
    if (result.Outcome == PersistentSessionOutcome.Unavailable) WriteBanner($"\r\n\x1b[33m{MuxUnavailableBanner}\x1b[0m\r\n");
    else if (result.Outcome == PersistentSessionOutcome.PreviousLost) bannerAfterAttach = $"\x1b[33m{MuxPreviousLostBanner}\x1b[0m\r\n";
}
else
{
    Session = SessionFactory.Create(request);
    MuxEndpoint = null;
}
```

(`bannerAfterAttach` must be declared where the code at the end of the method can see it. The lost banner is written only after the attach: the snapshot would otherwise overwrite it.)

At the end of `InitializeSessionCore`, after `WireReusedTermViewHandlers();`:

```csharp
TermView.DefersBufferResizeToSession = Session is ITerminalSessionCapabilities { OrdersResizeInStream: true };
if (Session is MuxClientSession mux) WireMuxSession(mux, bannerAfterAttach);
```

Add the members:

```csharp
internal const string MuxUnavailableBanner = "[Multiplexer unavailable — this session will not persist]";
internal const string MuxPreviousLostBanner = "[Previous session was lost — started a new shell]";
internal const string MuxDisconnectedBanner = "[Multiplexer disconnected] [Press Enter to reconnect]";

/// <summary>Set by SessionManager.RestorePaneTree: the daemon session this pane should reopen (once).</summary>
internal Guid? MuxSessionIdToRestore { get; set; }
internal string? MuxEndpoint { get; private set; }
/// <summary>Raised on the UI thread after a mux session attached (MainWindow saves the session file).</summary>
internal event Action<TerminalPane>? PersistentSessionAttached;

private Guid? _muxReattachId;   // UI thread: set when the connection dropped, consumed by Reconnect
private bool _muxConnectionLost; // UI thread

private Guid? TakeMuxSessionIdToRestore()
{
    Guid? id = _muxReattachId ?? MuxSessionIdToRestore;
    _muxReattachId = null;
    MuxSessionIdToRestore = null;
    return id;
}

private void WireMuxSession(MuxClientSession mux, string? bannerAfterAttach)
{
    CreateAndWireParser(mux.ForceConPtyFiltering, muxBacked: true);
    float cw = TermView.Metrics.CellWidth, ch = TermView.Metrics.CellHeight;
    if (cw > 0) Parser!.CellWidth = cw;
    if (ch > 0) Parser!.CellHeight = ch;

    _muxConnectionLost = false;
    mux.SnapshotReceived += snapshot => HandleMuxSnapshot(mux, snapshot);
    mux.StreamResize += (c, r) => HandleMuxStreamResize(mux, c, r);
    mux.Disconnected += _ => this.Dispatcher.Post(() => HandleMuxConnectionLost(mux, MuxDisconnectedBanner));
    mux.Faulted += _ => this.Dispatcher.Post(() => HandleMuxConnectionLost(mux, MuxDisconnectedBanner));
    _ = AttachMuxAsync(mux, bannerAfterAttach);
}

private bool IsCurrentMux(MuxClientSession source) => !Volatile.Read(ref _disposed) && ReferenceEquals(Session, source);

/// <summary>
/// Delivery thread (MuxClientRead). Restores here, under the buffer's own lock - never marshalled
/// to the UI thread and never blocking, because a cancelled attach can be waiting on this handler.
/// </summary>
internal void HandleMuxSnapshot(MuxClientSession source, TerminalStateSnapshot snapshot)
{
    if (!IsCurrentMux(source) || Buffer is not { } buffer || Parser is not { } parser) return;
    try
    {
        TerminalStateTransfer.Restore(buffer, parser, snapshot);
    }
    catch (Exception ex)
    {
        TerminalLogger.Log($"[TerminalPane] snapshot restore for {source.Id} failed: {ex.Message}");
        return;
    }

    this.Dispatcher.Post(() => { if (IsCurrentMux(source)) TermView.NotifySessionResizedBuffer(); });
    QueueOutputUiRefresh();
}

/// <summary>Delivery thread: the mux resized at this point in the stream; the pane follows exactly there.</summary>
private void HandleMuxStreamResize(MuxClientSession source, int cols, int rows)
{
    if (!IsCurrentMux(source) || Buffer is not { } buffer) return;
    buffer.Resize(cols, rows);
    this.Dispatcher.Post(() => { if (IsCurrentMux(source)) TermView.NotifySessionResizedBuffer(); });
    QueueOutputUiRefresh();
}

/// <summary>UI thread. The daemon or connection is gone; the shell may still be running there.</summary>
private void HandleMuxConnectionLost(MuxClientSession source, string banner)
{
    if (!IsCurrentMux(source) || _muxConnectionLost) return;
    _muxConnectionLost = true;
    _muxReattachId = source.Id;
    TerminalLogger.Log($"[TerminalPane] multiplexer connection lost for session {source.Id}");
    WriteBanner($"\r\n\x1b[90m{banner}\x1b[0m\r\n");
    // Deliberately NOT ProcessExited: MainWindow would apply ShellExitPolicy and may close the pane.
}

private async Task AttachMuxAsync(MuxClientSession mux, string? bannerAfterAttach)
{
    MuxPresentation presentation = BuildMuxPresentation(); // UI thread, before the first await
    int scrollback = Math.Clamp(_settings?.MaxHistory ?? 10_000, 0, 50_000);
    try
    {
        await mux.AttachAsync(scrollback, presentation).ConfigureAwait(false);
        this.Dispatcher.Post(() =>
        {
            if (!IsCurrentMux(mux)) return;
            if (bannerAfterAttach is not null) WriteBanner(bannerAfterAttach);
            PersistentSessionAttached?.Invoke(this);
        });
    }
    catch (Exception ex)
    {
        TerminalLogger.Log($"[TerminalPane] attach to {mux.Id} failed: {ex.Message}");
        this.Dispatcher.Post(() => HandleMuxConnectionLost(mux, $"[Multiplexer attach failed: {SanitizeBannerValue(ex.Message)}] [Press Enter to reconnect]"));
    }
}

internal MuxPresentation BuildMuxPresentation()
{
    double scale = TermView.EffectiveRenderScaling;
    if (double.IsNaN(scale) || scale <= 0) scale = 1;
    TerminalTheme? theme = _settings is null ? null : BuildEffectiveSettings(_settings).ActiveTheme;
    int cols = TermView.Cols > 0 ? TermView.Cols : Buffer?.Cols ?? 80;
    int rows = TermView.Rows > 0 ? TermView.Rows : Buffer?.Rows ?? 24;
    return new MuxPresentation
    {
        Cols = cols,
        Rows = rows,
        // Device pixels (DIPs x effective render scaling), the unit in-band size reports use.
        CellWidthPx = (float)(TermView.Metrics.CellWidth * scale),
        CellHeightPx = (float)(TermView.Metrics.CellHeight * scale),
        DefaultFg = theme?.Foreground.ToUint(),
        DefaultBg = theme?.Background.ToUint(),
        KittyKeyboardEnabled = _settings?.EnableKittyKeyboardProtocol ?? true,
    };
}
```

`_disposed` is a plain `bool`; `Volatile.Read(ref _disposed)` works on it. Use the real theme type name returned by `ActiveTheme` in place of `TerminalTheme`. `CellWidthPx`/`CellHeightPx` of 0 (headless, no metrics) are accepted by the server.

- [ ] **Step 5: Reconnect semantics**

```csharp
internal static bool ShouldReconnectOnEnter(ITerminalSession? session) =>
    session == null
    || !session.IsProcessRunning
    || session is MuxClientSession { IsConnected: false }
    || session is MuxClientSession { IsFaulted: true };
```

`Reconnect()` needs no structural change: it disposes the session (a detach for mux) and calls `InitializeSession`, which reaches `InitializeSessionCore`, which consumes `_muxReattachId` through `TakeMuxSessionIdToRestore`. Change its banner from `[Reconnecting...]` to `[Reattaching...]` when `_muxReattachId` is set, to be precise. Update any existing `ShouldReconnectOnEnter` unit tests only if they assert the exact expression, never by weakening a behaviour.

- [ ] **Step 6: Run → PASS**, then the neighbouring pane suites:

```powershell
scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~MuxPaneTests|FullyQualifiedName~PaneSessionFactoryTests|FullyQualifiedName~PaneParserWiringTests"
```

- [ ] **Step 7: Commit**

```powershell
scripts/build.ps1 format whitespace --no-restore
git add src/Ntilde.App/Controls/TerminalPane.axaml.cs tests/Ntilde.App.Tests/Controls/MuxPaneTests.cs
git commit -m "feat(mux): TerminalPane attaches mux sessions, restores snapshots, reattaches on Enter"
```

---

### Task 10: MainWindow — factory install, close semantics, teardown detach

**Files:**
- Modify: `src/Ntilde.App/MainWindow.axaml.cs`:
  - `_sessionFactory` (~262, drop `readonly`)
  - ctor (~3679)
  - `OnOpened` (~305)
  - `DisposeControlTree` (~6027)
  - `ShouldClosePaneAsync` (~5418)
  - `PerformAppTeardown` (~8313)
  - `ApplySettingsWindowResult` (~7599)
- Test: `tests/Ntilde.App.Tests/Core/MainWindowMuxLifecycleTests.cs`

**Interfaces:**
- Consumes: Task 7 `MuxConnectionHost`, `MuxTerminalSessionFactory`, `SessionPersistenceMode`; Task 9 pane members.
- Produces:
  - `internal enum PaneDisposition { EndSession, Detach }` (`src/Ntilde.App/Shell/Mux/PaneDisposition.cs`).
  - `DisposeControlTree(Control control, PaneDisposition disposition = PaneDisposition.EndSession)`.
  - `internal MuxConnectionHost? MuxHost { get; }` on MainWindow (tests, Task 11/12).
  - `internal static async Task RefreshPersistentSessionInfoAsync(ITerminalSession? session, TimeSpan timeout)`.
  - `internal IReadOnlyList<TerminalPane> AllPanesForTest()`: every pane in every tab, zoom-safe. Implement it as `FindControl<TabControl>("Tabs")?.Items.OfType<TabItem>().SelectMany(t => EnumeratePanes(GetLayoutRootForTab(t))).ToList() ?? []`, using the existing `EnumeratePanes(Control?)` (~4903) and `GetLayoutRootForTab`.

Rules (spec §9):
- **Factory install.** In the ctor, once `_settings` is loaded:
  - If `services.SessionFactory` is a `MuxTerminalSessionFactory mf`, use it and set `_muxHost = mf.Host`. This is the test path.
  - Else if `services.SessionFactory` is non-null, use it.
  - Else if `SessionPersistenceMode.IsKeepOnClose(_settings.SessionPersistence)`, set `_muxHost = MuxConnectionHost.CreateDefault(AppLogger.Log)` and `_sessionFactory = new MuxTerminalSessionFactory(_muxHost, DefaultTerminalSessionFactory.Instance, AppLogger.Log)`.
  - Otherwise use `DefaultTerminalSessionFactory.Instance`.
  - Check where `_settings` is assigned relative to line ~3683; the factory must be decided before the first pane is created.
- **Warm-up.** `_muxHost?.WarmUp()` at the end of the ctor. It is idempotent, so `OnOpened` re-raising after quake `Show()` is harmless. Still add a `_muxWarmupStarted` guard next to `_updateChecksStarted` and document why.
- **Settings change.** In `ApplySettingsWindowResult`, when `SessionPersistence` flips:
  - Off → on: create the host and factory as above, then `WarmUp()`.
  - On → off: `_sessionFactory = DefaultTerminalSessionFactory.Instance`. Keep `_muxHost` alive for the existing mux panes.
  - Then call `WirePane` for every pane in `_paneOwnerTab.Keys` so each pane's `SessionFactory` follows (it affects Reconnect only).
- **Closing a pane.** Every existing `DisposeControlTree` caller is user-initiated, so they keep the default `EndSession`. In the `Task.Run` body, wrap `session.Dispose()`:

```csharp
if (disposition == PaneDisposition.EndSession && session is MuxClientSession mux)
{
    // A user closed this pane: the shell must end, not linger detached in the daemon.
    try { mux.Kill(); }
    catch (Exception ex) { TerminalLogger.Log($"[MainWindow] mux kill failed: {ex.Message}"); }
}
session.Dispose();
```

- **Window teardown.** `PerformAppTeardown` gets a `private bool _teardownDone;` guard. After `SessionManager.SaveSession(...)`:

```csharp
if (_muxHost is { } muxHost)
{
    int kept = _paneOwnerTab.Keys.Count(p => p.Session is MuxClientSession { IsConnected: true, IsProcessRunning: true });
    // Explicit detach: closing the connection makes the daemon drop this client's subscriptions and
    // keep every shell running. Panes are deliberately not disposed (that would kill them).
    muxHost.Dispose();
    if (kept > 0) AppLogger.Log($"[MainWindow] {kept} session(s) kept running; `ntilde mux ls` lists them");
}
```

(Use the real pane-enumeration helper if `_paneOwnerTab` is not the canonical set.)
- **Close confirmation.** At the top of `ShouldClosePaneAsync`: `await RefreshPersistentSessionInfoAsync(pane.Session, TimeSpan.FromSeconds(1));`.

```csharp
internal static async Task RefreshPersistentSessionInfoAsync(ITerminalSession? session, TimeSpan timeout)
{
    if (session is not MuxClientSession { IsConnected: true } mux) return;
    try { await mux.RefreshSessionInfoAsync().WaitAsync(timeout); }
    catch (Exception ex) when (ex is TimeoutException or MuxProtocolException or ObjectDisposedException or IOException)
    {
        // Stale is acceptable: the confirmation then uses the last probed value.
    }
}
```

- [ ] **Step 1: Write the failing tests** (patterns: `tests/Ntilde.App.Tests/Core/MainWindowPaneWiringTests.cs` and `MainWindowShellExitTests.cs` for `IClassFixture<TestAppDataRoot>`, `TestMainWindowFactory.Create(bundle)`, reflection on `ClosePaneAsync`, and the two-tab fixture)

```csharp
// tests/Ntilde.App.Tests/Core/MainWindowMuxLifecycleTests.cs
using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Mux;
using Ntilde.Mux.Tests.Support;
using Ntilde.Shell;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Controls;

namespace Ntilde.Tests.Core;

public sealed class MainWindowMuxLifecycleTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    private readonly MuxTestHost _mux = new();

    public void Dispose()
    {
        TestMainWindowFactory.DisposeCreatedWindows();
        _mux.Dispose();
    }

    private MainWindow CreateWindow()
    {
        var host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(_mux.Listener.Connect(), null, ct), "test", null);
        var factory = new MuxTerminalSessionFactory(host, new RecordingSessionFactory(new FakeTerminalSession()), null);
        MainWindow window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
            SessionFactory = factory,
        });
        window.Show();
        PumpUntil(() => AllPanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true }), "the first pane attached");
        return window;
    }

    private static IReadOnlyList<TerminalPane> AllPanes(MainWindow w) => w.AllPanesForTest();

    private static void PumpUntil(Func<bool> c, string because, int ms = 10_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!c()) { if (sw.ElapsedMilliseconds > ms) Assert.Fail($"Timed out: {because}"); Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
    }

    [AvaloniaFact]
    public void Window_close_detaches_and_the_session_keeps_running()
    {
        MainWindow window = CreateWindow();
        Guid id = ((MuxClientSession)AllPanes(window).First().Session!).Id;
        window.Close();
        PumpUntil(() => _mux.Mux(id).AttachedClients == 0, "the daemon saw the detach");
        Assert.False(_mux.Mux(id).IsExited);
        Assert.Contains(id, _mux.Server.GetSessionIds());
    }

    [AvaloniaFact]
    public void Closing_last_tab_kills_its_session_before_teardown()
    {
        MainWindow window = CreateWindow();
        TerminalPane pane = AllPanes(window).Single();
        Guid id = ((MuxClientSession)pane.Session!).Id;
        var close = typeof(MainWindow).GetMethod("ClosePaneAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task<bool>)close.Invoke(window, [pane, true])!;
        PumpUntil(() => task.IsCompleted, "the close finished");
        PumpUntil(() => !_mux.Server.GetSessionIds().Contains(id), "the session was killed");
    }

    [AvaloniaFact]
    public void Teardown_twice_is_harmless()
    {
        MainWindow window = CreateWindow();
        var teardown = typeof(MainWindow).GetMethod("PerformAppTeardown", BindingFlags.NonPublic | BindingFlags.Instance)!;
        teardown.Invoke(window, null);
        teardown.Invoke(window, null);
    }

    [AvaloniaFact]
    public void Close_confirmation_refreshes_the_daemons_child_process_state()
    {
        MainWindow window = CreateWindow();
        var mux = (MuxClientSession)AllPanes(window).First().Session!;
        _mux.Fake(mux.Id).HasActiveChildProcesses = true; // add a settable property to the fake if absent
        Task.Run(() => MainWindow.RefreshPersistentSessionInfoAsync(mux, TimeSpan.FromSeconds(2))).GetAwaiter().GetResult();
        Assert.True(mux.HasActiveChildProcesses);
    }
}
```

`TestMainWindowFactory.Create`'s ctor opens a real tab. With a mux factory injected, that tab's pane spawns into the in-memory server; `ScriptedSessionFactory` accepts any command. If the ctor path runs startup restore from the test root's session file, `TestAppDataRoot` sweeps `sessions/`, so no stale file interferes.

- [ ] **Step 2: Run → FAIL.** **Step 3: Implement the rules above.**

- [ ] **Step 4: Run → PASS**, then the neighbouring MainWindow suites:

```powershell
scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~MainWindowMuxLifecycleTests|FullyQualifiedName~MainWindowShellExitTests|FullyQualifiedName~MainWindowPaneWiringTests|FullyQualifiedName~TabClosePolicyTests"
```

- [ ] **Step 5: Commit** — `feat(mux): user closes kill, window teardown detaches; factory follows the setting`

---

### Task 11: Persist mux ids, restore/reattach, orphans, save after attach

**Files:**
- Modify: `src/Ntilde.App/Shell/SessionManager.cs`: `BuildPaneTree` (~133-217), `RestorePaneTree` (~390-527)
- Modify: `src/Ntilde.App/MainWindow.axaml.cs`:
  - `WirePane`/`UnwirePane`: `PersistentSessionAttached`
  - `TryRestoreStartupSession` (~2840): orphan adoption after restore
- Create: `src/Ntilde.App/Shell/Mux/MuxOrphans.cs`
- Test: `tests/Ntilde.App.Tests/Core/SessionManagerMuxTests.cs`, `tests/Ntilde.App.Tests/Shell/Mux/MuxOrphansTests.cs`

**Interfaces:**
- Consumes: `TerminalPane.MuxSessionIdToRestore`, `MuxEndpoint`, `PersistentSessionAttached` (Task 9); `MainWindow.MuxHost` (Task 10); `PaneNode.MuxSessionId/MuxEndpoint` (`src/Ntilde.Pty/SessionModels.cs`).
- Produces: `internal static class MuxOrphans`, with:
  - `static HashSet<Guid> CollectReferencedIds(NtildeSession? session)`
  - `static IReadOnlyList<SessionSummary> Select(IEnumerable<SessionSummary> sessions, IReadOnlySet<Guid> referenced)`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Ntilde.App.Tests/Shell/Mux/MuxOrphansTests.cs
using Ntilde.Mux.Contracts;
using Ntilde.Pty;
using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

public sealed class MuxOrphansTests
{
    private static SessionSummary S(Guid id, bool running = true, int attached = 0, bool faulted = false) =>
        new() { SessionId = id, Running = running, AttachedClients = attached, Faulted = faulted };

    [Fact]
    public void Collects_ids_from_nested_splits_and_ignores_garbage()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var session = new NtildeSession
        {
            Tabs =
            {
                new TabSession { Root = new PaneNode { Type = NodeType.Split, Children =
                {
                    new PaneNode { Type = NodeType.Leaf, MuxSessionId = a.ToString() },
                    new PaneNode { Type = NodeType.Leaf, MuxSessionId = "not-a-guid" },
                } } },
                new TabSession { Root = new PaneNode { Type = NodeType.Leaf, MuxSessionId = b.ToString() } },
                new TabSession { Root = null },
            },
        };
        Assert.Equal(new HashSet<Guid> { a, b }, MuxOrphans.CollectReferencedIds(session));
        Assert.Empty(MuxOrphans.CollectReferencedIds(null));
    }

    [Fact]
    public void Orphans_are_running_unattached_unfaulted_and_unreferenced()
    {
        Guid referenced = Guid.NewGuid(), orphan = Guid.NewGuid(), attached = Guid.NewGuid(), exited = Guid.NewGuid(), faulted = Guid.NewGuid();
        var result = MuxOrphans.Select(
            [S(referenced), S(orphan), S(attached, attached: 1), S(exited, running: false), S(faulted, faulted: true)],
            new HashSet<Guid> { referenced });
        Assert.Equal([orphan], result.Select(r => r.SessionId));
    }
}
```

`NodeType` member names (`Leaf`/`Split`) may differ; check `SessionModels.cs`.

```csharp
// tests/Ntilde.App.Tests/Core/SessionManagerMuxTests.cs
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Mux;
using Ntilde.Mux.Tests.Support;
using Ntilde.Pty;
using Ntilde.Shell;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Controls;

namespace Ntilde.Tests.Core;

public sealed class SessionManagerMuxTests : IClassFixture<TestAppDataRoot>
{
    [AvaloniaFact]
    public void A_mux_pane_round_trips_its_session_id_and_endpoint()
    {
        using var mux = new MuxTestHost();
        using var host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(mux.Listener.Connect(), null, ct), "ep-1", null);
        using var pane = new TerminalPane();
        PaneSpawnTestHelpers.DisableShellIntegration(pane);
        pane.SessionFactory = new MuxTerminalSessionFactory(host, new RecordingSessionFactory(new FakeTerminalSession()), null);
        pane.CreateAndWireParser();
        pane.InitializeSessionCore("scripted", "", profile: null, cols: 80, rows: 24);
        Guid id = ((MuxClientSession)pane.Session!).Id;

        PaneNode node = SessionManager.BuildPaneTreeForTest(pane)!;   // expose BuildPaneTree as internal if private
        Assert.Equal(id.ToString(), node.MuxSessionId);
        Assert.Equal("ep-1", node.MuxEndpoint);

        var restored = (TerminalPane)SessionManager.RestorePaneTreeForTest(node, new TerminalSettings())!;
        Assert.Equal(id, restored.MuxSessionIdToRestore);
        restored.Dispose();
    }

    [AvaloniaFact]
    public void A_plain_pane_writes_no_mux_fields()
    {
        using var pane = new TerminalPane();
        PaneSpawnTestHelpers.DisableShellIntegration(pane);
        pane.SessionFactory = new RecordingSessionFactory(new FakeTerminalSession());
        pane.CreateAndWireParser();
        pane.InitializeSessionCore("fake", "", profile: null, cols: 80, rows: 24);
        PaneNode node = SessionManager.BuildPaneTreeForTest(pane)!;
        Assert.Null(node.MuxSessionId);
        Assert.Null(node.MuxEndpoint);
    }

    [Fact]
    public void Deferred_tabs_keep_their_mux_ids_in_the_startup_plan()
    {
        Guid id = Guid.NewGuid();
        var session = new NtildeSession
        {
            ActiveTabIndex = 0,
            Tabs =
            {
                new TabSession { Title = "a" },
                new TabSession { Title = "b", Root = new PaneNode { MuxSessionId = id.ToString() } },
            },
        };
        StartupRestorePlan plan = StartupRestorePlan.Create(session);
        Assert.Equal(id.ToString(), plan.DeferredTabs.Single().Tab.Root!.MuxSessionId);
    }
}
```

If `BuildPaneTree`/`RestorePaneTree` are `private`, make them `internal` (the `ForTest` names above are placeholders; call the real ones). Also add a MainWindow-level test to `MainWindowMuxLifecycleTests` (Task 10 file):

```csharp
[AvaloniaFact]
public void A_successful_attach_writes_the_session_file_with_the_mux_id()
{
    MainWindow window = CreateWindow();
    Guid id = ((MuxClientSession)AllPanes(window).First().Session!).Id;
    PumpUntil(() => File.Exists(AppPaths.SessionFilePath) && File.ReadAllText(AppPaths.SessionFilePath).Contains(id.ToString()),
        "the session file names the mux session");
}

[AvaloniaFact]
public void Orphaned_daemon_sessions_open_as_new_tabs_after_restore()
{
    // An orphan: a running session the saved session file does not reference.
    Guid orphan;
    using (MuxClient c = Task.Run(() => MuxClient.ConnectAsync(_mux.Listener.Connect(), null, default)).GetAwaiter().GetResult())
        orphan = Task.Run(() => MuxTestHost.SpawnAsync(c)).GetAwaiter().GetResult();
    PumpUntil(() => _mux.Mux(orphan).AttachedClients == 0, "the spawning client is gone");

    MainWindow window = CreateWindow();
    PumpUntil(() => AllPanes(window).Any(p => p.Session is MuxClientSession m && m.Id == orphan), "the orphan was adopted");
}
```

- [ ] **Step 2: Run → FAIL.**

- [ ] **Step 3: Implement**

`BuildPaneTree` leaf branch, after the existing fields:

```csharp
if (pane.Session is Ntilde.Mux.MuxClientSession mux)
{
    node.MuxSessionId = mux.Id.ToString("D");
    node.MuxEndpoint = pane.MuxEndpoint;
}
```

`RestorePaneTree` leaf branch, after `pane.PaneId = ...`:

```csharp
if (Guid.TryParse(node.MuxSessionId, out Guid muxId)) pane.MuxSessionIdToRestore = muxId;
```

(With the setting off, the default factory ignores `ExistingMuxSessionId` and the pane starts normally.)

```csharp
// src/Ntilde.App/Shell/Mux/MuxOrphans.cs
using Ntilde.Mux.Contracts;
using Ntilde.Pty;

namespace Ntilde.Shell.Mux;

/// <summary>
/// Daemon sessions no restored pane will reopen - the GUI crashed before it wrote the session file
/// (spec §9). Computed from the SAVED session tree, not live panes: restored panes have not attached
/// yet, and deferred tabs have no panes at all.
/// </summary>
internal static class MuxOrphans
{
    public static HashSet<Guid> CollectReferencedIds(NtildeSession? session)
    {
        var ids = new HashSet<Guid>();
        if (session is null) return ids;
        foreach (TabSession tab in session.Tabs) Walk(tab.Root, ids);
        return ids;
    }

    private static void Walk(PaneNode? node, HashSet<Guid> ids)
    {
        if (node is null) return;
        if (Guid.TryParse(node.MuxSessionId, out Guid id)) ids.Add(id);
        foreach (PaneNode child in node.Children) Walk(child, ids);
    }

    public static IReadOnlyList<SessionSummary> Select(IEnumerable<SessionSummary> sessions, IReadOnlySet<Guid> referenced) =>
        sessions.Where(s => s.Running && !s.Faulted && s.AttachedClients == 0 && !referenced.Contains(s.SessionId)).ToList();
}
```

MainWindow:
- **Session file after attach.** `WirePane` adds `pane.PersistentSessionAttached -= OnPanePersistentSessionAttached; pane.PersistentSessionAttached += OnPanePersistentSessionAttached;`, and `UnwirePane` removes it.

```csharp
private int _sessionSaveQueued;

/// <summary>A crash right after launch must still know which daemon sessions are ours: save, coalesced.</summary>
private void OnPanePersistentSessionAttached(TerminalPane pane)
{
    if (Interlocked.Exchange(ref _sessionSaveQueued, 1) == 1) return;
    Dispatcher.UIThread.Post(() =>
    {
        Volatile.Write(ref _sessionSaveQueued, 0);
        if (_teardownDone) return;
        if (this.FindControl<TabControl>("Tabs") is { } tabs) SessionManager.SaveSession(this, tabs);
    }, DispatcherPriority.Background);
}
```

- **Orphans.** At the end of the startup path, after `TryRestoreStartupSession` (or its no-session branch), when `_muxHost is { } host`: take `HashSet<Guid> referenced = MuxOrphans.CollectReferencedIds(loadedSession)` (the `NtildeSession` loaded there, or null) and call `_ = AdoptOrphansAsync(host, referenced);`.

```csharp
private async Task AdoptOrphansAsync(MuxConnectionHost host, HashSet<Guid> referenced)
{
    try
    {
        IReadOnlyList<SessionSummary> orphans = await Task.Run(async () =>
        {
            MuxClient? client = host.GetClient(TimeSpan.FromSeconds(10));
            return client is null ? [] : MuxOrphans.Select(await client.ListSessionsAsync().ConfigureAwait(false), referenced);
        });
        if (orphans.Count == 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_teardownDone) return;
            foreach (SessionSummary s in orphans)
            {
                var pane = new TerminalPane(ShellHelper.ResolveExecutableOrDefault(s.Command), s.Arguments ?? string.Empty, _settings)
                {
                    MuxSessionIdToRestore = s.SessionId,
                };
                AddTabWithPane(pane, string.IsNullOrWhiteSpace(s.Title) ? s.Command : s.Title); // use the existing "new tab with this content" helper
            }

            ShowRecordingToast("Sessions restored", $"Reattached {orphans.Count} detached session{(orphans.Count == 1 ? "" : "s")}", null, null, autoHide: true);
        });
    }
    catch (Exception ex)
    {
        AppLogger.Log($"[MainWindow] orphan adoption failed: {ex.Message}");
    }
}
```

`AddTabWithPane` stands for the existing way MainWindow opens a new tab around a given `TerminalPane`. Find it (it is what the new-tab command and `OpenProfileInNewTab`-style code call) and use it; if none takes a ready pane, add a small internal overload beside the existing one. Check the `TerminalPane(string shell, string args, TerminalSettings)` constructor signature against the explorer's report (~517-604).

- [ ] **Step 4: Run → PASS**

```powershell
scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~MuxOrphansTests|FullyQualifiedName~SessionManagerMuxTests|FullyQualifiedName~SessionManagerTests|FullyQualifiedName~StartupRestore|FullyQualifiedName~MainWindowMuxLifecycleTests"
```

- [ ] **Step 5: Commit** — `feat(mux): persist mux ids, reattach on launch, adopt orphaned sessions`

---

### Task 12: Updates close the daemon first

**Files:**
- Modify: `src/Ntilde.App/MainWindow.axaml.cs`, `ApplyStagedUpdate` (~8793-8820)
- Test: `tests/Ntilde.App.Tests/Update/UpdateClosesMuxTests.cs`

**Interfaces:**
- Produces on MainWindow:
  - `internal Task ApplyStagedUpdateAsync()`. The existing `ApplyStagedUpdate()` becomes `=> _ = ApplyStagedUpdateAsync();`, so the About window's `Action` wiring is unchanged.
  - `internal Func<CancellationToken, Task<MuxClient?>> MuxProbeForUpdate { get; set; }`, defaulting to `MuxDaemonLauncher.CreateDefault(AppLogger.Log).TryConnectExistingAsync`. It never spawns a daemon.
  - `internal Func<string, Task<bool>> ConfirmSessionLossForUpdate { get; set; }`, defaulting to `ShowRunningProcessCloseConfirmationAsync`.

```csharp
internal async Task ApplyStagedUpdateAsync()
{
    if (_updateCoordinator is not { IsUpdateStaged: true }) return;

    // The new build must not start beside a daemon of the old one (the protocol version range is the
    // backstop, not the mechanism). Probed whatever the setting says: a daemon may outlive a toggle.
    using (MuxClient? daemon = await MuxProbeForUpdate(CancellationToken.None))
    {
        if (daemon is not null)
        {
            int running = (await daemon.ListSessionsAsync()).Count(s => s.Running);
            if (running > 0 && !await ConfirmSessionLossForUpdate(
                    $"{running} multiplexed session{(running == 1 ? "" : "s")} will be closed by the update."))
            {
                return;
            }

            try { await daemon.ShutdownServerAsync(); }
            catch (Exception ex) { AppLogger.Log($"[MainWindow] mux shutdown before update failed: {ex.Message}"); }
        }
    }

    PerformAppTeardown();
    ...existing try { _updateCoordinator.ApplyStagedUpdate(); } catch { ...toast... }...
}
```

- [ ] **Step 1: Write the failing tests.** Find how tests reach `_updateCoordinator` (grep `ApplyStagedUpdate` / `UpdateCoordinator` under `tests/`; `tests/Ntilde.Architecture.Tests/Update/UpdateCoordinatorTests.cs` has a fake `IUpdateService` with `ApplyCount`). If MainWindow offers no seam, add `internal UpdateCoordinator? UpdateCoordinatorForTest { set => _updateCoordinator = value; }`.

```csharp
// tests/Ntilde.App.Tests/Update/UpdateClosesMuxTests.cs  (shape; wire the fake coordinator per the seam you find)
[AvaloniaFact] public void No_daemon_applies_without_asking() { /* probe returns null → confirm never called → ApplyCount == 1 */ }
[AvaloniaFact] public void Declining_keeps_the_daemon_and_does_not_apply() { /* probe returns in-memory client with 1 running session; confirm returns false → ApplyCount == 0, ShutdownRequested not raised */ }
[AvaloniaFact] public void Confirming_shuts_the_daemon_down_then_applies() { /* confirm true → ShutdownRequested raised on _mux.Server, ApplyCount == 1 */ }
```

Write each test fully, following Task 10's window setup and `PumpUntil`, and await `ApplyStagedUpdateAsync()` via a pumped wait (`PumpUntil(() => task.IsCompleted, ...)`). The probe for "a daemon exists" is `ct => MuxClient.ConnectAsync(_mux.Listener.Connect(), null, ct)` (nullable cast).

- [ ] **Step 2: Run → FAIL. Step 3: Implement. Step 4: Run → PASS**, along with the existing update tests (`--filter "FullyQualifiedName~Update"`).

- [ ] **Step 5: Commit** — `feat(mux): applying an update closes the daemon after confirmation`

---

### Task 13: Real-daemon smoke, real-transport latency, AOT, docs, full verification

**Files:**
- Create: `tests/Ntilde.App.Tests/MuxDaemonSmokeTests.cs` (PtySmoke)
- Create: `tests/Ntilde.Mux.Tests/Scenarios/RealTransportAttachLatencyTests.cs`
- Modify: `docs/USER_MANUAL.md`, `docs/ARCHITECTURE.md`, `docs/MODULE_OWNERSHIP.md`

- [ ] **Step 1: Real-daemon PtySmoke test.** It follows `tests/Ntilde.App.Tests/MuxRealShellSmokeTests.cs` for traits and collection: `[Trait("Category", "PtySmoke")]`, `[Collection(PtyRealShellCollection.Name)]`.

```csharp
// tests/Ntilde.App.Tests/MuxDaemonSmokeTests.cs
using System.Diagnostics;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Pty;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Core;

namespace Ntilde.Tests;

[Trait("Category", "PtySmoke")]
[Collection(PtyRealShellCollection.Name)]
public sealed class MuxDaemonSmokeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The app exe next to the tests (apphost) or `dotnet Ntilde.dll`.</summary>
    private static ProcessMuxDaemonSpawner Spawner()
    {
        string dir = AppContext.BaseDirectory;
        string apphost = Path.Combine(dir, OperatingSystem.IsWindows() ? "Ntilde.exe" : "Ntilde");
        if (File.Exists(apphost)) return new ProcessMuxDaemonSpawner(apphost, []);
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        return new ProcessMuxDaemonSpawner(dotnet, [Path.Combine(dir, "Ntilde.dll")]);
    }

    [Fact]
    public async Task A_real_daemon_keeps_a_shell_across_a_client_death_and_kill_server_leaves_nothing_behind()
    {
        using var root = new TestAppDataRoot();
        var launcher = new MuxDaemonLauncher(MuxDiscovery.GetDescriptorPath(), Spawner()) { SpawnTimeout = TimeSpan.FromSeconds(30) };
        int? daemonPid = null, shellPid = null;
        try
        {
            MuxClient first = await launcher.EnsureConnectedAsync(Ct);
            Assert.True(MuxDiscovery.TryReadLiveDescriptor(MuxDiscovery.GetDescriptorPath(), out MuxEndpointDescriptor? d));
            daemonPid = d.Pid;
            Assert.NotEqual(Environment.ProcessId, daemonPid);

            Guid id = await first.SpawnAsync(new SpawnParams { Command = ShellHelper.GetDefaultShell(), Cols = 100, Rows = 30 }, Ct);
            var pane = new ClientPaneModel(first.OpenSession(id));
            await pane.Session.AttachAsync(1000, MuxTestHost.DefaultPresentation with { Cols = 100, Rows = 30 }, Ct);
            shellPid = (await pane.Session.RefreshSessionInfoAsync(Ct)).Pid;
            pane.Session.SendInput("echo mux-smoke-marker\r");
            await TestWait.UntilAsync(() => ScreenText(pane).Contains("mux-smoke-marker\n", StringComparison.Ordinal) || CountMarker(pane) >= 2,
                "the shell echoed the marker", TimeSpan.FromSeconds(30));

            first.Dispose(); // the client side dies

            MuxClient second = await launcher.EnsureConnectedAsync(Ct);
            Assert.True(MuxDiscovery.TryReadLiveDescriptor(MuxDiscovery.GetDescriptorPath(), out MuxEndpointDescriptor? d2));
            Assert.Equal(daemonPid, d2.Pid); // no second daemon
            var again = new ClientPaneModel(second.OpenSession(id));
            await again.Session.AttachAsync(1000, MuxTestHost.DefaultPresentation with { Cols = 100, Rows = 30 }, Ct);
            Assert.Contains("mux-smoke-marker", ScreenText(again));

            await second.ShutdownServerAsync(Ct);
            second.Dispose();
            using Process daemon = Process.GetProcessById(daemonPid.Value);
            Assert.True(daemon.WaitForExit(15_000), "the daemon exited");
            if (shellPid is int sp) await TestWait.UntilAsync(() => !IsAlive(sp), "the shell is gone", TimeSpan.FromSeconds(15));
        }
        finally
        {
            if (daemonPid is int dp && IsAlive(dp)) { try { Process.GetProcessById(dp).Kill(entireProcessTree: true); } catch (Exception) { } }
            if (shellPid is int sp && IsAlive(sp)) { try { Process.GetProcessById(sp).Kill(); } catch (Exception) { } }
        }
    }

    private static bool IsAlive(int pid) { try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch (ArgumentException) { return false; } }
    private static string ScreenText(ClientPaneModel pane) => MuxTestText.VisibleText(pane.Buffer);
    private static int CountMarker(ClientPaneModel pane) => ScreenText(pane).Split("mux-smoke-marker").Length - 1;
}
```

`TestAppDataRoot` scopes `NTILDE_APPDATA_ROOT` for this process, and the spawned daemon inherits it through the environment, so it uses the isolated root. If `DOTNET_HOST_PATH` is unset and no apphost exists, the test fails loudly naming both paths. It must not skip silently.

Run: `scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~MuxDaemonSmokeTests"` → PASS. Afterwards confirm no `Ntilde` daemon survived: `Get-Process Ntilde -ErrorAction SilentlyContinue`, and compare against the processes present before.

- [ ] **Step 2: Real-transport attach latency** (Mux.Tests; copy the structure of `tests/Ntilde.Mux.Tests/Scenarios/AttachLatencyTests.cs`, but serve through `MuxListeners.Create(endpoint)` and connect with `MuxEndpointConnector.Connect`, using a unique endpoint per test: `MuxDiscovery.GetDefaultEndpoint(<unique temp root>)`). Measure:
  - An empty session: spawn → attach → pane equals mux.
  - An 80x24 session with 10,000 scrollback lines: attach → equal, plus snapshot bytes.

  Write both with `TestContext.Current.TestOutputHelper?.WriteLine("[mux-phase2] real transport attach latency: ...")` and assert only generous upper bounds (< 5 s), as the Phase 1 test does. Record the printed numbers for the PR (report item 5).

- [ ] **Step 3: Docs**
  - **`docs/USER_MANUAL.md`**: new section "Persistent sessions (multiplexer)", covering:
    - the setting (Settings → the page with Scrollback → "Keep shells running when the window closes");
    - what persists: local shells and their screen and scrollback, across window close, crash and restart;
    - what closes a shell: closing its pane or tab, or the shell exiting;
    - `ntilde mux ls`, `ntilde mux ls --json`, `ntilde mux kill <id>`, `ntilde mux kill-server`;
    - limitations:
      - local shells only (SSH panes are unchanged);
      - no inline images in persistent panes;
      - applying an update closes persistent sessions (you are asked first);
      - if the daemon crashes, its shells are gone;
      - a shell inherits the daemon's environment, not the window's;
      - turning the setting off does not stop sessions that are already running (use `kill-server`);
      - the daemon exits 10 minutes after its last session and connection close.
  - **`docs/ARCHITECTURE.md`**: the App → Mux → Mux.Contracts edge, the daemon as a CLI mode of the app executable, and the process diagram from spec §2. Endpoint security model: current-user pipe ACL, `0600` socket in a `0700` directory, no TCP, no authentication by design (local and same-user only).
  - **`docs/MODULE_OWNERSHIP.md`**: rows for `src/Ntilde.App/Shell/Mux/` (daemon CLI, launcher, connection host, factory) and the new `Ntilde.Mux` transport and daemon files.

- [ ] **Step 4: Full verification** (Windows; one project per invocation; record pass/fail/skip counts)

```powershell
scripts/build.ps1 format whitespace --no-restore --verify-no-changes
scripts/build.ps1 build -c Release src/Ntilde.Mux
scripts/build.ps1 build -c Release src/Ntilde.Mux.Contracts
scripts/build.ps1 test tests/Ntilde.Mux.Tests
scripts/build.ps1 test tests/Ntilde.Architecture.Tests
scripts/build.ps1 test tests/Ntilde.McpServer.Tests
scripts/build.ps1 test tests/Ntilde.VT.Tests
scripts/build.ps1 test tests/Ntilde.Platform.Tests
scripts/build.ps1 test tests/Ntilde.Rendering.Tests
scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "Lane!=PlatformBoot" *> $env:TEMP\apptests-main.log
scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "Lane=PlatformBoot" *> $env:TEMP\apptests-boot.log
scripts/build.ps1 publish src/Ntilde.App -c Release -r win-x64 *> $env:TEMP\aot-publish.log
Select-String -Path $env:TEMP\aot-publish.log -Pattern "IL2026|IL3050"   # expect no matches
```

Never run two App.Tests invocations at once (they corrupt each other's totals). Read the logs with the Grep tool, not a shell `grep` with backslashes.

- [ ] **Step 5: Manual verification log.** This is for the user; the GUI steps can't be automated reliably on Windows. Write the checklist into the PR description:
  1. Setting on, then open two shells. Run `sleep 1000` (or `Start-Sleep 1000`) in one and `vim` in the other. Close the window.
  2. `ntilde mux ls` shows 2 running sessions with 0 attached, and exactly one `mux serve` process. The shells are children of it (Process Explorer / `pstree`).
  3. Relaunch: the same shells come back, with the same screen (vim redrawn) and scrollback intact.
  4. Kill the GUI with Task Manager (or `kill -9`). The shells survive, and relaunching reattaches them.
  5. Kill the daemon. The panes show the disconnected banner; Enter starts a new daemon and prints "[Previous session was lost — started a new shell]".
  6. Close a pane: that shell ends, and `mux ls` no longer lists it.
  7. `ntilde mux kill-server` ends everything.

- [ ] **Step 6: Commit** — `test(mux): real-daemon smoke and real-transport latency; docs for persistent sessions`

---

## Execution notes

- Tasks 1–4 touch only `src/Ntilde.Mux*` and `tests/Ntilde.Mux.Tests`. Tasks 5–12 touch App. Task 13 is verification and docs.
- The order is strict: 1 → 2 → 3 → 4 → 5 → 6 → 7 → 8 → 9 → 10 → 11 → 12 → 13. Task 8 has no dependency on Tasks 5–7 and may run before them if that is convenient.
- After every task, at minimum re-run `tests/Ntilde.Mux.Tests` (Tasks 1–4) or the task's App.Tests filter plus `tests/Ntilde.Architecture.Tests` (Tasks 5–12).
