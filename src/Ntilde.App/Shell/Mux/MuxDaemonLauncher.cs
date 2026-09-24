using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Transport;

namespace Ntilde.Shell.Mux;

internal sealed class MuxUnavailableException : Exception
{
    public MuxUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Connect to the running daemon, or start one and connect (spec §6).</summary>
internal sealed class MuxDaemonLauncher
{
    private readonly string _descriptorPath;
    private readonly IMuxDaemonSpawner _spawner;
    private readonly MuxClientOptions _clientOptions;
    private readonly Action<string>? _log;

    public MuxDaemonLauncher(string descriptorPath, IMuxDaemonSpawner spawner, MuxClientOptions? clientOptions = null, Action<string>? log = null)
    {
        _descriptorPath = descriptorPath;
        _spawner = spawner;
        _clientOptions = clientOptions ?? new MuxClientOptions { Log = log };
        _log = log;
    }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan SpawnTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public static MuxDaemonLauncher CreateDefault(Action<string>? log) =>
        new(MuxDiscovery.GetDescriptorPath(), ProcessMuxDaemonSpawner.CreateDefault(), log: log);

    public async Task<MuxClient?> TryConnectExistingAsync(CancellationToken cancellationToken)
    {
        if (!MuxDiscovery.TryReadLiveDescriptor(_descriptorPath, out MuxEndpointDescriptor? d)) return null;
        try
        {
            Stream stream = await Task.Run(() => MuxEndpointConnector.Connect(d.Endpoint, ConnectTimeout), cancellationToken).ConfigureAwait(false);
            return await MuxClient.ConnectAsync(stream, _clientOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or MuxProtocolException or System.Net.Sockets.SocketException)
        {
            _log?.Invoke($"[Mux] connecting to {d.Endpoint} (pid {d.Pid}) failed: {ex.Message}");
            return null;
        }
    }

    public async Task<MuxClient> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (await TryConnectExistingAsync(cancellationToken).ConfigureAwait(false) is { } existing) return existing;

        _log?.Invoke("[Mux] starting the multiplexer daemon");
        try { _spawner.Spawn(); }
        catch (Exception ex) { throw new MuxUnavailableException($"Could not start the multiplexer: {ex.Message}", ex); }

        DateTime deadline = DateTime.UtcNow + SpawnTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryConnectExistingAsync(cancellationToken).ConfigureAwait(false) is { } client) return client;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        throw new MuxUnavailableException($"The multiplexer did not come up within {SpawnTimeout.TotalSeconds:0} s.");
    }
}
