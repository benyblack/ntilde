using Ntilde.Mux;
using Ntilde.Mux.Cli;
using Ntilde.Platform.Ssh.Exec;
using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.Native;
using Ntilde.Services.Ssh;

namespace Ntilde.Shell.Mux.Remote;

/// <summary>
/// Builds the <see cref="MuxConnectionHost"/> for an <c>ssh:</c> endpoint (Phase 4 spec §5): what
/// <see cref="MuxConnectionHosts"/> calls the first time a pane uses that endpoint. It only constructs.
/// The registry calls it outside its lock and may throw a duplicate away unused, so it never connects;
/// the host connects on its first <see cref="MuxConnectionHost.GetClient"/>.
/// </summary>
internal static class RemoteMuxHostFactory
{
    /// <summary>How long a disconnect waits for the channel's exit status; the host caps its classification at this too.</summary>
    private static readonly TimeSpan DisconnectExitWait = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The host for <paramref name="id"/>, or null to decline: the local endpoint (not this factory's), or a
    /// profile that is gone.
    /// </summary>
    /// <remarks>
    /// The host has <see cref="MuxHostPolicy.Remote"/> named <c>user@host</c>, and one
    /// <see cref="RemoteMuxConnector"/> for its whole life - until the window releases it, no pane needing its
    /// endpoint any more (<see cref="MuxConnectionHosts.Release"/>), or closes: a fresh client instance id (a Guid
    /// in N format) sent in every hello, and the secrets its user-started attempts remember, forgotten with it. Each attempt
    /// reads the profile again, and builds its transport through <paramref name="transportFor"/>, told
    /// whether a user is waiting (<see cref="MuxConnectAttempt"/>). The host tells a stopped daemon from a
    /// lost link by the exit status of the proxy under the lost client (<see cref="ClassifyDisconnectAsync"/>).
    /// <para>
    /// A profile whose <see cref="SshMuxOptions.PersistRemoteSessions"/> is off still gets its host (codex C2). The
    /// flag decides where that profile's tabs go - <see cref="MuxTerminalSessionFactory.RoutesRemote"/> reads it
    /// before any host is asked for, so such a tab opens plain SSH - not whether a shell the user closed ends: a
    /// pane restored with a pending <c>ssh:</c> id keeps it after the flag goes off (spec §15), and its close kills
    /// that shell through this host, in one automatic attempt (<see cref="MuxConnectionHost.KillWhenConnected"/>),
    /// after which the window releases the host. Declining lost the kill, and nothing adopts a remote orphan.
    /// </para>
    /// </remarks>
    /// <param name="resolveProfile">The SSH profile store's lookup.</param>
    /// <param name="transportFor">Builds one attempt's transport (<see cref="CreateTransport"/> in the app).</param>
    /// <param name="log">The host's, the connector's and the mux client's log.</param>
    /// <param name="userPrompts">The window's prompt handler, for the native backend's user-started attempts.</param>
    /// <param name="scheduler">The clock of the host's liveness ping and reconnect loop; <see cref="SystemMuxTimerScheduler.Instance"/> when null.</param>
    /// <param name="isTrustedHostKey">
    /// Whether a host-key request names a key the user already trusts: the only host keys an automatic
    /// (non-interactive) attempt accepts (<see cref="RemoteMuxInteractionHandler"/>). Null, as the app passes
    /// it, means the app's native known-hosts store, the one the window's prompts record accepted keys in. A
    /// caller with a store of its own passes it here (the Docker E2E: the app's store is bound once per process).
    /// </param>
    public static MuxConnectionHost? Create(
        MuxEndpointId id,
        Func<Guid, SshProfile?> resolveProfile,
        Func<SshProfile, RemoteMuxTransportRequest, ISshExecTransport> transportFor,
        Action<string>? log,
        ISshInteractionHandler? userPrompts = null,
        IMuxTimerScheduler? scheduler = null,
        Func<SshInteractionRequest, bool>? isTrustedHostKey = null)
    {
        ArgumentNullException.ThrowIfNull(resolveProfile);
        ArgumentNullException.ThrowIfNull(transportFor);

        if (id.SshProfileId is not Guid profileId) return null;

        SshProfile? profile = resolveProfile(profileId);
        if (profile is null)
        {
            log?.Invoke($"[RemoteMux] no SSH profile {profileId:N} for {id}; its sessions cannot persist");
            return null;
        }

        MuxHostPolicy policy = MuxHostPolicy.Remote(RemoteMuxConnector.DisplayNameOf(profile));
        var connector = new RemoteMuxConnector(
            // A profile deleted since keeps connecting as it was: the panes that use it decide when to stop.
            () => resolveProfile(profileId) ?? profile,
            transportFor,
            new RemoteMuxInteractionHandler(userPrompts, isTrustedHostKey),
            Guid.NewGuid().ToString("N"),
            log)
        {
            PreambleTimeout = policy.ConnectTimeout,
        };

        return new MuxConnectionHost((attempt, ct) => connector.ConnectAsync(attempt.Interactive, ct), id.ToString(), log, policy)
        {
            Connector = connector,
            ClassifyDisconnect = client => ClassifyDisconnectAsync(connector, client),
            Scheduler = scheduler ?? SystemMuxTimerScheduler.Instance,
        };
    }

    /// <summary>
    /// Why <paramref name="client"/>'s connection ended (Phase 4 spec §7.3), from how the proxy's channel
    /// under it did (<see cref="RemoteMuxConnector.ExitAsync"/>): exit 3
    /// (<see cref="MuxProxyExitCodes.DaemonClosed"/>) means the daemon closed it and its sessions are gone.
    /// Anything else - ssh's 255, a channel that was killed or failed natively (no status), no status within
    /// a second - is a lost link.
    /// </summary>
    internal static async Task<MuxDisconnectKind> ClassifyDisconnectAsync(RemoteMuxConnector connector, MuxClient client)
    {
        ArgumentNullException.ThrowIfNull(connector);
        int? exit = await connector.ExitAsync(client, DisconnectExitWait).ConfigureAwait(false);
        return exit == MuxProxyExitCodes.DaemonClosed ? MuxDisconnectKind.DaemonStopped : MuxDisconnectKind.LinkLost;
    }

    /// <summary>
    /// The app's transport for one attempt, by the profile's backend (spec §8.2, §8.3):
    /// <list type="bullet">
    /// <item>OpenSSH: ssh with the profile's launch plan. A user-started attempt prompts through the
    /// askpass helper; an automatic one runs in batch mode, without askpass, so it fails rather than
    /// prompt - a password-only OpenSSH profile then reconnects on Enter, or through keys, the agent or
    /// an existing ControlMaster.</item>
    /// <item>Native: the native exec transport, its prompts answered by
    /// <see cref="RemoteMuxTransportRequest.Prompts"/>.</item>
    /// </list>
    /// </summary>
    /// <param name="openSshLaunch">The profile's OpenSSH launch plan (<see cref="SshConnectionService.BuildLaunchDetails(Guid, Ntilde.Platform.Ssh.Launch.SshDiagnosticsLevel)"/>).</param>
    /// <param name="nativeInterop">The native layer (<see cref="NativeSshInterop"/>).</param>
    /// <param name="askPassHelperPath">The askpass helper (<see cref="SshAskPassCommand.LocateHelper()"/>), or null for none.</param>
    public static ISshExecTransport CreateTransport(
        SshProfile profile,
        RemoteMuxTransportRequest request,
        Func<SshProfile, SshLaunchDetails> openSshLaunch,
        Func<INativeSshInterop> nativeInterop,
        string? askPassHelperPath,
        Action<string>? log)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(openSshLaunch);
        ArgumentNullException.ThrowIfNull(nativeInterop);

        if (profile.BackendKind == SshBackendKind.Native)
        {
            return new NativeSshExecTransport(profile, nativeInterop(), request.Prompts, NativeSshConnectionOptionsFactory.Create, log);
        }

        SshLaunchDetails launch = openSshLaunch(profile);
        return new OpenSshExecTransport(
            profile,
            launch.SshPath,
            launch.PlanArguments,
            request.Interactive ? askPassHelperPath : null,
            diagnosticsArguments: null,
            log,
            batchMode: !request.Interactive);
    }
}
