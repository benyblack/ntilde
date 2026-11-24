using Avalonia.Headless.XUnit;
using Moq;
using System.Linq;
using Ntilde.Pty;
using Ntilde.Shell;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.Input
{
    /// <summary>
    /// Smooth wheel scrolling eases the viewport toward a target over several frames. The
    /// target must survive its own animation: every line the wheel asked for has to arrive,
    /// and output landing mid-gesture (a TUI redrawing its spinner) must not snap the viewport
    /// back to the live line while the gesture is still in flight. Only an external seek -
    /// typing, the scrollbar, search - cancels the animation.
    ///
    /// These drive the internal seams <see cref="TerminalView.ScrollByWheelLines"/> (what the
    /// wheel handler calls once a notch's lines are accumulated) and
    /// <see cref="TerminalView.AdvanceScrollAnimation"/> (what the 16 ms timer tick calls), so
    /// the easing under test is the one that ships.
    /// </summary>
    public class TerminalViewSmoothScrollTests
    {
        private const int WheelLinesPerNotch = 3;

        private static (TerminalView View, TerminalBuffer Buffer) CreateViewAtBottom()
        {
            var session = new Mock<ITerminalSession>();
            session.SetupGet(x => x.IsProcessRunning).Returns(true);
            var view = new TerminalView();
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);
            for (int i = 1; i <= 60; i++)
            {
                parser.Process($"line {i}\r\n");
            }
            Assert.True(buffer.TotalLines - buffer.Rows >= 12, "test setup: expected ample scrollback");
            view.SetBuffer(buffer);
            view.SetSession(session.Object);
            Assert.Equal(0, view.ScrollOffset);
            return (view, buffer);
        }

        private static void RunAnimationToCompletion(TerminalView view)
        {
            // Generous bound: the easing moves at least one line per frame.
            for (int frame = 0; frame < 200; frame++)
            {
                if (!view.AdvanceScrollAnimation()) return;
            }
            Assert.Fail("smooth-scroll animation did not settle");
        }

        [AvaloniaFact]
        public void ScrollByWheelLines_OneNotch_AnimationDeliversEveryLine()
        {
            var (view, _) = CreateViewAtBottom();

            view.ScrollByWheelLines(WheelLinesPerNotch);
            RunAnimationToCompletion(view);

            Assert.Equal(WheelLinesPerNotch, view.ScrollOffset);
        }

        [AvaloniaFact]
        public void ScrollByWheelLines_SecondNotchMidAnimation_AccumulatesOntoTarget()
        {
            var (view, _) = CreateViewAtBottom();

            view.ScrollByWheelLines(WheelLinesPerNotch);
            Assert.True(view.AdvanceScrollAnimation(), "first frame should leave the gesture in flight");
            view.ScrollByWheelLines(WheelLinesPerNotch);
            RunAnimationToCompletion(view);

            Assert.Equal(2 * WheelLinesPerNotch, view.ScrollOffset);
        }

        [AvaloniaFact]
        public void EnsureCursorVisible_WhileWheelAnimationInFlight_DoesNotSnapToBottom()
        {
            var (view, _) = CreateViewAtBottom();

            view.ScrollByWheelLines(WheelLinesPerNotch);
            Assert.True(view.AdvanceScrollAnimation(), "first frame should leave the gesture in flight");
            Assert.InRange(view.ScrollOffset, 1, 2); // inside the follow-output band

            // Output arrives mid-gesture (the pane calls this after every PTY chunk).
            view.EnsureCursorVisible();
            RunAnimationToCompletion(view);

            Assert.Equal(WheelLinesPerNotch, view.ScrollOffset);
        }

        [AvaloniaFact]
        public void EnsureCursorVisible_WithNoGestureInFlightNearBottom_StillFollowsOutput()
        {
            var (view, _) = CreateViewAtBottom();
            view.ScrollOffset = 2;

            view.EnsureCursorVisible();

            Assert.Equal(0, view.ScrollOffset);
            Assert.False(view.AdvanceScrollAnimation(), "no animation should be pending after a snap");
        }

        [AvaloniaTheory]
        [InlineData("marker\r\n")]
        [InlineData("\u001b[?2026hmarker\r\n\u001b[?2026l")]
        public void OutputWhileScrolledBack_HoldsTheSameVisibleRows(string output)
        {
            var (view, buffer) = CreateViewAtBottom();
            view.ScrollByWheelLines(10 * WheelLinesPerNotch);
            RunAnimationToCompletion(view);
            string[] anchor = ReadVisibleRows(view, buffer);
            Assert.Equal("line 8", anchor[0]);
            Assert.Equal("line 31", anchor[^1]);

            new AnsiParser(buffer).Process(output);
            view.EnsureCursorVisible();
            view.EnsureCursorVisible(); // The same appended row must not be counted twice.

            Assert.Equal(anchor, ReadVisibleRows(view, buffer));
            Assert.Equal(31, view.ScrollOffset);

            view.ScrollToInputLine();
            Assert.Equal("marker", ReadVisibleRows(view, buffer)[^2]);
        }

        [AvaloniaFact]
        public void GrowthDuringSmoothScroll_PreservesTheRequestedAnchor()
        {
            var (view, buffer) = CreateViewAtBottom();
            view.ScrollByWheelLines(10 * WheelLinesPerNotch);
            Assert.True(view.AdvanceScrollAnimation());

            new AnsiParser(buffer).Process("marker\r\n");
            view.EnsureCursorVisible();
            RunAnimationToCompletion(view);

            Assert.Equal("line 8", ReadVisibleRows(view, buffer)[0]);
        }

        [AvaloniaFact]
        public void ContinuousOutput_StillLetsSmoothScrollingReachTheLiveBottom()
        {
            var (view, buffer) = CreateViewAtBottom();
            view.ScrollOffset = WheelLinesPerNotch;
            view.ScrollByWheelLines(-WheelLinesPerNotch);
            var parser = new AnsiParser(buffer);

            for (int frame = 0; frame < WheelLinesPerNotch; frame++)
            {
                parser.Process($"marker {frame}\r\n");
                view.EnsureCursorVisible();
                view.AdvanceScrollAnimation();
            }

            Assert.Equal(0, view.ScrollOffset);
            Assert.False(view.AdvanceScrollAnimation());
            Assert.Equal("marker 2", ReadVisibleRows(view, buffer)[^2]);
        }

        [AvaloniaFact]
        public void OutputEvictingAnOlderPage_HoldsARetainedAnchor()
        {
            var (view, buffer) = CreateViewAtBottom();
            const int budgetRows = 128;
            buffer.MaxHistory = budgetRows;
            var parser = new AnsiParser(buffer);
            int extra = budgetRows - buffer.Scrollback.Count;
            for (int i = 0; i < extra; i++)
            {
                parser.Process($"extra {i}\r\n");
            }
            Assert.Equal(0, buffer.Scrollback.TotalRowsEvicted);

            view.ScrollOffset = 30;
            string[] anchor = ReadVisibleRows(view, buffer);
            parser.Process("marker\r\n");
            view.EnsureCursorVisible();

            Assert.True(buffer.Scrollback.TotalRowsEvicted > 0);
            Assert.Equal(anchor, ReadVisibleRows(view, buffer));
        }

        [AvaloniaFact]
        public void ClearedHistory_ClampsAnAnchorFromThePreviousGeneration()
        {
            var (view, buffer) = CreateViewAtBottom();
            view.ScrollOffset = 30;
            var parser = new AnsiParser(buffer);
            parser.Process("\u001b[3J");
            parser.Process("marker\r\n");
            view.EnsureCursorVisible();

            Assert.Equal(1, view.ScrollOffset);
            Assert.Equal(1, view.MaxScrollOffset);
        }

        private static string[] ReadVisibleRows(TerminalView view, TerminalBuffer buffer)
        {
            int start = buffer.TotalLines - buffer.Rows - view.ScrollOffset;
            buffer.Lock.EnterReadLock();
            try
            {
                return Enumerable.Range(start, buffer.Rows).Select(row => new string(Enumerable.Range(0, buffer.Cols)
                    .Select(col => buffer.GetCellAbsolute(col, row).Character).ToArray()).TrimEnd(' ', '\0')).ToArray();
            }
            finally { buffer.Lock.ExitReadLock(); }
        }

        [AvaloniaFact]
        public void ScrollToInputLine_WhileWheelAnimationInFlight_CancelsTheGesture()
        {
            var (view, _) = CreateViewAtBottom();

            view.ScrollByWheelLines(3 * WheelLinesPerNotch);
            Assert.True(view.AdvanceScrollAnimation(), "first frame should leave the gesture in flight");

            view.ScrollToInputLine();

            Assert.Equal(0, view.ScrollOffset);
            Assert.False(view.AdvanceScrollAnimation(), "an external seek must retire the wheel target");
            Assert.Equal(0, view.ScrollOffset);
        }
    }
}
