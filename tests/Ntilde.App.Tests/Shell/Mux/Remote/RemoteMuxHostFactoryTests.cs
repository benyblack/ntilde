using Ntilde.Mux;
using Ntilde.Platform.Ssh.Exec;
using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.Native;
using Ntilde.Platform.Ssh.Sessions;
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

    /// <summary>
    /// Codex C2: a profile whose <c>PersistRemoteSessions</c> is off still gets a host. The flag decides where its tabs
    /// go (<see cref="MuxTerminalSessionFactory.RoutesRemote"/>); a host is also what delivers the kill of a shell
    /// it once had there, which the user closed. Only the local endpoint and a missing profile are declined.
    /// </summary>
    [Fact]
    public void Declines_the_local_endpoint_and_a_missing_profile_but_not_one_that_stopped_persisting()
    {
        SshProfile flagged = RemoteMuxConnectorTests.Profile();
        SshProfile unflagged = RemoteMuxConnectorTests.Profile();
        unflagged.MuxOptions.PersistRemoteSessions = false;
        var profiles = new Dictionary<Guid, SshProfile> { [flagged.Id] = flagged, [unflagged.Id] = unflagged };
        MuxConnectionHost? Build(MuxEndpointId id) =>
            RemoteMuxHostFactory.Create(id, profileId => profiles.GetValueOrDefault(profileId), (_, _) => _remote, log: null);

        Assert.Null(Build(MuxEndpointId.Local));
        Assert.Null(Build(MuxEndpointId.ForSsh(Guid.NewGuid())));
        Assert.NotNull(Own(Build(MuxEndpointId.ForSsh(unflagged.Id))!));
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

    /// <summary>The target a transport was asked for: <c>user@host:port</c>.</summary>
    private static string TargetOf(SshProfile profile) => $"{profile.User}@{profile.Host}:{profile.Port}";

    /// <summary>Drops <paramref name="client"/>'s link and waits until the host has seen it go.</summary>
    private async Task CutAsync(MuxClient client)
    {
        Task<string?> lost = WhenDisconnected(client);
        _remote.CutLink();
        await lost;
    }

    /// <summary>
    /// Codex D2, residual R2: the install metadata comes from the profile as it is now only while that profile still
    /// names the pinned target. An install path recorded for the first host is used at once; once the profile moves to
    /// another host - where the user then installs, under another home - the connection keeps the first host's path:
    /// the other one would not exist there.
    /// </summary>
    [Fact]
    public async Task After_a_retarget_the_install_metadata_stays_the_pinned_hosts()
    {
        using var otherHost = new FakeRemoteHost("other@host-b");
        SshProfile first = RemoteMuxConnectorTests.Profile();
        SshProfile current = first;
        var targets = new List<string>();
        MuxConnectionHost host = Own(RemoteMuxHostFactory.Create(
            MuxEndpointId.ForSsh(first.Id),
            _ => current,
            (profile, _) =>
            {
                lock (targets) targets.Add(TargetOf(profile));
                return profile.Host == "host-b" ? otherHost : _remote;
            },
            log: null)!);
        await CutAsync(host.GetClient(Patient)!);

        current = RemoteMuxConnectorTests.Edited(first, host: "fake-host", recordedPath: "/home/nova/.local/share/ntilde/bin/ntilde-mux");
        await CutAsync(host.GetClient(Patient)!);
        current = RemoteMuxConnectorTests.Edited(first, host: "host-b", port: 2200, user: "other", recordedPath: "/home/other/.local/share/ntilde/bin/ntilde-mux");
        Assert.NotNull(host.GetClient(Patient));

        Assert.Equal(new[] { "nova@fake-host:22", "nova@fake-host:22", "nova@fake-host:22" }, targets);
        Assert.Equal(
            new[]
            {
                RemoteMuxCommand.Proxy(new SshMuxOptions()),
                "/home/nova/.local/share/ntilde/bin/ntilde-mux proxy --stdio",
                "/home/nova/.local/share/ntilde/bin/ntilde-mux proxy --stdio",
            },
            _remote.Commands);
        Assert.Equal(0, otherHost.StartCount);
    }

    /// <summary>
    /// Codex D2, residual R3: only where to connect is pinned - host, port, user, jump hosts. How to sign in comes from
    /// the profile as it is now on every attempt: a rotated key, a new extra ssh argument, another backend reach the
    /// live connection's next reconnect, though the host moved and is kept.
    /// </summary>
    [Fact]
    public async Task After_the_first_connect_how_to_sign_in_follows_the_profile_but_where_to_connect_does_not()
    {
        using var otherHost = new FakeRemoteHost("nova@host-b");
        SshProfile first = RemoteMuxConnectorTests.Profile();
        first.JumpHops.Add(new SshJumpHop { Host = "bastion", User = "ops", Port = 2200 });
        SshProfile current = first;
        var seen = new List<SshProfile>();
        MuxConnectionHost host = Own(RemoteMuxHostFactory.Create(
            MuxEndpointId.ForSsh(first.Id),
            _ => current,
            (profile, _) =>
            {
                lock (seen) seen.Add(profile);
                return profile.Host == "host-b" ? otherHost : _remote;
            },
            log: null)!);
        await CutAsync(host.GetClient(Patient)!);

        SshProfile edited = SshConnectionService.CloneProfile(first);
        edited.Host = "host-b";
        edited.JumpHops = [new SshJumpHop { Host = "other-bastion" }];
        edited.AuthMode = SshAuthMode.IdentityFile;
        edited.IdentityFilePath = "/home/me/.ssh/id_rotated";
        edited.ExtraSshArgs = "-o KexAlgorithms=curve25519-sha256";
        edited.BackendKind = SshBackendKind.Native;
        current = edited;
        Assert.NotNull(host.GetClient(Patient));

        SshProfile next = seen[^1];
        Assert.Equal(2, seen.Count);
        Assert.Equal(("fake-host", 22, "nova"), (next.Host, next.Port, next.User));
        Assert.Equal("ops@bastion:2200", Assert.Single(next.JumpHops).ToString());
        Assert.Equal((SshAuthMode.IdentityFile, "/home/me/.ssh/id_rotated"), (next.AuthMode, next.IdentityFilePath));
        Assert.Equal("-o KexAlgorithms=curve25519-sha256", next.ExtraSshArgs);
        Assert.Equal(SshBackendKind.Native, next.BackendKind);
        Assert.Equal(0, otherHost.StartCount);
    }

    /// <summary>
    /// Codex D2, residual R4: an attempt after the host connected asks its transport for a pinned plan (for OpenSSH, one
    /// that reads no shared config file, which an edit could rewrite under it); the attempts before do not.
    /// </summary>
    [Fact]
    public async Task Attempts_after_the_first_connect_ask_for_a_pinned_plan()
    {
        SshProfile profile = RemoteMuxConnectorTests.Profile();
        var pinned = new List<bool>();
        _remote.Script = FakeRemoteScript.ConnectionRefused;
        MuxConnectionHost host = Own(RemoteMuxHostFactory.Create(
            MuxEndpointId.ForSsh(profile.Id),
            _ => profile,
            (_, request) =>
            {
                lock (pinned) pinned.Add(request.Pinned);
                return _remote;
            },
            log: null)!);
        Assert.Null(host.GetClient(Patient));
        _remote.Script = null;
        await CutAsync(host.GetClient(Patient)!);
        Assert.NotNull(host.GetClient(Patient));

        Assert.Equal(new[] { false, false, true }, pinned);
    }

    /// <summary>
    /// Codex D2, final round: a pinned attempt is retargeted - must share no ssh master - only while the profile names
    /// another destination than the pinned one; an unedited pinned attempt keeps sharing as before.
    /// </summary>
    [Fact]
    public async Task A_pinned_attempt_is_retargeted_only_while_the_profile_names_another_destination()
    {
        using var otherHost = new FakeRemoteHost("nova@host-b");
        SshProfile first = RemoteMuxConnectorTests.Profile();
        SshProfile current = first;
        var requests = new List<(bool Pinned, bool Retargeted)>();
        MuxConnectionHost host = Own(RemoteMuxHostFactory.Create(
            MuxEndpointId.ForSsh(first.Id),
            _ => current,
            (profile, request) =>
            {
                lock (requests) requests.Add((request.Pinned, request.Retargeted));
                return profile.Host == "host-b" ? otherHost : _remote;
            },
            log: null)!);
        await CutAsync(host.GetClient(Patient)!);
        await CutAsync(host.GetClient(Patient)!);                         // unedited
        current = RemoteMuxConnectorTests.Edited(first, host: "host-b");
        Assert.NotNull(host.GetClient(Patient));                          // edited: still the first host

        Assert.Equal(new[] { (false, false), (true, false), (true, true) }, requests);
        Assert.Equal(0, otherHost.StartCount);
    }

    /// <summary>
    /// Codex D2, final round: a retargeted attempt's ssh gets <c>-o ControlPath=none</c> ahead of the plan's own options
    /// - first value wins - so the block's <c>ControlPath</c>, keyed by the profile id, cannot lead it to a master a
    /// plain tab opened to the profile's new host. Any other attempt's plan is unchanged.
    /// </summary>
    [Fact]
    public void A_retargeted_attempt_uses_and_creates_no_ssh_master()
    {
        var prompts = new RemoteMuxInteractionHandler(user: null, _ => false);
        SshLaunchDetails launch = new()
        {
            SshPath = "/usr/bin/ssh",
            ConfigPath = "none",
            Alias = "ntilde_0123",
            CommandLine = "/usr/bin/ssh -F none ntilde_0123",
            PlanArguments = ["-F", "none", "ntilde_0123", "-o", "ControlMaster auto", "-o", "ControlPath /home/me/.ssh/mux/cm_0123"],
        };

        IReadOnlyList<string> retargeted = RemoteMuxHostFactory.PlanArgumentsFor(launch, new RemoteMuxTransportRequest(false, prompts.BeginAttempt(false), Pinned: true, Retargeted: true));
        IReadOnlyList<string> unedited = RemoteMuxHostFactory.PlanArgumentsFor(launch, new RemoteMuxTransportRequest(false, prompts.BeginAttempt(false), Pinned: true));

        string[] expected = ["-o", "ControlPath=none", .. launch.PlanArguments];
        Assert.Equal(expected, retargeted);
        Assert.Equal(launch.PlanArguments, unedited);
        IReadOnlyList<string> argv = OpenSshExecCommandLine.Build([], retargeted, "ntilde-mux proxy --stdio");
        int none = Enumerable.Range(0, argv.Count).First(i => argv[i] == "ControlPath=none");
        Assert.True(none < argv.ToList().IndexOf("ControlPath /home/me/.ssh/mux/cm_0123"), "ControlPath=none must come first: ssh keeps an option's first value");
    }

    /// <summary>Residual R4: the transport plans OpenSSH as the request says - pinned or not.</summary>
    [Fact]
    public void An_OpenSSH_transport_is_planned_pinned_only_when_the_request_says_so()
    {
        SshProfile profile = RemoteMuxConnectorTests.Profile();
        profile.BackendKind = SshBackendKind.OpenSsh;
        var prompts = new RemoteMuxInteractionHandler(user: null, _ => false);
        var asked = new List<bool>();
        foreach (bool pinned in new[] { false, true })
        {
            RemoteMuxHostFactory.CreateTransport(
                profile,
                new RemoteMuxTransportRequest(false, prompts.BeginAttempt(false), pinned),
                (_, selfContained) =>
                {
                    asked.Add(selfContained);
                    return Launch;
                },
                () => throw new InvalidOperationException("an OpenSSH profile never needs the native layer"),
                static () => true,
                askPassHelperPath: null,
                log: null);
        }

        Assert.Equal(new[] { false, true }, asked);
    }

    /// <summary>Codex D2: until an attempt connects, each one reads the profile again, so fixing a typo and pressing Enter works.</summary>
    [Fact]
    public void Until_an_attempt_connects_each_one_goes_where_the_profile_says_now()
    {
        using var otherHost = new FakeRemoteHost("nova@host-b");
        SshProfile first = RemoteMuxConnectorTests.Profile();
        SshProfile current = first;
        MuxConnectionHost host = Own(RemoteMuxHostFactory.Create(MuxEndpointId.ForSsh(first.Id), _ => current, (profile, _) => profile.Host == "host-b" ? otherHost : _remote, log: null)!);
        _remote.Script = FakeRemoteScript.ConnectionRefused;   // the wrong host

        Assert.Null(host.GetClient(Patient));
        current = RemoteMuxConnectorTests.Edited(first, host: "host-b");
        Assert.NotNull(host.GetClient(Patient));

        Assert.Equal(1, _remote.StartCount);
        Assert.Equal(1, otherHost.StartCount);
    }

    /// <summary>
    /// Codex D2: a profile deleted after the first connect keeps the target that connected - here not the profile
    /// the host was built from, whose first attempt failed before the user fixed it.
    /// </summary>
    [Fact]
    public async Task A_profile_deleted_after_the_first_connect_keeps_the_target_that_connected()
    {
        using var otherHost = new FakeRemoteHost("nova@host-b") { Script = FakeRemoteScript.ConnectionRefused };
        SshProfile built = RemoteMuxConnectorTests.Edited(RemoteMuxConnectorTests.Profile(), host: "host-b");
        SshProfile? current = built;
        MuxConnectionHost host = Own(RemoteMuxHostFactory.Create(MuxEndpointId.ForSsh(built.Id), _ => current, (profile, _) => profile.Host == "host-b" ? otherHost : _remote, log: null)!);
        Assert.Null(host.GetClient(Patient));
        current = RemoteMuxConnectorTests.Edited(built, host: "fake-host");
        MuxClient connected = host.GetClient(Patient)!;
        Task<string?> lost = WhenDisconnected(connected);

        current = null;   // deleted
        _remote.CutLink();
        await lost;
        Assert.NotNull(host.GetClient(Patient));

        Assert.Equal(2, _remote.StartCount);
        Assert.Equal(1, otherHost.StartCount);
    }

    /// <summary>Codex D2: a profile deleted before any attempt connected keeps the last profile seen, not the one the host was built from.</summary>
    [Fact]
    public void A_profile_deleted_before_any_connect_keeps_the_last_profile_seen()
    {
        using var otherHost = new FakeRemoteHost("nova@host-b") { Script = FakeRemoteScript.ConnectionRefused };
        SshProfile built = RemoteMuxConnectorTests.Profile();
        SshProfile? current = built;
        _remote.Script = FakeRemoteScript.ConnectionRefused;
        MuxConnectionHost host = Own(RemoteMuxHostFactory.Create(MuxEndpointId.ForSsh(built.Id), _ => current, (profile, _) => profile.Host == "host-b" ? otherHost : _remote, log: null)!);
        Assert.Null(host.GetClient(Patient));
        current = RemoteMuxConnectorTests.Edited(built, host: "host-b");
        Assert.Null(host.GetClient(Patient));

        current = null;   // deleted
        Assert.Null(host.GetClient(Patient));

        Assert.Equal(1, _remote.StartCount);
        Assert.Equal(2, otherHost.StartCount);
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

    /// <summary>
    /// The reader the app gives a remote host (<see cref="RemoteMuxHostFactory.ReadSavedPassword"/>) looks the profile up as
    /// the prompts save it and the askpass helper reads it: by the profile's id first, then the older keys.
    /// </summary>
    [Fact]
    public void The_apps_saved_password_reader_finds_the_profiles_vault_entry()
    {
        SshProfile profile = RemoteMuxConnectorTests.Profile();
        var vault = new Ntilde.Shell.VaultService(new Ntilde.Shell.Secrets.InMemorySecretStore());
        var legacy = new Ntilde.Shell.VaultService(new Ntilde.Shell.Secrets.InMemorySecretStore());
        Assert.Null(RemoteMuxHostFactory.ReadSavedPassword(vault, profile));

        vault.SetSecret(Ntilde.Shell.VaultService.GetCanonicalSshProfileKey(profile.Id), "s3cret");
        legacy.SetSecret("SSH:nova@fake-host", "shared");

        Assert.Equal("s3cret", RemoteMuxHostFactory.ReadSavedPassword(vault, profile));
        Assert.Equal("shared", RemoteMuxHostFactory.ReadSavedPassword(legacy, profile));
    }

    [Fact]
    public void An_OpenSSH_profile_runs_ssh_in_batch_mode_only_for_automatic_attempts()
    {
        SshProfile profile = RemoteMuxConnectorTests.Profile();
        profile.BackendKind = SshBackendKind.OpenSsh;
        var prompts = new RemoteMuxInteractionHandler(user: null, _ => false);
        ISshExecTransport Build(bool interactive) => RemoteMuxHostFactory.CreateTransport(
            profile,
            new RemoteMuxTransportRequest(interactive, prompts.BeginAttempt(interactive)),
            (_, _) => Launch,
            () => throw new InvalidOperationException("an OpenSSH profile never needs the native layer"),
            static () => true,
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
            (_, _) => throw new InvalidOperationException("a native profile never plans an ssh command line"),
            () => new NativeSshInterop(),
            static () => true,
            askPassHelperPath: null,
            log: null);

        Assert.IsType<NativeSshExecTransport>(transport);
        Assert.Equal("nova@fake-host", transport.DisplayName);
    }

    /// <summary>
    /// Codex4 F: the global native SSH switch (Settings &gt; SSH) is read by each attempt's transport, not once per host.
    /// While it is off, a native profile's attempt is refused before anything is built - no native layer, no ssh plan -
    /// as <see cref="RemoteFailureKind.NeedsUser"/>, with the message the plain SSH path refuses with; once it is on
    /// again, the next attempt gets the native transport.
    /// </summary>
    [Fact]
    public void A_native_profile_is_refused_while_native_ssh_is_off_and_nothing_is_built()
    {
        SshProfile profile = RemoteMuxConnectorTests.Profile();
        profile.BackendKind = SshBackendKind.Native;
        var prompts = new RemoteMuxInteractionHandler(user: null, _ => false);
        bool enabled = false;
        int nativeLayers = 0;
        int reads = 0;
        ISshExecTransport Build() => RemoteMuxHostFactory.CreateTransport(
            profile,
            new RemoteMuxTransportRequest(false, prompts.BeginAttempt(false)),
            (_, _) => throw new InvalidOperationException("a native profile never plans an ssh command line"),
            () =>
            {
                nativeLayers++;
                return new NativeSshInterop();
            },
            () =>
            {
                reads++;
                return enabled;
            },
            askPassHelperPath: null,
            log: null);

        RemoteMuxUnavailableException refused = Assert.Throws<RemoteMuxUnavailableException>(() => Build());

        Assert.Equal(RemoteFailureKind.NeedsUser, refused.Failure.Kind);
        Assert.Equal(SshSessionFactory.NativeSshDisabledMessage, refused.Failure.Reason);
        Assert.Equal(RemoteNeedsUserCause.NativeSshDisabled, refused.Failure.Cause);   // the pane shows this reason as it is
        Assert.Equal(0, nativeLayers);

        enabled = true;   // turned on again
        Assert.IsType<NativeSshExecTransport>(Build());
        Assert.Equal((1, 2), (nativeLayers, reads));
    }

    /// <summary>Codex4 F: the switch is the native backend's; an OpenSSH profile's attempt is built while it is off.</summary>
    [Fact]
    public void An_OpenSSH_profile_is_built_while_native_ssh_is_off()
    {
        SshProfile profile = RemoteMuxConnectorTests.Profile();
        profile.BackendKind = SshBackendKind.OpenSsh;
        var prompts = new RemoteMuxInteractionHandler(user: null, _ => false);

        ISshExecTransport transport = RemoteMuxHostFactory.CreateTransport(
            profile,
            new RemoteMuxTransportRequest(false, prompts.BeginAttempt(false)),
            (_, _) => Launch,
            () => throw new InvalidOperationException("an OpenSSH profile never needs the native layer"),
            static () => false,
            askPassHelperPath: null,
            log: null);

        Assert.IsType<OpenSshExecTransport>(transport);
    }
}
