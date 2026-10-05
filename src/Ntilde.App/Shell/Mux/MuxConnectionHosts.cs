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
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly Dictionary<MuxEndpointId, MuxConnectionHost> _remotes = new(); // guarded by _gate
    private readonly List<MuxConnectionHost> _remoteOrder = new();                  // guarded by _gate; creation order
    private bool _disposed;                                                          // guarded by _gate

    /// <param name="local">The local daemon's host; owned from now on (disposed by <see cref="Dispose"/>).</param>
    /// <param name="createRemote">
    /// Builds the host for a remote endpoint, or returns null to decline (the profile is gone, or it does
    /// not persist remote sessions). Called outside this registry's lock, on whichever thread asked, so it
    /// may do real work (look the profile up, work out how to launch) and may call <see cref="TryGet"/>; it
    /// must not connect, and must not ask <see cref="GetOrCreate"/> for the endpoint it is building. Two
    /// threads asking at once can both build one: the first to register wins, and the other host is
    /// disposed unused. A decline is not remembered: the profile or its flag can change, so the next ask
    /// asks again.
    /// </param>
    /// <param name="log">Where a remote host's failed dispose is reported (it never stops the others, or the local one).</param>
    public MuxConnectionHosts(MuxConnectionHost local, Func<MuxEndpointId, MuxConnectionHost?> createRemote, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(createRemote);
        Local = local;
        _createRemote = createRemote;
        _log = log;
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
        }

        // Outside the lock: a creator that marshals to the UI thread must not deadlock against a
        // UI-thread TryGet waiting on this lock.
        MuxConnectionHost? created = _createRemote(id);
        if (created is null) return null;

        MuxConnectionHost? winner;
        lock (_gate)
        {
            if (_disposed)
            {
                winner = null;
            }
            else if (!_remotes.TryGetValue(id, out winner))
            {
                _remotes.Add(id, created);
                _remoteOrder.Add(created);
                return created;
            }
        }

        // Another thread registered one first, or the registry was disposed meanwhile: this host was
        // never handed out to anyone, and never connected (creators do not connect).
        DisposeQuietly(created);
        return winner;
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
    /// sum (each is bounded by its own KillFlushTimeout); the local host goes last. A remote host that
    /// fails to dispose is logged and skipped: it must not stop the others, the local host, or the
    /// window's teardown this runs in. Waited inside Task.Run like each host's own flush: no UI sync
    /// context is captured.
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
                Task.WaitAll(remotes.Select(host => Task.Run(() => DisposeQuietly(host), CancellationToken.None)).ToArray());
            }
        }
        finally
        {
            Local.Dispose();
        }
    }

    private void DisposeQuietly(MuxConnectionHost host)
    {
        try
        {
            host.Dispose();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Mux] closing the connection to {host.Policy.DisplayName} ({host.Endpoint}) failed: {ex.Message}");
        }
    }
}
