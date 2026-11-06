namespace Ntilde.VT;

/// <summary>
/// Bounds the encoded Kitty and iTerm2 image bytes one pane or session keeps so snapshots can carry
/// them (#838), the counterpart of <see cref="SixelSourceBudget"/>. Retired sources (whose image is
/// gone) are evicted before the oldest live ones. An evicted source keeps its image on screen; only
/// snapshots lose it. Every call happens under the owning buffer's write lock; the gate below only
/// keeps the counters consistent for readers.
/// </summary>
public sealed class ImageSourceBudget
{
    /// <summary>Encoded bytes retained per pane or session.</summary>
    public const int DefaultMaxBytes = 32 * 1024 * 1024;

    /// <summary>Sources retained per pane or session (Kitty assets on both screens plus iTerm2 images).</summary>
    public const int DefaultMaxSources = 512;

    private readonly object _gate = new();
    private readonly LinkedList<ImageSource> _sources = new();
    private readonly LinkedList<ImageSource> _retiredSources = new();
    private readonly int _maxBytes;
    private readonly int _maxSources;
    private long _retainedBytes;

    public ImageSourceBudget(int maxBytes = DefaultMaxBytes, int maxSources = DefaultMaxSources)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSources);
        _maxBytes = maxBytes;
        _maxSources = maxSources;
    }

    public long RetainedBytes { get { lock (_gate) return _retainedBytes; } }
    public int Count { get { lock (_gate) return _sources.Count + _retiredSources.Count; } }

    /// <summary>
    /// Keeps <paramref name="data"/>, evicting retired then oldest sources to make room. Data larger
    /// than the whole budget is not kept: the source exists, with no data.
    /// </summary>
    public ImageSource Retain(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var source = new ImageSource(this);
        if (data.Length > _maxBytes) return source;
        lock (_gate)
        {
            while ((_sources.Count + _retiredSources.Count >= _maxSources || _retainedBytes > _maxBytes - data.Length)
                && (_retiredSources.First ?? _sources.First) is { } oldest)
                Release(oldest.Value);
            source.Data = data;
            source.Node = _sources.AddLast(source);
            _retainedBytes += data.Length;
        }
        return source;
    }

    internal void Retire(ImageSource source)
    {
        lock (_gate)
        {
            if (source.Node is not { } node || node.List != _sources) return;
            _sources.Remove(node);
            _retiredSources.AddLast(node);
        }
    }

    internal void Release(ImageSource source)
    {
        lock (_gate)
        {
            if (source.Node is { } node)
            {
                _retainedBytes -= source.Data?.Length ?? 0;
                node.List?.Remove(node);
                source.Node = null;
            }
            source.Data = null;
        }
    }
}

/// <summary>Encoded bytes an image was sent as, kept for snapshots; null once evicted.</summary>
public sealed class ImageSource
{
    private readonly ImageSourceBudget _owner;
    private byte[]? _data;
    internal ImageSource(ImageSourceBudget owner) => _owner = owner;
    internal ImageSourceBudget Owner => _owner;
    public byte[]? Data { get => Volatile.Read(ref _data); internal set => Volatile.Write(ref _data, value); }
    internal LinkedListNode<ImageSource>? Node { get; set; }

    /// <summary>The image is gone from the screen; the bytes go first when room is needed.</summary>
    internal void Retire() => _owner.Retire(this);

    /// <summary>Drops the bytes now.</summary>
    internal void Release() => _owner.Release(this);
}

/// <summary>How a Kitty image's retained bytes were sent, so they can be decoded again.</summary>
/// <param name="Format">Kitty <c>f=</c>: 24 or 32 for raw pixels, otherwise a container.</param>
/// <param name="Compressed">Kitty <c>o=z</c>: the bytes are a zlib stream.</param>
/// <param name="RawWidth">Kitty <c>s=</c> for raw pixels, else 0.</param>
/// <param name="RawHeight">Kitty <c>v=</c> for raw pixels, else 0.</param>
internal sealed record KittyImageEncoding(int Format, bool Compressed, int RawWidth, int RawHeight);
