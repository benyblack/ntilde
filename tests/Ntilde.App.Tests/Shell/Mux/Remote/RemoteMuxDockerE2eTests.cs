using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Platform.Ssh.Exec;
using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Platform.Ssh.Launch;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.Native;
using Ntilde.Platform.Ssh.Storage;
using Ntilde.Platform.Tests.Infra;
using Ntilde.Platform.Tests.Ssh;
using Ntilde.Pty;
using Ntilde.Replay;
using Ntilde.Services.Ssh;
using Ntilde.Shell.Mux;
using Ntilde.Shell.Mux.Remote;
using Ntilde.VT;

namespace Ntilde.Tests.Shell.Mux.Remote;

/// <summary>
/// Phase 4 spec §12.3: remote persistence end to end, against a real sshd in Docker
/// (<see cref="DockerSshFixture"/>, image v5) and a real linux-x64 <c>ntilde-mux</c>, on both SSH transports.
/// One scenario per transport, through the app's own pieces: <see cref="RemoteMuxInstaller"/> installs the
/// binary; <see cref="RemoteMuxHostFactory"/> builds the host, with the production transports
/// (<see cref="RemoteMuxHostFactory.CreateTransport"/>, the profile's OpenSSH launch plan from
/// <see cref="SshConnectionService"/>, or <see cref="NativeSshInterop"/>), the real timers
/// (<see cref="SystemMuxTimerScheduler"/>) and the production liveness (a 15 s ping, 10 s to answer); and
/// <see cref="MuxTerminalSessionFactory"/> opens the persisted SSH tab. It then runs <c>top</c> and:
/// <list type="number">
/// <item>drops the link (<c>docker network disconnect</c>, or <c>docker pause</c> where a reconnect would
/// lose the published port): <c>ConnectionLost</c>, then <c>Reconnected</c>, and the reattach shows the
/// same <c>top</c> (same pid, its header on screen);</item>
/// <item>kills the proxy only: the loop reattaches, and the daemon is the same process;</item>
/// <item>kills the daemon: <c>DaemonStopped</c>; Enter (<see cref="MuxConnectionHost.GetClient"/>) connects
/// to a new daemon, and the old id gives <c>PreviousLost</c> and a fresh shell;</item>
/// <item>measures attach latency, for an empty session and one with 10 000 lines of scrollback.</item>
/// </list>
/// Everything it does is logged with timestamps through <see cref="ITestOutputHelper"/>: the log is the
/// evidence the PR carries. Run it with <c>--logger "console;verbosity=detailed"</c>.
/// </summary>
/// <remarks>
/// <para>
/// Gated twice: <see cref="DockerFactAttribute"/> (<c>NTILDE_ENABLE_DOCKER_E2E=1</c>) and
/// <c>NTILDE_MUX_E2E_BINARY</c>, the path of a linux-x64 <c>ntilde-mux</c>. <c>NTILDE_MUX_E2E_DROP=pause</c>
/// forces the <c>docker pause</c> drop. Meant to run on its own, as CI does:
/// <c>dotnet test tests/Ntilde.App.Tests --filter "Category=DockerE2E"</c>.
/// </para>
/// <para>
/// The test points <c>NTILDE_APPDATA_ROOT</c> at a temporary directory while it runs - process-wide, hence
/// its own collection with parallelization off - so the SSH profile store and the generated OpenSSH config
/// live there, never in the machine's own profile. The native known-hosts store that an automatic reconnect
/// trusts host keys from is the test's own too, passed to <see cref="RemoteMuxHostFactory.Create"/>: the
/// app's is bound once per process, to whichever root the first user saw.
/// </para>
/// </remarks>
[Collection(nameof(RemoteMuxDockerE2eCollection))]
public sealed class RemoteMuxDockerE2eTests(ITestOutputHelper output)
{
    private const string BinaryVariable = "NTILDE_MUX_E2E_BINARY";
    private const string DropVariable = "NTILDE_MUX_E2E_DROP";
    private const string AppDataRootVariable = "NTILDE_APPDATA_ROOT";

    /// <summary>
    /// Where the daemon's descriptor is for the container's user: MuxDiscovery over ntilde-mux's own root,
    /// <c>~/.local/share/ntilde/ntilde-mux</c> (final review F3: never the GUI's <c>~/.local/share/ntilde</c>).
    /// </summary>
    private const string RemoteDescriptorPath = "/home/nova/.local/share/ntilde/ntilde-mux/mux/mux-endpoint.json";

    private const string ConnectionLost = nameof(MuxConnectionHost.ConnectionLost);
    private const string Reconnected = nameof(MuxConnectionHost.Reconnected);
    private const string ReconnectAbandoned = nameof(MuxConnectionHost.ReconnectAbandoned);
    private const string DaemonStopped = nameof(MuxConnectionHost.DaemonStopped);

    private const int LatencySamples = 5;
    private const int LatencyScrollbackRows = 10_000;

    private static readonly MuxPresentation Presentation = new() { Cols = 80, Rows = 24, CellWidthPx = 10, CellHeightPx = 20 };

    /// <summary>
    /// A silent link is declared lost at the end of the first 10 s ping slice that saw no frame, a slice
    /// starting at the first 15 s tick after the drop: at most 25 s, plus one more slice when frames were still
    /// arriving as the slice began.
    /// </summary>
    private static readonly TimeSpan LostWithin = TimeSpan.FromSeconds(40);

    /// <summary>The loop's first attempt is a second after the loss, the next ones back off 2, 4, 8 s.</summary>
    private static readonly TimeSpan BackWithin = TimeSpan.FromSeconds(90);

    /// <summary>A killed proxy or daemon ends the channel at once; only the classification (at most 1 s) is in between.</summary>
    private static readonly TimeSpan KillNoticedWithin = TimeSpan.FromSeconds(20);

    /// <summary>Beyond the remote host's 120 s connect timeout, so the factory's own answer comes first.</summary>
    private static readonly TimeSpan FactoryWithin = TimeSpan.FromSeconds(150);

    private static readonly TimeSpan ScreenWithin = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AttachWithin = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InstallWithin = TimeSpan.FromMinutes(6);

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private enum DropMethod
    {
        /// <summary><c>docker network disconnect bridge</c>, then <c>connect</c>: the link goes away, everything in the container keeps running.</summary>
        Network,

        /// <summary><c>docker pause</c>, then <c>unpause</c>: everything in the container freezes, so the link goes silent.</summary>
        Pause,
    }

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    public Task OpenSsh_top_survives_a_dropped_link_and_a_killed_proxy_and_a_killed_daemon_gives_a_fresh_shell() =>
        RunAsync(SshBackendKind.OpenSsh);

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    public Task Native_top_survives_a_dropped_link_and_a_killed_proxy_and_a_killed_daemon_gives_a_fresh_shell() =>
        RunAsync(SshBackendKind.Native);

    private async Task RunAsync(SshBackendKind backend)
    {
        string? binary = Environment.GetEnvironmentVariable(BinaryVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(binary), "set NTILDE_MUX_E2E_BINARY to a linux-x64 ntilde-mux");
        Assert.True(File.Exists(binary), $"{BinaryVariable} names no file: {binary}");

        string root = Path.Combine(Path.GetTempPath(), $"ntilde-mux-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string? previousRoot = Environment.GetEnvironmentVariable(AppDataRootVariable);
        Environment.SetEnvironmentVariable(AppDataRootVariable, root);
        var run = new Run(this, backend, root);
        try
        {
            await run.ExecuteAsync(binary!);
        }
        finally
        {
            await run.DisposeAsync();
            Environment.SetEnvironmentVariable(AppDataRootVariable, previousRoot);
            TryDeleteDirectory(root);
        }
    }

    /// <summary>One transport's scenario, and everything it has to tear down.</summary>
    private sealed class Run(RemoteMuxDockerE2eTests test, SshBackendKind backend, string root) : IAsyncDisposable
    {
        private readonly List<MuxClientSession> _sessions = [];
        private DockerSshFixture? _fixture;
        private MuxConnectionHosts? _hosts;
        private JsonSshProfileStore _store = null!;
        private SshConnectionService _ssh = null!;
        private readonly NativeKnownHostsStore _knownHosts = new(Path.Combine(root, "ssh", "native_known_hosts.json"));
        private NativeSshTestInteractionHandler _prompts = null!;
        private SshHostKeyInfo? _hostKey;
        private Guid _profileId;

        private DockerSshFixture Fixture => _fixture ?? throw new InvalidOperationException("The fixture has not started.");

        public async Task ExecuteAsync(string binary)
        {
            Log($"[e2e] transport {backend}; ntilde-mux {binary} ({new FileInfo(binary).Length} bytes); app data {root}");
            DropMethod method = await StartFixtureAsync();
            _prompts = new NativeSshTestInteractionHandler(Fixture.Password);
            string identityFile = await Fixture.CopyPrivateKeyAsync(NativeSshTestKey.Plain, Path.Combine(root, "id_ed25519"));
            RestrictToOwner(identityFile);
            CreateProfile(identityFile);
            if (backend == SshBackendKind.Native)
            {
                // What the window's prompt service does when the user accepts the key: an automatic
                // (non-interactive) reconnect accepts only a key this store already trusts.
                _hostKey = await Fixture.GetHostKeyAsync();
                TrustHostKey(Fixture.Port);
            }

            await InstallAsync(binary);

            _hosts = new MuxConnectionHosts(
                new MuxConnectionHost(_ => throw new InvalidOperationException("The E2E never uses the local daemon."), "local", null),
                id => RemoteMuxHostFactory.Create(
                    id,
                    _ssh.GetStoredProfile,
                    Transport,
                    HostLog,
                    backend == SshBackendKind.Native ? _prompts : null,
                    isTrustedHostKey: IsTrustedHostKey),
                HostLog);
            var factory = new MuxTerminalSessionFactory(_hosts, new NoPlainSsh(), _ssh.GetStoredProfile, HostLog);
            MuxConnectionHost host = _hosts.GetOrCreate(MuxEndpointId.ForSsh(_profileId))
                ?? throw new InvalidOperationException("The registry declined the persisted profile's host.");
            var events = new HostEvents(host, Log);

            // --- A persisted SSH tab, running top ---------------------------------------------------
            Assert.True(factory.RoutesRemote(Request()), "the persisted profile must route to its remote daemon");
            PersistentSessionResult opened = await CreateAsync(factory, Request(), "new persisted SSH tab");
            Assert.Equal(PersistentSessionOutcome.Spawned, opened.Outcome);
            Assert.Equal($"ssh:{_profileId:N}", opened.Endpoint);
            ScreenMirror tab = await AttachAsync(opened.Session, "the new tab");
            Guid sessionId = tab.Session.Id;
            await tab.WaitForAsync(screen => screen.Contains("nova$", StringComparison.Ordinal), "the remote login shell's prompt");
            tab.Session.SendInput("top -d 1\r");
            await tab.WaitForAsync(screen => screen.Contains("load average", StringComparison.Ordinal), "top's header");
            string topPid = await TopPidAsync();
            int daemonPid = await DaemonPidAsync();
            Log($"[e2e] top runs as pid {topPid} in session {sessionId}; ntilde-mux daemon pid {daemonPid}");

            // --- 1. The link drops ------------------------------------------------------------------
            Log($"[e2e] step 1: drop the link ({(method == DropMethod.Network ? "docker network disconnect/connect" : "docker pause/unpause")})");
            int mark = events.Count;
            long droppedAt;
            if (method == DropMethod.Network)
            {
                await Fixture.DisconnectNetworkAsync();
                droppedAt = Stopwatch.GetTimestamp();
                Log("[docker] network disconnect bridge: done");
                DockerExecResult interfaces = await Fixture.ExecAsync("ip -brief address");
                Log($"[docker] the container's interfaces while it is off the network: {OneLine(interfaces.Stdout)}");
            }
            else
            {
                await Fixture.PauseAsync();
                droppedAt = Stopwatch.GetTimestamp();
                Log("[docker] pause: done");
            }

            HostEvent lost = await events.WaitForAsync(ConnectionLost, mark, LostWithin);
            Log($"[e2e] step 1: the host noticed the drop {Stopwatch.GetElapsedTime(droppedAt, lost.Timestamp).TotalSeconds:0.0} s after it (liveness: 15 s ping, 10 s to answer)");
            if (method == DropMethod.Network)
            {
                int portBefore = Fixture.Port;
                bool reachable = await Fixture.ReconnectNetworkAsync();
                Assert.True(reachable, "docker network connect left sshd unreachable, although the probe before the session showed it comes back");
                Log($"[docker] network connect bridge: sshd answers on port {Fixture.Port} (was {portBefore})");
                if (Fixture.Port != portBefore) MoveProfileTo(Fixture.Port);
            }
            else
            {
                await Fixture.UnpauseAsync();
                Log("[docker] unpause: done");
            }

            HostEvent back = await events.WaitForAsync(Reconnected, lost.Index + 1, BackWithin);
            events.AssertNone(mark, ReconnectAbandoned, DaemonStopped);
            Assert.False(host.IsReconnecting);
            Log($"[e2e] step 1: link lost ({lost.Detail}) {lost.SecondsAfter(test._clock):0.0} s and back {back.SecondsAfter(test._clock):0.0} s into the run; {(back.At - lost.At).TotalSeconds:0.0} s down");
            tab.Dispose();
            tab = await ReattachAsync(factory, sessionId, "after the dropped link");
            Assert.Equal(topPid, await TopPidAsync());
            Log($"[e2e] step 1 passed: top is still pid {topPid}, and its screen came back");

            // --- 2. The proxy is killed, the daemon is not -------------------------------------------
            Log("[e2e] step 2: kill -9 the proxy only");
            DockerExecResult proxies = await Fixture.ExecAsync("pgrep -u nova -f '[n]tilde-mux proxy'");
            Log($"[e2e] proxy pid(s): {OneLine(proxies.Stdout)}");
            mark = events.Count;
            DockerExecResult proxyKill = await Fixture.ExecAsync("pkill -9 -u nova -f '[n]tilde-mux proxy'");
            Assert.True(proxyKill.ExitCode == 0, $"pkill found no ntilde-mux proxy to kill (exit {proxyKill.ExitCode}): {proxyKill.Stderr}");
            lost = await events.WaitForAsync(ConnectionLost, mark, KillNoticedWithin);
            back = await events.WaitForAsync(Reconnected, lost.Index + 1, BackWithin);
            events.AssertNone(mark, ReconnectAbandoned, DaemonStopped);
            Log($"[e2e] step 2: link lost ({lost.Detail}) and back after {(back.At - lost.At).TotalSeconds:0.0} s");
            tab.Dispose();
            tab = await ReattachAsync(factory, sessionId, "after the killed proxy");
            Assert.Equal(topPid, await TopPidAsync());
            Assert.Equal(daemonPid, await DaemonPidAsync());
            DockerExecResult daemonAlive = await Fixture.ExecAsync($"kill -0 {daemonPid}");
            Assert.True(daemonAlive.ExitCode == 0, $"the daemon (pid {daemonPid}) is gone after only the proxy was killed");
            Log($"[e2e] step 2 passed: top is still pid {topPid}, the daemon is still pid {daemonPid}");

            // --- 3. The daemon is killed: its sessions go with it ------------------------------------
            Log("[e2e] step 3: kill -9 the daemon");
            mark = events.Count;
            DockerExecResult daemonKill = await Fixture.ExecAsync("pkill -9 -u nova -f '[n]tilde-mux serve'");
            Assert.True(daemonKill.ExitCode == 0, $"pkill found no ntilde-mux daemon to kill (exit {daemonKill.ExitCode}): {daemonKill.Stderr}");
            HostEvent stopped = await events.WaitForAsync(DaemonStopped, mark, KillNoticedWithin);
            await host.EventsForTest.WaitAsync(KillNoticedWithin, Ct);
            events.AssertNone(mark, ConnectionLost, Reconnected);
            Assert.False(host.IsReconnecting, "a stopped daemon starts no reconnect loop");
            tab.Dispose();

            // Enter, as the pane's banner offers it: GetClient off the UI thread, then the factory for the old id.
            Log("[e2e] step 3: Enter - GetClient, then the tab's session id again");
            var enter = Stopwatch.StartNew();
            MuxClient? afterEnter = await Task.Run(() => host.GetClient(host.Policy.ConnectTimeout), Ct).WaitAsync(FactoryWithin, Ct);
            Assert.NotNull(afterEnter);
            Log($"[e2e] Enter connected in {enter.ElapsedMilliseconds} ms");
            PersistentSessionResult fresh = await CreateAsync(factory, Request(sessionId), "the tab's Enter after the daemon stopped");
            Assert.Equal(PersistentSessionOutcome.PreviousLost, fresh.Outcome);
            tab = await AttachAsync(fresh.Session, "the fresh shell");
            Assert.NotEqual(sessionId, tab.Session.Id);
            await tab.WaitForAsync(screen => screen.Contains("nova$", StringComparison.Ordinal), "the fresh shell's prompt");
            int newDaemonPid = await DaemonPidAsync();
            Assert.NotEqual(daemonPid, newDaemonPid);
            await WaitUntilAsync(async () => (await Fixture.ExecAsync("pgrep -u nova -x top")).ExitCode == 1, "the old top has gone with its daemon", ScreenWithin);
            await host.EventsForTest.WaitAsync(KillNoticedWithin, Ct);
            events.AssertNone(stopped.Index + 1, Reconnected);
            Log($"[e2e] step 3 passed: DaemonStopped, then PreviousLost with a fresh shell (session {tab.Session.Id}) on a new daemon (pid {newDaemonPid}); the old top is gone");
            tab.Dispose();

            // --- 4. Attach latency ------------------------------------------------------------------
            await MeasureLatencyAsync(factory, host);
            Log($"[e2e] all steps passed over {backend}; events: {events.Describe()}");
        }

        /// <summary>
        /// Starts the container and decides how the link will be dropped. The network is probed on the idle
        /// container, before anything connects: a reconnect that loses the published port leaves a container
        /// nothing can reach again, which must not happen with a session in it.
        /// </summary>
        private async Task<DropMethod> StartFixtureAsync()
        {
            _fixture = await DockerSshFixture.StartAsync();
            Log($"[docker] container {_fixture.ContainerName}: sshd on 127.0.0.1:{_fixture.Port}");
            if (string.Equals(Environment.GetEnvironmentVariable(DropVariable), "pause", StringComparison.OrdinalIgnoreCase))
            {
                Log($"[e2e] link drop method: docker pause/unpause ({DropVariable}=pause)");
                return DropMethod.Pause;
            }

            int before = _fixture.Port;
            await _fixture.DisconnectNetworkAsync();
            if (await _fixture.ReconnectNetworkAsync())
            {
                Log($"[e2e] link drop method: docker network disconnect/connect (probe: sshd answers again on port {_fixture.Port}, was {before})");
                return DropMethod.Network;
            }

            Log("[e2e] link drop method: docker pause/unpause - the probe's network disconnect/connect lost the published port for good; starting a fresh container");
            await _fixture.DisposeAsync();
            _fixture = await DockerSshFixture.StartAsync();
            Log($"[docker] container {_fixture.ContainerName}: sshd on 127.0.0.1:{_fixture.Port}");
            return DropMethod.Pause;
        }

        /// <summary>The persisted profile, in a profile store under the test's app data, as the connection editor saves one.</summary>
        private void CreateProfile(string identityFile)
        {
            _store = new JsonSshProfileStore(Path.Combine(root, "ssh", "profiles.json"));
            _ssh = new SshConnectionService(_store);
            _profileId = Guid.NewGuid();
            // Forward slashes: ssh reads its -o values with backslash escapes. The key only: should it be
            // refused, ssh fails at once rather than wait on a password prompt nobody can answer.
            string knownHosts = Path.Combine(root, "openssh_known_hosts").Replace('\\', '/');
            _store.SaveProfile(new SshProfile
            {
                Id = _profileId,
                Name = $"Docker E2E ({backend})",
                BackendKind = backend,
                Host = Fixture.Host,
                Port = Fixture.Port,
                User = Fixture.UserName,
                AuthMode = SshAuthMode.IdentityFile,
                IdentityFilePath = identityFile,
                ExtraSshArgs = backend == SshBackendKind.OpenSsh
                    ? $"-o StrictHostKeyChecking=no -o \"UserKnownHostsFile={knownHosts}\" -o PreferredAuthentications=publickey"
                    : string.Empty,
                MuxOptions = new SshMuxOptions { PersistRemoteSessions = true },
            });
            if (backend == SshBackendKind.OpenSsh) Log($"[e2e] OpenSSH client: {SshLaunchPlanner.ResolveSshExecutablePath()}");
        }

        private void TrustHostKey(int port)
        {
            SshHostKeyInfo key = _hostKey ?? throw new InvalidOperationException("No host key read.");
            _knownHosts.TrustHost(Fixture.Host, port, key.Algorithm, key.Fingerprint);
            Log($"[e2e] native known hosts ({_knownHosts.StoreFilePath}) trust {key.Algorithm} {key.Fingerprint} for {Fixture.Host}:{port}");
        }

        /// <summary>The automatic attempts' host-key trust: this run's store, never the app's process-wide one.</summary>
        private bool IsTrustedHostKey(SshInteractionRequest request)
        {
            bool trusted = _knownHosts.CheckHost(request.Host, request.Port, request.Algorithm, request.Fingerprint) == NativeKnownHostMatch.Trusted;
            Log($"[e2e] automatic attempt's host key {request.Algorithm} {request.Fingerprint} for {request.Host}:{request.Port}: {(trusted ? "trusted" : "NOT trusted")}");
            return trusted;
        }

        /// <summary>The container came back on another port: the profile follows it, as a user would edit it.</summary>
        private void MoveProfileTo(int port)
        {
            SshProfile profile = _store.GetProfile(_profileId) ?? throw new InvalidOperationException("The profile is gone.");
            profile.Port = port;
            _store.SaveProfile(profile);
            if (backend == SshBackendKind.Native) TrustHostKey(port);
            Log($"[e2e] the profile now connects to port {port}");
        }

        /// <summary>Phase 4 spec §9 through the real installer, over the profile's own exec transport; then the profile records it.</summary>
        private async Task InstallAsync(string binary)
        {
            SshProfile profile = _ssh.GetStoredProfile(_profileId) ?? throw new InvalidOperationException("The profile is gone.");
            var installer = new RemoteMuxInstaller(
                Transport(profile, new RemoteMuxTransportRequest(Interactive: true, _prompts)),
                new LocalFileMuxAssetSource(binary),
                (step, line) => Log($"[install] {step}: {line}"));
            RemoteMuxInstallResult result = await installer.InstallAsync(Ct).WaitAsync(InstallWithin, Ct);
            Log($"[install] {(result.Success ? "succeeded" : "FAILED")}: {result.Message}");
            Assert.True(result.Success, result.Message);
            Assert.NotNull(result.Installed);
            Log($"[install] version JSON: version {result.Installed.Version}, protocol {result.Installed.ProtocolMin}-{result.Installed.ProtocolMax}, rid {result.Installed.Rid}, path {result.Installed.Path}");
            Assert.Equal("linux-x64", result.Installed.Rid);

            SshProfile recorded = _ssh.RecordRemoteMuxInstall(_profileId, result.Installed, turnOnPersistRemoteSessions: true)
                ?? throw new InvalidOperationException("The profile is gone.");
            Assert.Equal(result.Installed.Path, recorded.MuxOptions.RemoteDaemonPath);
            Assert.True(recorded.MuxOptions.PersistRemoteSessions);
            Log($"[install] the profile records {recorded.MuxOptions.RemoteDaemonPath}; the proxy command is: {RemoteMuxCommand.Proxy(recorded.MuxOptions)}");
        }

        /// <summary>The app's per-attempt transport (MainWindow.CreateRemoteMuxTransport), without the askpass helper: the key needs no prompt.</summary>
        private ISshExecTransport Transport(SshProfile profile, RemoteMuxTransportRequest request) =>
            RemoteMuxHostFactory.CreateTransport(
                profile,
                request,
                p => _ssh.BuildLaunchDetails(p.Id, SshDiagnosticsLevel.None),
                static () => new NativeSshInterop(),
                askPassHelperPath: null,
                HostLog);

        /// <summary>What a persisted SSH pane asks the factory for: a new tab, or its own session again.</summary>
        private TerminalSessionRequest Request(Guid? existing = null, bool reattachAfterDrop = false) => new(
            Command: "ssh",
            Arguments: string.Empty,
            StartingDirectory: string.Empty,
            Cols: Presentation.Cols,
            Rows: Presentation.Rows,
            EnvironmentOverrides: null,
            SkipPowerShellPostLaunchInit: true,
            Ssh: new SshSessionDescriptor(_profileId, 0, _prompts, true),
            ExistingMuxSessionId: existing,
            ReattachAfterDrop: reattachAfterDrop);

        /// <summary>The factory, off the test's thread as the pane runs it (it may wait minutes for a remote connection).</summary>
        private async Task<PersistentSessionResult> CreateAsync(MuxTerminalSessionFactory factory, TerminalSessionRequest request, string what)
        {
            var timer = Stopwatch.StartNew();
            PersistentSessionResult result = await Task.Run(() => factory.CreatePersistent(request), Ct).WaitAsync(FactoryWithin, Ct);
            string? id = (result.Session as MuxClientSession)?.Id.ToString();
            Log($"[factory] {what}: {result.Outcome} on {result.Endpoint} ({result.HostDisplayName}) session {id ?? "none"} in {timer.ElapsedMilliseconds} ms{(result.Detail is null ? string.Empty : $": {result.Detail}")}");
            return result;
        }

        private async Task<ScreenMirror> AttachAsync(ITerminalSession? session, string what)
        {
            var mux = Assert.IsType<MuxClientSession>(session);
            var mirror = new ScreenMirror(mux);
            _sessions.Add(mux);
            var timer = Stopwatch.StartNew();
            await mux.AttachAsync(2_000, Presentation, Ct).WaitAsync(AttachWithin, Ct);
            Log($"[attach] {what}: session {mux.Id} attached ({mux.AttachMode}) in {timer.ElapsedMilliseconds} ms");
            return mirror;
        }

        /// <summary>
        /// The pane's reattach after its host's Reconnected (Phase 4 spec §7.4): its own session id, opened Shared.
        /// The snapshot alone must show top; a fresh frame after it shows top still updates.
        /// </summary>
        private async Task<ScreenMirror> ReattachAsync(MuxTerminalSessionFactory factory, Guid sessionId, string why)
        {
            PersistentSessionResult result = await CreateAsync(factory, Request(sessionId, reattachAfterDrop: true), $"reattach {why}");
            Assert.Equal(PersistentSessionOutcome.Reattached, result.Outcome);
            ScreenMirror mirror = await AttachAsync(result.Session, $"the tab {why}");
            Assert.Equal(sessionId, mirror.Session.Id);
            string snapshot = mirror.ScreenText();
            Assert.Contains("load average", snapshot, StringComparison.Ordinal);
            Log($"[attach] the snapshot's top line: {FirstLineWith(snapshot, "load average")}");
            await mirror.WaitForAsync(_ => mirror.Outputs > 0, "a frame of top's after the snapshot");
            return mirror;
        }

        /// <summary>Spec §12.3: OpenSession + AttachAsync until the snapshot is in, five times, for an empty session and a 10k-line one.</summary>
        private async Task MeasureLatencyAsync(MuxTerminalSessionFactory factory, MuxConnectionHost host)
        {
            Log("[e2e] step 4: attach latency");
            MuxClient client = host.CurrentClient ?? throw new InvalidOperationException("The host has no connection to measure on.");

            PersistentSessionResult empty = await CreateAsync(factory, Request(), "an empty session");
            Assert.Equal(PersistentSessionOutcome.Spawned, empty.Outcome);
            ScreenMirror emptyTab = await AttachAsync(empty.Session, "the empty session");
            await emptyTab.WaitForAsync(screen => screen.Contains("nova$", StringComparison.Ordinal), "the empty session's prompt");
            Guid emptyId = emptyTab.Session.Id;
            emptyTab.Dispose();

            PersistentSessionResult full = await CreateAsync(factory, Request(), "a session for 10 000 lines");
            Assert.Equal(PersistentSessionOutcome.Spawned, full.Outcome);
            ScreenMirror fullTab = await AttachAsync(full.Session, "the 10k session");
            await fullTab.WaitForAsync(screen => screen.Contains("nova$", StringComparison.Ordinal), "the 10k session's prompt");
            fullTab.Session.SendInput("seq 1 10000\r");
            await fullTab.WaitForAsync(screen => screen.Split('\n').Any(line => line.Trim() == "10000"), "seq's last line, 10000");
            Guid fullId = fullTab.Session.Id;
            fullTab.Dispose();

            double[] emptyMs = await MeasureAttachesAsync(client, emptyId, "empty", minimumScrollbackRows: 0);
            double[] fullMs = await MeasureAttachesAsync(client, fullId, "10k", minimumScrollbackRows: 9_000);
            Log(LatencyLine("empty", emptyMs));
            Log(LatencyLine("10k", fullMs));
        }

        private async Task<double[]> MeasureAttachesAsync(MuxClient client, Guid sessionId, string label, int minimumScrollbackRows)
        {
            var samples = new double[LatencySamples];
            for (int i = 0; i < samples.Length; i++)
            {
                TerminalStateSnapshot? snapshot = null;
                long start = Stopwatch.GetTimestamp();
                MuxClientSession session = client.OpenSession(sessionId, string.Empty, null, MuxAttachMode.Shared);
                try
                {
                    session.SnapshotReceived += received => snapshot = received;
                    await session.AttachAsync(LatencyScrollbackRows, Presentation, Ct).WaitAsync(AttachWithin, Ct);
                    samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    Assert.NotNull(snapshot);
                    Log($"[latency] {label} attach {i + 1}/{samples.Length}: {samples[i]:0.0} ms ({session.LastSnapshotByteCount} snapshot bytes, {snapshot.ScrollbackRowCount} scrollback rows)");
                    Assert.True(snapshot.ScrollbackRowCount >= minimumScrollbackRows, $"the {label} snapshot carried {snapshot.ScrollbackRowCount} scrollback rows, fewer than {minimumScrollbackRows}");
                }
                finally
                {
                    session.Dispose();
                }
            }

            return samples;
        }

        private static string LatencyLine(string label, double[] samples)
        {
            double[] sorted = samples.Order().ToArray();
            return string.Create(
                CultureInfo.InvariantCulture,
                $"[latency] {label} median={sorted[sorted.Length / 2]:0.0} ms (min {sorted[0]:0.0} ms, max {sorted[^1]:0.0} ms, {sorted.Length} attaches, maxScrollbackRows={LatencyScrollbackRows})");
        }

        private async Task<string> TopPidAsync()
        {
            DockerExecResult result = await Fixture.ExecAsync("pgrep -u nova -x top");
            Assert.True(result.ExitCode == 0, $"no top is running (pgrep exit {result.ExitCode}): {result.Stderr}");
            return Assert.Single(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        /// <summary>The daemon's pid, from its descriptor - what every proxy reads to find it.</summary>
        private async Task<int> DaemonPidAsync()
        {
            DockerExecResult result = await Fixture.ExecAsync($"cat {RemoteDescriptorPath}");
            Assert.True(result.ExitCode == 0, $"no daemon descriptor at {RemoteDescriptorPath}: {result.Stderr}");
            using JsonDocument descriptor = JsonDocument.Parse(result.Stdout);
            foreach (JsonProperty property in descriptor.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "pid", StringComparison.OrdinalIgnoreCase)) return property.Value.GetInt32();
            }

            throw new InvalidOperationException($"The daemon descriptor names no pid: {result.Stdout}");
        }

        private void Log(string line) => test.Log(line);

        private void HostLog(string line) => test.Log("[host] " + line);

        public async ValueTask DisposeAsync()
        {
            foreach (MuxClientSession session in _sessions) session.Dispose();
            if (_hosts is { } hosts)
            {
                try
                {
                    await Task.Run(hosts.Dispose, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Log($"[e2e] disposing the hosts failed: {ex.Message}");
                }
            }

            if (_fixture is { } fixture) await fixture.DisposeAsync();
        }
    }

    /// <summary>The fallback factory, which must never be reached: a persisted tab that became plain SSH would pass every check vacuously.</summary>
    private sealed class NoPlainSsh : ITerminalSessionFactory
    {
        public ITerminalSession Create(TerminalSessionRequest request) =>
            throw new InvalidOperationException("The factory fell back to a plain SSH session: the persisted path was not taken.");
    }

    /// <summary>
    /// What a pane does with a <see cref="MuxClientSession"/> (restore the snapshot, parse the output, follow the
    /// size), under a lock, so the test can read the screen while the client's delivery thread writes it.
    /// </summary>
    private sealed class ScreenMirror : IDisposable
    {
        private readonly object _gate = new();
        private readonly TerminalBuffer _buffer = new(Presentation.Cols, Presentation.Rows);
        private readonly AnsiParser _parser;
        private long _outputs;

        public ScreenMirror(MuxClientSession session)
        {
            Session = session;
            _parser = new AnsiParser(_buffer, session.ForceConPtyFiltering) { ImageDecoder = null, AllowNativeKittyGraphics = false };
            _parser.OnResponse = _ => { }; // a pane's parser answers nothing over a mux session
            session.SnapshotReceived += snapshot =>
            {
                lock (_gate) TerminalStateTransfer.Restore(_buffer, _parser, snapshot);
            };
            session.OnOutputReceived += text =>
            {
                lock (_gate) _parser.Process(text);
                Interlocked.Increment(ref _outputs);
            };
            session.StreamResize += (cols, rows) =>
            {
                lock (_gate) _buffer.Resize(cols, rows);
            };
        }

        public MuxClientSession Session { get; }

        /// <summary>Output frames since the attach (the snapshot not counted).</summary>
        public long Outputs => Interlocked.Read(ref _outputs);

        public string ScreenText()
        {
            lock (_gate) return string.Join('\n', BufferSnapshot.Capture(_buffer).Lines);
        }

        public Task WaitForAsync(Func<string, bool> condition, string what) =>
            WaitUntilAsync(() => Task.FromResult(condition(ScreenText())), what, ScreenWithin, () => $"Screen:\n{ScreenText()}");

        public void Dispose() => Session.Dispose();
    }

    /// <summary>The host's events in order, with when they came: the test waits on them and the log shows them.</summary>
    private sealed class HostEvents
    {
        private readonly object _gate = new();
        private readonly List<HostEvent> _events = [];
        private readonly Action<string> _log;

        public HostEvents(MuxConnectionHost host, Action<string> log)
        {
            _log = log;
            host.ConnectionLost += reason => Add(ConnectionLost, reason);
            host.Reconnected += client => Add(Reconnected, $"protocol {client.ProtocolVersion}");
            host.ReconnectAbandoned += () => Add(ReconnectAbandoned, string.Empty);
            host.DaemonStopped += () => Add(DaemonStopped, string.Empty);
        }

        public int Count
        {
            get
            {
                lock (_gate) return _events.Count;
            }
        }

        public async Task<HostEvent> WaitForAsync(string kind, int from, TimeSpan timeout)
        {
            HostEvent? found = null;
            await WaitUntilAsync(
                () =>
                {
                    lock (_gate) found = _events.Skip(from).FirstOrDefault(e => e.Kind == kind);
                    return Task.FromResult(found is not null);
                },
                $"the host raises {kind}",
                timeout,
                () => $"Events so far: {Describe()}");
            return found!;
        }

        public void AssertNone(int from, params string[] kinds)
        {
            lock (_gate)
            {
                HostEvent? unexpected = _events.Skip(from).FirstOrDefault(e => kinds.Contains(e.Kind));
                Assert.True(unexpected is null, $"Unexpected {unexpected?.Kind}; events: {DescribeLocked()}");
            }
        }

        public string Describe()
        {
            lock (_gate) return DescribeLocked();
        }

        private string DescribeLocked() =>
            _events.Count == 0 ? "none" : string.Join(", ", _events.Select(e => $"{e.At:HH:mm:ss.fff} {e.Kind}{(e.Detail.Length > 0 ? $"({e.Detail})" : string.Empty)}"));

        private void Add(string kind, string detail)
        {
            lock (_gate) _events.Add(new HostEvent(_events.Count, kind, detail, DateTime.Now, Stopwatch.GetTimestamp()));
            _log($"[event] {kind}{(detail.Length > 0 ? $" ({detail})" : string.Empty)}");
        }
    }

    private sealed record HostEvent(int Index, string Kind, string Detail, DateTime At, long Timestamp)
    {
        /// <summary>How far into the run (<paramref name="clock"/>) it came.</summary>
        public double SecondsAfter(Stopwatch clock) =>
            clock.Elapsed.TotalSeconds - Stopwatch.GetElapsedTime(Timestamp).TotalSeconds;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, TimeSpan timeout, Func<string>? describe = null)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out after {timeout.TotalSeconds:0} s waiting until {what}.{(describe is null ? string.Empty : "\n" + describe())}");
            }

            await Task.Delay(50, Ct);
        }
    }

    /// <summary>
    /// ssh refuses a private key anyone else can read ("UNPROTECTED PRIVATE KEY FILE"). The copy sits in the
    /// temp directory, whose inherited ACL can grant other principals (on the machine this was written on, a
    /// sandbox group), so it is narrowed to its owner, as a key in <c>~/.ssh</c> is.
    /// </summary>
    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            SecurityIdentifier owner = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No current Windows user.");
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static string OneLine(string text) => string.Join(" | ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string FirstLineWith(string screen, string text) =>
        screen.Split('\n').FirstOrDefault(line => line.Contains(text, StringComparison.Ordinal))?.Trim() ?? string.Empty;

    private void Log(string line)
    {
        string stamped = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:HH:mm:ss.fff} +{_clock.Elapsed.TotalSeconds,7:0.000}s {line}");
        try
        {
            output.WriteLine(stamped);
        }
        catch (InvalidOperationException)
        {
            // A host's log line after the test finished: there is no test to write it to any more.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a file still held by an exiting ssh is left to the temp directory's own cleanup.
        }
    }
}

/// <summary>
/// <see cref="RemoteMuxDockerE2eTests"/>' own collection, never run in parallel with another: the tests point
/// <c>NTILDE_APPDATA_ROOT</c> at their temporary directory, for the whole process, while they run.
/// </summary>
[CollectionDefinition(nameof(RemoteMuxDockerE2eCollection), DisableParallelization = true)]
public sealed class RemoteMuxDockerE2eCollection
{
}
