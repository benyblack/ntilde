using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Transport;

namespace Ntilde.Shell.Mux;

public sealed class MuxUnavailableException : Exception
{
    public MuxUnavailableException() : this("The multiplexer is unavailable.") { }

    public MuxUnavailableException(string message, Exception? inner = null, bool versionMismatch = false) : base(message, inner) => VersionMismatch = versionMismatch;

    /// <summary>A daemon is running but speaks a protocol version this build does not: spawning another cannot help.</summary>
    public bool VersionMismatch { get; }
}

/// <summary>Connect to the running daemon, or start one and connect (spec §6).</summary>
internal sealed class MuxDaemonLauncher
{
    internal const string KillServerHint = "Run 'ntilde mux kill-server' to replace it (this closes its sessions).";

    private readonly string _descriptorPath;
    private readonly string _root;
    private readonly IMuxDaemonSpawner _spawner;
    private readonly MuxClientOptions _clientOptions;
    private readonly Action<string>? _log;

    public MuxDaemonLauncher(string descriptorPath, IMuxDaemonSpawner spawner, MuxClientOptions? clientOptions = null, Action<string>? log = null)
    {
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

    public static MuxDaemonLauncher CreateDefault(Action<string>? log) =>
        new(MuxDiscovery.GetDescriptorPath(), ProcessMuxDaemonSpawner.CreateDefault(), log: log);

    public async Task<MuxClient?> TryConnectExistingAsync(CancellationToken cancellationToken)
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
            return await MuxClient.ConnectAsync(stream, _clientOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (MuxProtocolException ex) when (ex.Code == MuxErrorCodes.VersionMismatch)
        {
            string message = $"The running multiplexer (pid {d.Pid}) is a different version: {ex.Message} {KillServerHint}";
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
    internal static bool IsTrustedEndpoint(string endpoint, string root, out string? why)
    {
        string expected = MuxDiscovery.GetDefaultEndpoint(root);
        if (!string.Equals(endpoint, expected, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            why = $"its endpoint {endpoint} is not this profile's {expected}";
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
                why = $"its socket directory could not be checked: {ex.Message}";
                return false;
            }

            if (mode != Private)
            {
                why = $"its socket directory {dir} has mode {Convert.ToString((int)mode, 8)}, expected 700";
                return false;
            }
        }

        why = null;
        return true;
    }

    public async Task<MuxClient> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (await TryConnectExistingAsync(cancellationToken).ConfigureAwait(false) is { } existing) return existing;

        _log?.Invoke("[Mux] starting the multiplexer daemon");
        try { _spawner.Spawn(); }
        catch (Exception ex) { throw new MuxUnavailableException($"Could not start the multiplexer: {ex.Message}", ex); }

        DateTime spawnedAt = DateTime.UtcNow;
        DateTime deadline = spawnedAt + SpawnTimeout;
        bool respawned = false;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryConnectExistingAsync(cancellationToken).ConfigureAwait(false) is { } client) return client;

            // At most one extra spawn per call, and only once no daemon at all is advertised: a
            // live descriptor means one is up (or coming up) and a second would only lose the lock.
            if (!respawned && DateTime.UtcNow - spawnedAt >= RespawnAfter && !MuxDiscovery.TryReadLiveDescriptor(_descriptorPath, out _))
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
