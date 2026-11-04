using Ntilde.Replay;

namespace Ntilde.VT.Tests;

public sealed class RisCursorVisibilityTests
{
    private const string Esc = "\u001b";

    public static TheoryData<bool, string, bool> InitialStates
    {
        get
        {
            var states = new TheoryData<bool, string, bool>();
            foreach (bool alternate in new[] { false, true })
                foreach (string mode in new[] { "", Esc + "[?25h", Esc + "[?25l" })
                    foreach (bool chunks in new[] { false, true })
                        states.Add(alternate, mode, chunks);
            return states;
        }
    }

    [Theory]
    [MemberData(nameof(InitialStates))]
    public void Ris_makes_cursor_visible_from_default_set_or_reset_on_either_screen(bool alternate, string mode, bool chunks)
    {
        var buffer = new TerminalBuffer(12, 4);
        var parser = new AnsiParser(buffer);
        var replies = new List<string>(); parser.OnResponse = replies.Add;
        parser.Process("MAIN" + Esc + "[?47hALT");
        if (!alternate) parser.Process(Esc + "[?47l");
        parser.Process(Esc + "[2 q" + mode);
        bool initial = mode != Esc + "[?25l";
        Assert.Equal(initial, buffer.Modes.IsCursorVisible);
        parser.Process(Esc + "[?25$p");
        Assert.Equal(Esc + (initial ? "[?25;1$y" : "[?25;2$y"), Assert.Single(replies));
        replies.Clear();

        if (chunks) { parser.Process(Esc); parser.Process("c"); }
        else parser.Process(Esc + "c");

        Assert.True(buffer.Modes.IsCursorVisible);
        Assert.False(buffer.IsAltScreenActive);
        Assert.Equal((0, 0), (buffer.CursorRow, buffer.CursorCol));
        Assert.True(buffer.Modes.CursorStyleIsDefault);
        Assert.True(buffer.Modes.IsCursorBlinkEnabled);
        Assert.All(BufferSnapshot.Capture(buffer).Lines, line => Assert.Equal("", line));
        parser.Process(Esc + "[?25$p");
        Assert.Equal(Esc + "[?25;1$y", Assert.Single(replies));
        parser.Process(Esc + "[?47h");
        Assert.True(buffer.Modes.IsCursorVisible);
        Assert.All(BufferSnapshot.Capture(buffer).Lines, line => Assert.Equal("", line));
        parser.Process(Esc + "[?47l");
        Assert.True(buffer.Modes.IsCursorVisible);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Reset_publishes_visibility_with_the_reset_screen_and_detaches_old_render_snapshots(bool alternate, bool isolated)
    {
        var buffer = new TerminalBuffer(12, 4); var parser = new AnsiParser(buffer);
        parser.Process("MAIN" + Esc + "[?47hALT");
        if (!alternate) parser.Process(Esc + "[?47l");
        parser.Process(Esc + "[?25l");
        var request = new RenderSnapshotRequest { ViewportCols = 12, ViewportRows = 4, Isolated = isolated };
        using var before = buffer.CaptureRenderSnapshot(request, out _);
        Assert.False(before.CursorVisible);

        bool screenSwitchObserved = false;
        buffer.OnScreenSwitched += alt =>
        {
            Assert.False(alt);
            Assert.True(buffer.Lock.IsWriteLockHeld);
            Assert.True(buffer.Modes.IsCursorVisible);
            screenSwitchObserved = true;
        };
        buffer.Reset();
        Assert.Equal(alternate, screenSwitchObserved);
        using var after = buffer.CaptureRenderSnapshot(request, out _);
        Assert.True(after.CursorVisible);
        Assert.Equal((0, 0), (after.CursorRow, after.CursorCol));
        Assert.True(after.CursorStyleIsDefault);
        Assert.True(after.CursorBlinkEnabled);
        Assert.False(before.CursorVisible);
    }

    [Theory]
    [InlineData("\u001b[2J")]
    [InlineData("\u001b[H")]
    [InlineData("\u001b7\u001b8")]
    public void Unrelated_clear_position_and_saved_cursor_controls_preserve_hidden_mode(string control)
    {
        var buffer = new TerminalBuffer(12, 4); var parser = new AnsiParser(buffer);
        parser.Process(Esc + "[?25l" + control);
        Assert.False(buffer.Modes.IsCursorVisible);
        parser.Process(Esc + "[?25h"); Assert.True(buffer.Modes.IsCursorVisible);
        parser.Process(Esc + "[?25l"); Assert.False(buffer.Modes.IsCursorVisible);
    }
}
