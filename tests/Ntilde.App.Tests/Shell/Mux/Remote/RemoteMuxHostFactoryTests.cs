using Ntilde.Mux;
using Ntilde.Platform.Ssh.Exec;
using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.Native;
using Ntilde.Services.Ssh;
using Ntilde.Shell.Mux;
using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Tests.Shell.Mux.Remote;

/// <summary>
/// <see cref="RemoteMuxHostFactory"/> (Phase 4 spec §5): the remote host <see cref="MuxConnectionHosts"/>
/// builds for an <c>ssh:</c> endpoint. It only constructs - the registry calls it outside its lock and
/// may throw a duplicate away - and the host it builds connects through a <see cref="RemoteMuxConnector"/>.
/// </summary>
public sealed class RemoteMuxHostFactoryTests : IDisposable
{
    private static readonly TimeSpan Patient = TimeSpan.FromSeconds(30);
    private readonly FakeRemoteHost _remote = new();
    private readonly List<IDisposable> _owned = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _remote.Dispose();
    }

    private T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }

    private MuxConnectionHost Create(SshProfile profile, ISshInteractionHandler? user = null) =>
        Own(RemoteMuxHostFactory.Create(MuxEndpointId.ForSsh(profile.Id), _ => profile, (_, _) => _remote, log: null, userPrompts: user)
            ?? throw new InvalidOperationException("the factory declined"));

    private static Task<string?> WhenDisconnected(MuxClient client)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += reason => tcs.TrySetResult(reason);
        if (!client.IsConnected) tcs.TrySetResult(client.DisconnectReason);
        return tcs.Task.WaitAsync(Patient, Ct);
    }

    [Fact]
    public void Declines_the_local_endpoint_a_missing_profile_and_a_profile_that_does_not_persist()
    {
        SshProfile flagged = RemoteMuxConnectorTests.Profile();
        SshProfile unflagged = RemoteMuxConnectorTests.Profile();
        unflagged.MuxOptions.PersistRemoteSessions = false;
        var profiles = new Dictionary<Guid, SshProfile> { [flagged.Id] = flagged, [unflagged.Id] = unflagged };
        MuxConnectionHost? Build(MuxEndpointId id) =>
            RemoteMuxHostFactory.Create(id, profileId => profiles.GetValueOrDefault(profileId), (_, _) => _remote, log: null);

        Assert.Null(Build(MuxEndpointId.Local));
        Assert.Null(Build(MuxEndpointId.ForSsh(Guid.NewGuid())));
        Assert.Null(Build(MuxEndpointId.ForSsh(unflagged.Id)));
        Assert.NotNull(Own(Build(MuxEndpointId.ForSsh(flagged.Id))!));
        Assert.Equal(0, _remote.StartCount);
    }

    [Fact]
    public void Builds_a_remote_host_for_the_profile_and_does_not_connect()
    {
        SshProfile profile = RemoteMuxConnectorTests.Profile();

        MuxConnectionHost host = Create(profile);

        Assert.Equal(MuxHostPolicy.Remote("nova@fake-host"), host.Policy);
        Assert.True(host.Policy.IsRemote);
        Assert.Equal(TimeSpan.Zero, host.FailureCooldown);
        Assert.Equal(MuxEndpointId.ForSsh(profile.Id).ToString(), host.Endpoint);
        Assert.Equal(0, host.ConnectAttempts);
        Assert.Equal(0, _remote.StartCount);
        RemoteMuxConnector connector = Assert.IsType<RemoteMuxConnector>(host.Connector);
        Assert.Equal(host.Policy.ConnectTimeout, connector.PreambleTimeout);
    }

    [Fact]
    public void A_profile_without_a_user_is_named_by_its_host()
    {
        SshProfile profile = RemoteMuxConnectorTests.Profile();
        profile.User = string.Empty;

        Assert.Equal("fake-host", Create(profile).Policy.DisplayName);
    }

    [Fact]
    public void Each_host_has_an_instance_id_of_its_own()
    {
        string a = Assert.IsType<RemoteMuxConnector>(Create(RemoteMuxConnectorTests.Profile()).Connector).ClientInstanceId;
        string b = Assert.IsType<RemoteMuxConnector>(Create(RemoteMuxConnectorTests.Profile()).Connector).ClientInstanceId;

        Assert.Matches("^[0-9a-f]{32}$", a);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task A_user_request_connects_interactively_and_an_automatic_attempt_does_not()
    {
        var interactive = new List<bool>();
        SshProfile profile = RemoteMuxConnectorTests.Profile();
        MuxConnectionHost host = Own(RemoteMuxHostFactory.Create(
            MuxEndpointId.ForSsh(profile.Id),
            _ => profile,
            (_, request) =>
            {
                lock (interactive) interactive.Add(request.Interactive);
                return _remote;
            },
            log: null)!);

        MuxClient first = host.GetClient(Patient)!;
        Task<string?> lost = WhenDisconnected(first);
        _remote.CutLink();
        await lost;
        MuxClient second = await host.TryStartAutomaticAttempt()!.WaitAsync(Patient, Ct);

        Assert.True(second.IsConnected);
        Assert.NotSame(first, second);
        Assert.Equal(new[] { true, false }, interactive);
    }

    /// <summary>
    /// An automatic attempt accepts only a host key already trusted (ruling 3), and a caller with a
    /// known-hosts store of its own decides which, instead of the app's process-wide store: the request
    /// reaches the injected lookup, and its answer is the attempt's.
    /// </summary>
    [Fact]
    public async Task An_automatic_attempt_decides_host_keys_by_the_injected_trust_lookup()
    {
        var asked = new List<string>();
        var prompts = new List<ISshInteractionHandler>();
        SshProfile profile = RemoteMuxConnectorTests.Profile();
        MuxConnectionHost host = Own(RemoteMuxHostFactory.Create(
            MuxEndpointId.ForSsh(profile.Id),
            _ => profile,
            (_, request) =>
            {
                lock (prompts) prompts.Add(request.Prompts);
                return _remote;
            },
            log: null,
            isTrustedHostKey: request =>
            {
                lock (asked) asked.Add($"{request.Host}:{request.Port} {request.Algorithm} {request.Fingerprint}");
                return request.Fingerprint == "SHA256:trusted";
            })!);

        MuxClient client = await host.TryStartAutomaticAttempt()!.WaitAsync(Patient, Ct);
        ISshInteractionHandler automatic = Assert.Single(prompts);
        SshInteractionResponse trusted = await automatic.HandleAsync(HostKey("SHA256:trusted"), Ct);
        SshInteractionResponse other = await automatic.HandleAsync(HostKey("SHA256:other"), Ct);

        Assert.True(client.IsConnected);
        Assert.True(trusted.IsAccepted);
        Assert.False(other.IsAccepted);
        Assert.True(other.IsCanceled);
        Assert.Equal(new[] { "fake-host:2222 ssh-ed25519 SHA256:trusted", "fake-host:2222 ssh-ed25519 SHA256:other" }, asked);
    }

    private static SshInteractionRequest HostKey(string fingerprint) => new()
    {
        Kind = SshInteractionKind.UnknownHostKey,
        Host = "fake-host",
        Port = 2222,
        Algorithm = "ssh-ed25519",
        Fingerprint = fingerprint,
    };

    [Fact]
    public void A_failed_connect_leaves_its_classified_failure_as_LastFailure()
    {
        _remote.Script = FakeRemoteScript.NotInstalledDash;
        MuxConnectionHost host = Create(RemoteMuxConnectorTests.Profile());

        Assert.Null(host.GetClient(Patient));

        RemoteMuxUnavailableException failure = Assert.IsType<RemoteMuxUnavailableException>(host.LastFailure);
        Assert.Equal(RemoteFailureKind.NotInstalled, failure.Failure.Kind);
    }

    [Fact]
    public async Task Each_attempt_reads_the_profile_again()
    {
        SshProfile current = RemoteMuxConnectorTests.Profile();
        Guid id = current.Id;
        MuxConnectionHost host = Own(RemoteMuxHostFactory.Create(MuxEndpointId.ForSsh(id), _ => current, (_, _) => _remote, log: null)!);
        MuxClient first = host.GetClient(Patient)!;
        Task<string?> lost = WhenDisconnected(first);

        // Meanwhile the install flow recorded where it put the binary.
        current = RemoteMuxConnectorTests.Profile("/home/nova/.local/share/ntilde/bin/ntilde-mux");
        _remote.CutLink();
        await lost;
        Assert.NotNull(host.GetClient(Patient));

        Assert.Equal(
            new[] { RemoteMuxCommand.Proxy(new SshMuxOptions()), "/home/nova/.local/share/ntilde/bin/ntilde-mux proxy --stdio" },
            _remote.Commands);
    }

    [Fact]
    public async Task Disposing_the_host_forgets_what_its_prompts_remembered()
    {
        var user = new ScriptedUser(SshInteractionResponse.FromSecret("pw"));
        MuxConnectionHost host = Create(RemoteMuxConnectorTests.Profile(), user);
        RemoteMuxConnector connector = Assert.IsType<RemoteMuxConnector>(host.Connector);
        RemoteMuxInteractionHandler.Attempt attempt = connector.Prompts.BeginAttempt(interactive: true);
        Assert.Equal("pw", (await attempt.HandleAsync(RemoteMuxConnectorTests.PasswordPrompt, Ct)).Secret);
        attempt.Succeeded();
        Assert.True(connector.Prompts.Remembers(SshInteractionKind.Password));

        host.Dispose();

        Assert.False(connector.Prompts.Remembers(SshInteractionKind.Password));
        Assert.Single(user.Asked);
    }

    private static readonly SshLaunchDetails Launch = new()
    {
        SshPath = "/usr/bin/ssh",
        ConfigPath = "cfg",
        Alias = "ntilde_0123",
        CommandLine = "/usr/bin/ssh -F cfg ntilde_0123",
        PlanArguments = ["-F", "cfg", "ntilde_0123"],
    };

    [Fact]
    public void An_OpenSSH_profile_runs_ssh_in_batch_mode_only_for_automatic_attempts()
    {
        SshProfile profile = RemoteMuxConnectorTests.Profile();
        profile.BackendKind = SshBackendKind.OpenSsh;
        var prompts = new RemoteMuxInteractionHandler(user: null, _ => false);
        ISshExecTransport Build(bool interactive) => RemoteMuxHostFactory.CreateTransport(
            profile,
            new RemoteMuxTransportRequest(interactive, prompts.BeginAttempt(interactive)),
            _ => Launch,
            () => throw new InvalidOperationException("an OpenSSH profile never needs the native layer"),
            askPassHelperPath: "/opt/ntilde/ntilde",
            log: null);

        Assert.False(Assert.IsType<OpenSshExecTransport>(Build(interactive: true)).BatchMode);
        Assert.True(Assert.IsType<OpenSshExecTransport>(Build(interactive: false)).BatchMode);
    }

    [Fact]
    public void A_native_profile_gets_the_native_transport()
    {
        SshProfile profile = RemoteMuxConnectorTests.Profile();
        profile.BackendKind = SshBackendKind.Native;
        var prompts = new RemoteMuxInteractionHandler(user: null, _ => false);

        ISshExecTransport transport = RemoteMuxHostFactory.CreateTransport(
            profile,
            new RemoteMuxTransportRequest(false, prompts.BeginAttempt(false)),
            _ => throw new InvalidOperationException("a native profile never plans an ssh command line"),
            () => new NativeSshInterop(),
            askPassHelperPath: null,
            log: null);

        Assert.IsType<NativeSshExecTransport>(transport);
        Assert.Equal("nova@fake-host", transport.DisplayName);
    }
}
