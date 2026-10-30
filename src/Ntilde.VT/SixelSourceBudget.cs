namespace Ntilde.VT;

/// <summary>Bounds retained SIXEL text across one pane/session, evicting retired sources before oldest live sources.</summary>
public sealed class SixelSourceBudget
{
    public const int DefaultMaxChars = 2 * 1024 * 1024;
    public const int DefaultMaxSources = 256;
    private readonly object _gate = new();
    private readonly LinkedList<SixelSource> _sources = new();
    private readonly LinkedList<SixelSource> _retiredSources = new();
    private readonly int _maxChars;
    private readonly int _maxSources;
    private int _retainedChars;
    private long _evictionVersion;
    internal long EvictionVersion => Volatile.Read(ref _evictionVersion);

    public SixelSourceBudget(int maxChars = DefaultMaxChars, int maxSources = DefaultMaxSources)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChars);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSources);
        _maxChars = maxChars;
        _maxSources = maxSources;
    }

    public int RetainedChars { get { lock (_gate) return _retainedChars; } }
    public int Count { get { lock (_gate) return _sources.Count + _retiredSources.Count; } }

    public SixelSource Retain(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_gate)
        {
            var source = new SixelSource(this);
            if (data.Length > _maxChars) return source;
            while ((_sources.Count + _retiredSources.Count >= _maxSources || _retainedChars > _maxChars - data.Length)
                && (_retiredSources.First ?? _sources.First) is { } oldest)
                Release(oldest.Value);
            source.Data = data;
            source.Node = _sources.AddLast(source);
            _retainedChars += data.Length;
            return source;
        }
    }

    internal void Retire(SixelSource source)
    {
        lock (_gate)
        {
            if (source.Node is not { } node || node.List != _sources) return;
            _sources.Remove(node);
            _retiredSources.AddLast(node);
        }
    }

    internal void Release(SixelSource source)
    {
        lock (_gate)
        {
            if (source.Node is not { } node) return;
            _retainedChars -= source.Data?.Length ?? 0;
            source.Data = null;
            node.List?.Remove(node);
            source.Node = null;
            Interlocked.Increment(ref _evictionVersion);
        }
    }
}

/// <summary>An evictable source shared by placement metadata and deferred decoding.</summary>
public sealed class SixelSource : IDisposable
{
    private readonly SixelSourceBudget _owner;
    private string? _data;
    internal SixelSource(SixelSourceBudget owner) => _owner = owner;
    public string? Data { get => Volatile.Read(ref _data); internal set => Volatile.Write(ref _data, value); }
    internal LinkedListNode<SixelSource>? Node { get; set; }
    internal void Retire() => _owner.Retire(this);
    public void Dispose() => _owner.Release(this);
}
