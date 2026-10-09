using System.IO.Pipes;
using System.Text;
using Ntilde.Mux.Cli;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Daemon;
using Ntilde.Mux.Tests.Support;
using Ntilde.Mux.Transport;
using Ntilde.VT.Export;

namespace Ntilde.Mux.Tests.Cli;

/// <summary>
/// <c>ntilde-mux proxy --stdio</c> (Phase 4 spec §8.1, §12.1): an in-process daemon on a temp root, the
/// proxy on a thread of its own with real anonymous pipes for its stdin and stdout, and a
/// <see cref="MuxClient"/> speaking through <see cref="StdioMuxTransport"/> - the GUI's side of an exec
/// channel. The daemon endpoint is the real one: a Unix socket on Linux/macOS, a named pipe on Windows.
/// </summary>
public sealed class MuxProxyTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Patient = TimeSpan.FromSeconds(30);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nmxp" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<IDisposable> _owned = new();
    private readonly ScriptedSessionFactory _shells = new();

    public void Dispose()
    {
        IDisposable[] owned;
        lock (_owned) owned = _owned.ToArray();
        for (int i = owned.Length - 1; i >= 0; i--) owned[i].Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // Called from the test thread and, via InProcessSpawner.Spawn -> StartDaemon, the proxy's thread.
    private T Own<T>(T disposable) where T : IDisposable
    {
        lock (_owned) _owned.Add(disposable);
        return disposable;
    }

    private MuxDaemonHost StartDaemon()
    {
        var server = new MuxServer(_shells, new MuxServerOptions { ForceConPtyFiltering = false });
        var host = new MuxDaemonHost(server, new MuxDaemonOptions
        {
            Endpoint = MuxDiscovery.GetDefaultEndpoint(_root),
            DescriptorPath = MuxDiscovery.GetDescriptorPath(_root),
            IdleExitAfter = TimeSpan.Zero,
        });
        host.Start();
        return Own(host);
    }

    /// <summary>What the verb hands the proxy, with a test's spawner: the launcher on this root.</summary>
    private Func<CancellationToken, Task<(Stream Stream, MuxEndpointDescriptor Descriptor)>> ConnectThroughLauncher(IMuxDaemonSpawner spawner)
    {
        var launcher = new MuxDaemonLauncher(MuxDiscovery.GetDescriptorPath(_root), spawner) { SpawnTimeout = TimeSpan.FromSeconds(10) };
        return launcher.EnsureEndpointStreamAsync;
    }

    private async Task<MuxClient> ConnectDirectAsync() =>
        Own(await MuxClient.ConnectAsync(MuxEndpointConnector.Connect(MuxDiscovery.GetDefaultEndpoint(_root), TimeSpan.FromSeconds(5)), null, Ct));

    [Fact]
    public async Task Proxy_pumps_frames_both_ways_over_a_real_endpoint()
    {
        StartDaemon();
        ProxyRun proxy = Own(new ProxyRun(ConnectThroughLauncher(new NoSpawner())));
        StdioMuxConnection connection = await proxy.ConnectAsync();
        MuxClient client = Own(await MuxClient.ConnectAsync(connection.Stream, null, Ct));
        Assert.Equal(Environment.ProcessId, connection.DaemonPid);   // the in-process daemon's descriptor

        Guid id = await MuxTestHost.SpawnAsync(client);
        MuxClientSession session = client.OpenSession(id, "scripted");
        var pane = new ClientPaneModel(session);
        var output = new StringBuilder();
        var sawHi = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // After the pane's own handler: by the time this sees "hi", the pane's parser has drawn it.
        session.OnOutputReceived += text =>
        {
            lock (output)
            {
                output.Append(text);
                if (output.ToString().Contains("hi", StringComparison.Ordinal)) sawHi.TrySetResult();
            }
        };
        await session.AttachAsync(10_000, MuxTestHost.DefaultPresentation, Ct);

        session.SendInput("echo hi\r");
        ScriptedTerminalSession shell = _shells.LastScriptedSession!;
        await TestWait.UntilAsync(() => string.Concat(shell.SentInput).Contains("echo hi\r", StringComparison.Ordinal),
            "the input reached the session through the proxy");
        shell.Emit("hi\r\n");

        await sawHi.Task.WaitAsync(Patient, Ct);
        Assert.Contains(TerminalExporter.GetVisibleRowTexts(pane.Buffer), row => row.StartsWith("hi", StringComparison.Ordinal));
    }

    /// <summary>Stdout carries the preamble and the daemon's frames, nothing else: the daemon never speaks unasked.</summary>
    [Fact]
    public async Task The_preamble_is_all_the_proxy_writes_before_the_first_frame()
    {
        StartDaemon();
        ProxyRun proxy = Own(new ProxyRun(ConnectThroughLauncher(new NoSpawner())));
        byte[] expected = MuxProxyPreamble.Format(Environment.ProcessId);
        byte[] got = new byte[expected.Length];

        await proxy.ClientStdout.ReadExactlyAsync(got, Ct).AsTask().WaitAsync(Patient, Ct);
        proxy.ClientStdin.Dispose();

        Assert.Equal(expected, got);
        Assert.Equal(MuxProxyExitCodes.StdinClosed, await proxy.Exit.WaitAsync(Patient, Ct));
        Assert.Equal(-1, proxy.ClientStdout.ReadByte());   // stdout ends: nothing followed the preamble
        Assert.Equal(string.Empty, proxy.Stderr);
    }

    [Fact]
    public async Task Stdin_EOF_exits_0_and_the_daemon_keeps_the_session()
    {
        StartDaemon();
        ProxyRun proxy = Own(new ProxyRun(ConnectThroughLauncher(new NoSpawner())));
        StdioMuxConnection connection = await proxy.ConnectAsync();
        MuxClient client = Own(await MuxClient.ConnectAsync(connection.Stream, null, Ct));
        Guid id = await MuxTestHost.SpawnAsync(client);

        proxy.ClientStdin.Dispose();   // the channel's stdin ends: the GUI closed it

        Assert.Equal(MuxProxyExitCodes.StdinClosed, await proxy.Exit.WaitAsync(Patient, Ct));
        await TestWait.UntilAsync(() => !client.IsConnected, "the client saw the proxy's stdout end");
        MuxClient direct = await ConnectDirectAsync();
        Assert.Contains(await direct.ListSessionsAsync(Ct), s => s.SessionId == id && s.Running);
        Assert.Equal(string.Empty, proxy.Stderr);
    }

    /// <summary>
    /// A daemon that stops (kill-server, its idle exit, an update's shutdown) closes its connections first and
    /// exits a moment later. The proxy waits for that exit (codex D1), so its 3 still says the daemon is gone. The
    /// in-process daemon's process is this test's, so its liveness is the test's to say.
    /// </summary>
    [Fact]
    public async Task Daemon_closing_exits_3_once_its_process_is_gone()
    {
        MuxDaemonHost daemon = StartDaemon();
        long exitedAt = long.MaxValue; // Environment.TickCount64 from which the daemon's process is gone
        ProxyRun proxy = Own(new ProxyRun(ConnectThroughLauncher(new NoSpawner()), isDaemonAlive: _ => Environment.TickCount64 < Volatile.Read(ref exitedAt)));
        StdioMuxConnection connection = await proxy.ConnectAsync();
        MuxClient client = Own(await MuxClient.ConnectAsync(connection.Stream, null, Ct));
        await client.PingAsync(Ct);

        daemon.Dispose();                                                   // closes the connections...
        Volatile.Write(ref exitedAt, Environment.TickCount64 + 300);        // ...and the process exits a moment later

        await TestWait.UntilAsync(() => !client.IsConnected, "the client saw the proxy's stdout close");
        Assert.Equal(MuxProxyExitCodes.DaemonClosed, await proxy.Exit.WaitAsync(Patient, Ct));
    }

    /// <summary>
    /// Codex D1: a live daemon ends one connection too and keeps its sessions - here a newer connection with the same
    /// client instance id replaces it, as a reconnect does (a client too slow to keep up is dropped the same way).
    /// The daemon did not stop, so the proxy says 4, not 3. The real liveness check: the descriptor's pid, name and
    /// start token are this process's.
    /// </summary>
    [Fact]
    public async Task A_daemon_that_drops_this_connection_but_runs_on_exits_4()
    {
        StartDaemon();
        ProxyRun proxy = Own(new ProxyRun(ConnectThroughLauncher(new NoSpawner())));
        StdioMuxConnection connection = await proxy.ConnectAsync();
        MuxClient client = Own(await MuxClient.ConnectAsync(connection.Stream, new MuxClientOptions { ClientInstanceId = "gui-1" }, Ct));
        Guid id = await MuxTestHost.SpawnAsync(client);

        MuxClient twin = Own(await MuxClient.ConnectAsync(
            MuxEndpointConnector.Connect(MuxDiscovery.GetDefaultEndpoint(_root), TimeSpan.FromSeconds(5)),
            new MuxClientOptions { ClientInstanceId = "gui-1" },
            Ct));

        Assert.Equal(MuxProxyExitCodes.ConnectionClosed, await proxy.Exit.WaitAsync(Patient, Ct));
        await TestWait.UntilAsync(() => !client.IsConnected, "the client saw the proxy's stdout close");
        Assert.Contains(await twin.ListSessionsAsync(Ct), s => s.SessionId == id && s.Running);
    }

    /// <summary>
    /// Codex D1: the liveness check is the descriptor's pid, name and start token, so a process that took the pid
    /// since does not count as the daemon: its connection ending is a stopped daemon, 3.
    /// </summary>
    [Fact]
    public async Task A_recycled_pid_is_a_daemon_that_is_gone()
    {
        MuxDaemonHost daemon = StartDaemon();
        Func<CancellationToken, Task<(Stream Stream, MuxEndpointDescriptor Descriptor)>> launcher = ConnectThroughLauncher(new NoSpawner());
        ProxyRun proxy = Own(new ProxyRun(async ct =>
        {
            (Stream stream, MuxEndpointDescriptor descriptor) = await launcher(ct);
            // This process's pid and name, with another start token: someone else's process now.
            return (stream, descriptor with { StartTime = (descriptor.StartTime ?? 0) + TimeSpan.FromSeconds(5).Ticks });
        }));
        StdioMuxConnection connection = await proxy.ConnectAsync();
        MuxClient client = Own(await MuxClient.ConnectAsync(connection.Stream, null, Ct));
        await client.PingAsync(Ct);

        daemon.Dispose();

        Assert.Equal(MuxProxyExitCodes.DaemonClosed, await proxy.Exit.WaitAsync(Patient, Ct));
    }

    /// <summary>
    /// Codex D1: a stdout that can no longer be written means nobody reads the exit code, and it says nothing about
    /// the daemon: never 3, even with the daemon's process gone.
    /// </summary>
    [Fact]
    public async Task A_stdout_that_cannot_be_written_never_exits_3()
    {
        StartDaemon();
        FailingStdout? stdout = null;
        ProxyRun proxy = Own(new ProxyRun(ConnectThroughLauncher(new NoSpawner()), isDaemonAlive: _ => false, wrapStdout: s => stdout = new FailingStdout(s)));
        StdioMuxConnection connection = await proxy.ConnectAsync();   // the preamble got through
        stdout!.Fail = true;                                           // nobody reads stdout from here on

        Task<MuxClient> hello = MuxClient.ConnectAsync(connection.Stream, null, Ct);   // the daemon's welcome cannot be written

        Assert.Equal(MuxProxyExitCodes.ConnectionClosed, await proxy.Exit.WaitAsync(Patient, Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => hello.WaitAsync(Patient, Ct));
    }

    /// <summary>
    /// Codex D1, residual R1: once the daemon side ended, the process's own stdout and stderr end (on a real host,
    /// every descriptor of the channel's pipes goes to /dev/null) before the wait for the daemon's process, so the
    /// client sees the end at once, not when the proxy exits up to 1.5 s later. Nothing is written after.
    /// </summary>
    [Fact]
    public async Task The_channels_output_ends_before_the_wait_for_the_daemons_process()
    {
        StartDaemon();
        var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
        ProxyRun proxy = Own(new ProxyRun(
            ConnectThroughLauncher(new NoSpawner()),
            isDaemonAlive: _ =>
            {
                order.Enqueue("is the daemon alive?");
                return true;
            },
            endStdio: () => order.Enqueue("stdout and stderr ended")));
        StdioMuxConnection connection = await proxy.ConnectAsync();
        Own(await MuxClient.ConnectAsync(connection.Stream, new MuxClientOptions { ClientInstanceId = "gui-1" }, Ct));

        Own(await MuxClient.ConnectAsync(
            MuxEndpointConnector.Connect(MuxDiscovery.GetDefaultEndpoint(_root), TimeSpan.FromSeconds(5)),
            new MuxClientOptions { ClientInstanceId = "gui-1" },
            Ct));   // the daemon drops the proxy's connection, and runs on

        Assert.Equal(MuxProxyExitCodes.ConnectionClosed, await proxy.Exit.WaitAsync(Patient, Ct));
        Assert.Equal("stdout and stderr ended", order.FirstOrDefault());
        Assert.Single(order, step => step == "stdout and stderr ended");
        Assert.Equal(string.Empty, proxy.Stderr);
    }

    /// <summary>Codex D1: the same for the preamble, the first write.</summary>
    [Fact]
    public void A_stdout_closed_before_the_preamble_never_exits_3()
    {
        StartDaemon();
        var stdout = new FailingStdout(new MemoryStream()) { Fail = true };

        int code = MuxProxyCommand.Run(new MemoryStream(), stdout, new StringWriter(), ConnectThroughLauncher(new NoSpawner()), isDaemonAlive: _ => false);

        Assert.Equal(MuxProxyExitCodes.ConnectionClosed, code);
    }

    /// <summary>Phase 5 task 8: every proxy points the daemon's stable agent link at its own connection's agent.</summary>
    [Fact]
    public void The_proxy_repoints_the_agent_link_to_its_own_SSH_AUTH_SOCK()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix sockets and symlinks only.");
        string dir = Path.Combine(_root, "mux");
        Directory.CreateDirectory(dir);
        string agentA = Path.Combine(dir, "a.sock");
        string agentB = Path.Combine(dir, "b.sock");
        using var a = ListenOn(agentA);
        using var b = ListenOn(agentB);
        string link = AgentSocketLink.PathFor(dir);
        var descriptor = new MuxEndpointDescriptor { Endpoint = Path.Combine(dir, "mux.sock"), ProcessName = "x", Pid = 1 };
        Task<(Stream, MuxEndpointDescriptor)> Connect(CancellationToken _) => Task.FromResult<(Stream, MuxEndpointDescriptor)>((new MemoryStream(), descriptor));

        MuxProxyCommand.Run(new MemoryStream(), new MemoryStream(), new StringWriter(), Connect, _ => false, getEnvironmentVariable: n => n == "SSH_AUTH_SOCK" ? agentA : null);
        Assert.Equal(agentA, new FileInfo(link).LinkTarget);

        MuxProxyCommand.Run(new MemoryStream(), new MemoryStream(), new StringWriter(), Connect, _ => false, getEnvironmentVariable: n => n == "SSH_AUTH_SOCK" ? agentB : null);
        Assert.Equal(agentB, new FileInfo(link).LinkTarget);

        // No SSH_AUTH_SOCK on a later connection: the last good link stays.
        MuxProxyCommand.Run(new MemoryStream(), new MemoryStream(), new StringWriter(), Connect, _ => false, getEnvironmentVariable: _ => null);
        Assert.Equal(agentB, new FileInfo(link).LinkTarget);
    }

    private static System.Net.Sockets.Socket ListenOn(string path)
    {
        var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
        socket.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
        socket.Listen(4);
        return socket;
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("io")]
    [InlineData("timeout")]
    public void Unreachable_daemon_exits_1_with_a_message(string failure)
    {
        Exception why = failure switch
        {
            "unavailable" => new MuxUnavailableException("Another multiplexer is running but cannot be reached."),
            "io" => new IOException("Could not connect to /run/mux.sock: Connection refused"),
            _ => new TimeoutException("Timed out connecting to /run/mux.sock."),
        };
        var stdout = new MemoryStream();
        var stderr = new StringWriter();

        int code = MuxProxyCommand.Run(new MemoryStream(), stdout, stderr, _ => Task.FromException<(Stream, MuxEndpointDescriptor)>(why));

        Assert.Equal(MuxProxyExitCodes.DaemonUnavailable, code);
        Assert.Empty(stdout.ToArray());   // not even the preamble
        Assert.Equal($"mux: {why.Message}{Environment.NewLine}", stderr.ToString());
    }

    [Fact]
    public void A_daemon_that_never_comes_up_exits_1_through_the_launcher()
    {
        var launcher = new MuxDaemonLauncher(MuxDiscovery.GetDescriptorPath(_root), new NoopSpawner())
        {
            SpawnTimeout = TimeSpan.FromMilliseconds(300),
            RespawnAfter = TimeSpan.FromSeconds(30),
        };
        var stdout = new MemoryStream();
        var stderr = new StringWriter();

        int code = MuxProxyCommand.Run(new MemoryStream(), stdout, stderr, launcher.EnsureEndpointStreamAsync);

        Assert.Equal(MuxProxyExitCodes.DaemonUnavailable, code);
        Assert.Empty(stdout.ToArray());
        Assert.StartsWith("mux: The multiplexer did not come up", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Daemon_is_spawned_on_demand()
    {
        var spawner = new InProcessSpawner(this);
        ProxyRun proxy = Own(new ProxyRun(ConnectThroughLauncher(spawner)));

        StdioMuxConnection connection = await proxy.ConnectAsync();
        MuxClient client = Own(await MuxClient.ConnectAsync(connection.Stream, null, Ct));
        await client.PingAsync(Ct);

        Assert.Equal(1, Volatile.Read(ref spawner.Spawns));
        Assert.Equal(Environment.ProcessId, connection.DaemonPid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--stdin")]
    [InlineData("--stdio --extra")]
    public void Proxy_without_exactly_stdio_is_a_usage_error(string optionLine)
    {
        string[] options = optionLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        MuxCliHost host = new()
        {
            Paths = new MuxPaths(_root),
            UsagePrefix = "ntilde-mux",
            ServeArguments = ["serve"],
            SessionFactory = () => _shells,
            Verbs = MuxCliVerbs.Serve | MuxCliVerbs.Proxy | MuxCliVerbs.Version,
        };

        int code = MuxCli.Execute(["proxy", .. options], stdout, stderr, host);

        Assert.Equal(MuxProxyExitCodes.Usage, code);
        Assert.Equal(string.Empty, stdout.ToString());   // a proxy's stdout is the channel: usage goes to stderr
        Assert.Contains("ntilde-mux proxy --stdio", stderr.ToString(), StringComparison.Ordinal);
    }

    // ---- release hardening item 7: a listing's proxy (--no-spawn) has no side effects

    [Theory]
    [InlineData("--stdio", false)]
    [InlineData("--stdio --no-spawn", true)]
    [InlineData("--no-spawn --stdio", true)]
    public void Proxy_parses_stdio_and_an_optional_no_spawn(string optionLine, bool noSpawn)
    {
        Assert.True(MuxCli.TryParseProxy(["proxy", .. optionLine.Split(' ')], out bool parsed));
        Assert.Equal(noSpawn, parsed);
    }

    [Theory]
    [InlineData("--no-spawn")]
    [InlineData("--stdio --no-spawn --no-spawn")]
    [InlineData("--stdio --stdio")]
    public void Proxy_refuses_no_spawn_without_exactly_one_stdio(string optionLine)
    {
        Assert.False(MuxCli.TryParseProxy(["proxy", .. optionLine.Split(' ')], out _));
    }

    [Fact]
    public void A_no_spawn_proxy_with_no_daemon_exits_not_running_and_starts_nothing()
    {
        var spawner = new InProcessSpawner(this);
        var stdout = new MemoryStream();
        var stderr = new StringWriter();

        int code = MuxProxyCommand.Run(new MemoryStream(), stdout, stderr, MuxCli.ProxyConnector(MuxDiscovery.GetDescriptorPath(_root), spawner, noSpawn: true));

        Assert.Equal(MuxProxyExitCodes.NotRunning, code);
        Assert.Equal(0, Volatile.Read(ref spawner.Spawns));
        Assert.False(File.Exists(MuxDiscovery.GetDescriptorPath(_root)), "no daemon was started");
        Assert.Empty(stdout.ToArray());   // not even the preamble
        Assert.Equal($"mux: No multiplexer is running.{Environment.NewLine}", stderr.ToString());
    }

    [Fact]
    public async Task A_no_spawn_proxy_reaches_a_running_daemon()
    {
        StartDaemon();
        ProxyRun proxy = Own(new ProxyRun(MuxCli.ProxyConnector(MuxDiscovery.GetDescriptorPath(_root), new NoSpawner(), noSpawn: true)));

        StdioMuxConnection connection = await proxy.ConnectAsync();
        MuxClient client = Own(await MuxClient.ConnectAsync(connection.Stream, null, Ct));

        Assert.Empty(await client.ListSessionsAsync(Ct));
    }

    [Fact]
    public void Without_no_spawn_the_proxy_connector_still_spawns_on_demand()
    {
        var spawner = new InProcessSpawner(this);
        var stdout = new MemoryStream();

        int code = MuxProxyCommand.Run(new MemoryStream(), stdout, new StringWriter(), MuxCli.ProxyConnector(MuxDiscovery.GetDescriptorPath(_root), spawner, noSpawn: false), isDaemonAlive: _ => false);

        Assert.Equal(1, Volatile.Read(ref spawner.Spawns));
        Assert.NotEqual(MuxProxyExitCodes.NotRunning, code);
    }

    /// <summary>The daemon must already be running: a spawn here means the launcher missed it.</summary>
    private sealed class NoSpawner : IMuxDaemonSpawner
    {
        public void Spawn() => throw new InvalidOperationException("the daemon is already running; nothing should be spawned");
    }

    private sealed class NoopSpawner : IMuxDaemonSpawner
    {
        public void Spawn() { }
    }

    /// <summary>A "spawn" that starts the in-process daemon on this root, as the real one would start ntilde-mux serve.</summary>
    private sealed class InProcessSpawner(MuxProxyTests t) : IMuxDaemonSpawner
    {
        public int Spawns;

        public void Spawn()
        {
            Interlocked.Increment(ref Spawns);
            t.StartDaemon();
        }
    }

    /// <summary>
    /// A stdout whose reader can go away: once <see cref="Fail"/> is set, a write fails as a write into a pipe
    /// nobody reads does.
    /// </summary>
    private sealed class FailingStdout(Stream inner) : Stream
    {
        private volatile bool _fail;

        public bool Fail { get => _fail; set => _fail = value; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_fail) throw new IOException("The pipe is being closed.");
            inner.Write(buffer, offset, count);
        }

        public override void Flush()
        {
            if (_fail) throw new IOException("The pipe is being closed.");
            inner.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// One <see cref="MuxProxyCommand.Run"/> on a thread of its own, as sshd runs <c>ntilde-mux proxy
    /// --stdio</c>: its stdin and stdout are real anonymous pipes, and the test holds their other ends.
    /// </summary>
    private sealed class ProxyRun : IDisposable
    {
        private readonly AnonymousPipeClientStream _proxyStdin;
        private readonly AnonymousPipeClientStream _proxyStdout;
        private readonly StringWriter _stderr = new();
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <param name="isDaemonAlive">The proxy's liveness check; null for the real one.</param>
        /// <param name="wrapStdout">Wraps the proxy's stdout, to fail its writes.</param>
        /// <param name="endStdio">What the proxy calls to end its process's stdout and stderr; null, as in-process runs must.</param>
        public ProxyRun(
            Func<CancellationToken, Task<(Stream Stream, MuxEndpointDescriptor Descriptor)>> connectDaemon,
            Func<MuxEndpointDescriptor, bool>? isDaemonAlive = null,
            Func<Stream, Stream>? wrapStdout = null,
            Action? endStdio = null)
        {
            ClientStdin = new AnonymousPipeServerStream(PipeDirection.Out);
            ClientStdout = new AnonymousPipeServerStream(PipeDirection.In);
            _proxyStdin = new AnonymousPipeClientStream(PipeDirection.In, ClientStdin.ClientSafePipeHandle);
            _proxyStdout = new AnonymousPipeClientStream(PipeDirection.Out, ClientStdout.ClientSafePipeHandle);
            Stream proxyStdout = wrapStdout?.Invoke(_proxyStdout) ?? _proxyStdout;
            TextWriter stderr = TextWriter.Synchronized(_stderr);
            var thread = new Thread(() =>
            {
                try { _exit.TrySetResult(MuxProxyCommand.Run(_proxyStdin, proxyStdout, stderr, connectDaemon, isDaemonAlive, endStdio)); }
                catch (Exception ex) { _exit.TrySetException(ex); }
            })
            { IsBackground = true, Name = "TestMuxProxy" };
            thread.Start();
        }

        /// <summary>The channel's stdin, as the GUI holds it: what is written here reaches the proxy's stdin.</summary>
        public AnonymousPipeServerStream ClientStdin { get; }

        /// <summary>The channel's stdout, as the GUI holds it: the proxy's stdout.</summary>
        public AnonymousPipeServerStream ClientStdout { get; }

        public Task<int> Exit => _exit.Task;

        /// <summary>Read once the proxy has exited.</summary>
        public string Stderr => _stderr.ToString();

        public Task<StdioMuxConnection> ConnectAsync() => StdioMuxTransport.ConnectAsync(ClientStdout, ClientStdin, Patient, Ct);

        /// <summary>
        /// Writer ends first, so every blocked read sees its EOF (an anonymous pipe's read end does not
        /// unblock a pending read when disposed): the proxy's stdin, which ends a proxy still running,
        /// then the proxy's stdout, which ends the client's reader. Then the read ends.
        /// </summary>
        public void Dispose()
        {
            ClientStdin.Dispose();
            _proxyStdout.Dispose();
            _proxyStdin.Dispose();
            ClientStdout.Dispose();
        }
    }
}
