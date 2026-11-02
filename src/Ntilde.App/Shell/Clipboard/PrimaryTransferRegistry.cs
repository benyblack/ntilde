namespace Ntilde.Shell.Clipboard;

/// <summary>Bounds active payloads and releases each foreign window subscription with its last stream.</summary>
internal sealed class PrimaryTransferRegistry(Action<uint, bool> subscribe, Func<uint, bool> isLocal, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly List<PrimaryOutgoingTransfer> _streams = [];
    private readonly HashSet<uint> _subscriptions = [];
    internal static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(10);
    internal int SubscriptionCount => _subscriptions.Count;

    public bool TryStart(uint window, uint property, uint target, byte[] bytes)
    {
        Expire();
        if (_streams.Count >= 16 || _streams.Sum(s => (long)s.Payload.Length) + bytes.Length > 2L * VT.KittyClipboardPacket.MaxDataBytes ||
            Find(window, property) != null) return false;
        if (!isLocal(window) && _subscriptions.Add(window)) subscribe(window, true);
        _streams.Add(new(window, property, target, bytes, _clock.GetTimestamp()));
        return true;
    }

    public PrimaryOutgoingTransfer? Find(uint window, uint property) => _streams.FirstOrDefault(s => s.Window == window && s.Property == property);

    public void Retire(PrimaryOutgoingTransfer stream)
    {
        if (!_streams.Remove(stream)) return;
        if (_streams.Any(s => s.Window == stream.Window) || !_subscriptions.Remove(stream.Window)) return;
        subscribe(stream.Window, false);
    }

    public void Expire()
    {
        foreach (var stream in _streams.Where(s => _clock.GetElapsedTime(s.Started) >= Lifetime).ToArray()) Retire(stream);
    }

    public void Destroyed(uint window)
    {
        // The server already removed this connection's event mask with the window.
        _subscriptions.Remove(window);
        _streams.RemoveAll(s => s.Window == window);
    }
}

internal sealed class PrimaryOutgoingTransfer(uint window, uint property, uint target, byte[] payload, long started)
{
    public uint Window => window;
    public uint Property => property;
    public uint Target => target;
    public byte[] Payload => payload;
    public long Started => started;
    public int Offset;
}

internal sealed class PrimarySelectionOwnership
{
    public Dictionary<uint, byte[]> Values { get; private set; } = [];
    public uint Timestamp { get; private set; }
    public void Publish(Dictionary<uint, byte[]> values, uint timestamp) { Values = values; Timestamp = timestamp; }
    public void Clear() => Values = [];
    public bool AcceptsRequest(uint timestamp) => Values.Count > 0 &&
        (timestamp == 0 || unchecked((int)(timestamp - Timestamp)) >= 0);
    public void SelectionCleared(uint currentOwner, uint ownWindow)
    {
        // Owner queries and publication are serialized on the XCB worker. A queued
        // clear from an older ownership interval must not erase a successful reacquisition.
        if (currentOwner != ownWindow) Clear();
    }
}
