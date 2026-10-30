using Ntilde.VT;
using SkiaSharp;

namespace Ntilde.Rendering;

/// <summary>
/// Retains bounded SIXEL source for a pane that may never be drawn. The first render materializes
/// its bitmap. The terminal buffer's snapshot-session gate owns this handle and its disposal.
/// </summary>
public sealed class DeferredSixelImage(SixelSource source, int maxPixelDimension) : IDisposable, IDeferredSixelImage
{
    private readonly object _gate = new();
    private bool _decodeAttempted;
    private SKBitmap? _bitmap;
    private bool _disposed;

    public bool IsMaterialized { get { lock (_gate) return _bitmap is not null; } }
    internal bool IsDecoded => IsMaterialized;

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
            if (_decodeAttempted || source.Data is not { } data) return _bitmap;
            if (remainingDecodes <= 0) { pending = true; return null; }
            remainingDecodes--;
            _decodeAttempted = true;
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
            source.Dispose();
            _bitmap?.Dispose();
            _bitmap = null;
        }
    }
}
