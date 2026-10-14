using System.Text;

namespace Ntilde.Platform.Ssh.Exec;

/// <summary>
/// The last <c>capacityBytes</c> of a byte stream (an exec channel's stderr, Phase 4 spec §8.2), as a
/// ring: memory stays bounded however much ssh <c>-v</c> prints. Safe to append from one thread while
/// another reads it.
/// </summary>
internal sealed class BoundedTail
{
    private readonly byte[] _ring;
    private readonly object _gate = new();
    private int _next;        // where the next byte goes
    private int _count;       // bytes held, at most _ring.Length
    private bool _truncated;  // bytes were dropped from the front

    public BoundedTail(int capacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        _ring = new byte[capacityBytes];
    }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            if (bytes.Length >= _ring.Length)
            {
                _truncated |= _count > 0 || bytes.Length > _ring.Length;
                bytes[^_ring.Length..].CopyTo(_ring);
                _next = 0;
                _count = _ring.Length;
                return;
            }

            int first = Math.Min(bytes.Length, _ring.Length - _next);
            bytes[..first].CopyTo(_ring.AsSpan(_next));
            bytes[first..].CopyTo(_ring);
            _next = (_next + bytes.Length) % _ring.Length;
            int total = _count + bytes.Length;
            _truncated |= total > _ring.Length;
            _count = Math.Min(total, _ring.Length);
        }
    }

    /// <summary>
    /// The held bytes as UTF-8. Invalid sequences become U+FFFD; after a cut, the partial character at
    /// the front is dropped rather than shown as one.
    /// </summary>
    public override string ToString()
    {
        byte[] linear;
        bool truncated;
        lock (_gate)
        {
            linear = new byte[_count];
            int start = (_next - _count + _ring.Length) % _ring.Length;
            int first = Math.Min(_count, _ring.Length - start);
            _ring.AsSpan(start, first).CopyTo(linear);
            _ring.AsSpan(0, _count - first).CopyTo(linear.AsSpan(first));
            truncated = _truncated;
        }

        int skip = 0;
        if (truncated)
        {
            while (skip < 3 && skip < linear.Length && (linear[skip] & 0xC0) == 0x80) skip++;
        }

        return Encoding.UTF8.GetString(linear, skip, linear.Length - skip);
    }
}
