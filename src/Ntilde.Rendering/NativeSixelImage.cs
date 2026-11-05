using Ntilde.VT;
using SkiaSharp;

namespace Ntilde.Rendering;

/// <summary>Owns the predecode reservation through deferred materialization, snapshots and actual disposal.</summary>
public sealed class NativeSixelImage : INativeSixelImage
{
    private readonly object _gate = new();
    private SKBitmap? _bitmap;
    private bool _attempted;
    private bool _disposed;
    public SixelSource Source { get; }
    internal NativeSixelAllocation Allocation { get; }
    NativeSixelAllocation INativeSixelImage.Allocation => Allocation;
    public bool NeedsSource => !Volatile.Read(ref _attempted) && !Volatile.Read(ref _disposed);
    public bool IsMaterialized { get { lock (_gate) return _bitmap is not null; } }

    internal NativeSixelImage(SixelSource source, NativeSixelAllocation allocation)
    {
        Source = source;
        Allocation = allocation;
    }

    public SKBitmap? GetBitmap(ref int remainingDecodes, out bool pending)
    {
        lock (_gate)
        {
            pending = false;
            if (_disposed || _attempted) return _bitmap;
            if (remainingDecodes <= 0) { pending = true; return null; }
            remainingDecodes--;
            Volatile.Write(ref _attempted, true);
            try
            {
                if (Source.DecodeData is { } data) _bitmap = NativeSixelDecoder.Decode(data, Allocation);
            }
            catch (Exception ex) { TerminalLogger.Debug($"[SIXEL] Native deferred decode failed: {ex.Message}"); }
            finally { Allocation.Owner.CompleteDecode(Allocation); }
            return _bitmap;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            // A failed disposal keeps the reservation and remains retryable. Never release
            // at drain/handoff: the native bitmap can still be used by an older snapshot.
            _bitmap?.Dispose();
            _bitmap = null;
            Source.Dispose();
            Volatile.Write(ref _disposed, true);
        }
        Allocation.Owner.CompleteDisposal(Allocation);
    }
}
