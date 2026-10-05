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
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
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

    [Fact]
    public async Task Daemon_closing_exits_3()
    {
        MuxDaemonHost daemon = StartDaemon();
        ProxyRun proxy = Own(new ProxyRun(ConnectThroughLauncher(new NoSpawner())));
        StdioMuxConnection connection = await proxy.ConnectAsync();
        MuxClient client = Own(await MuxClient.ConnectAsync(connection.Stream, null, Ct));
        await client.PingAsync(Ct);

        daemon.Dispose();

        Assert.Equal(MuxProxyExitCodes.DaemonClosed, await proxy.Exit.WaitAsync(Patient, Ct));
        await TestWait.UntilAsync(() => !client.IsConnected, "the client saw the proxy's stdout close");
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
    /// One <see cref="MuxProxyCommand.Run"/> on a thread of its own, as sshd runs <c>ntilde-mux proxy
    /// --stdio</c>: its stdin and stdout are real anonymous pipes, and the test holds their other ends.
    /// </summary>
    private sealed class ProxyRun : IDisposable
    {
        private readonly AnonymousPipeClientStream _proxyStdin;
        private readonly AnonymousPipeClientStream _proxyStdout;
        private readonly StringWriter _stderr = new();
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ProxyRun(Func<CancellationToken, Task<(Stream Stream, MuxEndpointDescriptor Descriptor)>> connectDaemon)
        {
            ClientStdin = new AnonymousPipeServerStream(PipeDirection.Out);
            ClientStdout = new AnonymousPipeServerStream(PipeDirection.In);
            _proxyStdin = new AnonymousPipeClientStream(PipeDirection.In, ClientStdin.ClientSafePipeHandle);
            _proxyStdout = new AnonymousPipeClientStream(PipeDirection.Out, ClientStdout.ClientSafePipeHandle);
            TextWriter stderr = TextWriter.Synchronized(_stderr);
            var thread = new Thread(() =>
            {
                try { _exit.TrySetResult(MuxProxyCommand.Run(_proxyStdin, _proxyStdout, stderr, connectDaemon)); }
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
