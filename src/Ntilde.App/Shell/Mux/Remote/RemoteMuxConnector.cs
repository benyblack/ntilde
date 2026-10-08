using System.Runtime.CompilerServices;
using Ntilde.Mux;
using Ntilde.Mux.Transport;
using Ntilde.Platform.Ssh.Exec;
using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Services.Ssh;

namespace Ntilde.Shell.Mux.Remote;

/// <summary>
/// What one connect attempt's transport is built for (Phase 4 ruling: automatic reconnects are
/// non-interactive, but may sign in with the profile's saved password).
/// </summary>
/// <param name="Interactive">
/// A user is waiting: ssh may prompt. False: it must fail instead (OpenSSH's <c>BatchMode=yes</c>) - unless the attempt
/// signs in with the saved password (<paramref name="OfferSavedPassword"/>).
/// </param>
/// <param name="Prompts">The native backend's prompt handler for this attempt (<see cref="RemoteMuxInteractionHandler.Attempt"/>).</param>
/// <param name="Pinned">
/// The host's destination is pinned (codex D2, residual R4): the profile handed with this request is the connector's
/// own blend, which the store may not hold, so OpenSSH plans it without the shared generated config, which a save can
/// rewrite under it. False until the host first took a client.
/// </param>
/// <param name="Retargeted">
/// Pinned, and the profile now names another destination (codex D2, final round): OpenSSH must neither use nor create
/// a connection-sharing master for this attempt. The master's ControlPath is keyed by the profile id, so one a plain
/// tab opened to the profile's new host would carry the pinned attempt there.
/// </param>
/// <param name="OfferSavedPassword">
/// For an automatic attempt that may sign in with the profile's saved password
/// (<see cref="RemoteMuxInteractionHandler.Attempt.MaySignInWithSavedPassword"/>), null otherwise: called by a transport
/// whose prompts are answered outside the app - OpenSSH's askpass - when it is built, true when the vault holds the
/// password, which the attempt then counts as offered (<see cref="RemoteMuxInteractionHandler.Attempt.OfferSavedPassword"/>).
/// The native transport does not call it: its password prompt reaches <paramref name="Prompts"/>, which answers it. Nor
/// does an OpenSSH transport whose ssh is older than 8.4 (<see cref="RemoteMuxHostFactory.PrefixesKeyboardInteractivePrompts"/>).
/// </param>
/// <param name="WithoutSavedPassword">
/// A user's attempt that keeps the saved password away (<see cref="RemoteMuxInteractionHandler.Attempt.AvoidsSavedPassword"/>:
/// the refused value is still saved, or the destination moved): OpenSSH's askpass must not answer from the vault, so the
/// user's dialog comes at once. The native transport needs nothing: <paramref name="Prompts"/> keeps the window's handler
/// from the vault itself.
/// </param>
/// <param name="AskPassSession">
/// The attempt's askpass session token (<see cref="RemoteMuxInteractionHandler.Attempt.AskPassSession"/>) for OpenSSH's
/// helper, which records under it what it did; null for a new one per ssh (the install flow).
/// </param>
internal sealed record RemoteMuxTransportRequest(
    bool Interactive,
    ISshInteractionHandler Prompts,
    bool Pinned = false,
    bool Retargeted = false,
    Func<bool>? OfferSavedPassword = null,
    bool WithoutSavedPassword = false,
    string? AskPassSession = null);

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
/// cancelled one is aborted at once, inside the cancel, so ssh and any prompt it shows are gone before the
/// host's Dispose returns (final review F4). A connected one is ended once its client disconnects, on a pool
/// thread: ending an exec channel may block
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
    // Each connected client's own channel: a disconnect is classified by the channel under that client,
    // never by whichever attempt happens to be the latest by then.
    private readonly ConditionalWeakTable<MuxClient, OwnedChannel> _channels = new();
    private OwnedChannel? _latest; // guarded by _gate: the channel of the most recent attempt
    private bool _disposed;        // guarded by _gate
    // Each handed-out client's attempt profile: pinned if the host takes that client (Accept).
    private readonly ConditionalWeakTable<MuxClient, SshProfile> _attemptProfiles = new();
    // Guarded by _gate: where the host's first accepted client connected (codex D2) - its destination fields count -
    // with the install metadata last seen while the profile still named that destination.
    private SshProfile? _pinned;
    private bool _retargetLogged;  // guarded by _gate: the profile pointing elsewhere was logged

    /// <summary>One profile, one transport for every attempt, and no remembered prompts.</summary>
    public RemoteMuxConnector(SshProfile profile, ISshExecTransport transport, string clientInstanceId, Action<string>? log)
        : this(Fixed(profile), FixedTransport(transport), new RemoteMuxInteractionHandler(user: null), clientInstanceId, log)
    {
    }

    /// <param name="profile">
    /// The profile, read again for each attempt. Once the host took a client (<see cref="Accept"/>), every attempt keeps
    /// that client's destination - host, port, user, jump hosts - whatever the profile says since; everything else
    /// still comes from the profile (<see cref="ProfileForAttempt"/>).
    /// </param>
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
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled; the channel was aborted, in the cancel.</exception>
    /// <exception cref="ObjectDisposedException">The connector was disposed, before or during the start; a channel started meanwhile was aborted.</exception>
    public async Task<MuxClient> ConnectAsync(bool interactive, CancellationToken ct)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);

        (SshProfile profile, bool pinned, bool retargeted) = ProfileForAttempt();
        string host = DisplayNameOf(profile);
        SshMuxOptions muxOptions = profile.MuxOptions ?? new SshMuxOptions();
        string command = RemoteMuxCommand.Proxy(muxOptions);
        if (RemoteMuxCommand.RefusedRecordedPath(muxOptions.RemoteDaemonPath))
        {
            _log?.Invoke("[RemoteMux] recorded ntilde-mux path refused (unsafe characters); using the default install path");
        }

        // A native password prompt does not say which hop asks: with jump hops, a remembered password
        // could reach the jump host, so none is remembered or replayed - nor the saved one offered. Nor is the saved
        // password offered to a destination the profile no longer names (retargeted): it is the profile's, for where
        // the profile points now.
        RemoteMuxInteractionHandler.Attempt prompts = Prompts.BeginAttempt(
            interactive,
            passwordsReplayable: profile.JumpHops is not { Count: > 0 },
            savedPasswordProfile: profile,
            destinationMoved: retargeted);

        ISshExecChannel started;
        try
        {
            // Start may block (ssh launching), and the request and the transport are built here because building them
            // may too (OpenSSH plans its config file; whether the saved password is offered or kept away reads the
            // vault): all off the calling thread.
            started = await Task.Run(
                () =>
                {
                    var request = new RemoteMuxTransportRequest(
                        interactive,
                        prompts,
                        pinned,
                        retargeted,
                        prompts.MaySignInWithSavedPassword ? prompts.OfferSavedPassword : null,
                        WithoutSavedPassword: prompts.AvoidsSavedPassword,
                        AskPassSession: prompts.AskPassSession);
                    return _transportFor(profile, request).Start(command, ct);
                },
                ct).ConfigureAwait(false);
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
        // Final review F4: until a client has this channel, nothing else ends it, and an exiting app does not wait
        // for a graceful end on the pool - ssh, blocked on its askpass dialog, outlived the app. So a cancel (the
        // host's Dispose, or a user's request superseding this automatic attempt) aborts it right in the cancel:
        // the kill has happened before the host's Dispose returns. Registered on a token already cancelled, it
        // runs at once, here. The await below may also unwind inside that cancel, before the cancel reaches this
        // callback (an awaited cancellation's continuations can run inline), and its finally then unregisters it:
        // so the catch aborts too, synchronously, before that finally - one way or the other, the abort is done
        // before the cancel returns.
        CancellationTokenRegistration abortOnCancel = ct.Register(static state => ((OwnedChannel)state!).Abort(), channel);
        try
        {
            bool disposedMeanwhile;
            lock (_gate)
            {
                disposedMeanwhile = _disposed;
                if (!disposedMeanwhile) _latest = channel;
            }

            if (disposedMeanwhile)
            {
                // Dispose already ran and ended whatever channel it knew of; this one is ours, and nobody has it.
                channel.Abort();
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
                // Aborted here as well as by the cancel's callback (above): whichever runs first does it, and the
                // other waits for it. Then released off this thread.
                channel.Abort();
                _ = channel.EndAsync();
                throw;
            }
            catch (Exception ex)
            {
                throw await FailAsync(channel, prompts, ex, host).ConfigureAwait(false);
            }

            // Handed out from here on: the client's end ends the channel, gracefully. Disposing the registration
            // waits for an abort already running, so none can land on a channel a client holds.
            abortOnCancel.Dispose();
            _attemptProfiles.AddOrUpdate(client, profile);
            return HandOut(client, channel);
        }
        finally
        {
            abortOnCancel.Dispose();
        }
    }

    /// <summary>
    /// The host took <paramref name="client"/> as its own (codex D2, residual R5): the destination that attempt connected
    /// to is pinned now, unless one already is. Only then: a client the host throws away - an automatic attempt that got
    /// in just as a user's request superseded it - must not decide where the host's next attempts go. A client this
    /// connector did not hand out changes nothing.
    /// </summary>
    public void Accept(MuxClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!_attemptProfiles.TryGetValue(client, out SshProfile? profile)) return;
        lock (_gate) _pinned ??= SshConnectionService.CloneProfile(profile);
    }

    /// <summary>
    /// The profile one attempt connects with, and whether its destination is pinned (codex D2; residual R2, R3). Until
    /// the host took a client (<see cref="Accept"/>): the profile as it is now, so an edit - a typo fixed - applies to the
    /// next attempt. From then on, for the connector's whole life, only where to connect is pinned: the host, port, user
    /// and jump hosts of the client the host took, whatever the profile says since, because the shells its panes show,
    /// and the kills they send, are there. How to sign in - backend, identity, sign-in and agent settings, extra ssh
    /// arguments, keepalives - comes from the profile as it is now, so a rotated key or a new option still reaches the
    /// live connection. So does the install metadata (<see cref="SshMuxOptions.RemoteDaemonPath"/>,
    /// <see cref="SshMuxOptions.RemoteDaemonVersion"/>, <see cref="SshMuxOptions.RemoteDaemonRid"/>), but only while the
    /// profile still names the pinned destination: one recorded for another host (an absolute path under another home)
    /// would not exist on this one, so the last seen for this one is kept. Such an attempt is also retargeted: it must not
    /// share an ssh master (<see cref="RemoteMuxTransportRequest.Retargeted"/>). The first attempt to find the profile
    /// pointing elsewhere logs it.
    /// </summary>
    private (SshProfile Profile, bool Pinned, bool Retargeted) ProfileForAttempt()
    {
        SshProfile current = _profile();
        SshProfile attempt = SshConnectionService.CloneProfile(current);
        SshProfile pinned;
        bool retargeted;
        bool firstRetarget = false;
        lock (_gate)
        {
            if (_pinned is null) return (attempt, false, false);
            pinned = _pinned;
            retargeted = !SameDestination(pinned, current);
            if (retargeted)
            {
                CopyInstallMetadata(from: pinned, to: attempt);
                firstRetarget = !_retargetLogged;
                _retargetLogged = true;
            }
            else
            {
                CopyInstallMetadata(from: current, to: pinned);   // what this destination has now, for when the profile moves
            }

            attempt.Host = pinned.Host;
            attempt.Port = pinned.Port;
            attempt.User = pinned.User;
            attempt.JumpHops = [.. pinned.JumpHops.Select(hop => new SshJumpHop { Host = hop.Host, User = hop.User, Port = hop.Port })];
        }

        if (firstRetarget)
        {
            _log?.Invoke($"[RemoteMux] {current.Name}: the profile's host, port, user or jump hosts changed; this connection keeps {TargetOf(attempt)} until its tabs close");
        }

        return (attempt, true, retargeted);
    }

    private static void CopyInstallMetadata(SshProfile from, SshProfile to)
    {
        SshMuxOptions source = from.MuxOptions ?? new SshMuxOptions();
        to.MuxOptions.RemoteDaemonPath = source.RemoteDaemonPath;
        to.MuxOptions.RemoteDaemonVersion = source.RemoteDaemonVersion;
        to.MuxOptions.RemoteDaemonRid = source.RemoteDaemonRid;
    }

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> connect to the same place: host, port, user and jump hosts.</summary>
    private static bool SameDestination(SshProfile a, SshProfile b) =>
        string.Equals(a.Host, b.Host, StringComparison.Ordinal)
        && (a.Port > 0 ? a.Port : 22) == (b.Port > 0 ? b.Port : 22)
        && string.Equals(a.User, b.User, StringComparison.Ordinal)
        && a.JumpHops.Select(hop => hop.ToString()).SequenceEqual(b.JumpHops.Select(hop => hop.ToString()), StringComparer.Ordinal);

    /// <summary><c>user@host:port</c>, then <c> via </c> and the jump hops, if any.</summary>
    private static string TargetOf(SshProfile profile)
    {
        string user = string.IsNullOrWhiteSpace(profile.User) ? string.Empty : profile.User + "@";
        string target = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{user}{profile.Host}:{(profile.Port > 0 ? profile.Port : 22)}");
        return profile.JumpHops is { Count: > 0 } hops ? $"{target} via {string.Join(",", hops)}" : target;
    }

    /// <summary>The connected client, its channel recorded for <see cref="ExitAsync"/> and ended once the client is done with it.</summary>
    private MuxClient HandOut(MuxClient client, OwnedChannel channel)
    {
        _channels.AddOrUpdate(client, channel);
        // Ends the channel once the client is done with it, whoever ends the client.
        client.Disconnected += _ => channel.EndAsync();
        if (!client.IsConnected) _ = channel.EndAsync();
        return client;
    }

    /// <summary>
    /// How the most recent attempt's channel ended - its exit status (3: the daemon's process is gone;
    /// 4, <see cref="Ntilde.Mux.Cli.MuxProxyExitCodes.ConnectionClosed"/>: the daemon dropped this connection but
    /// runs on; 255: OpenSSH lost the link) - waiting at most <paramref name="wait"/> for it after a
    /// disconnect. Null when no channel was started yet, when its exit status is unknown (it was killed,
    /// or the native transport failed), or when it has not ended within the wait.
    /// </summary>
    public Task<int?> LastExitAsync(TimeSpan wait)
    {
        OwnedChannel? latest;
        lock (_gate) latest = _latest;
        return ExitOfAsync(latest, wait);
    }

    /// <summary>
    /// How the channel under <paramref name="client"/> ended, as <see cref="LastExitAsync(TimeSpan)"/> says it:
    /// what a disconnect of that client is classified by (Phase 4 spec §7.3). Unlike the latest channel, it
    /// cannot be another attempt's - one started meanwhile, or a superseded one that registered late. Null as
    /// well when <paramref name="client"/> is not one of this connector's.
    /// </summary>
    public Task<int?> ExitAsync(MuxClient client, TimeSpan wait)
    {
        ArgumentNullException.ThrowIfNull(client);
        return ExitOfAsync(_channels.TryGetValue(client, out OwnedChannel? channel) ? channel : null, wait);
    }

    private static async Task<int?> ExitOfAsync(OwnedChannel? channel, TimeSpan wait)
    {
        if (channel is null) return null;

        try
        {
            return await channel.Channel.Completion.WaitAsync(wait).ConfigureAwait(false);
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
        SavedPasswordVerdict verdict = SavedPasswordVerdictOf(prompts, failure);
        if (verdict == SavedPasswordVerdict.Refused)
        {
            // The user's rule: a wrong saved password is tried once. NeedsUser stops the reconnect loop at once, the pane
            // says the saved password was refused, and the host uses that value no more.
            prompts.SavedPasswordRefused();
            failure = new RemoteMuxFailure(
                RemoteFailureKind.NeedsUser,
                $"{host} did not accept the saved password ({failure.Reason}); an automatic reconnect does not try it again",
                RemoteNeedsUserCause.SavedPasswordRefused);
        }
        else if (verdict == SavedPasswordVerdict.NotEnough)
        {
            // Review I-1: the server took the saved password and asked for more (a second factor), which an automatic
            // attempt cannot give. Not a refusal: the user's Enter still fills it and asks only for the rest. The host's
            // automatic attempts stop offering it, so no more failed rounds.
            prompts.SavedPasswordNotEnough();
            failure = new RemoteMuxFailure(
                RemoteFailureKind.NeedsUser,
                $"signing in to {host} needs more than the saved password (a second factor), which an automatic reconnect does not ask for ({failure.Reason})");
        }
        else if (failure.Kind == RemoteFailureKind.SshFailed && !prompts.Interactive && prompts.HostKeyRejected)
        {
            // Native: the automatic attempt rejected a host key the user does not trust - never seen, or changed - and the
            // connection ended at the key exchange, before anything was sent. The next attempt meets the same key, so
            // retrying on a timer would only knock for ten minutes: NeedsUser, and Enter shows the key. A user's attempt
            // stays an SSH failure, as it was. (OpenSSH's own host-key failure is classified so already.)
            failure = new RemoteMuxFailure(
                RemoteFailureKind.NeedsUser,
                $"the host key of {host} is not one the user trusts (unknown, or changed), and an automatic reconnect accepts no new key ({failure.Reason})",
                RemoteNeedsUserCause.HostKey);
        }
        else if (prompts.AbortedPrompt is { } aborted)
        {
            // We ended it at auth, rather than send the server an empty answer: a quiet failure that says
            // why, marked NeedsUser so the reconnect loop stops instead of knocking again, and Enter is the way in.
            string needs = aborted == SshInteractionKind.KeyboardInteractive ? "keyboard-interactive input" : "a password";
            failure = new RemoteMuxFailure(RemoteFailureKind.NeedsUser, $"signing in to {host} needs {needs}, which an automatic reconnect does not ask for");
        }
        else if (failure.Kind == RemoteFailureKind.SshFailed && prompts.DeclinedPrompt is not null && !prompts.HasAuthenticated)
        {
            // Codex D3: it cancelled a key's passphrase it had nothing to answer with, and nothing else got in. Only
            // the user can give it, so the same NeedsUser: retrying on a timer would fail the same way for ten minutes.
            // Once sign-in was over, something else did get in (the agent, the saved password): the drop is the link's,
            // and the retries go on.
            failure = new RemoteMuxFailure(RemoteFailureKind.NeedsUser, $"signing in to {host} needs a key passphrase, which an automatic reconnect does not ask for");
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

    /// <summary>What a failed attempt's saved password had to do with the failure.</summary>
    private enum SavedPasswordVerdict
    {
        /// <summary>
        /// Nothing known: none was offered, or it was never asked for, or the failure came after sign-in, or OpenSSH said
        /// nothing of a refusal.
        /// </summary>
        None,

        /// <summary>The server refused it.</summary>
        Refused,

        /// <summary>The server took it and asked for more: a second factor.</summary>
        NotEnough,
    }

    /// <summary>
    /// What the saved password <paramref name="prompts"/> offered had to do with an SSH failure (review I-1), on evidence,
    /// never guesswork. Given to a native password prompt - from the vault, or the same value remembered from an earlier
    /// sign-in - it reached the server: the prompt after it says which - a code question is a second factor; anything else
    /// before sign-in was over is a refusal, a second prompt or none. rusty_ssh tries the identity file and the agent's
    /// keys first, so the password may be sshd's last try under MaxAuthTries, which it answers by cutting the connection:
    /// a bare disconnect is all the native layer sees. Once the native transport said sign-in was over, nothing counts.
    /// Handed to OpenSSH's askpass, the helper's record of this attempt's ssh says: a declined prompt naming the target (a
    /// second factor) is not a refusal; a refusal needs the helper to have filled the password - sshd refusing without
    /// asking for it (agent keys past MaxAuthTries, password auth off) does not count - and ssh to say the sign-in was
    /// refused (<see cref="RemoteMuxFailure.SignInRefused"/>). Any other failure after the fill - the link dropping - says
    /// nothing about it: no verdict, and the next attempt offers it again, once. Nothing counts once the greeting arrived.
    /// </summary>
    private static SavedPasswordVerdict SavedPasswordVerdictOf(RemoteMuxInteractionHandler.Attempt prompts, RemoteMuxFailure failure)
    {
        // Past sign-in (the native transport said so, or the proxy's greeting arrived), the password got in: a later failure
        // is the link's, not a refusal (re-review item 1), as Attempt.Refused keeps a remembered one.
        if (prompts.HasSucceeded || prompts.HasAuthenticated) return SavedPasswordVerdict.None;
        bool sshFailed = failure.Kind is RemoteFailureKind.SshFailed or RemoteFailureKind.NeedsUser || prompts.AbortedPrompt is not null;
        if (!sshFailed) return SavedPasswordVerdict.None;
        if (prompts.StoredPasswordAnswered)
        {
            // Native: the saved password, from the vault or remembered from an earlier sign-in (the live smoke test: the
            // window's handler had filled it on Enter, and the host remembered it), reached the server. A remembered password
            // that is not the saved one keeps the remembered rule: forgotten, and the next attempt may offer the saved one.
            if (!prompts.StoredPasswordIsTheSavedOne()) return SavedPasswordVerdict.None;
            return prompts.SecondFactorAfterSavedPassword ? SavedPasswordVerdict.NotEnough : SavedPasswordVerdict.Refused;
        }

        if (!prompts.SavedPasswordOffered) return SavedPasswordVerdict.None;
        Ntilde.SshAskPassRecord record = prompts.ReadAskPassRecord();
        if (record.Declined) return SavedPasswordVerdict.NotEnough;
        return record.Answered && failure.SignInRefused ? SavedPasswordVerdict.Refused : SavedPasswordVerdict.None;
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
        private readonly object _abortGate = new();
        private Task? _ending; // guarded by _gate; set once
        private bool _aborted; // guarded by _abortGate

        public ISshExecChannel Channel { get; } = channel;

        /// <summary>
        /// Stops the command at once, on the caller's thread (<see cref="ISshExecChannel.Abort"/>): for a channel
        /// nobody was handed. A graceful end already running is cut short. Never throws.
        /// </summary>
        public void Abort()
        {
            // Serialised: a second caller (the cancel's callback and the cancelled attempt's catch can race) returns
            // only once the first has stopped the command.
            lock (_abortGate)
            {
                if (_aborted) return;
                _aborted = true;
                try
                {
                    Channel.Abort();
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[RemoteMux] {host}: stopping the SSH channel failed: {ex.Message}");
                }
            }
        }

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
