using Ntilde.Mux;
using Ntilde.Mux.Cli;
using Ntilde.Platform.Ssh.Exec;
using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.Native;
using Ntilde.Platform.Ssh.Sessions;
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
    /// <summary>
    /// How long a channel this factory builds lets the remote command exit on stdin's EOF before it stops it
    /// (<see cref="OpenSshExecTransport.ExitGrace"/>, <see cref="NativeSshExecTransport.ExitGrace"/>): the first of the
    /// waits that tell a stopped daemon from a lost link, which must keep the order below.
    /// </summary>
    /// <remarks>
    /// When the daemon side of a connection ends, the proxy on the remote (<c>MuxProxyCommand</c>) ends its stdout at
    /// once, then waits up to <c>MuxProxyCommand.DaemonExitWait</c> (1.5 s) for the daemon's process to go before it
    /// exits 3 (gone: <see cref="MuxDisconnectKind.DaemonStopped"/>) or 4 (runs on: a lost link). Then ssh brings the code
    /// home - sshd sends it, ssh exits - which on a loaded remote takes a while too. The end of stdout reaches the client
    /// first, and from that moment three waits run here at once, each of which turns a 3 it does not see into a lost link:
    /// <code>
    /// DaemonExitWait + SshTeardownAllowance &lt;= ChannelExitGrace &lt; DisconnectExitWait &lt; ClassifyTimeout
    ///    1.5 s       +         2 s          &lt;=      3.5 s       &lt;        4 s         &lt;      4.5 s
    /// </code>
    /// The channel's end (<see cref="RemoteMuxConnector"/> disposes it once its client is done) stops ssh after
    /// <see cref="ChannelExitGrace"/>, and a stopped ssh has no status; <see cref="ClassifyDisconnectAsync"/> waits
    /// <see cref="DisconnectExitWait"/> for the status; <see cref="MuxConnectionHost"/> caps the classification at
    /// <see cref="ClassifyTimeout"/>. Each outlasts the one before it, and the grace leaves ssh
    /// <see cref="SshTeardownAllowance"/> after the proxy's own wait. Read wrongly, a stopped daemon's panes reconnect to
    /// a new one and each says its session was lost. The proxy is the remote binary and the transports are the platform's,
    /// so neither reads these: <c>MuxProxyCommand.DaemonExitWait</c> and the transports' default grace point here, the
    /// transports are given this grace (<see cref="CreateTransport"/>), and <c>RemoteMuxHostFactoryTests</c> pins the order.
    /// </remarks>
    internal static readonly TimeSpan ChannelExitGrace = TimeSpan.FromSeconds(3.5);

    /// <summary>
    /// What <see cref="ChannelExitGrace"/> leaves ssh, after the proxy's own wait for the daemon's process, to bring the
    /// exit code home on a loaded remote.
    /// </summary>
    internal static readonly TimeSpan SshTeardownAllowance = TimeSpan.FromSeconds(2);

    /// <summary>How long a disconnect waits for the channel's exit status: above <see cref="ChannelExitGrace"/> (see the order there).</summary>
    internal static readonly TimeSpan DisconnectExitWait = TimeSpan.FromSeconds(4);

    /// <summary>
    /// The host's cap on a disconnect's classification (<see cref="MuxConnectionHost.ClassifyTimeout"/>): above
    /// <see cref="DisconnectExitWait"/> (see the order at <see cref="ChannelExitGrace"/>).
    /// </summary>
    internal static readonly TimeSpan ClassifyTimeout = TimeSpan.FromSeconds(4.5);

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
    /// whether a user is waiting (<see cref="MuxConnectAttempt"/>). From the first client the host takes
    /// (<see cref="MuxConnectionHost.ClientAccepted"/>), its attempts keep that client's destination - host, port, user,
    /// jump hosts - for the host's life (codex D2): the shells its panes show are there, so an edit to those applies once
    /// the window releases the host and the next one reads it. How to sign in still follows the profile
    /// (<see cref="RemoteMuxConnector"/>). A profile deleted meanwhile keeps the last one the store gave. The host tells a
    /// stopped daemon from a lost link by the exit status of the proxy under the lost client (<see cref="ClassifyDisconnectAsync"/>).
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
    /// <param name="transportFor">
    /// Builds one attempt's transport (<see cref="CreateTransport"/> in the app), for the profile it is handed - which,
    /// once the host connected, is a copy the store may no longer hold as it is, so it must not be looked up again by id.
    /// </param>
    /// <param name="log">The host's, the connector's and the mux client's log.</param>
    /// <param name="userPrompts">The window's prompt handler, for the native backend's user-started attempts.</param>
    /// <param name="scheduler">The clock of the host's liveness ping and reconnect loop; <see cref="SystemMuxTimerScheduler.Instance"/> when null.</param>
    /// <param name="isTrustedHostKey">
    /// Whether a host-key request names a key the user already trusts: the only host keys an automatic
    /// (non-interactive) attempt accepts (<see cref="RemoteMuxInteractionHandler"/>). Null, as the app passes
    /// it, means the app's native known-hosts store, the one the window's prompts record accepted keys in. A
    /// caller with a store of its own passes it here (the Docker E2E: the app's store is bound once per process).
    /// </param>
    /// <param name="savedPassword">
    /// Reads a profile's password saved in the vault (<see cref="ReadSavedPassword"/> in the app), for the host's automatic
    /// attempts: they sign in with it, once, with no UI (<see cref="RemoteMuxInteractionHandler"/>). Null: they never do.
    /// </param>
    /// <param name="askPassRecords">
    /// Reads what OpenSSH's askpass helper did for an attempt's ssh (<see cref="SshAskPassSessionMarkers.Read"/> over
    /// <see cref="SshAskPassSessionMarkers.DefaultDirectory"/> in the app), so a saved password counts as refused only when
    /// the helper filled it, and a second factor does not count.
    /// </param>
    /// <param name="rpcTimeout">The host's request wait in place of <see cref="MuxHostPolicy.Remote"/>'s; null (the app) keeps it. Tests shorten it.</param>
    /// <param name="passwordScopes">
    /// Where the host's password scope lives (Phase 5 spec R8: what its user typed, for its persisted tabs' SFTP
    /// connections; <see cref="MuxConnectionHost.PasswordScopeId"/>): the app's <see cref="ActiveSshSessionRegistry.Instance"/>
    /// when null, which the panes register in.
    /// </param>
    public static MuxConnectionHost? Create(
        MuxEndpointId id,
        Func<Guid, SshProfile?> resolveProfile,
        Func<SshProfile, RemoteMuxTransportRequest, ISshExecTransport> transportFor,
        Action<string>? log,
        ISshInteractionHandler? userPrompts = null,
        IMuxTimerScheduler? scheduler = null,
        Func<SshInteractionRequest, bool>? isTrustedHostKey = null,
        Func<SshProfile, string?>? savedPassword = null,
        SshAskPassSessionMarkers? askPassRecords = null,
        TimeSpan? rpcTimeout = null,
        ActiveSshSessionRegistry? passwordScopes = null)
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
        if (rpcTimeout is { } wait) policy = policy with { RpcTimeout = wait };

        // A profile deleted since keeps connecting as the store last had it (the panes that use it decide when to
        // stop); once an attempt connected, the connector keeps that one's target anyway (codex D2).
        SshProfile lastKnown = profile;
        SshProfile CurrentProfile()
        {
            if (resolveProfile(profileId) is { } now)
            {
                Volatile.Write(ref lastKnown, now);
                return now;
            }

            return Volatile.Read(ref lastKnown);
        }

        var connector = new RemoteMuxConnector(
            CurrentProfile,
            transportFor,
            new RemoteMuxInteractionHandler(userPrompts, isTrustedHostKey, savedPassword, askPassRecords, log, passwordScopes ?? ActiveSshSessionRegistry.Instance),
            Guid.NewGuid().ToString("N"),
            log)
        {
            PreambleTimeout = policy.ConnectTimeout,
        };

        return new MuxConnectionHost((attempt, ct) => connector.ConnectAsync(attempt.Interactive, ct), id.ToString(), log, policy)
        {
            Connector = connector,
            ClientAccepted = connector.Accept,
            ClassifyDisconnect = client => ClassifyDisconnectAsync(connector, client),
            Scheduler = scheduler ?? SystemMuxTimerScheduler.Instance,
        };
    }

    /// <summary>
    /// <paramref name="profile"/>'s password saved in <paramref name="vault"/>, or null: looked up as the window's prompts
    /// save it and the askpass helper reads it (<see cref="VaultService.GetSshPasswordForProfile"/>, by the profile's id,
    /// then its older name-based keys), so an automatic attempt and the helper agree on whether there is one.
    /// </summary>
    internal static string? ReadSavedPassword(VaultService vault, SshProfile profile)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(profile);
        string? saved = vault.GetSshPasswordForProfile(new TerminalProfile
        {
            Type = ConnectionType.SSH,
            Id = profile.Id,
            Name = profile.Name ?? string.Empty,
            SshUser = profile.User ?? string.Empty,
            SshHost = profile.Host ?? string.Empty,
            SshPort = profile.Port,
        });
        return string.IsNullOrEmpty(saved) ? null : saved;
    }

    /// <summary>
    /// Why <paramref name="client"/>'s connection ended (Phase 4 spec §7.3), from how the proxy's channel
    /// under it did (<see cref="RemoteMuxConnector.ExitAsync"/>): exit 3
    /// (<see cref="MuxProxyExitCodes.DaemonClosed"/>) means the daemon's process is gone, its sessions with it.
    /// Anything else is a lost link: 4 (<see cref="MuxProxyExitCodes.ConnectionClosed"/>), a daemon that dropped
    /// this connection but runs on with its sessions (codex D1); ssh's 255; a channel that was killed or failed
    /// natively (no status); no status within <see cref="DisconnectExitWait"/>.
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
    /// askpass helper. An automatic one whose profile has a saved password it may use
    /// (<see cref="RemoteMuxTransportRequest.OfferSavedPassword"/>: no jump hops, the profile's own destination, not
    /// refused before on this host), through an ssh of 8.4 or later (<see cref="PrefixesKeyboardInteractivePrompts"/>),
    /// runs the helper in its vault-only mode, with <c>NumberOfPasswordPrompts=1</c>: the
    /// password is answered from the vault, once, and every other prompt is refused with no UI. Any other automatic one
    /// runs in batch mode, without askpass, so it fails rather than prompt - such a password-only OpenSSH profile then
    /// reconnects on Enter, or through keys, the agent or an existing ControlMaster. A user's attempt after a password was
    /// refused on the host (<see cref="RemoteMuxTransportRequest.WithoutSavedPassword"/>) runs the helper without the
    /// vault, so the user is asked at once, as does one through a jump host that could ask as the target
    /// (<see cref="SshAskPassVaultPolicy.MayOfferVault"/>); any other user's attempt has it fill the saved password once per ssh.</item>
    /// <item>Native: the native exec transport, its prompts answered by
    /// <see cref="RemoteMuxTransportRequest.Prompts"/> - unless the global native SSH switch is off, which
    /// refuses the attempt before anything is built (<see cref="ThrowIfNativeSshDisabled"/>).</item>
    /// </list>
    /// Either way the channels it starts end with <see cref="ChannelExitGrace"/>, so a stopped daemon's exit code still
    /// gets through.
    /// </summary>
    /// <param name="openSshLaunch">
    /// The OpenSSH launch plan of this very profile (<see cref="SshConnectionService.BuildLaunchDetailsFor"/>), which may
    /// be a host's blend of a profile edited or deleted since: a plan looked up by the profile's id would go where the
    /// store says now. Its second argument asks for the self-contained plan, which reads no shared config file: true
    /// when <see cref="RemoteMuxTransportRequest.Pinned"/>.
    /// </param>
    /// <param name="nativeInterop">The native layer (<see cref="NativeSshInterop"/>).</param>
    /// <param name="nativeSshEnabled">
    /// The global native SSH switch (Settings &gt; SSH, <c>ExperimentalNativeSshEnabled</c>) as the app's settings say now:
    /// asked on every call, so turning it off reaches a host's next attempt - its reconnect loop's, its kill delivery's,
    /// a user's Enter - not only hosts built later (codex4 F).
    /// </param>
    /// <param name="askPassHelperPath">The askpass helper (<see cref="SshAskPassCommand.LocateHelper()"/>), or null for none.</param>
    /// <param name="openSshVersions">
    /// The OpenSSH clients' versions (<see cref="OpenSshClientVersionCache.Shared"/> in the app), read for an automatic
    /// attempt that would be offered the saved password; the probe may block for seconds, so this is called off the UI
    /// thread.
    /// </param>
    /// <exception cref="RemoteMuxUnavailableException">A Native profile while native SSH is off (<see cref="ThrowIfNativeSshDisabled"/>).</exception>
    public static ISshExecTransport CreateTransport(
        SshProfile profile,
        RemoteMuxTransportRequest request,
        Func<SshProfile, bool, SshLaunchDetails> openSshLaunch,
        Func<INativeSshInterop> nativeInterop,
        Func<bool> nativeSshEnabled,
        string? askPassHelperPath,
        Action<string>? log,
        OpenSshClientVersionCache openSshVersions)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(openSshLaunch);
        ArgumentNullException.ThrowIfNull(nativeInterop);
        ArgumentNullException.ThrowIfNull(openSshVersions);

        ThrowIfNativeSshDisabled(profile, nativeSshEnabled);
        if (profile.BackendKind == SshBackendKind.Native)
        {
            return new NativeSshExecTransport(profile, nativeInterop(), request.Prompts, NativeSshConnectionOptionsFactory.Create, log, ChannelExitGrace);
        }

        SshLaunchDetails launch = openSshLaunch(profile, request.Pinned);
        IReadOnlyList<string> plan = PlanArgumentsFor(launch, request);
        // Offered only once a helper exists to answer it and the plan is built; only when the helper can recognise the
        // target's prompt - it names the profile's user@host (review M2), which the profile's own arguments must not change
        // (-l, -o User, -F, -o HostName, -o HostKeyAlias: re-review item 6); and not when the plan's own arguments go
        // through a jump host, which on OpenSSH before 8.4 could ask as the target (review M3). Nor with an ssh before 8.4 at
        // all, whose keyboard-interactive prompts do not name the target (PrefixesKeyboardInteractivePrompts): its version is
        // read only for an attempt that would otherwise be offered, and before the vault is.
        bool savedPasswordOnly = !request.Interactive
            && askPassHelperPath is not null
            && !string.IsNullOrWhiteSpace(profile.User)
            && !string.IsNullOrWhiteSpace(profile.Host)
            && !OpenSshExecCommandLine.ExtraArgumentsChangeWhoOrWhere(profile.ExtraSshArgs)
            && !OpenSshExecCommandLine.NamesAProxy(plan)
            && request.OfferSavedPassword is { } offerSavedPassword
            && PrefixesKeyboardInteractivePrompts(launch.SshPath, openSshVersions, log)
            && offerSavedPassword();
        // A user's attempt through a jump host: the helper's target-prompt match can be forged by a hop (an ssh before 8.4 puts
        // no (user@host) in front of a keyboard-interactive prompt; a hop with the target's user@host is indistinguishable),
        // so it runs without the vault and the user types the password. The version is read only when a jump host is in play.
        bool noVaultForJumpHost = request.Interactive
            && askPassHelperPath is not null
            && SshAskPassVaultPolicy.GoesThroughJumpHost(profile)
            && !SshAskPassVaultPolicy.MayOfferVault(profile, PrefixesKeyboardInteractivePrompts(launch.SshPath, openSshVersions, log));
        return new OpenSshExecTransport(
            profile,
            launch.SshPath,
            plan,
            request.Interactive || savedPasswordOnly ? askPassHelperPath : null,
            diagnosticsArguments: null,
            log,
            batchMode: !request.Interactive && !savedPasswordOnly,
            savedPasswordOnly: savedPasswordOnly,
            withoutSavedPassword: request.Interactive && (request.WithoutSavedPassword || noVaultForJumpHost),
            askPassSession: request.AskPassSession,
            exitGrace: ChannelExitGrace);
    }

    /// <summary>
    /// Whether the OpenSSH client at <paramref name="sshPath"/>, the one the attempt runs, puts <c>(user@host) </c> in front
    /// of its keyboard-interactive prompts (8.4 and later, <see cref="OpenSshClientVersion.PrefixesKeyboardInteractivePrompts"/>).
    /// The vault-only askpass answers only a prompt that names the target. An older client's <c>Password: </c> would go
    /// unanswered, and ssh then sends an empty answer, a failed login on every attempt. A second factor after a filled
    /// password would not be recorded as declined either, so it would read as the saved password refused. A client that is
    /// not OpenSSH counts as older, and so does one whose version could not be read this time - for this attempt only: that
    /// answer is not kept, and the next attempt reads the version again (<see cref="OpenSshClientVersionCache"/>). Each probe
    /// that leaves the attempt without the saved password is logged once, with why: a definitive answer once per executable,
    /// not on every attempt.
    /// </summary>
    internal static bool PrefixesKeyboardInteractivePrompts(string sshPath, OpenSshClientVersionCache versions, Action<string>? log)
    {
        OpenSshClientProbe probe = versions.Lookup(sshPath, out bool probedHere);
        bool prefixes = OpenSshClientVersion.PrefixesKeyboardInteractivePrompts(probe.Version);
        if (!prefixes && probedHere)
        {
            log?.Invoke(probe.Kind switch
            {
                OpenSshClientProbeKind.Version =>
                    $"[RemoteMux] {sshPath} is OpenSSH {probe.Version}, whose keyboard-interactive prompts do not name the target (8.4 and later do), so automatic OpenSSH reconnects are not offered the saved password",
                OpenSshClientProbeKind.NotOpenSsh =>
                    $"[RemoteMux] {sshPath} is not an OpenSSH client this can read ({probe.Reason}), so automatic OpenSSH reconnects are not offered the saved password",
                _ =>
                    $"[RemoteMux] the OpenSSH version of {sshPath} could not be read ({probe.Reason}), so this automatic reconnect is not offered the saved password; the next one reads it again",
            });
        }

        return prefixes;
    }

    /// <summary>
    /// Refuses a Native profile's attempt while the global native SSH switch is off (codex4 F), as the plain SSH path
    /// refuses such a profile's session (<see cref="SshSessionFactory"/>), and with its message: a profile saved as
    /// Native stays saved when the switch goes off, and its persistent tabs must not connect around it. Nothing is built
    /// or connected first. The failure is <see cref="RemoteFailureKind.NeedsUser"/>: another automatic attempt would be
    /// refused the same way, so the reconnect loop stops at once and the pane offers Enter, and says why - this message,
    /// as it is (<see cref="RemoteNeedsUserCause.NativeSshDisabled"/>); a kill waiting on the host stays queued. Once the
    /// switch is on, Enter connects. An OpenSSH profile does not read the switch.
    /// </summary>
    /// <exception cref="RemoteMuxUnavailableException">The profile is Native and <paramref name="nativeSshEnabled"/> says off.</exception>
    internal static void ThrowIfNativeSshDisabled(SshProfile profile, Func<bool> nativeSshEnabled)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(nativeSshEnabled);
        if (profile.BackendKind == SshBackendKind.Native && !nativeSshEnabled())
        {
            throw new RemoteMuxUnavailableException(
                new RemoteMuxFailure(RemoteFailureKind.NeedsUser, SshSessionFactory.NativeSshDisabledMessage, RemoteNeedsUserCause.NativeSshDisabled));
        }
    }

    /// <summary>
    /// The plan's arguments for one OpenSSH attempt. A retargeted one (<see cref="RemoteMuxTransportRequest.Retargeted"/>)
    /// gets <c>-o ControlPath=none</c> in front (codex D2, final round): its block's ControlPath is keyed by the profile
    /// id, and <c>ControlMaster=no</c> still uses a live master there - one a plain tab may have opened to the profile's
    /// new host, which would run the proxy there. ssh keeps an option's first value, so it neither uses nor creates a
    /// master. Every other attempt's plan, an unedited pinned one's included, is unchanged.
    /// </summary>
    internal static IReadOnlyList<string> PlanArgumentsFor(SshLaunchDetails launch, RemoteMuxTransportRequest request)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(request);
        return request.Retargeted ? ["-o", "ControlPath=none", .. launch.PlanArguments] : launch.PlanArguments;
    }
}
