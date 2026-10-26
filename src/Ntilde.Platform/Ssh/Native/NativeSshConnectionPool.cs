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
                && running.TryAddLease(warn, persist) is { } lease)
            {
                reused = true;
                return lease;
            }

            // Connecting is asynchronous on the native side, so this holds the gate only for the setup.
            var connection = new NativeSshSharedConnection(this, key, profile, options, opener);
            _connections[key] = connection;
            reused = false;
            return connection.TryAddLease(warn, persist)
                ?? throw new InvalidOperationException("A new shared native SSH connection refused its first lease.");
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
