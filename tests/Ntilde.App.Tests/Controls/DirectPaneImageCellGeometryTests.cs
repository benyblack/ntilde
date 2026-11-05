using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Shell;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.Controls;

/// <summary>
/// A direct (non-persistent) pane on a scaled display answers <c>CSI 14 t</c> and <c>CSI 16 t</c> in
/// device pixels: the logical cell times the view's effective render scaling, the unit Sixel placement,
/// the mode-2048 in-band resize report and a persistent pane's daemon already use. It used to answer in
/// logical pixels, so notcurses on a 2x Mac was told 8 x 18 px cells in a direct pane and 17 x 37 in a
/// persistent one, and drew Sixel at half size in the direct pane. The headless top level reports
/// RenderScaling 1.0, so the interface scale below is the whole effective scale.
/// </summary>
public sealed class DirectPaneImageCellGeometryTests
{
    private static string Query(AnsiParser parser, string query)
    {
        var replies = new List<string>();
        Action<string>? previous = parser.OnResponse;
        parser.OnResponse = replies.Add;
        try { parser.Process(query); }
        finally { parser.OnResponse = previous; }
        return Assert.Single(replies);
    }

    [AvaloniaFact]
    public void Csi16t_and_csi14t_report_device_pixels_and_follow_a_scale_change()
    {
        UiScale.Apply(2.0);
        var pane = new TerminalPane();
        var window = new Window { Width = 800, Height = 600, Content = pane };
        try
        {
            pane.CreateAndWireParser();
            pane.WireReusedTermViewHandlers();
            window.Show();
            Dispatcher.UIThread.RunJobs();

            AnsiParser parser = Assert.IsType<AnsiParser>(pane.Parser);
            TerminalBuffer buffer = Assert.IsType<TerminalBuffer>(pane.Buffer);
            Assert.Equal(2.0, pane.TermView.EffectiveRenderScaling, precision: 6);
            float logicalWidth = pane.TermView.Metrics.CellWidth;
            float logicalHeight = pane.TermView.Metrics.CellHeight;
            Assert.True(logicalWidth > 0 && logicalHeight > 0);

            // The parser keeps the logical cell; the reports come from the device-pixel override.
            Assert.Equal((logicalWidth, logicalHeight), (parser.CellWidth, parser.CellHeight));
            Assert.Equal(new SixelCellSize(logicalWidth * 2, logicalHeight * 2), parser.SixelCellSizeOverride);
            Assert.Equal(
                $"\x1b[6;{(int)Math.Round(logicalHeight * 2)};{(int)Math.Round(logicalWidth * 2)}t",
                Query(parser, "\x1b[16t"));
            Assert.Equal(
                $"\x1b[4;{(int)Math.Round(buffer.Rows * logicalHeight * 2)};{(int)Math.Round(buffer.Cols * logicalWidth * 2)}t",
                Query(parser, "\x1b[14t"));

            // Back to 100%: the view remeasures and the next reply is in the new unit.
            UiScale.Apply(1.0);
            Dispatcher.UIThread.RunJobs();
            float width = pane.TermView.Metrics.CellWidth;
            float height = pane.TermView.Metrics.CellHeight;
            Assert.Equal(new SixelCellSize(width, height), parser.SixelCellSizeOverride);
            Assert.Equal($"\x1b[6;{(int)Math.Round(height)};{(int)Math.Round(width)}t", Query(parser, "\x1b[16t"));
        }
        finally
        {
            window.Close();
            pane.Dispose();
            UiScale.Apply(1.0);
        }
    }
}
