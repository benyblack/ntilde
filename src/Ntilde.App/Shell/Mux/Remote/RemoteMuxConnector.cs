using Ntilde.Mux;
using Ntilde.Mux.Transport;
using Ntilde.Platform.Ssh.Exec;
using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Platform.Ssh.Models;

namespace Ntilde.Shell.Mux.Remote;

/// <summary>
/// What one connect attempt's transport is built for (Phase 4 ruling: automatic reconnects are
/// non-interactive).
/// </summary>
/// <param name="Interactive">A user is waiting: ssh may prompt. False: it must fail instead (OpenSSH's <c>BatchMode=yes</c>).</param>
/// <param name="Prompts">The native backend's prompt handler for this attempt (<see cref="RemoteMuxInteractionHandler.Attempt"/>).</param>
internal sealed record RemoteMuxTransportRequest(bool Interactive, ISshInteractionHandler Prompts);

/// <summary>
/// Connects to the <c>ntilde-mux</c> daemon on one SSH host (Phase 4 spec §7.1): runs
/// <see cref="RemoteMuxCommand.Proxy"/> over an exec channel, finds the proxy's greeting in its stdout
/// (§8.1), and says hello through it with this host's client instance id. Every failure becomes a
/// <see cref="RemoteMuxUnavailableException"/> carrying a <see cref="RemoteMuxFailure"/> the UI can
/// explain. One per remote <see cref="MuxConnectionHost"/>: its connect function, and its
/// <see cref="MuxConnectionHost.Connector"/>.
/// </summary>
/// <remarks>
/// <para>
/// The connector owns each channel it starts. A failed attempt ends its channel before it throws. A
/// connected one is ended once its client disconnects, on a pool thread: ending an exec channel may block
/// for seconds, and it is what frees a read the client's reader has pending on the channel's stdout. On
/// Windows, closing a pipe's read end does not wake such a read - ssh's exit does - and on a dead link
/// nothing else would ever end it.
/// </para>
/// <para>
/// The greeting wait is the remote host's connect timeout (<see cref="MuxHostPolicy.RemoteConnectTimeout"/>),
/// not a short protocol timeout: the native transport returns from Start before connect and auth, so the
/// wait includes the user answering prompts.
/// </para>
/// </remarks>
internal sealed class RemoteMuxConnector : IDisposable
{
    /// <summary>How long a failed attempt waits for its ended channel's exit status, to classify the failure.</summary>
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(1);

    private readonly Func<SshProfile> _profile;
    private readonly Func<SshProfile, RemoteMuxTransportRequest, ISshExecTransport> _transportFor;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private OwnedChannel? _latest; // guarded by _gate: the channel of the most recent attempt
    private bool _disposed;        // guarded by _gate

    /// <summary>One profile, one transport for every attempt, and no remembered prompts.</summary>
    public RemoteMuxConnector(SshProfile profile, ISshExecTransport transport, string clientInstanceId, Action<string>? log)
        : this(Fixed(profile), FixedTransport(transport), new RemoteMuxInteractionHandler(user: null), clientInstanceId, log)
    {
    }

    /// <param name="profile">The profile, read again for each attempt: the install flow may have recorded a path since.</param>
    /// <param name="transportFor">Builds the transport for one attempt (by the profile's backend, and whether anyone is waiting).</param>
    /// <param name="prompts">This host's prompts: what its attempts remember for its lifetime (<see cref="Dispose"/> forgets).</param>
    /// <param name="clientInstanceId">Sent in every hello, so a reconnect evicts this host's dead connection on the daemon (spec §2.5).</param>
    /// <param name="log">The mux client's and this connector's log.</param>
    public RemoteMuxConnector(
        Func<SshProfile> profile,
        Func<SshProfile, RemoteMuxTransportRequest, ISshExecTransport> transportFor,
        RemoteMuxInteractionHandler prompts,
        string clientInstanceId,
        Action<string>? log)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(transportFor);
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientInstanceId);
        _profile = profile;
        _transportFor = transportFor;
        Prompts = prompts;
        ClientInstanceId = clientInstanceId;
        _log = log;
    }

    /// <summary>Sent in every hello: the same for every attempt of this connector.</summary>
    public string ClientInstanceId { get; }

    /// <summary>How long an attempt waits for the proxy's greeting, from the moment its channel starts.</summary>
    public TimeSpan PreambleTimeout { get; init; } = MuxHostPolicy.RemoteConnectTimeout;

    internal RemoteMuxInteractionHandler Prompts { get; }

    /// <summary><c>user@host</c>, or the bare host when the profile names no user.</summary>
    internal static string DisplayNameOf(SshProfile profile) =>
        string.IsNullOrWhiteSpace(profile.User) ? profile.Host : $"{profile.User}@{profile.Host}";

    /// <summary>An interactive attempt: the <see cref="MuxConnectionHost"/> connect function for a user's request.</summary>
    public Task<MuxClient> ConnectAsync(CancellationToken ct) => ConnectAsync(interactive: true, ct);

    /// <summary>
    /// Starts the proxy and connects a <see cref="MuxClient"/> through it.
    /// </summary>
    /// <param name="interactive">False for an attempt nobody is waiting on: nothing may prompt.</param>
    /// <exception cref="RemoteMuxUnavailableException">The attempt failed; its channel has been ended.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled; the channel is being ended.</exception>
    /// <exception cref="ObjectDisposedException">The connector was disposed, before or during the start; a channel started meanwhile is being ended.</exception>
    public async Task<MuxClient> ConnectAsync(bool interactive, CancellationToken ct)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);

        SshProfile profile = _profile();
        string host = DisplayNameOf(profile);
        string command = RemoteMuxCommand.Proxy(profile.MuxOptions ?? new SshMuxOptions());
        // A native password prompt does not say which hop asks: with jump hops, a remembered password
        // could reach the jump host, so none is remembered or replayed.
        RemoteMuxInteractionHandler.Attempt prompts = Prompts.BeginAttempt(interactive, passwordsReplayable: profile.JumpHops is not { Count: > 0 });

        ISshExecChannel started;
        try
        {
            // Start may block (ssh launching), and the transport is built here because building it may
            // too (OpenSSH plans its config file): both off the calling thread.
            started = await Task.Run(() => _transportFor(profile, new RemoteMuxTransportRequest(interactive, prompts)).Start(command, ct), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RemoteMuxFailure failure = RemoteMuxFailureClassifier.StartFailed(ex);
            _log?.Invoke($"[RemoteMux] {host}: {failure.Kind}: {failure.Reason}");
            throw new RemoteMuxUnavailableException(failure, ex);
        }

        var channel = new OwnedChannel(started, host, _log);
        bool disposedMeanwhile;
        lock (_gate)
        {
            disposedMeanwhile = _disposed;
            if (!disposedMeanwhile) _latest = channel;
        }

        if (disposedMeanwhile)
        {
            // Dispose already ran and ended whatever channel it knew of; this one is ours to end.
            _ = channel.EndAsync();
            throw new ObjectDisposedException(nameof(RemoteMuxConnector));
        }

        MuxClient client;
        try
        {
            StdioMuxConnection proxy = await StdioMuxTransport.ConnectAsync(started.Stdout, started.Stdin, PreambleTimeout, ct).ConfigureAwait(false);
            // The remote command runs, so SSH auth succeeded: whatever answered its prompts was right.
            prompts.Succeeded();
            client = await MuxClient.ConnectAsync(proxy.Stream, new MuxClientOptions { ClientInstanceId = ClientInstanceId, Log = _log }, ct)
                .ConfigureAwait(false);
            _log?.Invoke($"[RemoteMux] {host}: connected (daemon pid {proxy.DaemonPid}, protocol {client.ProtocolVersion}, {(interactive ? "user request" : "automatic")})");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Not awaited: the host is going away (its dispose cancelled us) and must not wait on ssh.
            _ = channel.EndAsync();
            throw;
        }
        catch (Exception ex)
        {
            throw await FailAsync(channel, prompts, ex, host).ConfigureAwait(false);
        }

        // Ends the channel once the client is done with it, whoever ends the client.
        client.Disconnected += _ => channel.EndAsync();
        if (!client.IsConnected) _ = channel.EndAsync();
        return client;
    }

    /// <summary>
    /// How the most recent attempt's channel ended - its exit status (3: the daemon closed the
    /// connection; 255: OpenSSH lost the link) - waiting at most <paramref name="wait"/> for it after a
    /// disconnect. Null when no channel was started yet, when its exit status is unknown (it was killed,
    /// or the native transport failed), or when it has not ended within the wait.
    /// </summary>
    public async Task<int?> LastExitAsync(TimeSpan wait)
    {
        OwnedChannel? latest;
        lock (_gate) latest = _latest;
        if (latest is null) return null;

        try
        {
            return await latest.Channel.Completion.WaitAsync(wait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// Forgets the remembered secrets, and ends the latest channel if its client has not already (the
    /// host closes its client first, which ends it). Never blocks: the channel ends on a pool thread.
    /// </summary>
    public void Dispose()
    {
        OwnedChannel? latest;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            latest = _latest;
        }

        Prompts.Forget();
        _ = latest?.EndAsync();
    }

    private async Task<RemoteMuxUnavailableException> FailAsync(OwnedChannel channel, RemoteMuxInteractionHandler.Attempt prompts, Exception error, string host)
    {
        await channel.EndAsync().ConfigureAwait(false);

        int? exitCode;
        try
        {
            exitCode = await channel.Channel.Completion.WaitAsync(ExitWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            exitCode = null;
        }

        string captured = (error as MuxProxyHandshakeException)?.CapturedText ?? string.Empty;
        RemoteMuxFailure failure = RemoteMuxFailureClassifier.Classify(exitCode, captured, channel.Channel.StderrTail, error, host, automatic: !prompts.Interactive);
        if (prompts.AbortedPrompt is { } aborted)
        {
            // We ended it at auth, rather than send the server an empty answer: a quiet failure that says
            // why, marked NeedsUser so the reconnect loop stops instead of knocking again, and Enter is the way in.
            string needs = aborted == SshInteractionKind.KeyboardInteractive ? "keyboard-interactive input" : "a password";
            failure = new RemoteMuxFailure(RemoteFailureKind.NeedsUser, $"signing in to {host} needs {needs}, which an automatic reconnect does not ask for");
        }

        if (failure.Kind is RemoteFailureKind.SshFailed or RemoteFailureKind.NeedsUser)
        {
            // SSH itself failed - auth, or the connection before the command ran - so a remembered
            // secret this attempt offered may be what the server refused: forget it rather than replay
            // it on every reconnect. A failure after auth (NotInstalled, Unsupported, ProxyFailed) keeps it.
            prompts.Refused();
        }

        _log?.Invoke($"[RemoteMux] {host}: {failure.Kind} (exit {(exitCode is { } code ? code.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown")}): {failure.Reason}");
        return new RemoteMuxUnavailableException(failure, error);
    }

    private static Func<SshProfile> Fixed(SshProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return () => profile;
    }

    private static Func<SshProfile, RemoteMuxTransportRequest, ISshExecTransport> FixedTransport(ISshExecTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        return (_, _) => transport;
    }

    /// <summary>A started channel and its one ending: disposed once, on a pool thread, never on the caller's.</summary>
    private sealed class OwnedChannel(ISshExecChannel channel, string host, Action<string>? log)
    {
        private readonly object _gate = new();
        private Task? _ending; // guarded by _gate; set once

        public ISshExecChannel Channel { get; } = channel;

        public Task EndAsync()
        {
            lock (_gate)
            {
                return _ending ??= Task.Run(() =>
                {
                    try
                    {
                        Channel.Dispose();
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"[RemoteMux] {host}: ending the SSH channel failed: {ex.Message}");
                    }
                });
            }
        }
    }
}
