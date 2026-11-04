using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.AgentHost;
using Ntilde.Shell;
using Ntilde.VT;

namespace Ntilde.Tests.RenderTests;

public sealed class NativeRasterPresenterContextTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Presenter_density_must_be_known_finite_and_positive(double scale)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new NativeRasterPresenterContext(scale));

    [AvaloniaFact]
    public void Presenter_context_tracks_attached_density_and_withdraws_on_detach()
    {
        UiScale.Apply(1);
        var view = new TerminalView();
        var window = new Window { Width = 400, Height = 300, Content = view };
        NativeRasterPresenterContext? published = null;
        int publications = 0;
        view.PresenterContextChanged += () => { published = view.NativeRasterPresenter; publications++; };
        Assert.Null(view.NativeRasterPresenter);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, published!.DevicePixelsPerDip);
            var initial = published;
            UiScale.PinScale(window, 1.5);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1.5, published!.DevicePixelsPerDip);
            Assert.Equal(1, initial.DevicePixelsPerDip);
            Assert.True(publications >= 2);
            window.Content = null;
            Dispatcher.UIThread.RunJobs();
            Assert.Null(published);
            Assert.Null(view.NativeRasterPresenter);
            window.Content = view;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1.5, published!.DevicePixelsPerDip);
        }
        finally { window.Close(); UiScale.Apply(1); }
    }

    [AvaloniaTheory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(1.25, 1.5)]
    public void Presenter_and_capture_scales_remain_independent_and_do_not_change_legacy_pixels_or_v1_json(double presenterScale, double captureScale)
    {
        var buffer = new TerminalBuffer(10, 3);
        new AnsiParser(buffer).Process("\x1b[?25lA");
        var metrics = new CellMetrics { CellWidth = 10, CellHeight = 20, Baseline = 15, Ascent = 15, Descent = 5 };
        var parameters = new PaneRenderParameters(metrics, TerminalSnapshotOptions.DefaultTypefaceFamily, 14, false, true)
        {
            NativeRasterPresenter = new NativeRasterPresenterContext(presenterScale),
        };
        var options = new TerminalSnapshotOptions { RenderScaling = captureScale, FillBackground = true, IncludeImageDetails = true };
        using var legacy = TerminalSnapshotRenderer.CaptureWithContext(buffer, metrics, 108, 60, options, true, out var legacyInfo);
        using var withContext = TerminalSnapshotRenderer.CaptureWithContext(buffer, metrics, 108, 60,
            options with { NativeRasterPresenter = parameters.NativeRasterPresenter }, true, out var info);
        Assert.Equal(legacy.Bytes, withContext.Bytes);
        Assert.Equal((int)Math.Round(108 * captureScale), withContext.Width);
        Assert.Same(parameters.NativeRasterPresenter, info.Context!.NativeRasterPresenter);
        Assert.Equal(presenterScale, info.Context.NativeRasterPresenter!.DevicePixelsPerDip);
        var before = AgentCaptureDetails.FromCapture(legacyInfo.Context);
        var after = AgentCaptureDetails.FromCapture(info.Context);
        Assert.NotNull(before.Render);
        Assert.NotNull(before.Images);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before.Render), System.Text.Json.JsonSerializer.Serialize(after.Render));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before.Images), System.Text.Json.JsonSerializer.Serialize(after.Images));
        var copy = parameters with { FontSize = 18 };
        Assert.Same(parameters.NativeRasterPresenter, copy.NativeRasterPresenter);
    }
}
