using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Shell;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.Controls;

public sealed class OscCellSizeGeometryTests
{
    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1.25)]
    public void Reports_separate_logical_points_and_backing_scale_including_a_held_window(double heldScale)
    {
        var pane = new TerminalPane();
        var window = new Window { Width = 800, Height = 600, Content = pane };
        try
        {
            pane.CreateAndWireParser();
            pane.WireReusedTermViewHandlers();
            if (heldScale > 0) UiScale.PinScale(window, heldScale);
            window.Show();
            foreach (double scale in new[] { 1.0, 2.0, 1.5, 1.0 })
            {
                UiScale.Apply(scale);
                Dispatcher.UIThread.RunJobs();
                var parser = Assert.IsType<AnsiParser>(pane.Parser);
                var actual = pane.TermView.Metrics;
                double zoom = heldScale > 0 ? heldScale : scale;
                Assert.Equal(1.0, window.RenderScaling);
                Assert.Equal(zoom, pane.TermView.EffectiveRenderScaling);
                Assert.Equal(new CellSizeReportMetrics(actual.CellWidth * zoom, actual.CellHeight * zoom, 1, actual.Ascent * zoom), parser.CellSizeReports);
                var replies = new List<string>();
                parser.OnResponse = replies.Add;
                parser.Process("\x1b]1337;ReportCellSize\a\x1b]776;?\a");
                Assert.Equal(2, replies.Count);
                Assert.Equal(string.Create(CultureInfo.InvariantCulture,
                    $"\x1b]1337;ReportCellSize={actual.CellHeight * zoom:G9};{actual.CellWidth * zoom:G9};1\x1b\\"), replies[0]);
                static int Px(double value, double factor) => Math.Max(1, (int)Math.Round(value * factor, MidpointRounding.AwayFromZero));
                Assert.Equal($"\x1b]776;{Px(actual.CellWidth, zoom)};{Px(actual.CellHeight, zoom)};{Px(actual.Ascent, zoom)}\a", replies[1]);
            }
        }
        finally { window.Close(); pane.Dispose(); UiScale.Apply(1.0); }
    }
}
