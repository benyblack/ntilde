using Ntilde.Mux;
using Ntilde.Mux.Cli;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Mux.Transport;
using Ntilde.Platform.Ssh.Exec;
using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.Native;
using Ntilde.Shell.Mux;
using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Tests.Shell.Mux.Remote;

/// <summary>
/// <see cref="RemoteMuxConnector"/> (Phase 4 spec §7.1) against <see cref="FakeRemoteHost"/>: the real
/// proxy and daemon in-process, behind an exec channel that can fail the ways a real one does.
/// </summary>
public sealed class RemoteMuxConnectorTests : IDisposable
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

    internal static SshProfile Profile(string recordedPath = "") => new()
    {
        Id = Guid.NewGuid(),
        Name = "box",
        Host = "fake-host",
        User = "nova",
        MuxOptions = new SshMuxOptions { PersistRemoteSessions = true, RemoteDaemonPath = recordedPath },
    };

    /// <summary>
    /// <paramref name="profile"/> as the user edited it: the same id and backend, another target, and the install
    /// path the install flow recorded (unchanged when null).
    /// </summary>
    internal static SshProfile Edited(SshProfile profile, string host, int port = 22, string? user = null, string? recordedPath = null) => new()
    {
        Id = profile.Id,
        Name = profile.Name,
        BackendKind = profile.BackendKind,
        Host = host,
        Port = port,
        User = user ?? profile.User,
        MuxOptions = new SshMuxOptions { PersistRemoteSessions = true, RemoteDaemonPath = recordedPath ?? profile.MuxOptions.RemoteDaemonPath },
    };

    internal static SshInteractionRequest PasswordPrompt { get; } = new() { Kind = SshInteractionKind.Password, Prompt = "Password:" };

    internal static SshInteractionRequest PassphrasePrompt { get; } =
        new() { Kind = SshInteractionKind.Passphrase, Prompt = "Enter passphrase for key '/home/nova/.ssh/id_ed25519':" };

    private T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }

    private RemoteMuxConnector Connector(string instanceId = "instance-1") =>
        Own(new RemoteMuxConnector(Profile(), _remote, instanceId, log: null));

    private static Task<string?> WhenDisconnected(MuxClient client)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += reason => tcs.TrySetResult(reason);
        if (!client.IsConnected) tcs.TrySetResult(client.DisconnectReason);
        return tcs.Task.WaitAsync(Patient, Ct);
    }

    [Fact]
    public async Task Connects_through_the_proxy_and_the_client_works()
    {
        MuxClient client = Own(await Connector().ConnectAsync(Ct));

        Guid id = await MuxTestHost.SpawnAsync(client);
        IReadOnlyList<SessionSummary> sessions = await client.ListSessionsAsync(Ct);

        Assert.Contains(sessions, s => s.SessionId == id && s.Running);
        Assert.Equal(MuxProtocol.MaxSupportedVersion, client.ProtocolVersion);
        Assert.Equal(1, _remote.StartCount);
        Assert.Equal(RemoteMuxCommand.Proxy(new SshMuxOptions()), Assert.Single(_remote.Commands));
    }

    [Fact]
    public async Task The_recorded_install_path_is_the_command()
    {
        var connector = Own(new RemoteMuxConnector(Profile("/home/nova/.local/share/ntilde/bin/ntilde-mux"), _remote, "i", null));

        Own(await connector.ConnectAsync(Ct));

        Assert.Equal("/home/nova/.local/share/ntilde/bin/ntilde-mux proxy --stdio", Assert.Single(_remote.Commands));
    }

    [Fact]
    public async Task Noise_before_the_greeting_is_skipped()
    {
        _remote.Noise = "Welcome to Ubuntu 24.04 LTS\r\n\r\nLast login: Mon Oct  5 09:00:00 2026 from 10.0.0.2\r\n";

        MuxClient client = Own(await Connector().ConnectAsync(Ct));

        await client.PingAsync(Ct);
    }

    [Fact]
    public async Task A_missing_binary_is_NotInstalled_and_its_channel_is_ended()
    {
        _remote.Script = FakeRemoteScript.NotInstalledDash;

        var ex = await Assert.ThrowsAsync<RemoteMuxUnavailableException>(() => Connector().ConnectAsync(Ct));

        Assert.Equal(new RemoteMuxFailure(RemoteFailureKind.NotInstalled, "ntilde-mux is not installed"), ex.Failure);
        Assert.IsType<MuxProxyHandshakeException>(ex.InnerException);
        Assert.True(_remote.LastChannel!.IsDisposed);
    }

    [Fact]
    public async Task The_instance_id_is_sent_so_a_reconnect_evicts_its_dead_twin()
    {
        RemoteMuxConnector connector = Connector("host-instance");
        MuxClient first = Own(await connector.ConnectAsync(Ct));
        Task<string?> firstGone = WhenDisconnected(first);

        // The same host connecting again over a new channel, while the daemon still holds the old one.
        MuxClient second = Own(await connector.ConnectAsync(Ct));

        await firstGone;
        Assert.True(second.IsConnected);
        Assert.Equal("host-instance", connector.ClientInstanceId);

        // Another host, with its own instance id, leaves it alone.
        MuxClient other = Own(await Connector("other-instance").ConnectAsync(Ct));
        await other.PingAsync(Ct);
        await second.PingAsync(Ct);
        Assert.True(second.IsConnected);
    }

    [Fact]
    public async Task OpenSsh_failing_on_its_own_is_SshFailed_with_its_last_stderr_line()
    {
        _remote.Script = FakeRemoteScript.ConnectionRefused;

        var ex = await Assert.ThrowsAsync<RemoteMuxUnavailableException>(() => Connector().ConnectAsync(Ct));

        Assert.Equal(new RemoteMuxFailure(RemoteFailureKind.SshFailed, "ssh: connect to host fake-host port 22: Connection refused"), ex.Failure);
    }

    /// <summary>
    /// Task 20's ruling: an automatic attempt runs ssh in batch mode, so a refusal means a password or a
    /// passphrase is needed - the user, not another try. A user's attempt refused the same way failed SSH.
    /// </summary>
    [Fact]
    public async Task An_automatic_attempt_ssh_refused_needs_the_user_and_a_user_attempt_is_an_ssh_failure()
    {
        _remote.Script = new FakeRemoteScript(Stderr: "nova@fake-host: Permission denied (publickey,password).\r\n", ExitCode: FakeRemoteHost.LinkLostExitCode);
        RemoteMuxConnector connector = Connector();

        var automatic = await Assert.ThrowsAsync<RemoteMuxUnavailableException>(() => connector.ConnectAsync(interactive: false, Ct));
        var user = await Assert.ThrowsAsync<RemoteMuxUnavailableException>(() => connector.ConnectAsync(interactive: true, Ct));

        Assert.Equal(new RemoteMuxFailure(RemoteFailureKind.NeedsUser, "nova@fake-host: Permission denied (publickey,password)."), automatic.Failure);
        Assert.Equal(new RemoteMuxFailure(RemoteFailureKind.SshFailed, "nova@fake-host: Permission denied (publickey,password)."), user.Failure);
    }

    [Fact]
    public async Task A_native_transport_failure_is_SshFailed_with_the_native_message()
    {
        _remote.Script = FakeRemoteScript.NativeFailure("Host key verification failed");

        var ex = await Assert.ThrowsAsync<RemoteMuxUnavailableException>(() => Connector().ConnectAsync(Ct));

        Assert.Equal(new RemoteMuxFailure(RemoteFailureKind.SshFailed, "Host key verification failed"), ex.Failure);
        Assert.IsType<SshExecTransportException>(ex.InnerException?.InnerException);
    }

    [Fact]
    public async Task A_daemon_of_another_protocol_is_VersionMismatch()
    {
        using var remote = new FakeRemoteHost(serverOptions: new MuxServerOptions { ForceConPtyFiltering = false, MinProtocolVersion = 3, MaxProtocolVersion = 4 });
        var connector = Own(new RemoteMuxConnector(Profile(), remote, "i", null));

        var ex = await Assert.ThrowsAsync<RemoteMuxUnavailableException>(() => connector.ConnectAsync(Ct));

        Assert.Equal(RemoteFailureKind.VersionMismatch, ex.Failure.Kind);
        Assert.Equal(
            $"ntilde-mux on nova@fake-host speaks protocol 3-4; this app speaks {MuxProtocol.MinSupportedVersion}-{MuxProtocol.MaxSupportedVersion}",
            ex.Failure.Reason);
        await remote.LastChannel!.Disposed.WaitAsync(Patient, Ct);
    }

    [Fact]
    public async Task A_transport_that_cannot_start_is_SshFailed()
    {
        _remote.OnStart = _ => throw new FileNotFoundException("Unable to locate system OpenSSH executable (ssh).");

        var ex = await Assert.ThrowsAsync<RemoteMuxUnavailableException>(() => Connector().ConnectAsync(Ct));

        Assert.Equal(RemoteFailureKind.SshFailed, ex.Failure.Kind);
        Assert.Contains("Unable to locate system OpenSSH executable (ssh).", ex.Failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_a_connect_that_waits_for_the_greeting_ends_its_channel()
    {
        _remote.Script = FakeRemoteScript.Silent;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Task<MuxClient> connecting = Connector().ConnectAsync(cts.Token);
        await TestWait.UntilAsync(() => _remote.LastChannel is not null, "the channel started");

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        FakeRemoteChannel channel = _remote.LastChannel!;
        await channel.Disposed.WaitAsync(Patient, Ct);
        Assert.Null(await channel.Completion.WaitAsync(Patient, Ct));   // stopped by the channel
    }

    /// <summary>
    /// Ruling 5: a client over an exec channel owns nothing that can end a read pending on the channel's
    /// stdout - on Windows, closing a pipe's read end does not; ssh's exit does - so the connector ends
    /// the channel once its client disconnects. Here the link is silent, so no EOF would ever come.
    /// </summary>
    [Fact]
    public async Task A_client_that_disconnects_ends_its_channel_so_its_reader_returns_even_on_a_dead_link()
    {
        RemoteMuxConnector connector = Connector();
        MuxClient client = await connector.ConnectAsync(Ct);
        FakeRemoteChannel channel = _remote.LastChannel!;
        await client.PingAsync(Ct);
        _remote.StallLink();

        client.Dispose();

        await channel.Disposed.WaitAsync(Patient, Ct);
        Assert.Equal(0, channel.PendingStdoutReads);
        Assert.Null(await connector.LastExitAsync(Patient));   // the channel had to stop it
    }

    [Fact]
    public async Task LastExitAsync_tells_a_stopped_daemon_from_a_lost_link()
    {
        RemoteMuxConnector connector = Connector();
        MuxClient client = Own(await connector.ConnectAsync(Ct));
        Task<string?> stopped = WhenDisconnected(client);

        _remote.StopDaemon();

        await stopped;
        Assert.Equal(MuxProxyExitCodes.DaemonClosed, await connector.LastExitAsync(Patient));

        MuxClient again = Own(await connector.ConnectAsync(Ct));
        Task<string?> lost = WhenDisconnected(again);

        _remote.CutLink();

        await lost;
        Assert.Equal(FakeRemoteHost.LinkLostExitCode, await connector.LastExitAsync(Patient));
    }

    /// <summary>
    /// Review fix: a disconnect is classified by the lost client's own channel. By then a new attempt may
    /// have started, and the latest channel is that one, still running.
    /// </summary>
    [Fact]
    public async Task ExitAsync_reads_the_lost_clients_own_channel_not_the_latest()
    {
        RemoteMuxConnector connector = Connector();
        MuxClient stopped = Own(await connector.ConnectAsync(Ct));
        Task<string?> gone = WhenDisconnected(stopped);
        _remote.StopDaemon();
        await gone;

        Own(await connector.ConnectAsync(Ct));   // a new attempt, before the loss was classified

        Assert.Equal(MuxProxyExitCodes.DaemonClosed, await connector.ExitAsync(stopped, Patient));
        Assert.Null(await connector.LastExitAsync(TimeSpan.FromMilliseconds(50)));   // the latest is the new, running channel
    }

    [Fact]
    public async Task LastExitAsync_is_null_before_any_channel_and_while_the_channel_runs()
    {
        RemoteMuxConnector connector = Connector();

        Assert.Null(await connector.LastExitAsync(TimeSpan.FromMilliseconds(10)));

        Own(await connector.ConnectAsync(Ct));
        Assert.Null(await connector.LastExitAsync(TimeSpan.FromMilliseconds(10)));
    }

    /// <summary>Ruling 1: the native transport starts before auth, so the greeting wait includes the user answering prompts.</summary>
    [Fact]
    public void The_greeting_wait_is_the_remote_connect_timeout()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), Connector().PreambleTimeout);
        Assert.Equal(MuxHostPolicy.Remote("x").ConnectTimeout, Connector().PreambleTimeout);
    }

    [Fact]
    public async Task Each_attempt_builds_its_transport_for_its_interactivity()
    {
        var requests = new List<RemoteMuxTransportRequest>();
        var connector = Own(new RemoteMuxConnector(
            () => Profile(),
            (_, request) =>
            {
                lock (requests) requests.Add(request);
                return _remote;
            },
            new RemoteMuxInteractionHandler(user: null, isTrustedHostKey: _ => false),
            "i",
            null));

        Own(await connector.ConnectAsync(Ct));
        Own(await connector.ConnectAsync(interactive: false, Ct));

        Assert.Equal(new[] { true, false }, requests.Select(r => r.Interactive));
        Assert.All(requests, r => Assert.NotNull(r.Prompts));
    }

    [Fact]
    public async Task A_password_answered_on_a_successful_attempt_answers_the_next_automatic_one()
    {
        var user = new ScriptedUser(SshInteractionResponse.FromSecret("hunter2"));
        var answers = new List<SshInteractionResponse>();
        // The native transport asks from its poll thread while auth runs, before the greeting: here, in Start.
        var connector = Own(new RemoteMuxConnector(
            () => Profile(),
            (_, request) =>
            {
                _remote.OnStart = _ => answers.Add(request.Prompts.HandleAsync(PasswordPrompt, CancellationToken.None).GetAwaiter().GetResult());
                return _remote;
            },
            new RemoteMuxInteractionHandler(user, _ => false),
            "i",
            null));

        Own(await connector.ConnectAsync(Ct));
        Own(await connector.ConnectAsync(interactive: false, Ct));

        Assert.Equal(new[] { "hunter2", "hunter2" }, answers.Select(a => a.Secret));
        Assert.Single(user.Asked);
    }

    [Fact]
    public async Task Dispose_forgets_the_remembered_secrets()
    {
        var connector = new RemoteMuxConnector(() => Profile(), (_, _) => _remote,
            new RemoteMuxInteractionHandler(new ScriptedUser(SshInteractionResponse.FromSecret("hunter2")), _ => false), "i", null);
        RemoteMuxInteractionHandler.Attempt attempt = connector.Prompts.BeginAttempt(interactive: true);
        await attempt.HandleAsync(PasswordPrompt, Ct);
        attempt.Succeeded();
        Assert.True(connector.Prompts.Remembers(SshInteractionKind.Password));

        connector.Dispose();

        Assert.False(connector.Prompts.Remembers(SshInteractionKind.Password));
    }

    /// <summary>
    /// Review fix round 1, with the native auth shape: rusty_ssh asks for the password once, falls to
    /// keyboard-interactive, then fails. Once the server's password changes, the automatic attempt's
    /// remembered password is refused, keyboard-interactive is not answered, the attempt fails NeedsUser -
    /// and the stale password is forgotten, so the next user attempt asks instead of replaying it.
    /// </summary>
    [Fact]
    public async Task A_remembered_password_the_server_refuses_is_forgotten_and_the_next_user_attempt_asks()
    {
        var user = new ScriptedUser(SshInteractionResponse.FromSecret("old"), SshInteractionResponse.FromSecret("new"));
        var prompts = new RemoteMuxInteractionHandler(user, _ => false);
        string serverPassword = "old";
        var offered = new List<string>();
        var connector = Own(new RemoteMuxConnector(
            () => Profile(),
            (_, request) =>
            {
                _remote.OnStart = _ =>
                {
                    SshInteractionResponse password = request.Prompts.HandleAsync(PasswordPrompt, CancellationToken.None).GetAwaiter().GetResult();
                    lock (offered) offered.Add(password.IsCanceled ? "<cancelled>" : password.Secret);
                    if (password.Secret == serverPassword)
                    {
                        _remote.Script = null;
                        return;
                    }

                    try
                    {
                        request.Prompts.HandleAsync(KeyboardPrompt, CancellationToken.None).GetAwaiter().GetResult();
                        _remote.Script = FakeRemoteScript.NativeFailure("SSH authentication failed");
                    }
                    catch (RemoteMuxPromptAbortedException ex)
                    {
                        // An automatic attempt has nothing to answer with: the native channel closes the
                        // session without answering (see the native tests below), and SSH fails.
                        _remote.Script = FakeRemoteScript.NativeFailure($"the native session failed: {ex.Message}");
                    }
                };
                return _remote;
            },
            prompts,
            "i",
            null));
        Own(await connector.ConnectAsync(Ct));   // the user types "old": it works, and is remembered
        serverPassword = "new";                   // changed on the server

        var refused = await Assert.ThrowsAsync<RemoteMuxUnavailableException>(() => connector.ConnectAsync(interactive: false, Ct));

        Assert.Equal(RemoteFailureKind.NeedsUser, refused.Failure.Kind);
        Assert.False(prompts.Remembers(SshInteractionKind.Password));
        Own(await connector.ConnectAsync(Ct));
        Assert.Equal(new[] { "old", "old", "new" }, offered);
        Assert.Equal(2, user.Asked.Count);
        Assert.All(user.Asked, request => Assert.Equal(SshInteractionKind.Password, request.Kind));
    }

    /// <summary>Review fix round 1: a failure after auth (the binary is missing) does not cost the remembered password.</summary>
    [Fact]
    public async Task A_failure_after_auth_keeps_the_remembered_password()
    {
        var prompts = new RemoteMuxInteractionHandler(new ScriptedUser(SshInteractionResponse.FromSecret("pw")), _ => false);
        var connector = Own(new RemoteMuxConnector(
            () => Profile(),
            (_, request) =>
            {
                _remote.OnStart = _ => request.Prompts.HandleAsync(PasswordPrompt, CancellationToken.None).GetAwaiter().GetResult();
                return _remote;
            },
            prompts,
            "i",
            null));
        Own(await connector.ConnectAsync(Ct));
        _remote.Script = FakeRemoteScript.NotInstalledDash;

        var failed = await Assert.ThrowsAsync<RemoteMuxUnavailableException>(() => connector.ConnectAsync(interactive: false, Ct));

        Assert.Equal(RemoteFailureKind.NotInstalled, failed.Failure.Kind);
        Assert.True(prompts.Remembers(SshInteractionKind.Password));
    }

    /// <summary>
    /// Review fix round 1: native password prompts do not say which hop asks, so a profile with a jump
    /// host never remembers or replays a password - its automatic attempts fail quietly, and Enter asks.
    /// </summary>
    [Fact]
    public async Task With_a_jump_host_a_password_is_neither_remembered_nor_replayed()
    {
        SshProfile profile = Profile();
        profile.JumpHops.Add(new SshJumpHop { Host = "bastion", User = "nova", Port = 22 });
        var user = new ScriptedUser(SshInteractionResponse.FromSecret("pw"));
        var answers = new List<string>();
        var connector = Own(new RemoteMuxConnector(
            () => profile,
            (_, request) =>
            {
                _remote.OnStart = _ =>
                {
                    try
                    {
                        answers.Add(request.Prompts.HandleAsync(PasswordPrompt, CancellationToken.None).GetAwaiter().GetResult().Secret);
                        _remote.Script = null;
                    }
                    catch (RemoteMuxPromptAbortedException ex)
                    {
                        // As the native channel does: the session closes without answering.
                        answers.Add("<aborted>");
                        _remote.Script = FakeRemoteScript.NativeFailure($"the native session failed: {ex.Message}");
                    }
                };
                return _remote;
            },
            new RemoteMuxInteractionHandler(user, _ => false),
            "i",
            null));

        Own(await connector.ConnectAsync(Ct));

        Assert.False(connector.Prompts.Remembers(SshInteractionKind.Password));
        var automatic = await Assert.ThrowsAsync<RemoteMuxUnavailableException>(() => connector.ConnectAsync(interactive: false, Ct));
        Assert.Equal(RemoteFailureKind.NeedsUser, automatic.Failure.Kind);
        Assert.Equal(new[] { "pw", "<aborted>" }, answers);
        Assert.Single(user.Asked);
    }

    [Fact]
    public async Task A_disposed_connector_does_not_connect()
    {
        RemoteMuxConnector connector = Connector();
        connector.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => connector.ConnectAsync(Ct));
        Assert.Equal(0, _remote.StartCount);
    }

    [Fact]
    public async Task A_channel_started_while_the_connector_is_disposed_is_ended()
    {
        RemoteMuxConnector connector = Connector();
        _remote.OnStart = _ => connector.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => connector.ConnectAsync(Ct));

        await _remote.LastChannel!.Disposed.WaitAsync(Patient, Ct);
    }

    private static SshProfile NativeProfile()
    {
        SshProfile profile = Profile();
        profile.BackendKind = SshBackendKind.Native;
        return profile;
    }

    private static RemoteMuxConnector NativeConnector(PromptingNativeSshInterop interop, ISshInteractionHandler? user) =>
        new(
            NativeProfile,
            (profile, request) => RemoteMuxHostFactory.CreateTransport(
                profile,
                request,
                (_, _) => throw new InvalidOperationException("a native profile never plans an ssh command line"),
                () => interop,
                static () => true,
                askPassHelperPath: null,
                log: _ => { }),
            new RemoteMuxInteractionHandler(user, _ => false),
            "i",
            null);

    /// <summary>
    /// Review fix round 2, end to end through the real native exec transport: an automatic attempt with
    /// no remembered password must not answer the password prompt at all - a cancel would reach the
    /// server as an empty password, a failed login on every reconnect. The session is closed instead (in
    /// rusty_ssh the close wakes the pending prompt with no answer, and auth stops before sending
    /// anything), and the attempt fails quietly as NeedsUser (Task 20's ruling: the marker the reconnect
    /// loop stops on).
    /// </summary>
    [Fact]
    public async Task An_automatic_native_attempt_with_nothing_remembered_never_answers_the_password_prompt()
    {
        var interop = new PromptingNativeSshInterop(PromptingNativeSshInterop.PasswordPrompt);
        var user = new ScriptedUser(SshInteractionResponse.FromSecret("never asked"));
        RemoteMuxConnector connector = Own(NativeConnector(interop, user));

        var failed = await Assert.ThrowsAsync<RemoteMuxUnavailableException>(() => connector.ConnectAsync(interactive: false, Ct));

        Assert.Empty(interop.Submissions);
        Assert.Equal(1, interop.Closes);
        Assert.Empty(user.Asked);
        Assert.Equal(RemoteFailureKind.NeedsUser, failed.Failure.Kind);
        Assert.Equal("signing in to nova@fake-host needs a password, which an automatic reconnect does not ask for", failed.Failure.Reason);
    }

    /// <summary>
    /// Codex D3, end to end through the real native transport: an automatic attempt meets an encrypted key's passphrase
    /// prompt with nothing remembered. It cancels it - a passphrase only unlocks a local key, so nothing reaches the
    /// server, and the agent or another key may still get in - but this time nothing else does, and SSH fails. Signing
    /// in needs the user, so the failure is NeedsUser, the marker the reconnect loop stops on, not SshFailed.
    /// </summary>
    [Fact]
    public async Task An_automatic_native_attempt_that_cancels_an_unremembered_passphrase_and_then_fails_needs_the_user()
    {
        var interop = new PromptingNativeSshInterop(
            PromptingNativeSshInterop.PassphrasePrompt,
            PromptingNativeSshInterop.Error("Authentication failed: no authentication method succeeded"),
            NativeSshEvent.Closed());
        var user = new ScriptedUser(SshInteractionResponse.FromSecret("never asked"));
        RemoteMuxConnector connector = Own(NativeConnector(interop, user));

        var failed = await Assert.ThrowsAsync<RemoteMuxUnavailableException>(() => connector.ConnectAsync(interactive: false, Ct));

        (NativeSshResponseKind kind, string payload) = Assert.Single(interop.Submissions);
        Assert.Equal(NativeSshResponseKind.Passphrase, kind);
        Assert.Equal("""{"text":""}""", payload);   // the cancel: no secret, and none sent to the server
        Assert.Empty(user.Asked);
        Assert.Equal(RemoteFailureKind.NeedsUser, failed.Failure.Kind);
        Assert.Equal("signing in to nova@fake-host needs a key passphrase, which an automatic reconnect does not ask for", failed.Failure.Reason);
    }

    /// <summary>Codex D3: the same cancelled passphrase, but another way in (the agent, another key) works: connected, nothing recorded against it.</summary>
    [Fact]
    public async Task An_automatic_attempt_that_cancels_a_passphrase_but_gets_in_another_way_connects()
    {
        var answers = new List<SshInteractionResponse>();
        var connector = Own(new RemoteMuxConnector(
            () => Profile(),
            (_, request) =>
            {
                _remote.OnStart = _ => answers.Add(request.Prompts.HandleAsync(PassphrasePrompt, CancellationToken.None).GetAwaiter().GetResult());
                return _remote;
            },
            new RemoteMuxInteractionHandler(new ScriptedUser(), _ => false),
            "i",
            null));

        MuxClient client = Own(await connector.ConnectAsync(interactive: false, Ct));

        Assert.True(client.IsConnected);
        Assert.True(Assert.Single(answers).IsCanceled);
        Assert.False(connector.Prompts.Remembers(SshInteractionKind.Passphrase));
    }

    /// <summary>Codex D3: a passphrase that got a connection in is still remembered and offered to the next automatic attempt, unchanged.</summary>
    [Fact]
    public async Task A_remembered_passphrase_answers_the_next_automatic_attempt()
    {
        var user = new ScriptedUser(SshInteractionResponse.FromSecret("correct horse"));
        var answers = new List<SshInteractionResponse>();
        var connector = Own(new RemoteMuxConnector(
            () => Profile(),
            (_, request) =>
            {
                _remote.OnStart = _ => answers.Add(request.Prompts.HandleAsync(PassphrasePrompt, CancellationToken.None).GetAwaiter().GetResult());
                return _remote;
            },
            new RemoteMuxInteractionHandler(user, _ => false),
            "i",
            null));

        Own(await connector.ConnectAsync(Ct));
        Own(await connector.ConnectAsync(interactive: false, Ct));

        Assert.Equal(new[] { "correct horse", "correct horse" }, answers.Select(a => a.Secret));
        Assert.Single(user.Asked);
    }

    /// <summary>The same prompt on a user-started attempt: the user's answer is what is submitted.</summary>
    [Fact]
    public async Task A_user_native_attempt_submits_the_typed_password()
    {
        var interop = new PromptingNativeSshInterop(PromptingNativeSshInterop.PasswordPrompt);
        RemoteMuxConnector connector = Own(NativeConnector(interop, new ScriptedUser(SshInteractionResponse.FromSecret("pw"))));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        Task<MuxClient> connecting = connector.ConnectAsync(interactive: true, cts.Token);
        await TestWait.UntilAsync(() => interop.Submissions.Count > 0, "the password was submitted");
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        (NativeSshResponseKind kind, string payload) = Assert.Single(interop.Submissions);
        Assert.Equal(NativeSshResponseKind.Password, kind);
        Assert.Equal("""{"text":"pw"}""", payload);
    }

    private static SshInteractionRequest KeyboardPrompt { get; } = new()
    {
        Kind = SshInteractionKind.KeyboardInteractive,
        KeyboardPrompts = [new SshKeyboardPrompt("Password: ", echo: false)],
    };

    private static readonly Ntilde.Services.Ssh.SshLaunchDetails Launch = new()
    {
        SshPath = "/usr/bin/ssh",
        ConfigPath = "cfg",
        Alias = "ntilde_0123",
        CommandLine = "/usr/bin/ssh -F cfg ntilde_0123",
        PlanArguments = ["-F", "cfg", "ntilde_0123"],
    };

    /// <summary>sshd refusing an OpenSSH sign-in: exit 255 and its reason on stderr.</summary>
    private static readonly FakeRemoteScript PermissionDenied =
        new(Stderr: "nova@fake-host: Permission denied (publickey,password).\r\n", ExitCode: FakeRemoteHost.LinkLostExitCode);

    /// <summary>The vault as the app reads it for a remote host: a value, and every read counted.</summary>
    private sealed class SavedPasswords(string? value)
    {
        private int _reads;

        public string? Value { get; set; } = value;

        public int Reads => Volatile.Read(ref _reads);

        public string? Read(SshProfile profile)
        {
            Interlocked.Increment(ref _reads);
            return Value;
        }
    }

    /// <summary>
    /// A connector over the app's own transport choice (<see cref="RemoteMuxHostFactory.CreateTransport"/>) for an
    /// OpenSSH profile: each attempt's transport is built and recorded - not started - and the fake remote runs in its
    /// place, with the script <paramref name="script"/> picks for that transport (sshd's answer to how it signs in).
    /// </summary>
    private RemoteMuxConnector OpenSshConnector(
        SshProfile profile, SavedPasswords saved, List<OpenSshExecTransport> built, Func<OpenSshExecTransport, FakeRemoteScript?>? script = null)
    {
        profile.BackendKind = SshBackendKind.OpenSsh;
        return Own(new RemoteMuxConnector(
            () => profile,
            (p, request) =>
            {
                var transport = Assert.IsType<OpenSshExecTransport>(RemoteMuxHostFactory.CreateTransport(
                    p,
                    request,
                    (_, _) => Launch,
                    () => throw new InvalidOperationException("an OpenSSH profile never needs the native layer"),
                    static () => true,
                    askPassHelperPath: "/opt/ntilde/ntilde",
                    log: _ => { }));
                lock (built) built.Add(transport);
                _remote.Script = script?.Invoke(transport);
                return _remote;
            },
            new RemoteMuxInteractionHandler(user: null, _ => false, saved.Read),
            "i",
            null));
    }

    /// <summary>
    /// The user's choice after the smoke test: an automatic OpenSSH attempt for a profile whose password is saved runs
    /// ssh with the askpass helper in its vault-only mode, which answers the password with no UI. A user's attempt is
    /// unchanged, and reads nothing here: its helper asks the vault itself.
    /// </summary>
    [Fact]
    public async Task An_automatic_OpenSSH_attempt_with_a_saved_password_runs_the_vault_only_askpass()
    {
        var saved = new SavedPasswords("s3cret");
        var built = new List<OpenSshExecTransport>();
        RemoteMuxConnector connector = OpenSshConnector(Profile(), saved, built);

        Own(await connector.ConnectAsync(interactive: true, Ct));
        Own(await connector.ConnectAsync(interactive: false, Ct));

        Assert.Equal(new[] { (false, false), (false, true) }, built.Select(t => (t.BatchMode, t.SavedPasswordOnly)));
        Assert.Equal(1, saved.Reads);
    }

    /// <summary>With jump hops (the same conservative rule as native), or nothing saved, an automatic attempt is the batch mode it was.</summary>
    [Fact]
    public async Task With_jump_hops_or_nothing_saved_an_automatic_OpenSSH_attempt_runs_in_batch_mode()
    {
        SshProfile viaJumpHost = Profile();
        viaJumpHost.JumpHops.Add(new SshJumpHop { Host = "bastion", User = "nova", Port = 22 });
        var hopsSaved = new SavedPasswords("s3cret");
        var nothingSaved = new SavedPasswords(null);
        var built = new List<OpenSshExecTransport>();

        Own(await OpenSshConnector(viaJumpHost, hopsSaved, built).ConnectAsync(interactive: false, Ct));
        Own(await OpenSshConnector(Profile(), nothingSaved, built).ConnectAsync(interactive: false, Ct));

        Assert.All(built, t => Assert.Equal((true, false), (t.BatchMode, t.SavedPasswordOnly)));
        Assert.Equal(2, built.Count);
        Assert.Equal(0, hopsSaved.Reads);
        Assert.Equal(1, nothingSaved.Reads);
    }

    /// <summary>
    /// The saved password is the profile's, for where the profile points now. Once the host's destination is pinned and
    /// the profile is edited to another host (codex D2), the attempts still go to the pinned one: they do not offer it there.
    /// </summary>
    [Fact]
    public async Task A_retargeted_automatic_attempt_does_not_offer_the_saved_password()
    {
        SshProfile first = Profile();
        SshProfile current = first;
        var offers = new List<(bool Retargeted, bool MayOffer)>();
        var connector = Own(new RemoteMuxConnector(
            () => current,
            (_, request) =>
            {
                lock (offers) offers.Add((request.Retargeted, request.OfferSavedPassword is not null));
                return _remote;
            },
            new RemoteMuxInteractionHandler(user: null, _ => false, _ => "s3cret"),
            "i",
            null));
        MuxClient pinned = Own(await connector.ConnectAsync(interactive: true, Ct));
        connector.Accept(pinned);

        Own(await connector.ConnectAsync(interactive: false, Ct));          // pinned, the profile unedited
        current = Edited(first, host: "host-b");
        Own(await connector.ConnectAsync(interactive: false, Ct));          // pinned, the profile now names host-b

        Assert.Equal(new[] { (false, false), (false, true), (true, false) }, offers);
    }

    /// <summary>
    /// A refused saved password stops at once: NeedsUser (the reconnect loop stops on it), saying the saved password was
    /// refused - not ssh's words, which stay in the reason. With BatchMode=no, "Permission denied" is still the refusal.
    /// The host's next automatic attempt does not offer it again: batch mode, which needs the user too.
    /// </summary>
    [Theory]
    [InlineData("nova@fake-host: Permission denied (publickey,password).\r\n")]
    [InlineData("Received disconnect from 10.0.0.2 port 22:2: Too many authentication failures\r\nDisconnected from 10.0.0.2 port 22\r\n")]
    public async Task A_refused_saved_password_on_OpenSSH_needs_the_user_and_is_not_offered_again(string sshdSaid)
    {
        var saved = new SavedPasswords("wrong");
        var built = new List<OpenSshExecTransport>();
        RemoteMuxConnector connector = OpenSshConnector(Profile(), saved, built, _ => new FakeRemoteScript(Stderr: sshdSaid, ExitCode: FakeRemoteHost.LinkLostExitCode));

        var refused = await Assert.ThrowsAsync<RemoteMuxUnavailableException>(() => connector.ConnectAsync(interactive: false, Ct));
        var again = await Assert.ThrowsAsync<RemoteMuxUnavailableException>(() => connector.ConnectAsync(interactive: false, Ct));

        Assert.Equal((RemoteFailureKind.NeedsUser, RemoteNeedsUserCause.SavedPasswordRefused), (refused.Failure.Kind, refused.Failure.Cause));
        Assert.Equal((RemoteFailureKind.NeedsUser, RemoteNeedsUserCause.SignIn), (again.Failure.Kind, again.Failure.Cause));
        Assert.Equal(new[] { (false, true), (true, false) }, built.Select(t => (t.BatchMode, t.SavedPasswordOnly)));
        Assert.Equal(1, saved.Reads);
    }

    /// <summary>
    /// An attempt that never reached the password - nothing listening, the host down - did not have it refused: the
    /// failure stays SshFailed, the loop retries, and the next automatic attempt offers the saved password again.
    /// </summary>
    [Fact]
    public async Task An_OpenSSH_attempt_that_never_reached_sign_in_keeps_offering_the_saved_password()
    {
        var saved = new SavedPasswords("s3cret");
        var built = new List<OpenSshExecTransport>();
        RemoteMuxConnector connector = OpenSshConnector(Profile(), saved, built, _ => FakeRemoteScript.ConnectionRefused);

        var down = await Assert.ThrowsAsync<RemoteMuxUnavailableException>(() => connector.ConnectAsync(interactive: false, Ct));
        await Assert.ThrowsAsync<RemoteMuxUnavailableException>(() => connector.ConnectAsync(interactive: false, Ct));

        Assert.Equal(RemoteFailureKind.SshFailed, down.Failure.Kind);
        Assert.Equal(2, built.Count);
        Assert.All(built, t => Assert.True(t.SavedPasswordOnly));
    }

    /// <summary>A saved password the server takes: the automatic attempt connects, with no NeedsUser.</summary>
    [Fact]
    public async Task A_good_saved_password_connects_an_automatic_OpenSSH_attempt()
    {
        var built = new List<OpenSshExecTransport>();
        RemoteMuxConnector connector = OpenSshConnector(Profile(), new SavedPasswords("s3cret"), built, t => t.SavedPasswordOnly ? null : PermissionDenied);

        MuxClient client = Own(await connector.ConnectAsync(interactive: false, Ct));

        Assert.True(client.IsConnected);
    }

    private static RemoteMuxConnector NativeConnector(PromptingNativeSshInterop interop, SavedPasswords saved) =>
        new(
            NativeProfile,
            (profile, request) => RemoteMuxHostFactory.CreateTransport(
                profile,
                request,
                (_, _) => throw new InvalidOperationException("a native profile never plans an ssh command line"),
                () => interop,
                static () => true,
                askPassHelperPath: null,
                log: _ => { }),
            new RemoteMuxInteractionHandler(new ScriptedUser(SshInteractionResponse.FromSecret("never asked")), _ => false, saved.Read),
            "i",
            null);

    /// <summary>
    /// End to end through the real native exec transport: an automatic attempt answers the password prompt with the saved
    /// password, once. rusty_ssh then fails auth: the attempt needs the user, saying the saved password was refused. The
    /// host's next automatic attempt does not send it again - it closes the session at the prompt, answering nothing.
    /// </summary>
    [Fact]
    public async Task An_automatic_native_attempt_submits_the_saved_password_once_and_a_refusal_needs_the_user()
    {
        var interop = new PromptingNativeSshInterop(
            PromptingNativeSshInterop.PasswordPrompt,
            PromptingNativeSshInterop.Error("Authentication failed: no authentication method succeeded"),
            NativeSshEvent.Closed(),
            PromptingNativeSshInterop.PasswordPrompt);
        var saved = new SavedPasswords("s3cret");
        RemoteMuxConnector connector = Own(NativeConnector(interop, saved));

        var refused = await Assert.ThrowsAsync<RemoteMuxUnavailableException>(() => connector.ConnectAsync(interactive: false, Ct));
        var again = await Assert.ThrowsAsync<RemoteMuxUnavailableException>(() => connector.ConnectAsync(interactive: false, Ct));

        (NativeSshResponseKind kind, string payload) = Assert.Single(interop.Submissions);
        Assert.Equal(NativeSshResponseKind.Password, kind);
        Assert.Equal("""{"text":"s3cret"}""", payload);
        Assert.Equal((RemoteFailureKind.NeedsUser, RemoteNeedsUserCause.SavedPasswordRefused), (refused.Failure.Kind, refused.Failure.Cause));
        Assert.Equal((RemoteFailureKind.NeedsUser, RemoteNeedsUserCause.SignIn), (again.Failure.Kind, again.Failure.Cause));
        Assert.Equal(1, saved.Reads);
    }

    /// <summary>rusty_ssh asking for a password again after the saved one: never offered twice, and never an empty answer.</summary>
    [Fact]
    public async Task A_second_native_password_prompt_after_the_saved_password_ends_the_attempt_without_answering()
    {
        var interop = new PromptingNativeSshInterop(PromptingNativeSshInterop.PasswordPrompt, PromptingNativeSshInterop.PasswordPrompt);
        RemoteMuxConnector connector = Own(NativeConnector(interop, new SavedPasswords("s3cret")));

        var refused = await Assert.ThrowsAsync<RemoteMuxUnavailableException>(() => connector.ConnectAsync(interactive: false, Ct));

        Assert.Single(interop.Submissions);
        Assert.Equal(1, interop.Closes);
        Assert.Equal((RemoteFailureKind.NeedsUser, RemoteNeedsUserCause.SavedPasswordRefused), (refused.Failure.Kind, refused.Failure.Cause));
    }

    /// <summary>The native shape with a server that takes the saved password: the automatic attempt connects, with no NeedsUser.</summary>
    [Fact]
    public async Task A_good_saved_password_connects_an_automatic_native_attempt()
    {
        var saved = new SavedPasswords("s3cret");
        var answers = new List<string>();
        var connector = Own(new RemoteMuxConnector(
            NativeProfile,
            (_, request) =>
            {
                _remote.OnStart = _ =>
                {
                    string answer = request.Prompts.HandleAsync(PasswordPrompt, CancellationToken.None).GetAwaiter().GetResult().Secret;
                    lock (answers) answers.Add(answer);
                    _remote.Script = answer == "s3cret" ? null : FakeRemoteScript.NativeFailure("Authentication failed");
                };
                return _remote;
            },
            new RemoteMuxInteractionHandler(new ScriptedUser(), _ => false, saved.Read),
            "i",
            null));

        MuxClient client = Own(await connector.ConnectAsync(interactive: false, Ct));

        Assert.True(client.IsConnected);
        Assert.Equal(new[] { "s3cret" }, answers);
    }
}

/// <summary>A user at the prompt dialogs: answers from a script, in order, and records what was asked.</summary>
internal sealed class ScriptedUser(params SshInteractionResponse[] answers) : ISshInteractionHandler
{
    private readonly Queue<SshInteractionResponse> _answers = new(answers);
    private readonly List<SshInteractionRequest> _asked = [];

    public IReadOnlyList<SshInteractionRequest> Asked
    {
        get { lock (_asked) return _asked.ToArray(); }
    }

    public Task<SshInteractionResponse> HandleAsync(SshInteractionRequest request, CancellationToken cancellationToken)
    {
        lock (_asked)
        {
            _asked.Add(request);
            return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : SshInteractionResponse.Cancel());
        }
    }
}
