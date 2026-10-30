using Ntilde.VT;
using SkiaSharp;

namespace Ntilde.Rendering;

/// <summary>
/// Retains bounded SIXEL source for a pane that may never be drawn. The first render materializes
/// its bitmap. The terminal buffer's snapshot-session gate owns this handle and its disposal.
/// </summary>
public sealed class DeferredSixelImage(string source, int maxPixelDimension) : IDisposable
{
    private readonly object _gate = new();
    private string? _source = source;
    private SKBitmap? _bitmap;
    private bool _disposed;

    internal bool IsDecoded { get { lock (_gate) return _bitmap is not null; } }

    /// <summary>Decodes once on demand; a disposed or undecodable handle returns null.</summary>
    /// <remarks>The renderer holds the owning buffer's snapshot session while using the returned bitmap.</remarks>
    public SKBitmap? GetBitmap()
    {
        int budget = int.MaxValue;
        return GetBitmap(ref budget, out _);
    }

    /// <summary>Consumes one decode from the frame budget; already decoded images are free.</summary>
    public SKBitmap? GetBitmap(ref int remainingDecodes, out bool pending)
    {
        lock (_gate)
        {
            pending = false;
            if (_disposed) return null;
            if (_source is not { } data) return _bitmap;
            if (remainingDecodes <= 0) { pending = true; return null; }
            remainingDecodes--;
            _source = null;
            try { _bitmap = new SixelDecoder { MaxPixelDimension = maxPixelDimension }.Decode(data); }
            catch (Exception ex) { TerminalLogger.Debug($"[SIXEL] Deferred decode failed: {ex.Message}"); }
            return _bitmap;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _source = null;
            _bitmap?.Dispose();
            _bitmap = null;
        }
    }
}
