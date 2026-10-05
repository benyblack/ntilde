namespace Ntilde.Shell.Mux;

/// <summary>
/// One <see cref="MuxConnectionHost"/> per endpoint (Phase 4 spec §5). <see cref="Local"/> is the
/// existing local daemon host, unchanged. A remote host is built the first time its endpoint is asked
/// for, by the creator the window supplies (from the SSH profile), and lives until <see cref="Dispose"/>.
/// Thread-safe: the UI thread (spawns, closes) and the pool (orphan adoption, the attach picker) both ask.
/// </summary>
internal sealed class MuxConnectionHosts : IDisposable
{
    private readonly Func<MuxEndpointId, MuxConnectionHost?> _createRemote;
    private readonly object _gate = new();
    private readonly Dictionary<MuxEndpointId, MuxConnectionHost> _remotes = new(); // guarded by _gate
    private readonly List<MuxConnectionHost> _remoteOrder = new();                  // guarded by _gate; creation order
    private bool _disposed;                                                          // guarded by _gate

    /// <param name="local">The local daemon's host; owned from now on (disposed by <see cref="Dispose"/>).</param>
    /// <param name="createRemote">
    /// Builds the host for a remote endpoint, or returns null to decline (the profile is gone, or it does
    /// not persist remote sessions). Called under this registry's lock, at most once per endpoint it
    /// accepts, so it must only build the host - never connect, and never call back into this registry.
    /// A decline is not remembered: the profile or its flag can change, so the next ask asks again.
    /// </param>
    public MuxConnectionHosts(MuxConnectionHost local, Func<MuxEndpointId, MuxConnectionHost?> createRemote)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(createRemote);
        Local = local;
        _createRemote = createRemote;
    }

    public MuxConnectionHost Local { get; }

    /// <summary>The host for <paramref name="id"/>, building a remote one on first use. Null when the creator declines, or once disposed.</summary>
    public MuxConnectionHost? GetOrCreate(MuxEndpointId id)
    {
        if (id.IsLocal) return Local;
        lock (_gate)
        {
            if (_disposed) return null;
            if (_remotes.TryGetValue(id, out MuxConnectionHost? existing)) return existing;
            MuxConnectionHost? created = _createRemote(id);
            if (created is null) return null;
            _remotes.Add(id, created);
            _remoteOrder.Add(created);
            return created;
        }
    }

    /// <summary>The host for <paramref name="id"/> if one exists; never builds one.</summary>
    public MuxConnectionHost? TryGet(MuxEndpointId id)
    {
        if (id.IsLocal) return Local;
        lock (_gate) return _remotes.GetValueOrDefault(id);
    }

    /// <summary>Every host: <see cref="Local"/> first, then the remote ones in the order they were built.</summary>
    public IReadOnlyList<MuxConnectionHost> All
    {
        get
        {
            lock (_gate) return [Local, .. _remoteOrder];
        }
    }

    /// <summary>
    /// Disposes every host; each flushes its own tracked kills first (MuxConnectionHost.Dispose). The
    /// remote hosts go together, in parallel, so the wait is the slowest one's flush rather than their
    /// sum (each is bounded by its own KillFlushTimeout); the local host goes last. Waited inside
    /// Task.Run like each host's own flush: no UI sync context is captured.
    /// </summary>
    public void Dispose()
    {
        MuxConnectionHost[] remotes;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            remotes = _remoteOrder.ToArray();
        }

        try
        {
            if (remotes.Length > 0)
            {
                Task.WaitAll(remotes.Select(host => Task.Run(host.Dispose, CancellationToken.None)).ToArray());
            }
        }
        finally
        {
            Local.Dispose();
        }
    }
}
