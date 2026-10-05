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

    internal static SshInteractionRequest PasswordPrompt { get; } = new() { Kind = SshInteractionKind.Password, Prompt = "Password:" };

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
                _ => throw new InvalidOperationException("a native profile never plans an ssh command line"),
                () => interop,
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
