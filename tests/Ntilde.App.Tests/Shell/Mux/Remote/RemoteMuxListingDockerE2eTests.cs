using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ntilde.Models;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Platform.Ssh.Exec;
using Ntilde.Platform.Ssh.Launch;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.Native;
using Ntilde.Platform.Ssh.Storage;
using Ntilde.Platform.Tests.Infra;
using Ntilde.Platform.Tests.Ssh;
using Ntilde.Services.Ssh;
using Ntilde.Shell;
using Ntilde.Shell.Mux;
using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Tests.Shell.Mux.Remote;

/// <summary>
/// The Phase 5 manual checklist's remote listing steps, automated against a real sshd in Docker
/// (<see cref="DockerSshFixture"/>) and a real linux-x64 <c>ntilde-mux</c>, through the app's own pieces: what
/// <c>ntilde mux ls --all</c> and a persistent tab's sidebar listing do to the host (release hardening, PR #512).
/// <list type="bullet">
/// <item>steps 41 and 43, H7: <c>ls --all</c> starts no daemon where none runs and lists one that does; a host key
/// nobody trusts is never accepted;</item>
/// <item>step 55, H7: an <c>ntilde-mux</c> that refuses <c>--no-spawn</c> is listed as needing an update;</item>
/// <item>step 56, H3: a listing forwards no agent, so the shells' agent link keeps pointing at the tab's connection;</item>
/// <item>step 57, H5: a password the server refuses is offered once, then leaves the host's scope.</item>
/// </list>
/// </summary>
/// <remarks>
/// Gated as <see cref="RemoteMuxDockerE2eTests"/> is (<c>NTILDE_ENABLE_DOCKER_E2E=1</c>, and <c>NTILDE_MUX_E2E_BINARY</c> for the
/// tests that install <c>ntilde-mux</c>), and in its collection: each test points <c>NTILDE_APPDATA_ROOT</c> at its own directory,
/// process-wide, while it runs. The listings use the OpenSSH backend: a native automatic attempt trusts host keys from the app's
/// own store, which is bound once per process to whichever root saw it first.
/// </remarks>
[Collection(nameof(RemoteMuxDockerE2eCollection))]
public sealed class RemoteMuxListingDockerE2eTests(ITestOutputHelper output)
{
    private const string BinaryVariable = "NTILDE_MUX_E2E_BINARY";
    private const string AppDataRootVariable = "NTILDE_APPDATA_ROOT";
    private const string AgentVariable = "SSH_AUTH_SOCK";

    /// <summary>Where the host's daemon describes itself: <c>ntilde-mux</c>'s own root (see <see cref="RemoteMuxDockerE2eTests"/>).</summary>
    private const string RemoteDescriptorPath = "/home/nova/.local/share/ntilde/ntilde-mux/mux/mux-endpoint.json";

    /// <summary>
    /// An <c>ntilde-mux</c> from before <c>proxy --no-spawn</c>, stood in for by the real one: the option is renamed to one no
    /// version knows, so the binary refuses it exactly as an older one does, with its usage and exit 2. No release ships an older
    /// <c>ntilde-mux</c> to install instead.
    /// </summary>
    private const string OlderDaemonPath = "/home/nova/.local/share/ntilde/bin/ntilde-mux-before-no-spawn";

    private const string DaemonPattern = "'[n]tilde-mux serve'";
    private const string ListedTitle = "e2e-listed";

    /// <summary>Generous for a container on a shared runner; the command's own wait is 15 s per host.</summary>
    private static readonly TimeSpan PerHost = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan CommandWithin = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan InstallWithin = TimeSpan.FromMinutes(6);

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Steps 41, 43 and 55 in one <c>ls --all</c> over three profiles of the same host: one that lists it, one whose host key
    /// nobody trusts, and one whose <c>ntilde-mux</c> predates <c>--no-spawn</c>. With no daemon on the host, then with one.
    /// </summary>
    [DockerFact]
    [Trait("Category", "DockerE2E")]
    public async Task Ls_all_starts_no_multiplexer_lists_a_running_one_and_never_trusts_a_key_or_starts_an_older_one()
    {
        await using Run run = await Run.StartAsync(this, needsBinary: true);
        SshProfile listed = run.AddOpenSshProfile("a listed");
        string untrustedKnownHosts = Path.Combine(run.Root, "untrusted_known_hosts");
        File.WriteAllText(untrustedKnownHosts, string.Empty);
        SshProfile untrusted = run.AddOpenSshProfile("b untrusted key", knownHostsFile: untrustedKnownHosts, strictHostKeys: true);
        string installed = await run.InstallAsync(listed);
        await run.WriteOlderDaemonAsync(installed);
        SshProfile older = run.AddOpenSshProfile("c older ntilde-mux", daemonPath: OlderDaemonPath);
        Assert.Equal(1, (await run.Fixture.ExecAsync($"pgrep -u nova -f {DaemonPattern}")).ExitCode);

        // --- No daemon on the host: nothing starts one --------------------------------------------------------
        (int code, string text) = await run.LsAllAsync();
        Assert.Equal(1, code); // only this computer's listing sets the exit code, and no multiplexer runs here
        Assert.Contains(HostLine(listed, MuxListingError.NotRunning), text, StringComparison.Ordinal);
        Assert.Contains(HostLine(untrusted, MuxListingError.NeedsSignIn), text, StringComparison.Ordinal);
        Assert.Contains(HostLine(older, MuxListingError.NeedsUpdate), text, StringComparison.Ordinal);
        using (JsonDocument json = await run.LsAllJsonAsync())
        {
            Assert.Equal(RemoteMuxLister.Reason(MuxListingError.NotRunning), ErrorOf(json, listed));
            Assert.Equal(RemoteMuxLister.Reason(MuxListingError.NeedsSignIn), ErrorOf(json, untrusted));
            Assert.Equal(RemoteMuxLister.Reason(MuxListingError.NeedsUpdate), ErrorOf(json, older));
        }

        DockerExecResult none = await run.Fixture.ExecAsync($"pgrep -u nova -af {DaemonPattern}");
        Assert.True(none.ExitCode == 1, $"ls --all started a daemon on the host: {none.Stdout}");
        Assert.Equal(string.Empty, File.ReadAllText(untrustedKnownHosts)); // no key was trusted for it
        Log("[e2e] steps 41.1, 43, 55 passed with no daemon on the host: nothing was started, no key was trusted");

        // --- A daemon with a session, started as a tab starts it: ls --all lists it and starts no other ---------
        Guid session;
        using (MuxClient tab = await run.ConnectAsync(listed))
        {
            session = await tab.SpawnAsync(new SpawnParams { Command = string.Empty, Cols = 80, Rows = 24, Title = ListedTitle }, Ct);
        }

        int daemonPid = await run.DaemonPidAsync();
        (code, text) = await run.LsAllAsync();
        Assert.Equal(1, code);
        string row = Assert.Single(text.Split('\n'), line => line.Contains(session.ToString(), StringComparison.Ordinal));
        Assert.StartsWith(RemoteMuxConnector.DisplayNameOf(listed), row, StringComparison.Ordinal);
        Assert.Contains(ListedTitle, row, StringComparison.Ordinal);
        Assert.Contains(HostLine(untrusted, MuxListingError.NeedsSignIn), text, StringComparison.Ordinal);
        Assert.Contains(HostLine(older, MuxListingError.NeedsUpdate), text, StringComparison.Ordinal);
        DockerExecResult daemons = await run.Fixture.ExecAsync($"pgrep -u nova -f {DaemonPattern}");
        Assert.Equal(daemonPid.ToString(CultureInfo.InvariantCulture), daemons.Stdout.Trim());
        Assert.Equal(string.Empty, File.ReadAllText(untrustedKnownHosts));
        Log($"[e2e] step 41.2 passed: session {session} listed under {RemoteMuxConnector.DisplayNameOf(listed)}; still one daemon (pid {daemonPid})");
    }

    /// <summary>
    /// Step 56: a tab that forwards the agent (<c>-A</c>) points the shells' agent link at its connection's socket. A listing of
    /// the same profile forwards none (<c>-o ForwardAgent=no</c>) and its proxy leaves the link alone, so the link still names a
    /// live socket afterwards. Before H3 it named the listing's own socket, gone with the listing.
    /// </summary>
    [DockerFact]
    [Trait("Category", "DockerE2E")]
    public async Task A_listing_leaves_the_agent_link_a_forwarding_tab_set()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the test runs its own ssh-agent on a Unix socket");
        Assert.SkipWhen(FindOnPath("ssh-agent") is null || FindOnPath("ssh-add") is null, "ssh-agent and ssh-add are needed");
        await using Run run = await Run.StartAsync(this, needsBinary: true);
        await run.StartAgentAsync();
        SshProfile forwarding = run.AddOpenSshProfile("forwarding", forwardAgent: true);
        await run.InstallAsync(forwarding);

        using MuxClient tab = await run.ConnectAsync(forwarding);
        string link = await run.AgentLinkPathAsync();
        string before = await run.LinkTargetAsync(link);
        Assert.True((await run.Fixture.ExecAsync($"test -S '{before}'")).ExitCode == 0, $"the agent link names no socket: {link} -> {before}");
        Log($"[e2e] the tab's connection set the agent link: {link} -> {before}");

        (int code, string text) = await run.LsAllAsync();
        Assert.Equal(1, code);
        Assert.DoesNotContain("unreachable", Assert.Single(text.Split('\n'), l => l.StartsWith(RemoteMuxConnector.DisplayNameOf(forwarding), StringComparison.Ordinal)), StringComparison.Ordinal);

        string after = await run.LinkTargetAsync(link);
        Assert.Equal(before, after);
        Assert.True((await run.Fixture.ExecAsync($"test -S '{after}'")).ExitCode == 0, $"after the listing the agent link names no socket: {link} -> {after}");
        Log($"[e2e] step 56 passed: after ls --all the link still names the tab's socket ({after})");
    }

    /// <summary>
    /// Step 57: a persistent tab's host scope holds the password typed for it. The server changes it; the sidebar listing that
    /// offers the old one is refused once (one <c>Failed password</c> in sshd's log), forgets it, and offers it no more. A
    /// password typed since is used.
    /// </summary>
    [DockerFact]
    [Trait("Category", "DockerE2E")]
    public async Task A_listing_refused_at_sign_in_offers_the_scope_password_no_more()
    {
        await using Run run = await Run.StartAsync(this, needsBinary: false);
        Environment.SetEnvironmentVariable(AgentVariable, null); // the profile signs in by password only; the run restores it
        SshProfile profile = run.AddNativePasswordProfile("password");
        await run.TrustHostKeyAsync();
        var registry = new ActiveSshSessionRegistry();
        Guid sessionId = Guid.NewGuid();
        Guid scope = Guid.NewGuid();
        registry.Register(new ActiveSshSessionDescriptor(sessionId, profile.Id, SshBackendKind.Native, scope));
        registry.SetRuntimePassword(scope, profile.Host, profile.Port, profile.User, run.Fixture.Password);
        var service = new RemoteDirectoryBrowserService(new NativeSshInterop(), registry, () => run.Ssh, static _ => null);

        RemoteSidebarListingResult first = await ListAsync(service, profile, sessionId);
        Assert.True(first.IsSuccess, $"the listing with the right password failed: {first.ErrorMessage}");
        int failedBefore = await run.FailedPasswordsAsync();

        await run.Fixture.ExecAsync("echo 'nova:t30-rotated' | chpasswd");
        RemoteSidebarListingResult refused = await ListAsync(service, profile, sessionId);
        Assert.False(refused.IsSuccess);
        Assert.False(registry.TryGetRuntimePassword(scope, profile.Host, profile.Port, profile.User, out _), "the refused password is still in the host's scope");
        await WaitUntilAsync(async () => await run.FailedPasswordsAsync() > failedBefore, "sshd logged the refused password", TimeSpan.FromSeconds(15));
        int failedAfterRefusal = await run.FailedPasswordsAsync();
        Assert.Equal(1, failedAfterRefusal - failedBefore); // offered once per attempt, never retried

        for (int i = 0; i < 2; i++) Assert.False((await ListAsync(service, profile, sessionId)).IsSuccess);
        await Task.Delay(TimeSpan.FromSeconds(2), Ct); // sshd writes its line as it refuses: give a stray one time to show
        Assert.Equal(failedAfterRefusal, await run.FailedPasswordsAsync());
        Log($"[e2e] step 57: {failedAfterRefusal - failedBefore} refused sign-in(s), then none for two more listings");

        registry.SetRuntimePassword(scope, profile.Host, profile.Port, profile.User, "t30-rotated"); // typed again, in the host's reconnect
        RemoteSidebarListingResult retyped = await ListAsync(service, profile, sessionId);
        Assert.True(retyped.IsSuccess, $"the listing with the new password failed: {retyped.ErrorMessage}");
        Log("[e2e] step 57 passed: the refused password left the scope, and the one typed since is used");
    }

    private static Task<RemoteSidebarListingResult> ListAsync(RemoteDirectoryBrowserService service, SshProfile profile, Guid sessionId) =>
        service.ListDirectoryAsync(profile.Id, sessionId, "/tmp", Ct).WaitAsync(CommandWithin, Ct);

    /// <summary>The table's line for <paramref name="profile"/>'s host when it could not be listed, as <see cref="MuxLsAll"/> writes it.</summary>
    private static string HostLine(SshProfile profile, MuxListingError error) =>
        $"{RemoteMuxConnector.DisplayNameOf(profile)}  unreachable: {RemoteMuxLister.Reason(error)}";

    private static string? ErrorOf(JsonDocument json, SshProfile profile)
    {
        string endpoint = MuxEndpointId.ForSsh(profile.Id).ToString();
        JsonElement host = json.RootElement.GetProperty("endpoints").EnumerateArray()
            .Single(e => e.GetProperty("endpoint").GetString() == endpoint);
        return host.TryGetProperty("error", out JsonElement error) ? error.GetString() : null;
    }

    private static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, name))
            .FirstOrDefault(File.Exists);

    /// <summary>One test's container, app-data root, profiles and processes, and everything it has to tear down.</summary>
    private sealed class Run : IAsyncDisposable
    {
        private readonly RemoteMuxListingDockerE2eTests _test;
        private readonly List<IDisposable> _owned = [];
        private readonly string? _previousRoot;
        private readonly string? _previousAgent;
        private readonly JsonSshProfileStore _store;
        private readonly string _identityFile;
        private readonly string? _binary;
        private Process? _agent;

        private Run(RemoteMuxListingDockerE2eTests test, DockerSshFixture fixture, string root, string identityFile, string? binary)
        {
            _test = test;
            Fixture = fixture;
            Root = root;
            _identityFile = identityFile;
            _binary = binary;
            _previousRoot = Environment.GetEnvironmentVariable(AppDataRootVariable);
            _previousAgent = Environment.GetEnvironmentVariable(AgentVariable);
            Environment.SetEnvironmentVariable(AppDataRootVariable, root);
            _store = new JsonSshProfileStore(Path.Combine(root, "ssh", "profiles.json"));
            Ssh = new SshConnectionService(_store);
        }

        public DockerSshFixture Fixture { get; }

        /// <summary>This test's <c>NTILDE_APPDATA_ROOT</c>, and the local daemon root <c>ls --all</c> reads (where none runs).</summary>
        public string Root { get; }

        public SshConnectionService Ssh { get; }

        public static async Task<Run> StartAsync(RemoteMuxListingDockerE2eTests test, bool needsBinary)
        {
            string? binary = Environment.GetEnvironmentVariable(BinaryVariable);
            Assert.SkipWhen(needsBinary && string.IsNullOrEmpty(binary), "set NTILDE_MUX_E2E_BINARY to a linux-x64 ntilde-mux");
            Assert.True(!needsBinary || File.Exists(binary), $"{BinaryVariable} names no file: {binary}");
            string root = Path.Combine(Path.GetTempPath(), $"ntilde-ls-e2e-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            DockerSshFixture? fixture = null;
            try
            {
                fixture = await DockerSshFixture.StartAsync();
                test.Log($"[docker] container {fixture.ContainerName}: sshd on 127.0.0.1:{fixture.Port}; app data {root}");
                string identityFile = await fixture.CopyPrivateKeyAsync(NativeSshTestKey.Plain, Path.Combine(root, "id_ed25519"));
                RemoteMuxDockerE2eTests.RestrictToOwner(identityFile);
                return new Run(test, fixture, root, identityFile, needsBinary ? binary : null);
            }
            catch
            {
                // No Run owns them yet: its DisposeAsync would have.
                if (fixture is not null) await fixture.DisposeAsync();
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best effort: the setup's own failure is the one to report.
                }

                throw;
            }
        }

        /// <summary>
        /// A persisted OpenSSH profile signing in with the test's key. The host key is accepted into a file of the test's own,
        /// unless <paramref name="strictHostKeys"/>: then only a key already in <paramref name="knownHostsFile"/> is.
        /// </summary>
        public SshProfile AddOpenSshProfile(string name, string? knownHostsFile = null, bool strictHostKeys = false, bool forwardAgent = false, string? daemonPath = null)
        {
            // Forward slashes: ssh reads its -o values with backslash escapes.
            string knownHosts = (knownHostsFile ?? Path.Combine(Root, "openssh_known_hosts")).Replace('\\', '/');
            var profile = new SshProfile
            {
                Id = Guid.NewGuid(),
                Name = name,
                BackendKind = SshBackendKind.OpenSsh,
                Host = Fixture.Host,
                Port = Fixture.Port,
                User = Fixture.UserName,
                AuthMode = SshAuthMode.IdentityFile,
                IdentityFilePath = _identityFile,
                ExtraSshArgs = (forwardAgent ? "-A " : string.Empty)
                    + $"-o StrictHostKeyChecking={(strictHostKeys ? "yes" : "no")} -o \"UserKnownHostsFile={knownHosts}\" -o PreferredAuthentications=publickey",
                MuxOptions = new SshMuxOptions { PersistRemoteSessions = true, RemoteDaemonPath = daemonPath ?? string.Empty },
            };
            _store.SaveProfile(profile);
            return profile;
        }

        /// <summary>A persisted native profile with no key: it signs in with the password the session's scope holds.</summary>
        public SshProfile AddNativePasswordProfile(string name)
        {
            var profile = new SshProfile
            {
                Id = Guid.NewGuid(),
                Name = name,
                BackendKind = SshBackendKind.Native,
                Host = Fixture.Host,
                Port = Fixture.Port,
                User = Fixture.UserName,
                AuthMode = SshAuthMode.Default,
                MuxOptions = new SshMuxOptions { PersistRemoteSessions = true },
            };
            _store.SaveProfile(profile);
            return profile;
        }

        /// <summary>The host key, trusted in the store a native listing reads (<see cref="AppPaths.NativeKnownHostsFilePath"/> under this root).</summary>
        public async Task TrustHostKeyAsync()
        {
            SshHostKeyInfo key = await Fixture.GetHostKeyAsync();
            new NativeKnownHostsStore(AppPaths.NativeKnownHostsFilePath).TrustHost(Fixture.Host, Fixture.Port, key.Algorithm, key.Fingerprint);
        }

        /// <summary>Phase 4 spec §9 through the real installer, as the profile editor runs it; returns where it installed.</summary>
        public async Task<string> InstallAsync(SshProfile profile)
        {
            var installer = new RemoteMuxInstaller(
                Transport(profile, Interactive()),
                new LocalFileMuxAssetSource(_binary ?? throw new InvalidOperationException("This test has no ntilde-mux to install.")),
                (step, line) => Log($"[install] {step}: {line}"));
            RemoteMuxInstallResult result = await installer.InstallAsync(Ct).WaitAsync(InstallWithin, Ct);
            Assert.True(result.Success, result.Message);
            Assert.NotNull(result.Installed);
            Log($"[install] {result.Installed.Version} at {result.Installed.Path}");
            return result.Installed.Path;
        }

        /// <summary>Puts <see cref="OlderDaemonPath"/> on the host: <paramref name="installed"/>, with <c>--no-spawn</c> renamed.</summary>
        public async Task WriteOlderDaemonAsync(string installed)
        {
            string script = string.Join('\n',
                "#!/bin/sh",
                "for a in \"$@\"; do",
                "  shift",
                "  [ \"$a\" = --no-spawn ] && a=--option-from-the-future",
                "  set -- \"$@\" \"$a\"",
                "done",
                $"exec '{installed}' \"$@\"");
            DockerExecResult written = await Fixture.ExecAsync(
                $"cat > '{OlderDaemonPath}' <<'EOF'\n{script}\nEOF\nchmod 755 '{OlderDaemonPath}' && chown nova:nova '{OlderDaemonPath}'");
            Assert.True(written.ExitCode == 0, $"writing {OlderDaemonPath} failed: {written.Stderr}");
        }

        /// <summary>An interactive connection, as a tab's: it starts the host's daemon when none runs. The connector goes with the run.</summary>
        public async Task<MuxClient> ConnectAsync(SshProfile profile)
        {
            var connector = new RemoteMuxConnector(profile, Transport(profile, Interactive()), "e2e-tab-" + Guid.NewGuid().ToString("N"), HostLog);
            _owned.Add(connector);
            return await connector.ConnectAsync(interactive: true, Ct).WaitAsync(CommandWithin, Ct);
        }

        /// <summary><c>ntilde mux ls --all</c>, as the app runs it, with this run's profiles and the app's transports.</summary>
        public async Task<(int Code, string Text)> LsAllAsync(params string[] extra)
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            MuxLsAllRemotes remotes = new(
                Ssh,
                profile => RemoteMuxLister.CreateConnector(profile, Transport, savedPassword: null, askPassRecords: null, HostLog),
                PerHost,
                HostLog,
                OperatingSystem.IsWindows());
            int code = await Task.Run(() => MuxCommand.Execute(["mux", "ls", "--all", .. extra], stdout, stderr, Root, _ => remotes), Ct)
                .WaitAsync(CommandWithin, Ct);
            string text = stdout.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
            Log($"[ls --all{(extra.Length > 0 ? " " + string.Join(' ', extra) : string.Empty)}] exit {code}\n{text}{stderr}");
            return (code, text);
        }

        public async Task<JsonDocument> LsAllJsonAsync() => JsonDocument.Parse((await LsAllAsync("--json")).Text);

        public async Task<int> DaemonPidAsync()
        {
            using JsonDocument descriptor = await DescriptorAsync();
            return descriptor.RootElement.GetProperty("pid").GetInt32();
        }

        /// <summary>The link the daemon's shells see as <c>SSH_AUTH_SOCK</c>: <c>agent.sock</c> beside its endpoint (Phase 5 R15).</summary>
        public async Task<string> AgentLinkPathAsync()
        {
            using JsonDocument descriptor = await DescriptorAsync();
            string endpoint = descriptor.RootElement.GetProperty("endpoint").GetString() ?? throw new InvalidOperationException("The descriptor names no endpoint.");
            return endpoint[..endpoint.LastIndexOf('/')] + "/agent.sock";
        }

        public async Task<string> LinkTargetAsync(string link)
        {
            DockerExecResult target = await Fixture.ExecAsync($"readlink '{link}'");
            Assert.True(target.ExitCode == 0, $"no agent link at {link}: {target.Stderr}");
            return target.Stdout.Trim();
        }

        /// <summary>How many sign-ins with a wrong password sshd has refused so far.</summary>
        public async Task<int> FailedPasswordsAsync() =>
            Regex.Count(await Fixture.LogsAsync(), "Failed password for nova ");

        /// <summary>An <c>ssh-agent</c> of this run's own, holding the test's key, as <c>SSH_AUTH_SOCK</c> for the process.</summary>
        public async Task StartAgentAsync()
        {
            string socket = Path.Combine(Root, "agent.sock");
            _agent = Process.Start(new ProcessStartInfo("ssh-agent") { ArgumentList = { "-D", "-a", socket }, RedirectStandardOutput = true, UseShellExecute = false })
                ?? throw new InvalidOperationException("ssh-agent did not start.");
            await WaitUntilAsync(() => Task.FromResult(File.Exists(socket)), "ssh-agent's socket exists", TimeSpan.FromSeconds(15));
            Environment.SetEnvironmentVariable(AgentVariable, socket);
            using Process add = Process.Start(new ProcessStartInfo("ssh-add") { ArgumentList = { _identityFile }, RedirectStandardError = true, UseShellExecute = false })
                ?? throw new InvalidOperationException("ssh-add did not start.");
            string addError = await add.StandardError.ReadToEndAsync(Ct);
            await add.WaitForExitAsync(Ct);
            Assert.True(add.ExitCode == 0, $"ssh-add failed ({add.ExitCode}): {addError}");
            Log($"[e2e] ssh-agent pid {_agent.Id} on {socket} holds the test's key");
        }

        private async Task<JsonDocument> DescriptorAsync()
        {
            DockerExecResult result = await Fixture.ExecAsync($"cat {RemoteDescriptorPath}");
            Assert.True(result.ExitCode == 0, $"no daemon descriptor at {RemoteDescriptorPath}: {result.Stderr}");
            return JsonDocument.Parse(result.Stdout);
        }

        private RemoteMuxTransportRequest Interactive() => new(Interactive: true, new NativeSshTestInteractionHandler(Fixture.Password));

        /// <summary>The app's per-attempt transport (MainWindow.CreateRemoteMuxTransport), without the askpass helper: the key needs no prompt.</summary>
        private ISshExecTransport Transport(SshProfile profile, RemoteMuxTransportRequest request) =>
            RemoteMuxHostFactory.CreateTransport(
                profile,
                request,
                (p, selfContained) => Ssh.BuildLaunchDetailsFor(p, SshDiagnosticsLevel.None, selfContained),
                static () => new NativeSshInterop(),
                static () => true,
                askPassHelperPath: null,
                HostLog,
                openSshVersions: OpenSshClientVersionCache.Shared);

        private void Log(string line) => _test.Log(line);

        private void HostLog(string line) => _test.Log("[host] " + line);

        public async ValueTask DisposeAsync()
        {
            try
            {
                for (int i = _owned.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        _owned[i].Dispose();
                    }
                    catch (Exception ex)
                    {
                        Log($"[e2e] disposing {_owned[i].GetType().Name} failed: {ex.Message}");
                    }
                }

                if (_agent is { } agent)
                {
                    try
                    {
                        agent.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                        // Already gone.
                    }

                    agent.Dispose();
                }

                await Fixture.DisposeAsync();
            }
            finally
            {
                // Whatever failed above, the next test must not inherit this run's app data or its dead agent socket.
                Environment.SetEnvironmentVariable(AgentVariable, _previousAgent);
                Environment.SetEnvironmentVariable(AppDataRootVariable, _previousRoot);
                try
                {
                    Directory.Delete(Root, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best effort: a file still held by an exiting ssh is left to the temp directory's own cleanup.
                }
            }
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out after {timeout.TotalSeconds:0} s waiting until {what}.");
            await Task.Delay(100, Ct);
        }
    }

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
}
