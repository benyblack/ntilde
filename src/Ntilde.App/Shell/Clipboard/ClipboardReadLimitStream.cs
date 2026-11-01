namespace Ntilde.Shell.Clipboard;

/// <summary>Bounds PNG encoder output before growing the managed buffer.</summary>
internal sealed class ClipboardReadLimitStream(int limit) : Stream
{
    private readonly MemoryStream _buffer = new();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _buffer.Length;
    public override long Position { get => Length; set => throw new NotSupportedException(); }
    public byte[] ToArray() => _buffer.ToArray();
    public override void Flush() => _buffer.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length > limit - Length) throw new IOException("Clipboard encoder output exceeds the read budget.");
        _buffer.Write(buffer);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) _buffer.Dispose();
        base.Dispose(disposing);
    }
}
