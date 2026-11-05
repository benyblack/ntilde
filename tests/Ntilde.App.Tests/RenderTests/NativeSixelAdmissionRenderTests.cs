using Ntilde.Platform;
using Ntilde.Rendering;
using Ntilde.Shell;
using Ntilde.Tests.Infra;
using Ntilde.VT;
using SkiaSharp;

namespace Ntilde.Tests.RenderTests;

[Trait("Lane", "PlatformBoot")]
[Collection("GoldenPng")]
public sealed class NativeSixelAdmissionRenderTests
{
    [Fact]
    public void Internal_native_owner_materializes_through_the_real_offscreen_path_without_activating_native_sizing()
    {
        var budget = new NativeSixelBudget();
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, budget);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false)
        {
            ImageDecoder = new SkiaImageDecoder(),
            CellWidth = 10,
            CellHeight = 20,
        };
        parser.Process("\x1b[?80h\x1bPq#2!16~-!16~-!16N\x1b\\");
        var placement = Assert.Single(buffer.Images);
        var owner = Assert.IsType<NativeSixelImage>(placement.ImageHandle);
        Assert.Equal((2, 1), (placement.CellWidth, placement.CellHeight));
        Assert.False(owner.IsMaterialized);
        Assert.Equal((1, 16L * 18 * 6 + 16384), budget.Usage);
        var metrics = new CellMetrics { CellWidth = 10, CellHeight = 20, Baseline = 15, Ascent = 15, Descent = 5 };
        using var pixels = SKBitmap.Decode(SnapshotService.CapturePng(buffer, metrics, 100, 120,
            new SnapshotCaptureOptions { HideCursor = true }));
        Assert.True(owner.IsMaterialized);
        Assert.Equal((1, 16L * 18 * 4), budget.Usage);
        Assert.Equal(SKColors.Red, pixels.GetPixel(23, 15)); // Existing whole-cell draw, not native fidelity.
        Assert.NotEqual(SKColors.Red, pixels.GetPixel(25, 15));
        Assert.Equal(0, SixelRasterPolicy.EnabledVersion);
        Assert.Throws<InvalidOperationException>(() => new TerminalBuffer(10, 6, 1));
        buffer.Clear(true);
        var retired = new List<object>();
        buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Same(owner, Assert.Single(retired));
        Assert.Equal((1, 16L * 18 * 4), budget.Usage);
        owner.Dispose();
        Assert.Equal((0, 0L), budget.Usage);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void Failed_native_disposal_is_retried_by_the_actual_view_without_releasing_credit_early()
    {
        var budget = new NativeSixelBudget();
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, budget);
        var allocation = Assert.IsType<NativeSixelAllocation>(budget.TryReserve(1, 6, 2));
        var source = buffer.SixelSources.Retain("q~");
        var handle = new RetryImage(source, allocation, buffer);
        buffer.AddImage(new TerminalImage(handle, 0, 0, 1, 1));
        var view = new TerminalView();
        view.SetBuffer(buffer);
        int snapshot = buffer.BeginSnapshotSession();
        buffer.Clear(true);
        view.DisposeRetiredImageBitmaps(long.MaxValue);
        Assert.Equal(0, handle.Attempts);
        buffer.EndSnapshotSession(snapshot);
        view.DisposeRetiredImageBitmaps(long.MaxValue);
        Assert.Equal(1, handle.Attempts);
        Assert.False(handle.Gone);
        Assert.Equal(1, budget.Usage.Placements);
        view.DisposeRetiredImageBitmaps(long.MaxValue);
        Assert.Equal(2, handle.Attempts);
        Assert.True(handle.Gone);
        Assert.Equal((0, 0L), budget.Usage);
        view.DisposeRetiredImageBitmaps(long.MaxValue);
        Assert.Equal(2, handle.Attempts);
    }

    private sealed class RetryImage(SixelSource source, NativeSixelAllocation allocation, TerminalBuffer buffer) : INativeSixelImage
    {
        public SixelSource Source => source;
        public NativeSixelAllocation Allocation => allocation;
        public bool NeedsSource => true;
        public bool IsMaterialized => false;
        internal int Attempts;
        internal bool Gone;
        public void Dispose()
        {
            Assert.False(buffer.Lock.IsWriteLockHeld);
            if (++Attempts == 1) throw new InvalidOperationException("transient disposal failure");
            source.Dispose();
            Gone = true;
            allocation.Owner.CompleteDisposal(allocation);
        }
    }

}
