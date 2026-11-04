using System.Collections.Generic;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Moq;
using Ntilde.Controls;
using Ntilde.Pty;
using Ntilde.Shell;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.Input
{
    /// <summary>
    /// Issue #828: alternate scroll (?1007). On the alternate screen with mouse reporting off, the
    /// real wheel handler (<see cref="TerminalView"/>'s <c>OnPointerWheelChanged</c>, reached by
    /// raising a genuine <see cref="PointerWheelEventArgs"/>) sends one cursor key per notch,
    /// encoded from the pane's own DECCKM state. Every assertion is on the exact bytes sent.
    /// </summary>
    public class AlternateScrollWheelTests
    {
        private sealed class Harness
        {
            public required TerminalView View { get; init; }
            public required TerminalBuffer Buffer { get; init; }
            public required AnsiParser Parser { get; init; }
            public List<string> Sent { get; } = new();

            public void Wheel(double deltaY)
            {
                using var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
                var properties = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other);
                var args = new PointerWheelEventArgs(View, pointer, View, new Point(1, 1), 0, properties, KeyModifiers.None, new Vector(0, deltaY));
                View.RaiseEvent(args);
            }

            public string SentText => string.Concat(Sent);
        }

        private static Harness Create(string setup, int scrollbackLines = 0)
        {
            var view = new TerminalView();
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);
            for (int i = 0; i < scrollbackLines; i++) parser.Process($"line {i}\r\n");
            parser.Process(setup);
            var harness = new Harness { View = view, Buffer = buffer, Parser = parser };
            var session = new Mock<ITerminalSession>();
            session.SetupGet(s => s.IsProcessRunning).Returns(true);
            session.Setup(s => s.SendInput(It.IsAny<string>())).Callback<string>(harness.Sent.Add);
            view.SetBuffer(buffer);
            view.SetSession(session.Object);
            return harness;
        }

        [AvaloniaFact]
        public void Normal_cursor_mode_sends_CSI_A_and_CSI_B_one_per_notch()
        {
            var h = Create("\u001b[?1049h");

            h.Wheel(1);
            Assert.Equal(new[] { "\u001b[A" }, h.Sent);
            Assert.Equal(0, h.View.ScrollOffset);

            h.Sent.Clear();
            h.Wheel(-2);
            Assert.Equal(new[] { "\u001b[B\u001b[B" }, h.Sent);
        }

        [AvaloniaFact]
        public void Application_cursor_mode_sends_SS3_A_and_SS3_B()
        {
            var h = Create("\u001b[?1049h\u001b[?1h");

            h.Wheel(2);
            Assert.Equal("\u001bOA\u001bOA", h.SentText);

            h.Sent.Clear();
            h.Wheel(-1);
            Assert.Equal("\u001bOB", h.SentText);
        }

        [AvaloniaFact]
        public void Mouse_reporting_wins_over_alternate_scroll()
        {
            var h = Create("\u001b[?1049h\u001b[?1h\u001b[?1000h\u001b[?1006h");

            h.Wheel(1);
            h.Wheel(-1);

            Assert.Equal(new[] { "\u001b[<64;1;1M", "\u001b[<65;1;1M" }, h.Sent);
        }

        [AvaloniaFact]
        public void Main_screen_scrolls_the_scrollback_and_sends_nothing()
        {
            var h = Create(string.Empty, scrollbackLines: 60);

            h.Wheel(1);
            for (int frame = 0; frame < 200 && h.View.AdvanceScrollAnimation(); frame++) { }

            Assert.Empty(h.Sent);
            Assert.Equal(3, h.View.ScrollOffset); // WheelLinesPerNotch default, as before #828
        }

        [AvaloniaFact]
        public void High_resolution_deltas_accumulate_to_one_key_per_whole_notch()
        {
            var h = Create("\u001b[?1049h");

            for (int i = 0; i < 3; i++) h.Wheel(0.25);
            Assert.Empty(h.Sent);
            h.Wheel(0.25);
            Assert.Equal("\u001b[A", h.SentText);

            // The fraction carries: 0.75 more is still short of a notch, 0.5 after it completes one.
            h.Sent.Clear();
            h.Wheel(0.75);
            Assert.Empty(h.Sent);
            h.Wheel(0.5);
            Assert.Equal("\u001b[A", h.SentText);

            // Reversing discards the stale upward remainder rather than climbing back through it.
            h.Sent.Clear();
            h.Wheel(-0.5);
            h.Wheel(-0.5);
            Assert.Equal("\u001b[B", h.SentText);
        }

        [AvaloniaFact]
        public void Application_reset_disables_translation_and_the_wheel_stays_inert()
        {
            var h = Create("\u001b[?1049h\u001b[?1007l");

            h.Wheel(1);

            Assert.Empty(h.Sent);
            Assert.Equal(0, h.View.ScrollOffset);
        }

        [AvaloniaFact]
        public void Host_default_off_is_overridden_by_the_applications_set()
        {
            var h = Create("\u001b[?1049h");
            h.Buffer.Modes.AlternateScrollDefault = false;

            h.Wheel(1);
            Assert.Empty(h.Sent);

            h.Parser.Process("\u001b[?1007h");
            h.Wheel(1);
            Assert.Equal("\u001b[A", h.SentText);

            // RIS returns to the host default (off) - and to the main screen.
            h.Sent.Clear();
            h.Parser.Process("\u001bc\u001b[?1049h");
            h.Wheel(1);
            Assert.Empty(h.Sent);
        }

        [AvaloniaFact]
        public void Sent_keys_are_announced_for_input_observers()
        {
            var h = Create("\u001b[?1049h");
            int announced = 0;
            h.View.AlternateScrollKeysSent += () => announced++;

            h.Wheel(0.5);
            Assert.Equal(0, announced);
            h.Wheel(0.5);
            Assert.Equal(1, announced);
        }

        [AvaloniaFact]
        public void Pane_setting_is_carried_into_the_buffers_mode_default_and_DECRQM()
        {
            using var pane = new TerminalPane();
            pane.CreateAndWireParser();
            var replies = new List<string>();
            pane.Parser!.OnResponse += replies.Add;

            var off = new TerminalSettings { AlternateScroll = false };
            Assert.False(pane.BuildEffectiveSettings(off).AlternateScroll);
            pane.ApplySettings(off);
            Assert.False(pane.Buffer!.Modes.AlternateScrollDefault);
            pane.Parser.Process("\u001b[?1007$p");

            // The application's choice survives a later settings change; RIS hands control back.
            pane.Parser.Process("\u001b[?1007h");
            pane.ApplySettings(new TerminalSettings { AlternateScroll = false });
            pane.Parser.Process("\u001b[?1007$p");
            pane.Parser.Process("\u001bc");
            pane.Parser.Process("\u001b[?1007$p");

            pane.ApplySettings(new TerminalSettings { AlternateScroll = true });
            Assert.True(pane.BuildEffectiveSettings(new TerminalSettings()).AlternateScroll);
            pane.Parser.Process("\u001b[?1007$p");

            Assert.Equal(new[] { "\u001b[?1007;2$y", "\u001b[?1007;1$y", "\u001b[?1007;2$y", "\u001b[?1007;1$y" }, replies);
        }
    }
}
