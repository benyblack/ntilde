using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.AgentHost;
using Ntilde.Shell;
using Ntilde.VT;
using SkiaSharp;
using Xunit;

namespace Ntilde.Tests.RenderTests
{
    /// <summary>
    /// The buffer's row-diff baseline belongs to the live renderer: each live frame's dirty spans
    /// are the cells changed since the previous live frame, and only those are repainted over the
    /// row picture drawn last time. Every other capture of the same buffer has to leave that
    /// baseline alone, or the next live frame omits the changes made before the capture and those
    /// cells stay stale on screen.
    ///
    /// These drive the product's two off-screen capture entry points between two stand-in live
    /// frames (direct live-mode snapshots, which is what <c>TerminalDrawOperation</c> takes on the
    /// render thread) and check the second frame still reports every change since the first.
    /// </summary>
    public sealed class OffscreenCaptureIsolationTests
    {
        private static readonly CellMetrics Metrics = new()
        {
            CellWidth = 8f,
            CellHeight = 16f,
            Baseline = 12f,
            Ascent = 12f,
            Descent = 4f,
            Leading = 0f,
        };

        private static void WriteLabelledRows(AnsiParser parser, int rows)
        {
            for (int r = 0; r < rows; r++)
            {
                parser.Process($"\u001b[{r + 1};1Hline {r}");
            }
        }

        private static void WriteAt(AnsiParser parser, int row, string text)
            => parser.Process($"\u001b[{row + 1};1H{text}");

        private static TerminalRenderSnapshot LiveFrame(TerminalBuffer buffer, int viewportRows, int viewportCols)
            => buffer.CaptureRenderSnapshot(new RenderSnapshotRequest
            {
                ViewportRows = viewportRows,
                ViewportCols = viewportCols,
            }, out _);

        private static int AbsRowOfBufferRow(TerminalBuffer buffer, int row)
        {
            buffer.Lock.EnterReadLock();
            try
            {
                return buffer.InternalTotalLines - buffer.Rows + row;
            }
            finally
            {
                buffer.Lock.ExitReadLock();
            }
        }

        /// <summary>The dirty columns a snapshot reports for the visual row showing <paramref name="absRow"/>.</summary>
        private static HashSet<int> DirtyColumnsForAbsRow(TerminalRenderSnapshot snapshot, int absRow)
        {
            int visualRow = absRow - snapshot.AbsDisplayStart;
            Assert.InRange(visualRow, 0, snapshot.ViewportRows - 1);

            var cols = new HashSet<int>();
            for (int i = 0; i < snapshot.DirtySpans.Length; i++)
            {
                var span = snapshot.DirtySpans.Array[i];
                if (span.Row != visualRow) continue;
                for (int c = span.ColStart; c < span.ColEnd; c++) cols.Add(c);
            }

            return cols;
        }

        private static void AssertLiveFrameCovers(TerminalRenderSnapshot live, int absRow, int colCount, string when)
        {
            var dirty = DirtyColumnsForAbsRow(live, absRow);
            var missing = Enumerable.Range(0, colCount).Where(c => !dirty.Contains(c)).ToArray();
            Assert.True(
                missing.Length == 0,
                $"the row changed {when} the off-screen capture, but the next live frame's dirty spans omit " +
                $"cols [{string.Join(",", missing)}] - the capture advanced the live renderer's baseline, so " +
                "those cells would stay stale on screen.");
        }

        [AvaloniaFact]
        public void AgentRenderCapture_LeavesTheLiveDiffBaselineAlone()
        {
            // The agent-host captureScreen `render` mode: AgentSessionRegistration.TryCapturePng,
            // called on the IPC thread, rendering through TerminalSnapshotRenderer.
            var buffer = new TerminalBuffer(40, 6);
            var parser = new AnsiParser(buffer);
            WriteLabelledRows(parser, buffer.Rows);

            var registration = new AgentSessionRegistration(
                Guid.NewGuid(), buffer, "title", "Profile", "local", isActive: true);
            registration.UpdateRenderParameters(new PaneRenderParameters(
                Metrics,
                TerminalSnapshotOptions.DefaultTypefaceFamily,
                FontSize: 14f,
                EnableLigatures: false,
                EnableComplexShaping: true));

            using (LiveFrame(buffer, buffer.Rows, buffer.Cols)) { }

            WriteAt(parser, 1, "AAAA");
            Assert.True(
                registration.TryCapturePng(maxWidth: 0, scale: 1.0, out var capture, out var error),
                $"capture failed: {error}");
            Assert.NotEmpty(capture.Png);
            WriteAt(parser, 3, "BBB");

            using var live = LiveFrame(buffer, buffer.Rows, buffer.Cols);
            AssertLiveFrameCovers(live, AbsRowOfBufferRow(buffer, 1), 4, "before");
            AssertLiveFrameCovers(live, AbsRowOfBufferRow(buffer, 3), 3, "after");
        }

        [AvaloniaFact]
        public void AgentLiveCapture_LeavesTheLiveDiffBaselineAlone()
        {
            // The agent-host captureScreen `live` mode: TerminalPane.CaptureLiveForAgent
            // photographs the on-screen TerminalView with a RenderTargetBitmap. That renders the
            // view synchronously on the UI thread while the compositor's render thread may be
            // drawing a live frame of the same buffer - a second consumer, just like the IPC one.
            using var pane = new Ntilde.Controls.TerminalPane();
            pane.CreateAndWireParser();
            var buffer = pane.Buffer!;
            var parser = pane.Parser!;
            WriteLabelledRows(parser, buffer.Rows);
            pane.ApplySettings(new TerminalSettings
            {
                FontFamily = "Consolas",
                FontSize = 14,
                WindowOpacity = 1.0,
            });

            var window = new Window { Content = pane, Width = 680, Height = 280 };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                Dispatcher.UIThread.RunJobs();

                var view = Assert.IsType<TerminalView>(pane.ActiveControl);

                // Rows counted from the bottom of the buffer, because those are the ones on
                // screen whether or not the pane has resized the buffer to the view yet.
                int visibleRows = Math.Min(view.Rows, buffer.Rows);
                Assert.True(visibleRows >= 4 && view.Cols > 4, $"TerminalView laid out to {view.Rows}x{view.Cols}.");
                int rowA = buffer.Rows - 2;
                int rowB = buffer.Rows - 4;

                // The stand-in live frames use the view's own viewport, as TerminalView.Render
                // does. No dispatcher jobs run from here on, so the window's real compositor
                // cannot slip a frame in between.
                using (LiveFrame(buffer, view.Rows, view.Cols)) { }

                WriteAt(parser, rowA, "AAAA");
                var capture = pane.CaptureLiveForAgent(maxWidth: 0, scale: 1.0);
                Assert.NotNull(capture);
                AssertCaptureIsNotBlank(capture!.Value);
                WriteAt(parser, rowB, "BBB");

                using var live = LiveFrame(buffer, view.Rows, view.Cols);
                AssertLiveFrameCovers(live, AbsRowOfBufferRow(buffer, rowA), 4, "before");
                AssertLiveFrameCovers(live, AbsRowOfBufferRow(buffer, rowB), 3, "after");
            }
            finally
            {
                window.Content = null;
                Dispatcher.UIThread.RunJobs();
                window.Close();
            }
        }

        /// <summary>
        /// Guards the test above against passing vacuously: TerminalView.Render only fills the
        /// background, without constructing a draw operation or capturing a snapshot, while its
        /// fonts are not ready. Any non-background pixel means the terminal really was drawn.
        /// </summary>
        private static void AssertCaptureIsNotBlank(AgentLiveCapture capture)
        {
            using var bitmap = SKBitmap.Decode(capture.Png)
                ?? throw new InvalidOperationException("capture PNG did not decode");

            var background = bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1);
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y) != background) return;
                }
            }

            Assert.Fail("the live capture is a blank background - the view never drew the terminal.");
        }
    }
}
