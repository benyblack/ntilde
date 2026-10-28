using System.Collections.Generic;

namespace Ntilde.Pty;

/// <summary>
/// Decoded output a session produced before anything subscribed to it, replayed to the first subscriber.
/// </summary>
/// <remarks>
/// Sessions start reading in their constructors, so a shell's first prompt can arrive before the pane wires
/// its handler. Every caller subscribes within moments, but nothing bounded the wait: a session nobody ever
/// subscribed to kept every chunk it produced (#173). Past <see cref="MaxChars"/> the oldest chunks go, as
/// they would off the top of a screen; the newest chunk is always kept whole, however long it is.
/// Not thread-safe: the sessions call it under their output gate.
/// </remarks>
public sealed class PendingOutputReplay
{
    /// <summary>About two megabytes of UTF-16: tens of screens of startup output, far more than a prompt.</summary>
    public const int DefaultMaxChars = 1 << 20;

    private readonly Queue<string> _chunks = new();
    private readonly int _maxChars;
    private long _chars;

    public PendingOutputReplay(int maxChars = DefaultMaxChars)
    {
        _maxChars = maxChars;
    }

    public int MaxChars => _maxChars;

    /// <summary>Characters held right now.</summary>
    public long Chars => _chars;

    /// <summary>Whether older output has been dropped to stay within <see cref="MaxChars"/>.</summary>
    public bool DroppedOutput { get; private set; }

    public void Add(string text)
    {
        _chunks.Enqueue(text);
        _chars += text.Length;
        while (_chars > _maxChars && _chunks.Count > 1)
        {
            _chars -= _chunks.Dequeue().Length;
            DroppedOutput = true;
        }
    }

    /// <summary>The held chunks, oldest first.</summary>
    public string[] ToArray() => _chunks.ToArray();
}
