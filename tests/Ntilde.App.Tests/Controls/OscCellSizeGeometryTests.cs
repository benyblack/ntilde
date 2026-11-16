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
    public void Reports_follow_the_actual_view_metrics_and_scale_without_resizing()
    {
        var pane = new TerminalPane();
        var window = new Window { Width = 800, Height = 600, Content = pane };
        try
        {
            pane.CreateAndWireParser();
            pane.WireReusedTermViewHandlers();
            window.Show();
            foreach (double scale in new[] { 1.0, 2.0, 1.5, 1.0 })
            {
                UiScale.Apply(scale);
                Dispatcher.UIThread.RunJobs();
                var parser = Assert.IsType<AnsiParser>(pane.Parser);
                var actual = pane.TermView.Metrics;
                Assert.Equal(new CellSizeReportMetrics(actual.CellWidth, actual.CellHeight, scale, actual.Ascent), parser.CellSizeReports);
                var replies = new List<string>();
                parser.OnResponse = replies.Add;
                parser.Process("\x1b]1337;ReportCellSize\a\x1b]776;?\a");
                Assert.Equal(2, replies.Count);
                Assert.Equal(string.Create(CultureInfo.InvariantCulture,
                    $"\x1b]1337;ReportCellSize={actual.CellHeight:G9};{actual.CellWidth:G9};{scale:G9}\x1b\\"), replies[0]);
                static int Px(double value, double factor) => Math.Max(1, (int)Math.Round(value * factor, MidpointRounding.AwayFromZero));
                Assert.Equal($"\x1b]776;{Px(actual.CellWidth, scale)};{Px(actual.CellHeight, scale)};{Px(actual.Ascent, scale)}\a", replies[1]);
            }
        }
        finally { window.Close(); pane.Dispose(); UiScale.Apply(1.0); }
    }
}
