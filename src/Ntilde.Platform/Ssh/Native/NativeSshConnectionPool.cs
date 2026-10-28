using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Platform.Ssh.Models;

namespace Ntilde.Platform.Ssh.Native;

/// <summary>
/// The native backend's answer to OpenSSH's ControlMaster: the shared connections that the tabs of a profile with
/// multiplexing on ride, one per profile and connection settings. A tab takes a lease with <see cref="Acquire"/> and
/// opens its shell on the leased connection; the connection lives while any lease does, and then for the profile's
/// ControlPersist seconds.
/// </summary>
/// <remarks>
/// A connection is shared only by tabs whose connection settings match exactly (<see cref="KeyFor"/>): tabs of
/// another profile never join it, even for the same host, and editing the profile's destination, credentials, jump
/// chain or forwards gives later tabs a connection of their own while the old one serves the tabs it already has.
/// </remarks>
public sealed class NativeSshConnectionPool
{
    /// <summary>The app's pool: every native session shares connections through it unless a test supplies its own.</summary>
    public static NativeSshConnectionPool Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, NativeSshSharedConnection> _connections = new(StringComparer.Ordinal);

    /// <summary>Live connections, for tests.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _connections.Count;
            }
        }
    }

    /// <summary>
    /// A lease on the running connection for <paramref name="profile"/> and <paramref name="options"/>, or on a new
    /// one when there is none. A new connection starts connecting at once, on <paramref name="opener"/>'s native layer;
    /// its prompts go to the opener's handler and are tagged with its session id. <paramref name="warn"/> receives the connection's user-facing warnings (a refused forward) while the
    /// lease is held.
    /// </summary>
    /// <param name="reused">True when the lease joined a connection that was already running.</param>
    /// <exception cref="InvalidOperationException">A forward's local port could not be bound.</exception>
    internal NativeSshSharedConnection.Lease Acquire(
        SshProfile profile,
        NativeSshConnectionOptions options,
        NativeSshSharedConnection.Opener opener,
        Action<string> warn,
        out bool reused)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(opener);

        string key = KeyFor(profile, options);
        TimeSpan persist = TimeSpan.FromSeconds(Math.Max(0, profile.MuxOptions.ControlPersistSeconds));
        lock (_gate)
        {
            if (_connections.TryGetValue(key, out NativeSshSharedConnection? running)
                && running.TryAddLease(warn, persist, opener.InteractionHandler) is { } lease)
            {
                reused = true;
                return lease;
            }

            // Connecting is asynchronous on the native side, so this holds the gate only for the setup.
            var connection = new NativeSshSharedConnection(this, key, profile, options, opener);
            _connections[key] = connection;
            reused = false;
            return connection.TryAddLease(warn, persist, opener.InteractionHandler)
                ?? throw new InvalidOperationException("A new shared native SSH connection refused its first lease.");
        }
    }

    /// <summary>
    /// Whether a native session of <paramref name="profile"/> shares its connection: multiplexing on, as ControlMaster
    /// auto. Without auto, OpenSSH only joins a master another client made, which has no native equivalent.
    /// </summary>
    public static bool Shares(SshProfile profile) => profile.MuxOptions is { Enabled: true, ControlMasterAuto: true };

    /// <summary>
    /// A lease and a channel <paramref name="open"/> opened on the profile's shared connection, joining the running one
    /// or starting it (<see cref="Acquire"/>). A connection that ended between the lookup and the open is abandoned for a
    /// new one, once: the pool would otherwise hand the same dead connection out again. Only the open is retried: a
    /// connection that could not be set up at all would fail the same way again.
    /// </summary>
    /// <exception cref="InvalidOperationException">The retry's connection ended too.</exception>
    internal (NativeSshSharedConnection.Lease Lease, NovaSshSafeHandle Channel) AcquireAndOpen(
        SshProfile profile,
        NativeSshConnectionOptions options,
        NativeSshSharedConnection.Opener opener,
        Action<string> warn,
        Func<NativeSshSharedConnection, NovaSshSafeHandle> open)
    {
        ArgumentNullException.ThrowIfNull(open);

        NativeSshSharedConnection.Lease lease = Acquire(profile, options, opener, warn, out bool reused);
        if (TryOpen(lease, open, out NovaSshSafeHandle? channel))
        {
            opener.Log($"[NativeSshConnectionPool] shared-connection={(reused ? "reused" : "new")}");
            return (lease, channel);
        }

        opener.Log("[NativeSshConnectionPool] shared connection ended before the channel opened; reconnecting");
        lease.Connection.Abandon();
        lease.Dispose();

        lease = Acquire(profile, options, opener, warn, out _);
        try
        {
            return (lease, open(lease.Connection));
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <summary>
    /// <paramref name="open"/> on the leased connection. False when the connection has ended (the open threw
    /// <see cref="InvalidOperationException"/>); any other failure releases the lease and propagates.
    /// </summary>
    private static bool TryOpen(
        NativeSshSharedConnection.Lease lease,
        Func<NativeSshSharedConnection, NovaSshSafeHandle> open,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out NovaSshSafeHandle? channel)
    {
        try
        {
            channel = open(lease.Connection);
            return true;
        }
        catch (InvalidOperationException)
        {
            channel = null;
            return false;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <summary>
    /// An SFTP transfer on the profile's shared connection, when one is running and logged in. False when there is none,
    /// or it ended first: the caller transfers over a connection of its own, as without sharing. Never starts a
    /// connection: SFTP cannot answer a prompt, and a connection it started would be one other tabs could not log in on.
    /// </summary>
    public bool TryRunSftpTransfer(
        SshProfile profile,
        NativeSftpTransferOptions transfer,
        Action<NativeSftpTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Held for the transfer, so the last tab closing meanwhile does not take the connection from under it.
        using NativeSshSharedConnection.Lease? lease = TryJoinConnected(profile);
        return lease is not null && lease.Connection.TryRunSftpTransfer(transfer, progress, cancellationToken);
    }

    /// <summary>A listing on the profile's shared connection; null when there is none, as for <see cref="TryRunSftpTransfer"/>.</summary>
    public IReadOnlyList<NativeRemotePathEntry>? TryListRemoteDirectory(
        SshProfile profile,
        string remotePath,
        CancellationToken cancellationToken)
    {
        using NativeSshSharedConnection.Lease? lease = TryJoinConnected(profile);
        return lease?.Connection.TryListRemoteDirectory(remotePath, cancellationToken);
    }

    /// <summary>A lease on the profile's running, logged-in connection, keyed as a tab of the profile would key it now.</summary>
    private NativeSshSharedConnection.Lease? TryJoinConnected(SshProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!Shares(profile))
        {
            return null;
        }

        string key = KeyFor(profile, NativeSshConnectionOptionsFactory.Create(profile));
        TimeSpan persist = TimeSpan.FromSeconds(Math.Max(0, profile.MuxOptions.ControlPersistSeconds));
        lock (_gate)
        {
            return _connections.TryGetValue(key, out NativeSshSharedConnection? running) && running.IsConnected
                ? running.TryAddLease(static _ => { }, persist)
                : null;
        }
    }

    /// <summary>Forgets <paramref name="connection"/> so the next tab gets a new one. A no-op for a replaced entry.</summary>
    internal void Remove(NativeSshSharedConnection connection)
    {
        lock (_gate)
        {
            if (_connections.TryGetValue(connection.Key, out NativeSshSharedConnection? current) && ReferenceEquals(current, connection))
            {
                _connections.Remove(connection.Key);
            }
        }
    }

    /// <summary>
    /// What two tabs must agree on to share a connection: the profile, and everything the connection itself is made
    /// of. Terminal and shell settings are not in it; those belong to each tab's own channel.
    /// </summary>
    internal static string KeyFor(SshProfile profile, NativeSshConnectionOptions options) => string.Join(
        '\n',
        profile.Id.ToString("N"),
        options.Host,
        options.User,
        options.Port,
        options.IdentityFilePath ?? string.Empty,
        options.UseAgent,
        options.KeepAliveIntervalSeconds,
        options.KeepAliveCountMax,
        string.Join(',', options.JumpHops),
        string.Join(',', profile.Forwards));
}
