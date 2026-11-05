using Ntilde.VT;
using SkiaSharp;

namespace Ntilde.Rendering;

/// <summary>Owns the predecode reservation through deferred materialization, snapshots and actual disposal.</summary>
public sealed class NativeSixelImage : INativeSixelImage
{
    private readonly object _gate = new();
    private SKBitmap? _bitmap;
    private int _state;
    public SixelSource Source { get; }
    internal NativeSixelAllocation Allocation { get; }
    NativeSixelAllocation INativeSixelImage.Allocation => Allocation;
    NativeSixelDecodeState INativeSixelImage.DecodeState => DecodeState;
    private NativeSixelDecodeState DecodeState => (NativeSixelDecodeState)Volatile.Read(ref _state);
    public bool NeedsSource => DecodeState == NativeSixelDecodeState.Pending;
    public bool IsMaterialized => DecodeState == NativeSixelDecodeState.Materialized;

    internal NativeSixelImage(SixelSource source, NativeSixelAllocation allocation)
    {
        Source = source;
        Allocation = allocation;
    }

    /// <summary>Materializes once within the frame decode allowance; callers hold the owning buffer's snapshot session.</summary>
    public SKBitmap? GetBitmap(ref int remainingDecodes, out bool pending)
    {
        SKBitmap? result;
        Exception? failure = null;
        lock (_gate)
        {
            pending = false;
            if (DecodeState != NativeSixelDecodeState.Pending) return _bitmap;
            if (remainingDecodes <= 0) { pending = true; return null; }
            remainingDecodes--;
            // Capture the immutable input before publishing in-flight state. Retention
            // eviction cannot invalidate this local reference or an existing snapshot pin.
            string? data = Source.DecodeData;
            Volatile.Write(ref _state, (int)NativeSixelDecodeState.Decoding);
            try
            {
                if (data is not null) _bitmap = NativeSixelDecoder.Decode(data, Allocation);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                Allocation.Owner.CompleteDecode(Allocation);
                Volatile.Write(ref _state, (int)(_bitmap is null ? NativeSixelDecodeState.Failed : NativeSixelDecodeState.Materialized));
            }
            result = _bitmap;
        }
        if (failure is not null) TerminalLogger.Debug($"[SIXEL] Native deferred decode failed: {failure.Message}");
        return result;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (DecodeState == NativeSixelDecodeState.Disposed) return;
            // A failed disposal keeps the reservation and remains retryable. Never release
            // at drain/handoff: the native bitmap can still be used by an older snapshot.
            _bitmap?.Dispose();
            _bitmap = null;
            Volatile.Write(ref _state, (int)NativeSixelDecodeState.Failed);
            Source.Dispose();
            Volatile.Write(ref _state, (int)NativeSixelDecodeState.Disposed);
        }
        Allocation.Owner.CompleteDisposal(Allocation);
    }
}
