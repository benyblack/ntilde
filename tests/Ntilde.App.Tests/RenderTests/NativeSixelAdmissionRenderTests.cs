using Ntilde.Controls;
using Ntilde.Platform;
using Ntilde.Rendering;
using Ntilde.Shell;
using Ntilde.Tests.Controls;
using Ntilde.Tests.Infra;
using Ntilde.VT;
using SkiaSharp;

namespace Ntilde.Tests.RenderTests;

[Trait("Lane", "PlatformBoot")]
[Collection("GoldenPng")]
public sealed class NativeSixelAdmissionRenderTests
{
    [Fact]
    public void Budgeted_native_owner_materializes_through_the_real_offscreen_path()
    {
        var budget = new NativeSixelBudget();
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, budget, new SixelCellSize(10, 20));
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
            new SnapshotCaptureOptions { HideCursor = true, NativeRasterPresenter = new(1) }));
        Assert.True(owner.IsMaterialized);
        Assert.Equal((1, 16L * 18 * 4), budget.Usage);
        Assert.Equal(SKColors.Red, pixels.GetPixel(19, 15));
        Assert.NotEqual(SKColors.Red, pixels.GetPixel(21, 15));
        Assert.Equal(SixelRasterPolicy.PresenterNativeVersion, SixelRasterPolicy.EnabledVersion);
        Assert.Throws<InvalidOperationException>(() => new TerminalBuffer(10, 6, SixelRasterPolicy.EnabledVersion + 1));
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
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, budget, new SixelCellSize(10, 20));
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

    [Fact]
    public void Replaced_buffer_retries_native_disposal_after_its_last_snapshot()
    {
        var budget = new NativeSixelBudget();
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, budget, new SixelCellSize(10, 20));
        var allocation = Assert.IsType<NativeSixelAllocation>(budget.TryReserve(1, 6, 2));
        var handle = new RetryImage(buffer.SixelSources.Retain("q~"), allocation, buffer);
        buffer.AddImage(new TerminalImage(handle, 0, 0, 1, 1));
        int snapshot = buffer.BeginSnapshotSession();
        TerminalView.ReleaseReplacedBufferImages(buffer);
        Assert.Equal(0, handle.Attempts);
        Assert.Equal(1, budget.Usage.Placements);

        buffer.EndSnapshotSession(snapshot);

        Assert.Equal(2, handle.Attempts);
        Assert.True(handle.Gone);
        Assert.Equal((0, 0L), budget.Usage);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void A_direct_session_replaces_a_retained_legacy_mux_buffer_before_output()
    {
        using var pane = new TerminalPane();
        pane.ApplySettings(new TerminalSettings
        {
            AlternateScroll = false,
            CursorStyle = "bar",
            CursorBlink = false,
            CommandAssistShellIntegrationEnabled = false,
        });
        pane.Buffer!.Modes.AlternateScrollOverride = true;
        var session = new FakeTerminalSession();
        pane.SessionFactory = new RecordingSessionFactory(session);
        Assert.True(pane.UseBufferSixelPolicy(0));
        var legacy = Assert.IsType<TerminalBuffer>(pane.Buffer);
        Assert.False(legacy.Modes.IsAlternateScroll);
        Assert.Null(legacy.Modes.AlternateScrollOverride);
        Assert.Equal(CursorStyle.Beam, legacy.Modes.CursorStyle);
        Assert.False(legacy.Modes.IsCursorBlinkEnabled);
        pane.CreateAndWireParser(muxBacked: true);
        var oldParser = pane.Parser;

        pane.InitializeSessionCore("fake-shell", string.Empty, profile: null, cols: 80, rows: 24);

        Assert.Same(session, pane.Session);
        Assert.NotSame(legacy, pane.Buffer);
        Assert.NotSame(oldParser, pane.Parser);
        Assert.Equal(SixelRasterPolicy.PresenterNativeVersion, pane.Buffer!.SixelRasterPolicyVersion);
        Assert.False(pane.Buffer.Modes.IsAlternateScroll);
        Assert.Equal(CursorStyle.Beam, pane.Buffer.Modes.CursorStyle);
        Assert.False(pane.Buffer.Modes.IsCursorBlinkEnabled);
        var metrics = Assert.IsType<SixelCellSize>(pane.Parser!.SixelCellSizeOverride);
        Assert.True(metrics.WidthPx >= 1 && metrics.HeightPx >= 1);
        session.EmitOutput("\x1bPq~\x1b\\");
        Assert.NotNull(Assert.Single(pane.Buffer.Images).NativeRaster);
    }

    private sealed class RetryImage(SixelSource source, NativeSixelAllocation allocation, TerminalBuffer buffer) : INativeSixelImage
    {
        public SixelSource Source => source;
        public NativeSixelAllocation Allocation => allocation;
        public bool NeedsSource => true;
        public bool IsMaterialized => false;
        public NativeSixelDecodeState DecodeState => NativeSixelDecodeState.Pending;
        internal int Attempts;
        internal bool Gone;
        /// <summary>
        /// Fails the first actual-view handoff while asserting disposal is outside the VT lock; only the retry releases credit.
        /// </summary>
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
