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
    /// <summary>This computer: no daemon runs (ls never starts one).</summary>
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
}

/// <summary>
/// The remote half of <c>ntilde mux ls --all</c> (Phase 5 spec §5): the sessions of every SSH profile that keeps its remote
/// sessions, listed all at once, each host within its own wait. The command cannot see what a window connected, so it
/// connects on its own, non-interactively (<see cref="CreateConnector"/>): one connect, one listing, and the connection
/// ends. A host that fails or does not answer in time gives its <see cref="MuxListingError"/>, and holds up no other.
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
    /// <paramref name="perHost"/>; never throws for one host's failure, which is logged and kept as a kind only.
    /// </summary>
    /// <param name="connectorFor">The connector for one profile (<see cref="CreateConnector"/> in the app); disposed here.</param>
    /// <param name="log">Where each failure is logged, with the exception's own text.</param>
    public static async Task<IReadOnlyList<RemoteListing>> ListAsync(
        SshConnectionService svc,
        Func<SshProfile, RemoteMuxConnector> connectorFor,
        TimeSpan perHost,
        Action<string>? log,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(connectorFor);
        SshProfile[] profiles = [.. svc.GetStoredProfiles()
            .Where(p => p is { MuxOptions.PersistRemoteSessions: true })
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Id)];
        // Each on the pool: building a connector, or starting its transport, must not hold up the next host.
        return await Task.WhenAll(profiles.Select(p => Task.Run(() => ListOneAsync(p, connectorFor, perHost, log, ct)))).ConfigureAwait(false);
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
            log);
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
        _ => "the connection failed",
    };

    /// <summary>What a failed listing's exception was, as a kind: its text is never printed.</summary>
    public static MuxListingError ErrorOf(Exception ex) => ex switch
    {
        RemoteMuxUnavailableException { Failure: var failure } => failure.Kind switch
        {
            RemoteFailureKind.NotInstalled => MuxListingError.NotInstalled,
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
    /// One host, cut at <paramref name="perHost"/>. The cut cancels the connect and the listing, which stops the host's ssh
    /// or native channel at once, inside the cancel (<see cref="RemoteMuxConnector.ConnectAsync(bool, CancellationToken)"/>).
    /// The wait is bounded on its own as well, <see cref="CutGrace"/> later, for what a cancel cannot cut short.
    /// </summary>
    private static async Task<RemoteListing> ListOneAsync(
        SshProfile profile, Func<SshProfile, RemoteMuxConnector> connectorFor, TimeSpan perHost, Action<string>? log, CancellationToken ct)
    {
        string host = RemoteOutputText.Clean(RemoteMuxConnector.DisplayNameOf(profile));
        var cut = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cut.CancelAfter(perHost);
        Task<IReadOnlyList<SessionSummary>> listing = ConnectAndListAsync(profile, connectorFor, cut.Token);
        // Disposed only once the listing is over: one still running after the bounded wait below needs its cancel.
        _ = listing.ContinueWith(
            static (done, state) =>
            {
                _ = done.Exception;   // observed: a late failure was already reported as the wait's
                ((CancellationTokenSource)state!).Dispose();
            },
            cut,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        try
        {
            return new(profile.Id, host, await listing.WaitAsync(perHost + CutGrace, ct).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Any failure: one host's must never stop the others from being listed.
            MuxListingError error = ErrorOf(ex);
            log?.Invoke($"[RemoteMux] ls --all: {host}: {error}: {ex.GetType().Name}: {ex.Message}");
            return new(profile.Id, host, null, error);
        }
    }

    private static async Task<IReadOnlyList<SessionSummary>> ConnectAndListAsync(
        SshProfile profile, Func<SshProfile, RemoteMuxConnector> connectorFor, CancellationToken ct)
    {
        RemoteMuxConnector connector = connectorFor(profile);
        try
        {
            using MuxClient client = await connector.ConnectAsync(interactive: false, ct).ConfigureAwait(false);
            return await client.ListSessionsAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            connector.Dispose();
        }
    }
}
