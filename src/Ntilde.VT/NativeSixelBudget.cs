namespace Ntilde.VT;

/// <summary>
/// Charges native decoded pixels, fixed decoder scratch and admitted placement identities.
/// A drained handle remains charged until the disposal owner confirms successful disposal.
/// This is a pixel-storage budget, not a bound on whole-process RSS or GPU caches.
/// </summary>
internal sealed class NativeSixelBudget
{
    public const int MaxPlacements = 256;
    public const long MaxBytes = 64L * 1024 * 1024;
    private readonly object _gate = new();
    private readonly int _maxPlacements;
    private readonly long _maxBytes;
    private long _bytes;
    private int _placements;

    public NativeSixelBudget(int maxPlacements = MaxPlacements, long maxBytes = MaxBytes)
    {
        if (maxPlacements is < 1 or > MaxPlacements) throw new ArgumentOutOfRangeException(nameof(maxPlacements));
        if (maxBytes is < 1 or > MaxBytes) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _maxPlacements = maxPlacements;
        _maxBytes = maxBytes;
    }

    /// <summary>One coherent observation; callers never combine counters from different admissions.</summary>
    public (int Placements, long Bytes) Usage { get { lock (_gate) return (_placements, _bytes); } }

    /// <summary>Reserves before decoder invocation, including scratch held by an undecoded handle.</summary>
    internal NativeSixelAllocation? TryReserve(int width, int height, int sourceChars)
    {
        if (width is < 1 or > SixelGeometry.DefaultMaxPixelDimension ||
            height is < 1 or > SixelGeometry.DefaultMaxPixelDimension ||
            sourceChars is < 1 or > SixelSourceBudget.DefaultMaxChars) return null;
        long pixels = (long)width * height;
        long live = pixels * sizeof(uint);
        long scratch = pixels * sizeof(ushort) + (SixelGeometry.DefaultMaxPaletteRegister + 1L) * sizeof(uint);
        lock (_gate)
        {
            if (_placements >= _maxPlacements || live + scratch > _maxBytes - _bytes) return null;
            _placements++;
            _bytes += live + scratch;
            return new NativeSixelAllocation(this, width, height, sourceChars, live, scratch);
        }
    }

    internal bool TryAdmitPlacement(NativeSixelAllocation allocation)
    {
        lock (_gate)
        {
            RequireOwner(allocation);
            if (allocation.Released) return false;
            if (!allocation.Claimed) { allocation.Claimed = true; return true; }
            if (_placements >= _maxPlacements) return false;
            allocation.Placements++;
            _placements++;
            return true;
        }
    }

    internal bool IsUnclaimed(NativeSixelAllocation allocation)
    {
        lock (_gate) { RequireOwner(allocation); return !allocation.Claimed && !allocation.Released; }
    }

    internal void CompleteDecode(NativeSixelAllocation allocation)
    {
        lock (_gate)
        {
            RequireOwner(allocation);
            if (allocation.Released || allocation.ScratchReleased) return;
            allocation.ScratchReleased = true;
            _bytes -= allocation.ScratchBytes;
        }
    }

    /// <summary>Only the owning lifetime/disposal boundary can release an allocation.</summary>
    internal void CompleteDisposal(NativeSixelAllocation allocation)
    {
        lock (_gate)
        {
            RequireOwner(allocation);
            if (allocation.Released) return;
            allocation.Released = true;
            _bytes -= allocation.PixelBytes + (allocation.ScratchReleased ? 0 : allocation.ScratchBytes);
            _placements -= allocation.Placements;
        }
    }

    private void RequireOwner(NativeSixelAllocation allocation)
    {
        if (!ReferenceEquals(allocation.Owner, this)) throw new InvalidOperationException("Foreign native SIXEL allocation.");
    }
}

/// <summary>Immutable decoder limits plus owner-only accounting; this is not a public release token.</summary>
internal sealed class NativeSixelAllocation
{
    internal NativeSixelBudget Owner { get; }
    internal long PixelBytes { get; }
    internal long ScratchBytes { get; }
    internal bool ScratchReleased { get; set; }
    internal bool Released { get; set; }
    internal bool Claimed { get; set; }
    internal int Placements { get; set; } = 1;
    public int Width { get; }
    public int Height { get; }
    public int SourceChars { get; }

    internal NativeSixelAllocation(NativeSixelBudget owner, int width, int height, int sourceChars, long pixels, long scratch)
    {
        Owner = owner;
        Width = width;
        Height = height;
        SourceChars = sourceChars;
        PixelBytes = pixels;
        ScratchBytes = scratch;
    }
}
