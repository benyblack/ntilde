using Ntilde.VT;

namespace Ntilde.Shell.Clipboard;

/// <summary>Owned bytes frozen before OpenReadAsync completes; no subsequent native reads.</summary>
internal sealed class PrimarySelectionSnapshot : IKittyClipboardSnapshot
{
    private Dictionary<string, byte[]>? _values;
    public IReadOnlyList<string> Types { get; }

    private PrimarySelectionSnapshot(IReadOnlyList<string> types, Dictionary<string, byte[]> values)
    {
        Types = types.ToArray(); _values = values;
    }

    internal static PrimarySelectionSnapshot Capture(IReadOnlyDictionary<string, uint> formats, IReadOnlyList<string>? requestedTypes,
        Func<uint, int, byte[]> read, int maxBytes = KittyClipboardPacket.MaxDataBytes)
    {
        var values = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (requestedTypes?.Count == 0) return new(formats.Keys.ToArray(), values); // MIME listing: no content access
        var selected = requestedTypes == null ? formats.Keys : requestedTypes.Distinct(StringComparer.Ordinal);
        long retained = 0;
        foreach (string mime in selected)
        {
            if (!formats.TryGetValue(mime, out uint atom)) continue;
            byte[] bytes = read(atom, maxBytes - checked((int)retained));
            retained += bytes.Length;
            if (retained > maxBytes) throw new IOException("PRIMARY snapshot exceeds the aggregate read budget.");
            values[mime] = bytes.ToArray(); // Providers cannot later mutate the snapshot's buffers.
        }
        return new(formats.Keys.ToArray(), values);
    }

    public Task<byte[]?> ReadAsync(string mime, int maxBytes = KittyClipboardPacket.MaxDataBytes)
    {
        if (_values == null || !_values.TryGetValue(mime, out byte[]? bytes)) return Task.FromResult<byte[]?>(null);
        if (bytes.Length > maxBytes) throw new IOException("PRIMARY item exceeds the remaining read budget.");
        return Task.FromResult<byte[]?>(bytes.ToArray());
    }

    public void Dispose() { _values?.Clear(); _values = null; }
}
