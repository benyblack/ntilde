using Ntilde.VT;

namespace Ntilde.VT.Tests;

public sealed class NativeSixelAdmissionTests
{
    private const string Wire = "\x1bPq~\x1b\\";

    [Fact]
    public void Refusal_precedes_decoder_and_cursor_effects_and_cannot_fall_back_to_legacy()
    {
        var budget = new NativeSixelBudget(maxPlacements: 1);
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 5, budget);
        var decoder = new ProbeDecoder(budget, buffer);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = decoder };
        parser.Process(Wire);
        Assert.Equal(1, decoder.Calls);
        Assert.Equal(0, decoder.LegacyCalls);
        Assert.Equal((1, 6L * 6 + 16384), decoder.AtEntry);
        int row = buffer.CursorRow, col = buffer.CursorCol;
        parser.Process(Wire);
        Assert.Equal(1, decoder.Calls);
        Assert.Equal((row, col), (buffer.CursorRow, buffer.CursorCol));
        Assert.Single(buffer.Images);
        var unsupported = TerminalBuffer.CreateNativeSixelTestBuffer(10, 5, new NativeSixelBudget());
        var legacy = new LegacyOnlyDecoder();
        new AnsiParser(unsupported, forceConPtyFiltering: false) { ImageDecoder = legacy }.Process(Wire);
        Assert.Equal(0, legacy.Calls);
        Assert.Empty(unsupported.Images);
        Assert.Equal((0, 0), (unsupported.CursorRow, unsupported.CursorCol));
    }

    [Fact]
    public void Retired_and_drained_handles_stay_charged_until_successful_outside_lock_disposal()
    {
        var budget = new NativeSixelBudget(maxPlacements: 1);
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 5, budget);
        var decoder = new ProbeDecoder(budget, buffer);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = decoder };
        parser.Process(Wire);
        var handle = Assert.IsType<ProbeImage>(Assert.Single(buffer.Images).ImageHandle);
        int session = buffer.BeginSnapshotSession();
        buffer.Clear(true);
        var retired = new List<object>();
        buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Empty(retired);
        parser.Process(Wire);
        Assert.Equal(1, decoder.Calls);
        buffer.EndSnapshotSession(session);
        buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Same(handle, Assert.Single(retired));
        Assert.Equal(1, budget.Usage.Placements);
        handle.FailDisposal = true;
        Assert.Throws<InvalidOperationException>(handle.Dispose);
        Assert.Equal(1, budget.Usage.Placements);
        parser.Process(Wire);
        Assert.Equal(1, decoder.Calls);
        handle.FailDisposal = false;
        handle.Dispose();
        Assert.Equal((0, 0L), budget.Usage);
        parser.Process(Wire);
        Assert.Equal(2, decoder.Calls);
    }

    [Fact]
    public void Alias_placements_cannot_exceed_the_limit_or_revive_after_handoff_or_cross_buffers()
    {
        var budget = new NativeSixelBudget(maxPlacements: 2);
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 5, budget);
        var decoder = new ProbeDecoder(budget, buffer);
        new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = decoder }.Process(Wire);
        var handle = Assert.IsType<ProbeImage>(Assert.Single(buffer.Images).ImageHandle);
        buffer.AddImage(new TerminalImage(handle, 5, 0, 1, 1));
        long bytes = budget.Usage.Bytes;
        Assert.Equal((2, bytes), budget.Usage);
        Assert.Throws<InvalidOperationException>(() => buffer.AddImage(new TerminalImage(handle, 7, 0, 1, 1)));
        Assert.Equal(2, buffer.Images.Count);
        var other = TerminalBuffer.CreateNativeSixelTestBuffer(10, 5, new NativeSixelBudget());
        Assert.Throws<InvalidOperationException>(() => other.AddImage(new TerminalImage(handle, 0, 0, 1, 1)));
        Assert.Empty(other.Images);
        buffer.Clear(true);
        var retired = new List<object>();
        buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Same(handle, Assert.Single(retired));
        Assert.Equal((2, bytes), budget.Usage);
        Assert.Throws<InvalidOperationException>(() => buffer.AddImage(new TerminalImage(handle, 0, 0, 1, 1)));
        handle.Dispose();
        Assert.Equal((0, 0L), budget.Usage);
    }

    [Fact]
    public void Source_refusal_and_decoder_null_release_unallocated_reservations_but_unknown_exception_fails_closed()
    {
        var budget = new NativeSixelBudget();
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 5, budget, new SixelSourceBudget(1, 1));
        var decoder = new ProbeDecoder(budget, buffer);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = decoder };
        parser.Process(Wire);
        Assert.Equal(0, decoder.Calls);
        Assert.Equal((0, 0L), budget.Usage);
        var normal = TerminalBuffer.CreateNativeSixelTestBuffer(10, 5, budget);
        decoder = new ProbeDecoder(budget, normal) { ReturnNull = true };
        parser = new AnsiParser(normal, forceConPtyFiltering: false) { ImageDecoder = decoder };
        parser.Process(Wire);
        Assert.Equal((0, 0L), budget.Usage);
        decoder.ReturnNull = false;
        decoder.Throw = true;
        parser.Process(Wire);
        Assert.Empty(normal.Images);
        Assert.Equal((1, 6L * 6 + 16384), budget.Usage);
    }

    [Fact]
    public void Source_eviction_retires_undecoded_owner_without_returning_its_native_credit()
    {
        var budget = new NativeSixelBudget();
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 5, budget, new SixelSourceBudget(2, 1));
        var decoder = new ProbeDecoder(budget, buffer);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = decoder };
        parser.Process(Wire);
        var first = Assert.IsType<ProbeImage>(Assert.Single(buffer.Images).ImageHandle);
        parser.Process("\x1bPq@\x1b\\");
        Assert.Equal(2, decoder.Calls);
        Assert.Single(buffer.Images);
        Assert.Equal(2, budget.Usage.Placements);
        var retired = new List<object>();
        buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Same(first, Assert.Single(retired));
        Assert.Equal(2, budget.Usage.Placements);
        first.Dispose();
        Assert.Equal(1, budget.Usage.Placements);
    }

    [Fact]
    public void Default_limit_includes_all_256_retired_snapshot_pinned_placements()
    {
        var budget = new NativeSixelBudget();
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 5, budget);
        var decoder = new ProbeDecoder(budget, buffer);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = decoder };
        parser.Process("\x1b[?80h");
        int snapshot = buffer.BeginSnapshotSession();
        for (int i = 0; i < NativeSixelBudget.MaxPlacements + 1; i++) parser.Process(Wire);
        Assert.Equal(NativeSixelBudget.MaxPlacements, decoder.Calls);
        Assert.Single(buffer.Images);
        Assert.Equal(NativeSixelBudget.MaxPlacements, budget.Usage.Placements);
        buffer.Clear(true);
        var retired = new List<object>();
        buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Empty(retired);
        buffer.EndSnapshotSession(snapshot);
        buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Equal(NativeSixelBudget.MaxPlacements, retired.Count);
        Assert.Equal(NativeSixelBudget.MaxPlacements, budget.Usage.Placements);
        foreach (var handle in retired) ((IDisposable)handle).Dispose();
        Assert.Equal((0, 0L), budget.Usage);
    }

    private sealed class ProbeDecoder(NativeSixelBudget budget, TerminalBuffer buffer) : IImageDecoder, INativeSixelDecoder
    {
        internal int Calls, LegacyCalls;
        internal bool ReturnNull, Throw;
        internal (int Placements, long Bytes) AtEntry;
        public INativeSixelImage? DecodeNativeSixel(SixelSource source, NativeSixelAllocation allocation)
        {
            Calls++;
            AtEntry = budget.Usage;
            if (Throw) throw new InvalidOperationException("unknown native resources");
            return ReturnNull ? null : new ProbeImage(source, allocation, buffer);
        }
        public object? DecodeSixel(string data, out int width, out int height) { LegacyCalls++; width = height = 0; return null; }
        public object? DecodeImageBytes(byte[] data, out int width, out int height) => DecodeSixel("", out width, out height);
        public object? DecodeRawImage(byte[] data, int bpp, int width, int height, out int w, out int h) => DecodeSixel("", out w, out h);
    }

    private sealed class ProbeImage(SixelSource source, NativeSixelAllocation allocation, TerminalBuffer buffer) : INativeSixelImage
    {
        public SixelSource Source => source;
        public NativeSixelAllocation Allocation => allocation;
        public bool NeedsSource => true;
        public bool IsMaterialized => false;
        internal bool FailDisposal;
        public void Dispose()
        {
            Assert.False(buffer.Lock.IsWriteLockHeld);
            if (FailDisposal) throw new InvalidOperationException("disposal failed");
            source.Dispose();
            allocation.Owner.CompleteDisposal(allocation);
        }
    }

    private sealed class LegacyOnlyDecoder : IImageDecoder
    {
        internal int Calls;
        public object? DecodeSixel(string data, out int w, out int h) { Calls++; w = h = 0; return null; }
        public object? DecodeImageBytes(byte[] data, out int w, out int h) => DecodeSixel("", out w, out h);
        public object? DecodeRawImage(byte[] data, int bpp, int width, int height, out int w, out int h) => DecodeSixel("", out w, out h);
    }
}
