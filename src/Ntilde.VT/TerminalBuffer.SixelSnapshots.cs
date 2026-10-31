namespace Ntilde.VT;

public partial class TerminalBuffer
{
    private readonly object _sixelSnapshotGate = new();
    private readonly Dictionary<int, List<IDisposable>> _sixelSnapshotLeases = new();
    private int _sixelSnapshotRetry;

    private OwnedImageSession? PinSnapshotImageSources(List<RenderImageSnapshot> images, int sessionId)
    {
        var sources = images.Where(image => image.ImageHandle is IDeferredSixelImage { NeedsSource: true } && image.Source is not null)
            .Select(image => image.Source).OfType<SixelSource>().Distinct().ToArray();
        IDisposable? lease = null;
        if (sources.Length > 0 && !TryPinSnapshotSources(sources, images, out lease))
        {
            Interlocked.Exchange(ref _sixelSnapshotRetry, 1);
            if (!TryPinSnapshotSources(sources, images, out lease))
                throw new InvalidOperationException("The bounded SIXEL snapshot allowance is in use; retry after current captures finish.");
        }
        if (lease is null) return null;
        bool ownsSession = sessionId == 0;
        if (ownsSession) sessionId = BeginSnapshotSession();
        lock (_sixelSnapshotGate)
        {
            if (!_activeSnapshotSessions.ContainsKey(sessionId))
            {
                lease.Dispose();
                throw new InvalidOperationException("The image snapshot session has already ended.");
            }
            if (!_sixelSnapshotLeases.TryGetValue(sessionId, out var leases))
            {
                leases = [];
                _sixelSnapshotLeases.Add(sessionId, leases);
            }
            leases.Add(lease);
        }
        return ownsSession ? new OwnedImageSession(this, sessionId) : null;
    }

    private sealed class OwnedImageSession(TerminalBuffer buffer, int sessionId) : IDisposable
    {
        private TerminalBuffer? _buffer = buffer;
        public void Dispose() => Interlocked.Exchange(ref _buffer, null)?.EndSnapshotSession(sessionId);
    }

    private bool TryPinSnapshotSources(SixelSource[] sources, List<RenderImageSnapshot> images, out IDisposable? lease)
    {
        var acquired = new List<IDisposable>();
        var expired = new HashSet<SixelSource>();
        try
        {
            foreach (var group in sources.GroupBy(source => source.Owner))
            {
                if (!group.Key.TryPinSnapshot(group.ToArray(), out var pin, expired))
                {
                    foreach (var item in acquired) item.Dispose();
                    lease = null;
                    return false;
                }
                if (pin is not null) acquired.Add(pin);
            }
            // Retention may evict before its parser obtains the buffer write lock to prune.
            // Omit those undecoded placements at the same budget-locked decision as pinning.
            images.RemoveAll(image => image.ImageHandle is IDeferredSixelImage { NeedsSource: true }
                && image.Source is not null && expired.Contains(image.Source));
            lease = acquired.Count > 0 ? new ImageSourcePins(acquired, OnSnapshotSourcesReleased) : null;
            return true;
        }
        catch
        {
            foreach (var item in acquired) item.Dispose();
            throw;
        }
    }

    private void OnSnapshotSourcesReleased()
    {
        if (Interlocked.Exchange(ref _sixelSnapshotRetry, 0) == 0) return;
        try { Invalidate(); }
        catch (Exception ex) { TerminalLogger.Debug($"[SIXEL] Snapshot retry invalidation failed: {ex.Message}"); }
    }

    private sealed class ImageSourcePins(List<IDisposable> pins, Action released) : IDisposable
    {
        private List<IDisposable>? _pins = pins;
        private Action? _released = released;
        public void Dispose()
        {
            var items = Interlocked.Exchange(ref _pins, null);
            if (items is null) return;
            foreach (var item in items) item.Dispose();
            Interlocked.Exchange(ref _released, null)?.Invoke();
        }
    }
}
