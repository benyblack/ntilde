namespace Ntilde.VT;

/// <summary>Bounded headless raster owner; retains source/intent without native decoding or a rendering dependency.</summary>
internal sealed class NativeSixelSourceOwner : INativeSixelImage
{
    private int _disposed;
    public SixelSource Source { get; }
    public NativeSixelAllocation Allocation { get; }
    public bool IsMaterialized => false;
    public bool NeedsSource => System.Threading.Volatile.Read(ref _disposed) == 0;
    public NativeSixelDecodeState DecodeState => NeedsSource ? NativeSixelDecodeState.Pending : NativeSixelDecodeState.Disposed;

    /// <summary>
    /// Creates a source-only owner with conservative pixel credit; no decoder can consume its unused scratch.
    /// </summary>
    internal NativeSixelSourceOwner(SixelSource source, NativeSixelAllocation allocation)
    {
        Source = source;
        Allocation = allocation;
        // This owner can never decode. Keep conservative pixel credit, without unused scratch.
        allocation.Owner.CompleteDecode(allocation);
    }

    /// <summary>The same snapshot-gated outside-lock disposal boundary releases source and conservative pixel charge.</summary>
    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Source.Dispose();
        Allocation.Owner.CompleteDisposal(Allocation);
    }
}
