namespace Ntilde.Shell.Mux;

/// <summary>
/// One <see cref="MuxConnectionHost"/> per endpoint (Phase 4 spec §5). <see cref="Local"/> is the
/// existing local daemon host, unchanged. A remote host is built the first time its endpoint is asked
/// for, by the creator the window supplies (from the SSH profile), and lives until no pane of the window
/// uses its endpoint any more (<see cref="Release"/>) or the registry is disposed.
/// Thread-safe: the UI thread (spawns, closes) and the pool (orphan adoption, the attach picker) both ask.
/// </summary>
/// <remarks>
/// Final review F1: a remote host the window no longer needs is released, or it would reconnect forever -
/// its hidden ssh, the remote proxy and daemon alive, pings every 15 s, the daemon never idling out, a closed
/// connection re-established after sleep, the remembered secret kept. A release waits for the host's kills
/// (<see cref="MuxConnectionHost.WhenKillsDrained"/>), so a closed tab's shell still ends; then the host is
/// forgotten and disposed. Asking for that endpoint again in the meantime (<see cref="GetOrCreate"/>) takes the
/// host back and cancels the release, so nobody is ever handed a host that is about to dispose; once it is
/// forgotten, the next ask builds a new one. Disposing a host by any other path forgets it too.
/// </remarks>
internal sealed class MuxConnectionHosts : IDisposable
{
    private readonly Func<MuxEndpointId, MuxConnectionHost?> _createRemote;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly Dictionary<MuxEndpointId, MuxConnectionHost> _remotes = new(); // guarded by _gate
    private readonly List<MuxConnectionHost> _remoteOrder = new();                  // guarded by _gate; creation order
    // Guarded by _gate: released, kills not yet delivered. The value is that release's own token (see Release).
    private readonly Dictionary<MuxConnectionHost, object> _releasePending = new();
    private readonly List<Task> _releasing = new();                                 // guarded by _gate: released hosts being disposed
    private bool _disposed;                                                          // guarded by _gate

    /// <param name="local">The local daemon's host; owned from now on (disposed by <see cref="Dispose"/>).</param>
    /// <param name="createRemote">
    /// Builds the host for a remote endpoint, or returns null to decline (the profile is gone; one that does
    /// not persist remote sessions still gets a host, to deliver kills: codex C2). Called outside this
    /// registry's lock, on whichever thread asked, so it may do real work (look the profile up, work out
    /// how to launch) and may call <see cref="TryGet"/>; it must not connect, and must not ask
    /// <see cref="GetOrCreate"/> for the endpoint it is building. Two threads asking at once can both
    /// build one: the first to register wins, and the other host is disposed unused. A decline is not
    /// remembered: a profile can come back (an import, a backup restore), so the next ask asks again.
    /// </param>
    /// <param name="log">Where a remote host's failed dispose, and a release, is reported (it never stops the others, or the local one).</param>
    public MuxConnectionHosts(MuxConnectionHost local, Func<MuxEndpointId, MuxConnectionHost?> createRemote, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(createRemote);
        Local = local;
        _createRemote = createRemote;
        _log = log;
    }

    public MuxConnectionHost Local { get; }

    /// <summary>
    /// The host for <paramref name="id"/>, building a remote one on first use. Null when the creator declines, or once disposed.
    /// A host whose release is pending (<see cref="Release"/>) is taken back: the release is cancelled, and it stays.
    /// </summary>
    public MuxConnectionHost? GetOrCreate(MuxEndpointId id)
    {
        if (id.IsLocal) return Local;
        lock (_gate)
        {
            if (_disposed) return null;
            if (TryGetLiveLocked(id) is { } existing)
            {
                if (_releasePending.Remove(existing)) _log?.Invoke($"[Mux] {existing.Policy.DisplayName}: in use again; it stays open");
                return existing;
            }
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
            else if ((winner = TryGetLiveLocked(id)) is null)
            {
                _remotes.Add(id, created);
                _remoteOrder.Add(created);
                created.Closed += closed => Forget(id, closed);
                return created;
            }
            else
            {
                _releasePending.Remove(winner);
            }
        }

        // Another thread registered one first, or the registry was disposed meanwhile: this host was
        // never handed out to anyone, and never connected (creators do not connect).
        DisposeQuietly(created);
        return winner;
    }

    /// <summary>The host for <paramref name="id"/> if one exists; never builds one, and never takes back a host whose release is pending.</summary>
    public MuxConnectionHost? TryGet(MuxEndpointId id)
    {
        if (id.IsLocal) return Local;
        lock (_gate) return TryGetLiveLocked(id);
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
    /// <see cref="All"/>, each host with its endpoint: <see cref="MuxEndpointId.Local"/> first, then the remote ones in
    /// the order they were built. One consistent picture, taken under the lock.
    /// </summary>
    public IReadOnlyList<(MuxEndpointId Id, MuxConnectionHost Host)> AllByEndpoint
    {
        get
        {
            lock (_gate)
            {
                var all = new List<(MuxEndpointId, MuxConnectionHost)>(_remoteOrder.Count + 1) { (MuxEndpointId.Local, Local) };
                foreach (MuxConnectionHost host in _remoteOrder)
                {
                    // Registered and forgotten together (GetOrCreate, ForgetLocked): every host in the order has its entry.
                    foreach ((MuxEndpointId id, MuxConnectionHost registered) in _remotes)
                    {
                        if (ReferenceEquals(registered, host)) all.Add((id, host));
                    }
                }

                return all;
            }
        }
    }

    /// <summary>Whether <see cref="Dispose"/> has begun: from then on <see cref="GetOrCreate"/> answers null for every remote endpoint.</summary>
    public bool IsDisposed
    {
        get
        {
            lock (_gate) return _disposed;
        }
    }

    /// <summary>The remote endpoints that have a host now (one whose release is pending included).</summary>
    public IReadOnlyList<MuxEndpointId> RemoteEndpoints
    {
        get
        {
            lock (_gate) return [.. _remotes.Keys];
        }
    }

    /// <summary>
    /// Final review F1: no pane of the window uses <paramref name="id"/> any more, so its host is closed once its
    /// kills are delivered (<see cref="MuxConnectionHost.WhenKillsDrained"/>): at once when none is waiting,
    /// otherwise when the last one is answered, or dropped because the daemon stopped. A host that gave up
    /// reconnecting with kills still queued stays registered, idle, until the endpoint is used again
    /// (<see cref="GetOrCreate"/> cancels the release, and that connect delivers them) or the registry is
    /// disposed. A no-op for <see cref="MuxEndpointId.Local"/>, an endpoint with no host, a release already
    /// pending, and once disposed.
    /// </summary>
    /// <remarks>
    /// Each release has a token of its own, which its drained callback carries (codex residual round). The host hands
    /// its callbacks out under its lock but runs them outside it, so one can be late: before it runs, the host is taken
    /// back by <see cref="GetOrCreate"/> (its release cancelled) and released again, waiting for a new kill. Only the callback of
    /// the release still pending closes the host; a late one finds another token and does nothing. Before, it read
    /// the new release as its own and closed the host with the new kill queued.
    /// </remarks>
    public void Release(MuxEndpointId id)
    {
        if (id.IsLocal) return;
        MuxConnectionHost? host;
        object release = new();
        lock (_gate)
        {
            if (_disposed || TryGetLiveLocked(id) is not { } found || !_releasePending.TryAdd(found, release)) return;
            host = found;
        }

        _log?.Invoke($"[Mux] {host.Policy.DisplayName}: no pane uses this connection any more; closing it once its kills are delivered");
        host.WhenKillsDrained(() => OnKillsDrained(id, host, release));
    }

    /// <summary>
    /// The released host has nothing left to deliver: forgotten under the lock - from then on every ask builds a new
    /// one - and disposed off the caller's thread (its dispose flushes and ends its channel: seconds). Not when it was
    /// taken back meanwhile, released again since (<paramref name="release"/> is no longer the pending one), or the
    /// registry is closing (its own Dispose disposes the host).
    /// </summary>
    private void OnKillsDrained(MuxEndpointId id, MuxConnectionHost host, object release)
    {
        lock (_gate)
        {
            if (_disposed || !_releasePending.TryGetValue(host, out object? pending) || !ReferenceEquals(pending, release)) return;
            _releasePending.Remove(host);
            ForgetLocked(id, host);
            _releasing.RemoveAll(t => t.IsCompleted);
            _releasing.Add(Task.Run(() =>
            {
                _log?.Invoke($"[Mux] {host.Policy.DisplayName}: closing the connection no pane uses");
                DisposeQuietly(host);
            }));
        }
    }

    /// <summary>A registered host that is not closing; one that is (disposed by another path) is forgotten here.</summary>
    private MuxConnectionHost? TryGetLiveLocked(MuxEndpointId id)
    {
        if (!_remotes.TryGetValue(id, out MuxConnectionHost? host)) return null;
        if (!host.IsClosed) return host;
        ForgetLocked(id, host);
        return null;
    }

    /// <summary>A host's <see cref="MuxConnectionHost.Closed"/>: whoever disposed it, the next ask for its endpoint builds a new one.</summary>
    private void Forget(MuxEndpointId id, MuxConnectionHost host)
    {
        lock (_gate) ForgetLocked(id, host);
    }

    private void ForgetLocked(MuxEndpointId id, MuxConnectionHost host)
    {
        if (_remotes.TryGetValue(id, out MuxConnectionHost? registered) && ReferenceEquals(registered, host)) _remotes.Remove(id);
        _remoteOrder.Remove(host);
        _releasePending.Remove(host);
    }

    /// <summary>
    /// Disposes every host; each flushes its own tracked kills first (MuxConnectionHost.Dispose). The
    /// remote hosts go together, in parallel, so the wait is the slowest one's flush rather than their
    /// sum (each is bounded by its own KillFlushTimeout); hosts already being released are waited for with
    /// them. The local host goes last. A remote host that fails to dispose is logged and skipped: it must
    /// not stop the others, the local host, or the window's teardown this runs in. Waited inside Task.Run
    /// like each host's own flush: no UI sync context is captured.
    /// </summary>
    public void Dispose()
    {
        MuxConnectionHost[] remotes;
        Task[] releasing;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            remotes = _remoteOrder.ToArray();
            releasing = _releasing.ToArray();
            _releasePending.Clear();
        }

        try
        {
            Task[] closing = [.. remotes.Select(host => Task.Run(() => DisposeQuietly(host), CancellationToken.None)), .. releasing];
            if (closing.Length > 0)
            {
                Task.WaitAll(closing);
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
