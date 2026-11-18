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
    [AvaloniaFact]
    public void Font_queries_follow_actual_font_and_zoom_but_never_change_the_window_or_font()
    {
        var pane = new TerminalPane();
        var window = new Window { Width = 800, Height = 600, Content = pane };
        try
        {
            pane.CreateAndWireParser(); pane.WireReusedTermViewHandlers(); window.Show();
            UiScale.Apply(1);
            var parser = Assert.IsType<AnsiParser>(pane.Parser);
            var replies = new List<string>(); parser.OnResponse = replies.Add;
            foreach (double size in new[] { 16.0, 24.0, 20.0, 14.0 })
            {
                pane.TermView.FontSize = size; pane.TermView.MeasureCharSize(); Dispatcher.UIThread.RunJobs();
                var client = window.ClientSize;
                parser.Process("\x1b]7770;?\a\x1b]7777;?\x1b\\");
                if (size == 14) Assert.Empty(replies);
                else Assert.Equal(new[] { $"\x1b]7770;{size * 0.75:0}\a", $"\x1b]7777;{size * 0.75:0}\x1b\\" }, replies);
                replies.Clear();
                parser.Process("\x1b]710;xft:Other:pixelsize=40\a\x1b]7770;+2\a\x1b]7777;30\a");
                Dispatcher.UIThread.RunJobs();
                Assert.Empty(replies); Assert.Equal(size, pane.TermView.FontSize); Assert.Equal(client, window.ClientSize);
            }
            pane.TermView.FontSize = 16;
            foreach (double zoom in new[] { 1.0, 1.5 })
            {
                UiScale.Apply(zoom); pane.TermView.MeasureCharSize(); Dispatcher.UIThread.RunJobs();
                var client = window.ClientSize;
                parser.Process("\x1b]7770;?\a\x1b]7777;?\x1b\\");
                int points = zoom == 1 ? 12 : 18;
                Assert.Equal(new[] { $"\x1b]7770;{points}\a", $"\x1b]7777;{points}\x1b\\" }, replies);
                Assert.Equal(16, pane.TermView.FontSize); Assert.Equal(client, window.ClientSize);
                replies.Clear();
            }
        }
        finally { window.Close(); pane.TermView.Retire(); UiScale.Apply(1); }
    }

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
                Assert.Equal(new CellSizeReportMetrics(actual.CellWidth * zoom, actual.CellHeight * zoom, 1, actual.Ascent * zoom) { FontSizePoints = pane.TermView.FontSize * zoom * 0.75 }, parser.CellSizeReports);
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
