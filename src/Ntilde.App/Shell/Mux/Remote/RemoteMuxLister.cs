using System.Runtime.CompilerServices;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Daemon;
using Ntilde.Platform.Ssh.Exec;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Services.Ssh;

namespace Ntilde.Shell.Mux.Remote;

/// <summary>
/// One SSH profile's sessions for <c>ntilde mux ls --all</c>, or why there are none to show (<paramref name="Error"/> set).
/// </summary>
/// <param name="Host">The profile's <c>user@host</c>, <see cref="RemoteOutputText.Clean"/>ed.</param>
internal sealed record RemoteListing(Guid ProfileId, string Host, IReadOnlyList<SessionSummary>? Sessions, MuxListingError? Error);

/// <summary>
/// Why <c>ntilde mux ls --all</c> could not list a host's sessions. It prints these in its own words
/// (<see cref="RemoteMuxLister.Reason"/>), never a server's or an exception's: those go to its log.
/// </summary>
internal enum MuxListingError
{
    /// <summary>No daemon runs, on this computer or on the host: ls never starts one (release hardening item 7).</summary>
    NotRunning,

    /// <summary>No connection could be had, or it broke during the listing.</summary>
    ConnectionFailed,

    /// <summary>The host did not answer within its wait.</summary>
    TimedOut,

    /// <summary>The daemon answered with a protocol error, speaks no protocol version in common, or cannot run there.</summary>
    NotUsable,

    /// <summary>ntilde-mux is not installed on the host.</summary>
    NotInstalled,

    /// <summary>Signing in needs an answer - a password, a key's passphrase, a host key nobody trusts yet - which is never asked for.</summary>
    NeedsSignIn,

    /// <summary>The profile uses the native SSH backend, which is switched off in Settings.</summary>
    NativeSshOff,

    /// <summary>
    /// Off Windows, an OpenSSH profile that goes through a jump host or a proxy is not connected to: its hop's ssh could
    /// prompt on the terminal <c>ls --all</c> runs in (<see cref="RemoteMuxLister.SkipsJumpHosts"/>).
    /// </summary>
    ThroughJumpHost,

    /// <summary>
    /// The host's ntilde-mux is older than <c>proxy --no-spawn</c>, which a listing needs so as not to start a daemon: it
    /// refused the option (<see cref="RemoteFailureKind.ProxyTooOld"/>). Updating it from a window fixes it.
    /// </summary>
    NeedsUpdate,
}

/// <summary>
/// The remote half of <c>ntilde mux ls --all</c> (Phase 5 spec §5): the sessions of every SSH profile that keeps its remote
/// sessions, listed all at once, each host within its own wait. The command cannot see what a window connected, so it
/// connects on its own, non-interactively (<see cref="CreateConnector"/>): one connect, one listing, and the connection
/// ends. A listing has no side effects on the host: its proxy never starts a daemon (release hardening item 7, which
/// reverses R28(a)), and it forwards no agent, which the proxy would point the daemon's shells at (item 3). A host that
/// fails or does not answer in time gives its <see cref="MuxListingError"/>, and holds up no other.
/// </summary>
internal static class RemoteMuxLister
{
    /// <summary>How long one host gets, connect and listing together, before <c>ls --all</c> cuts it.</summary>
    public static readonly TimeSpan PerHostTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a cut host's attempt gets to unwind after its cancel before <c>ls --all</c> stops waiting for it: the cancel
    /// stops its channel at once, but a failure that came first may still be ending its channel (a few seconds at most).
    /// </summary>
    private static readonly TimeSpan CutGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The sessions of every profile in <paramref name="svc"/>'s store that keeps its remote sessions, read from the store now,
    /// in the picker's order (by name, then id). Completes once each host has answered, failed or been cut at
    /// <paramref name="perHost"/>; never throws for one host's failure, which is logged and kept as a kind only. Off Windows,
    /// a profile <see cref="SkipsJumpHosts"/> names is reported without a connect.
    /// </summary>
    /// <param name="connectorFor">The connector for one profile (<see cref="CreateConnector"/> in the app); disposed here.</param>
    /// <param name="log">Where each failure is logged, with the exception's own text.</param>
    /// <param name="isWindows">The OS this runs on (<see cref="OperatingSystem.IsWindows"/> in the app).</param>
    /// <param name="releaseClient">
    /// How a host's client is let go once its listing is over: disposed, unless a test slows it down. Outside the host's wait,
    /// so a host whose sessions arrived in time is listed however long that takes.
    /// </param>
    public static async Task<IReadOnlyList<RemoteListing>> ListAsync(
        SshConnectionService svc,
        Func<SshProfile, RemoteMuxConnector> connectorFor,
        TimeSpan perHost,
        Action<string>? log,
        bool isWindows,
        CancellationToken ct,
        Action<MuxClient>? releaseClient = null)
    {
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(connectorFor);
        SshProfile[] profiles = [.. svc.GetStoredProfiles()
            .Where(p => p is { MuxOptions.PersistRemoteSessions: true })
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Id)];
        Action<MuxClient> release = releaseClient ?? (static client => client.Dispose());
        // Each on the pool: building a connector, or starting its transport, must not hold up the next host.
        return await Task.WhenAll(profiles.Select(p => SkipsJumpHosts(p, isWindows)
            ? Task.FromResult(Skipped(p, log))
            : Task.Run(() => ListOneAsync(p, connectorFor, perHost, log, release, ct), ct))).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether <c>ls --all</c> leaves <paramref name="profile"/> unconnected (Task 27 fix round 1, the controller's ruling (b)):
    /// off Windows, an OpenSSH profile that goes through a jump host or a proxy as ntilde can see it
    /// (<see cref="SshAskPassVaultPolicy.GoesThroughJumpHost"/>: its jump hops, which the launch plan compiles into
    /// <c>ProxyJump</c>, and its extra arguments' <c>-J</c>, <c>ProxyJump</c> or <c>ProxyCommand</c>, read as
    /// <see cref="OpenSshExecCommandLine.NamesAProxy"/> reads them). Its hop's ssh does not inherit <c>BatchMode</c>, and with
    /// askpass refused (<see cref="SshAskPassEnvironment.Suppress"/>) a prompt falls back to the terminal <c>ls --all</c> runs
    /// in, which a cut could leave with echo off. A native profile's prompts go to its handler, which asks nobody; Windows'
    /// ssh is unaffected. The window's picker still lists such hosts.
    /// </summary>
    internal static bool SkipsJumpHosts(SshProfile profile, bool isWindows) =>
        !isWindows && profile.BackendKind == SshBackendKind.OpenSsh && SshAskPassVaultPolicy.GoesThroughJumpHost(profile);

    private static RemoteListing Skipped(SshProfile profile, Action<string>? log)
    {
        string host = RemoteOutputText.Clean(RemoteMuxConnector.DisplayNameOf(profile));
        log?.Invoke($"[RemoteMux] ls --all: {host}: not connected to: OpenSSH through a jump host or proxy could prompt on this terminal");
        return new RemoteListing(profile.Id, host, null, MuxListingError.ThroughJumpHost);
    }

    /// <summary>
    /// The connector <c>ls --all</c> reaches <paramref name="profile"/>'s daemon through. Every attempt is automatic and no
    /// window's prompts are there to ask (<c>user: null</c>), so it signs in only with what needs nobody: keys, the agent, an
    /// existing connection-sharing master, and the profile's saved password - offered once per attempt, never an empty one
    /// (<see cref="RemoteMuxInteractionHandler"/>). Its client instance id is its own: the daemon evicts only a connection
    /// with the same id (a window's reconnect replacing its dead one), so its hello never evicts a window's.
    /// </summary>
    /// <param name="transportFor">One attempt's transport (<see cref="RemoteMuxHostFactory.CreateTransport"/> in the app).</param>
    /// <param name="savedPassword">Reads the profile's password saved in the vault; null: none is offered.</param>
    /// <param name="askPassRecords">What OpenSSH's askpass helper did for an attempt; null: a saved password never counts as refused.</param>
    public static RemoteMuxConnector CreateConnector(
        SshProfile profile,
        Func<SshProfile, RemoteMuxTransportRequest, ISshExecTransport> transportFor,
        Func<SshProfile, string?>? savedPassword,
        SshAskPassSessionMarkers? askPassRecords,
        Action<string>? log)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new RemoteMuxConnector(
            () => profile,
            transportFor,
            new RemoteMuxInteractionHandler(user: null, savedPassword: savedPassword, askPassRecords: askPassRecords, log: log),
            Guid.NewGuid().ToString("N"),
            log)
        {
            ForListing = true,
        };
    }

    /// <summary>The words for <paramref name="error"/> after "unreachable: ", and in the JSON's "error".</summary>
    public static string Reason(MuxListingError error) => error switch
    {
        MuxListingError.NotRunning => "no multiplexer is running",
        MuxListingError.TimedOut => "it did not answer in time",
        MuxListingError.NotUsable => "its multiplexer is not usable",
        MuxListingError.NotInstalled => "ntilde-mux is not installed there",
        MuxListingError.NeedsSignIn => "signing in needs an answer, which ls --all never asks for",
        MuxListingError.NativeSshOff => "the native SSH backend is turned off in Settings",
        MuxListingError.ThroughJumpHost => "it goes through a jump host, which ls --all does not sign in through",
        MuxListingError.NeedsUpdate => "its ntilde-mux is older than this app; update it to list its sessions",
        _ => "the connection failed",
    };

    /// <summary>What a failed listing's exception was, as a kind: its text is never printed.</summary>
    public static MuxListingError ErrorOf(Exception ex) => ex switch
    {
        RemoteMuxUnavailableException { Failure: var failure } => failure.Kind switch
        {
            RemoteFailureKind.NotInstalled => MuxListingError.NotInstalled,
            RemoteFailureKind.NotRunning => MuxListingError.NotRunning,
            RemoteFailureKind.ProxyTooOld => MuxListingError.NeedsUpdate,
            RemoteFailureKind.Unsupported or RemoteFailureKind.VersionMismatch => MuxListingError.NotUsable,
            RemoteFailureKind.NeedsUser when failure.Cause == RemoteNeedsUserCause.NativeSshDisabled => MuxListingError.NativeSshOff,
            RemoteFailureKind.NeedsUser => MuxListingError.NeedsSignIn,
            _ => MuxListingError.ConnectionFailed,
        },
        MuxProtocolException or MuxUnavailableException => MuxListingError.NotUsable,
        TimeoutException or OperationCanceledException => MuxListingError.TimedOut,
        _ => MuxListingError.ConnectionFailed,
    };

    /// <summary>
    /// One host, cut at <paramref name="perHost"/>. The cut cancels the connect and the listing; before the greeting that
    /// stops the host's ssh or native channel inside the cancel (<see cref="RemoteMuxConnector.ConnectAsync(bool, CancellationToken)"/>).
    /// The wait is bounded on its own as well, <see cref="CutGrace"/> later, for what a cancel cannot cut short. A host given
    /// up on - cut, bounded, or failed - has its channel killed before this returns (<see cref="RemoteMuxConnector.Abort"/>),
    /// greeting or not: the command may exit as soon as it has printed, and a graceful end on a dead link would leave its ssh
    /// running past it. The sessions are taken out before anything is let go, and the client and the connector are let go
    /// once the listing is over, outside the wait: a slow release never makes a host that answered in time look timed out.
    /// </summary>
    private static async Task<RemoteListing> ListOneAsync(
        SshProfile profile, Func<SshProfile, RemoteMuxConnector> connectorFor, TimeSpan perHost, Action<string>? log, Action<MuxClient> releaseClient, CancellationToken ct)
    {
        string host = RemoteOutputText.Clean(RemoteMuxConnector.DisplayNameOf(profile));
        RemoteMuxConnector connector;
        try
        {
            connector = connectorFor(profile);
        }
        catch (Exception ex)
        {
            return Failed(profile, host, ex, log);
        }

        var cut = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cut.CancelAfter(perHost);
        var connected = new StrongBox<MuxClient?>();
        Task<IReadOnlyList<SessionSummary>> listing = ConnectAndListAsync(connector, connected, cut.Token);
        // On the pool once the listing is over, however it ended - the cut's source too: a listing still running after the
        // bounded wait below needs its cancel.
        _ = listing.ContinueWith(
            static (done, state) =>
            {
                var (connector, connected, release, cut, host, log) =
                    ((RemoteMuxConnector, StrongBox<MuxClient?>, Action<MuxClient>, CancellationTokenSource, string, Action<string>?))state!;
                _ = done.Exception;   // observed: a failure was reported as the wait's, or came after it
                try
                {
                    try
                    {
                        if (connected.Value is { } client) release(client);
                    }
                    finally
                    {
                        connector.Dispose();
                        cut.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    // Nothing awaits this continuation, so a throw here would surface only as an unobserved task exception.
                    log?.Invoke($"[RemoteMux] ls --all: {host}: release failed: {ex.GetType().Name}: {ex.Message}");
                }
            },
            (connector, connected, releaseClient, cut, host, log),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
        try
        {
            return new(profile.Id, host, await listing.WaitAsync(perHost + CutGrace, ct).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            connector.Abort();
            return Failed(profile, host, ex, log);
        }
    }

    /// <summary>Any failure: one host's must never stop the others from being listed. Logged with its own text, kept as a kind.</summary>
    private static RemoteListing Failed(SshProfile profile, string host, Exception ex, Action<string>? log)
    {
        MuxListingError error = ErrorOf(ex);
        log?.Invoke($"[RemoteMux] ls --all: {host}: {error}: {ex.GetType().Name}: {ex.Message}");
        return new RemoteListing(profile.Id, host, null, error);
    }

    /// <summary>The connect, then the listing; <paramref name="connected"/> holds the client for its release afterwards.</summary>
    private static async Task<IReadOnlyList<SessionSummary>> ConnectAndListAsync(
        RemoteMuxConnector connector, StrongBox<MuxClient?> connected, CancellationToken ct)
    {
        MuxClient client = await connector.ConnectAsync(interactive: false, ct).ConfigureAwait(false);
        connected.Value = client;
        return await client.ListSessionsAsync(ct).ConfigureAwait(false);
    }
}
