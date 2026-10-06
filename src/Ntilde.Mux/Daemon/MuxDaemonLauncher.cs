using Ntilde.Mux.Contracts;
using Ntilde.Mux.Transport;

namespace Ntilde.Mux.Daemon;

public sealed class MuxUnavailableException : Exception
{
    public MuxUnavailableException() : this("The multiplexer is unavailable.") { }

    public MuxUnavailableException(string message, Exception? inner = null, bool versionMismatch = false, bool orphanedDaemon = false) : base(message, inner)
    {
        VersionMismatch = versionMismatch;
        OrphanedDaemon = orphanedDaemon;
    }

    /// <summary>A daemon is running but speaks a protocol version this build does not: spawning another cannot help.</summary>
    public bool VersionMismatch { get; }

    /// <summary>
    /// A daemon holds this root's lock but cannot be reached (its descriptor or socket was deleted):
    /// every new daemon exits with <see cref="MuxServeHost.LockHeldExitCode"/>, so spawning cannot help.
    /// </summary>
    public bool OrphanedDaemon { get; }
}

/// <summary>Connect to the running daemon, or start one and connect (Phase 2 spec §6; Phase 4 spec §6.2).</summary>
public sealed class MuxDaemonLauncher
{
    /// <summary>What the hint tells the user to run when the App is the host (<c>ntilde mux</c>).</summary>
    public const string DefaultKillServerCommand = "ntilde mux kill-server --force";

    /// <summary>The hint for <see cref="DefaultKillServerCommand"/>; a standalone host builds its own from its command.</summary>
    public const string KillServerHint = "Run '" + DefaultKillServerCommand + "' to replace it (this closes its sessions).";

    private readonly string _descriptorPath;
    private readonly string _killServerHint;
    private readonly string _root;
    private readonly IMuxDaemonSpawner _spawner;
    private readonly MuxClientOptions _clientOptions;
    private readonly Action<string>? _log;

    public MuxDaemonLauncher(string descriptorPath, IMuxDaemonSpawner spawner, MuxClientOptions? clientOptions = null, Action<string>? log = null,
        string killServerCommand = DefaultKillServerCommand)
    {
        _killServerHint = $"Run '{killServerCommand}' to replace it (this closes its sessions).";
        _descriptorPath = descriptorPath;
        // <root>/mux/mux-endpoint.json (MuxDiscovery.GetDescriptorPath).
        _root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(descriptorPath))!)!;
        _spawner = spawner;
        _clientOptions = clientOptions ?? new MuxClientOptions { Log = log };
        _log = log;
    }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan SpawnTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long after spawning, with still no live descriptor, the launcher spawns once more. The
    /// first daemon can lose the lock race to an old one that was idle-exiting at that moment: it
    /// exits at once, and the old one is gone soon after, leaving nothing to connect to.
    /// </summary>
    public TimeSpan RespawnAfter { get; init; } = TimeSpan.FromSeconds(1);

    /// <param name="serveArguments">What the spawned executable is given to serve: <c>["mux","serve"]</c> for the GUI's exe.</param>
    /// <param name="paths">The daemon's root, where the launcher looks and the spawned daemon serves; null = <see cref="MuxPaths.Default"/>.</param>
    /// <param name="killServerCommand">The command the version-mismatch hint names: the host's own.</param>
    public static MuxDaemonLauncher CreateDefault(Action<string>? log, IReadOnlyList<string> serveArguments, MuxPaths? paths = null,
        string killServerCommand = DefaultKillServerCommand)
    {
        MuxPaths p = paths ?? MuxPaths.Default();
        return new MuxDaemonLauncher(p.DescriptorPath, ProcessMuxDaemonSpawner.CreateDefault(serveArguments, p), log: log,
            killServerCommand: killServerCommand);
    }

    /// <summary>Test seam: the spawner this launcher starts daemons with.</summary>
    internal IMuxDaemonSpawner SpawnerForTest => _spawner;

    public async Task<MuxClient?> TryConnectExistingAsync(CancellationToken cancellationToken) =>
        (await TryConnectExistingCoreAsync(hello: true, cancellationToken).ConfigureAwait(false))?.Client;

    /// <summary>One connection: the stream, the descriptor it came from, and the client when the hello was sent.</summary>
    private readonly record struct Attempt(Stream Stream, MuxEndpointDescriptor Descriptor, MuxClient? Client);

    /// <summary>
    /// One try at the advertised daemon: its endpoint connected and, with <paramref name="hello"/>,
    /// greeted. Null when no trusted daemon is advertised or the connect or the hello fails (logged):
    /// that daemon then counts as absent, as one tearing down after its idle exit does. A version
    /// mismatch throws instead - another spawn cannot help.
    /// </summary>
    private async Task<Attempt?> TryConnectExistingCoreAsync(bool hello, CancellationToken cancellationToken)
    {
        if (!MuxDiscovery.TryReadLiveDescriptor(_descriptorPath, out MuxEndpointDescriptor? d)) return null;
        if (!IsTrustedEndpoint(d.Endpoint, _root, out string? why))
        {
            _log?.Invoke($"[Mux] ignoring the descriptor at {_descriptorPath}: {why}");
            return null;
        }

        try
        {
            Stream stream = await Task.Run(() => MuxEndpointConnector.Connect(d.Endpoint, ConnectTimeout), cancellationToken).ConfigureAwait(false);
            if (!hello) return new Attempt(stream, d, null);
            return new Attempt(stream, d, await MuxClient.ConnectAsync(stream, _clientOptions, cancellationToken).ConfigureAwait(false));
        }
        catch (MuxProtocolException ex) when (ex.Code == MuxErrorCodes.VersionMismatch)
        {
            string message = $"The running multiplexer (pid {d.Pid}) is a different version: {ex.Message} {_killServerHint}";
            _log?.Invoke($"[Mux] {message}");
            throw new MuxUnavailableException(message, ex, versionMismatch: true);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or MuxProtocolException or System.Net.Sockets.SocketException)
        {
            _log?.Invoke($"[Mux] connecting to {d.Endpoint} (pid {d.Pid}) failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The descriptor is a plain file: only connect where this root's daemon would listen. Off
    /// Windows the socket may sit in a TMPDIR fallback directory (MuxDiscovery.GetDefaultEndpoint),
    /// so its directory must also still be private (0700), as the listener made it - otherwise
    /// someone else may have put a socket there.
    /// </summary>
    public static bool IsTrustedEndpoint(string endpoint, string root, out string? reason)
    {
        string expected = MuxDiscovery.GetDefaultEndpoint(root);
        if (!string.Equals(endpoint, expected, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            reason = $"its endpoint {endpoint} is not this profile's {expected}";
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            string? dir = Path.GetDirectoryName(endpoint);
            const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            UnixFileMode mode;
            try
            {
                mode = dir is null ? UnixFileMode.None : File.GetUnixFileMode(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                reason = $"its socket directory could not be checked: {ex.Message}";
                return false;
            }

            if (mode != Private)
            {
                reason = $"its socket directory {dir} has mode {Convert.ToString((int)mode, 8)}, expected 700";
                return false;
            }
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// The daemon's endpoint, connected, spawning the daemon on demand - but no hello: the caller
    /// speaks first. <c>ntilde-mux proxy</c> (Phase 4 spec §8.1) relays its client's own hello over it.
    /// </summary>
    public async Task<(Stream Stream, MuxEndpointDescriptor Descriptor)> EnsureEndpointStreamAsync(CancellationToken cancellationToken)
    {
        Attempt connected = await EnsureAsync(hello: false, cancellationToken).ConfigureAwait(false);
        return (connected.Stream, connected.Descriptor);
    }

    /// <summary>
    /// <see cref="EnsureEndpointStreamAsync"/> plus the hello, sent on each attempt: a daemon that
    /// drops the connection before answering it (one tearing down after its idle exit) counts as
    /// absent, as it always has. A version mismatch is a <see cref="MuxUnavailableException"/> with
    /// <see cref="MuxUnavailableException.VersionMismatch"/> and the kill-server hint, and nothing
    /// more is spawned.
    /// </summary>
    public async Task<MuxClient> EnsureConnectedAsync(CancellationToken cancellationToken) =>
        (await EnsureAsync(hello: true, cancellationToken).ConfigureAwait(false)).Client!;

    private async Task<Attempt> EnsureAsync(bool hello, CancellationToken cancellationToken)
    {
        if (await TryConnectExistingCoreAsync(hello, cancellationToken).ConfigureAwait(false) is { } existing) return existing;

        _log?.Invoke("[Mux] starting the multiplexer daemon");
        try { _spawner.Spawn(); }
        catch (Exception ex) { throw new MuxUnavailableException($"Could not start the multiplexer: {ex.Message}", ex); }

        DateTime spawnedAt = DateTime.UtcNow;
        DateTime deadline = spawnedAt + SpawnTimeout;
        bool respawned = false;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryConnectExistingCoreAsync(hello, cancellationToken).ConfigureAwait(false) is { } connected) return connected;

            bool advertised = MuxDiscovery.TryReadLiveDescriptor(_descriptorPath, out _);
            if (DateTime.UtcNow - spawnedAt >= RespawnAfter && _spawner.LastSpawnExitCode == MuxServeHost.LockHeldExitCode && (respawned || advertised))
            {
                // Our daemon found the lock held, yet nothing answers: after the respawn (which covers
                // an old daemon idle-exiting), or while the descriptor names one we just failed to
                // connect to. A daemon whose descriptor or socket was deleted - more spawns cannot
                // help, and waiting out the timeout would only hide why.
                const string Message = "Another multiplexer is running but cannot be reached (it holds the lock, but its endpoint descriptor or socket is gone).";
                _log?.Invoke($"[Mux] {Message}");
                throw new MuxUnavailableException(Message, orphanedDaemon: true);
            }

            // At most one extra spawn per call, and only once no daemon at all is advertised: a
            // live descriptor means one is up (or coming up) and a second would only lose the lock.
            if (!respawned && DateTime.UtcNow - spawnedAt >= RespawnAfter && !advertised)
            {
                respawned = true;
                _log?.Invoke("[Mux] the multiplexer has not come up; starting it again");
                try { _spawner.Spawn(); }
                catch (Exception ex) { _log?.Invoke($"[Mux] starting the multiplexer again failed: {ex.Message}"); }
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        throw new MuxUnavailableException($"The multiplexer did not come up within {SpawnTimeout.TotalSeconds:0} s.");
    }
}
