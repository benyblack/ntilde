using Ntilde.VT;

namespace Ntilde.VT.Tests;

/// <summary>
/// rxvt's special-color protocol (OSC 5 set/query, OSC 105 reset) over its own five-slot resource set —
/// bold, underline, blink, reverse, italic — kept strictly separate from the OSC 4 palette, the OSC 10/11/12/17/19
/// dynamic colors and the kitty OSC 21 keys. The App stores, queries and resets these colors; it renders
/// attribute emphasis as flags, so a stored special color is never render-applied (pinned here, not assumed).
/// </summary>
public sealed class Osc5SpecialColorTests
{
    private const string Esc = "\u001b";
    private static string Osc(string value) => Esc + "]" + value + Esc + "\\";

    private static (AnsiParser Parser, TerminalBuffer Buffer, List<string> Replies) Create()
    {
        var buffer = new TerminalBuffer(10, 3);
        var parser = new AnsiParser(buffer) { DefaultForeground = TermColor.White, DefaultBackground = TermColor.Black };
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        return (parser, buffer, replies);
    }

    [Fact]
    public void Query_reports_the_default_foreground_with_a_standard_terminator()
    {
        var (parser, _, replies) = Create();
        parser.Process(Osc("5;0;?"));
        Assert.Equal(new[] { Osc("5;0;rgb:ffff/ffff/ffff") }, replies);
    }

    [Fact]
    public void Bel_terminated_request_gets_a_standard_terminator_reply()
    {
        var (parser, _, replies) = Create();
        parser.Process(Esc + "]5;0;?\u0007");
        Assert.Equal(new[] { Osc("5;0;rgb:ffff/ffff/ffff") }, replies);
    }

    [Theory]
    [InlineData("#123456", "rgb:1212/3434/5656")]
    [InlineData("#abc", "rgb:aaaa/bbbb/cccc")]
    [InlineData("rgb:f/0/0", "rgb:ffff/0000/0000")]
    [InlineData("rgb:1234/5678/9abc", "rgb:1212/5656/9a9a")]
    [InlineData("rgbi:1/0/0.5", "rgb:ffff/0000/7f7f")]
    public void Set_then_query_reports_the_stored_color_in_the_standard_frame(string value, string expected)
    {
        var (parser, _, replies) = Create();
        parser.Process(Osc("5;0;" + value + ";0;?"));
        Assert.Equal(new[] { Osc("5;0;" + expected) }, replies);
    }

    [Fact]
    public void Each_of_the_five_slots_is_stored_and_reported_separately()
    {
        var (parser, _, replies) = Create();
        parser.Process(Osc("5;0;#111111;1;#222222;2;#333333;3;#444444;4;#555555"));
        parser.Process(Osc("5;0;?;1;?;2;?;3;?;4;?"));
        Assert.Equal(new[]
        {
            Osc("5;0;rgb:1111/1111/1111"), Osc("5;1;rgb:2222/2222/2222"), Osc("5;2;rgb:3333/3333/3333"),
            Osc("5;3;rgb:4444/4444/4444"), Osc("5;4;rgb:5555/5555/5555")
        }, replies);
    }

    [Fact]
    public void Querying_the_same_slot_twice_in_one_sequence_answers_twice()
    {
        var (parser, _, replies) = Create();
        parser.Process(Osc("5;0;#101010;0;?;0;?"));
        Assert.Equal(new[] { Osc("5;0;rgb:1010/1010/1010"), Osc("5;0;rgb:1010/1010/1010") }, replies);
    }

    [Fact]
    public void Set_only_sequence_stays_silent()
    {
        var (parser, _, replies) = Create();
        parser.Process(Osc("5;0;#121212"));
        Assert.Empty(replies);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("9")]
    [InlineData("255")]
    [InlineData("bold")]
    [InlineData("foreground")]
    [InlineData("-1")]
    public void Unknown_selectors_change_nothing_and_stay_silent(string selector)
    {
        var (parser, buffer, replies) = Create();
        parser.Process(Osc($"5;{selector};#121212;{selector};?"));
        Assert.Empty(replies);
        buffer.Lock.EnterReadLock();
        try
        {
            Assert.All(Enumerable.Range(0, TerminalColorOverrides.SpecialColorCount),
            index => Assert.Null(buffer.CopyColorOverrides().SpecialColors[index]));
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void A_valid_pair_in_a_sequence_with_unknown_selectors_still_applies()
    {
        var (parser, _, replies) = Create();
        parser.Process(Osc("5;bold;#121212;0;#232323;0;?"));
        Assert.Equal(new[] { Osc("5;0;rgb:2323/2323/2323") }, replies);
    }

    [Fact]
    public void Reset_one_slot_returns_it_to_the_default_and_leaves_the_others()
    {
        var (parser, buffer, replies) = Create();
        parser.Process(Osc("5;0;#101010;1;#202020"));
        parser.Process(Osc("105;0"));
        parser.Process(Osc("5;0;?;1;?"));
        Assert.Equal(new[] { Osc("5;0;rgb:ffff/ffff/ffff"), Osc("5;1;rgb:2020/2020/2020") }, replies);
        Assert.Null(buffer.ExportState(0).Colors!.Current.SpecialColors[0]);
    }

    [Fact]
    public void Bare_reset_returns_every_slot_to_the_default()
    {
        var (parser, _, replies) = Create();
        parser.Process(Osc("5;0;#101010;1;#202020;2;#303030;3;#404040;4;#505050"));
        parser.Process(Osc("105"));
        parser.Process(Osc("5;0;?;1;?;2;?;3;?;4;?"));
        Assert.Equal(5, replies.Count);
        Assert.All(replies, reply => Assert.Contains("rgb:ffff/ffff/ffff", reply));
    }

    [Fact]
    public void Reset_ignores_unknown_selectors_and_noise()
    {
        var (parser, _, replies) = Create();
        parser.Process(Osc("5;0;#101010;1;#202020"));
        parser.Process(Osc("105;0;bold;5;-3;0"));
        parser.Process(Osc("5;0;?;1;?"));
        Assert.Equal(new[] { Osc("5;0;rgb:ffff/ffff/ffff"), Osc("5;1;rgb:2020/2020/2020") }, replies);
    }

    [Fact]
    public void Special_colors_are_not_interchangeable_with_the_dynamic_colors()
    {
        var (parser, _, replies) = Create();
        parser.Process(Osc("10;#abcdef")); // OSC 10 sets the dynamic foreground.
        parser.Process(Osc("5;0;?"));
        Assert.Equal(new[] { Osc("5;0;rgb:ffff/ffff/ffff") }, replies);
        parser.Process(Osc("5;0;#101010"));
        parser.Process(Osc("10;?"));
        Assert.Equal(new[] { Osc("5;0;rgb:ffff/ffff/ffff"), Osc("10;rgb:abab/cdcd/efef") }, replies);
    }

    [Fact]
    public void Special_colors_are_not_interchangeable_with_the_palette_or_kitty_keys()
    {
        var (parser, _, replies) = Create();
        parser.Process(Osc("4;1;#121212")); // OSC 4 sets palette slot 1.
        parser.Process(Osc("21;cursor=#131313")); // OSC 21 sets the kitty cursor key.
        parser.Process(Osc("5;1;?;2;?"));
        Assert.Equal(new[] { Osc("5;1;rgb:ffff/ffff/ffff"), Osc("5;2;rgb:ffff/ffff/ffff") }, replies);
        parser.Process(Osc("5;1;#141414"));
        parser.Process(Osc("4;1;?") + Osc("21;cursor=?"));
        Assert.Equal(new[]
        {
            Osc("5;1;rgb:ffff/ffff/ffff"), Osc("5;2;rgb:ffff/ffff/ffff"), Osc("4;1;rgb:1212/1212/1212"), Osc("21;cursor=rgb:13/13/13")
        }, replies);
    }

    [Fact]
    public void A_stored_special_color_never_reaches_the_render_theme()
    {
        var (parser, buffer, _) = Create();
        byte[] before = TerminalStateSerializer.ToBytes(buffer.ExportState(0));
        using var beforeSnapshot = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest { ViewportCols = 10, ViewportRows = 3, Isolated = true }, out _);
        parser.Process(Osc("5;0;#ff00ff;1;#00ff00")); // Bold and underline colors, both render-unmapped.
        using var afterSet = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest { ViewportCols = 10, ViewportRows = 3, Isolated = true }, out _);
        Assert.Equal(beforeSnapshot.Theme.Foreground, afterSet.Theme.Foreground);
        Assert.Equal(beforeSnapshot.Theme.Background, afterSet.Theme.Background);
        Assert.Equal(beforeSnapshot.Theme.CursorColor, afterSet.Theme.CursorColor);
        Assert.False(afterSet.Theme.HasForegroundOverride);
        Assert.False(afterSet.Theme.HasBackgroundOverride);
        Assert.False(afterSet.Theme.HasCursorOverride);
        Assert.False(afterSet.Theme.HasSelectionBackgroundOverride);
        Assert.False(afterSet.Theme.HasSelectionForegroundOverride);
        parser.Process(Osc("105"));
        Assert.Equal(before, TerminalStateSerializer.ToBytes(buffer.ExportState(0)));
    }

    [Fact]
    public void Special_color_changes_preserve_row_revisions_and_the_render_baseline()
    {
        var (parser, buffer, _) = Create();
        parser.Process("Text");
        var request = new RenderSnapshotRequest { ViewportCols = 10, ViewportRows = 3 };
        using var before = buffer.CaptureRenderSnapshot(request, out _);
        uint revision = before.RowsData.Array[0].Revision;
        foreach (string sequence in new[] { "5;0;#123456", "5;0;#abcdef", "30001", "105", "30101", "105" })
        {
            parser.Process(Osc(sequence));
            using var after = buffer.CaptureRenderSnapshot(request, out _);
            Assert.Equal(revision, after.RowsData.Array[0].Revision);
            Assert.Equal(0, after.DirtySpans.Length);
        }
        parser.Process(Osc("4;200;#123456"));
        using var palette = buffer.CaptureRenderSnapshot(request, out _);
        Assert.NotEqual(revision, palette.RowsData.Array[0].Revision);
        Assert.Equal(3, palette.DirtySpans.Length);
    }

    [Fact]
    public void Oversized_sequences_are_dropped_whole()
    {
        var (parser, buffer, replies) = Create();
        parser.Process(Osc("5;0;#101010;" + new string('#', 32769)));
        buffer.Lock.EnterReadLock();
        try { Assert.Null(buffer.CopyColorOverrides().SpecialColors[0]); }
        finally { buffer.Lock.ExitReadLock(); }
        parser.Process(Osc("5;0;?"));
        Assert.Equal(new[] { Osc("5;0;rgb:ffff/ffff/ffff") }, replies);
    }

    [Fact]
    public void Special_colors_survive_the_runtime_color_transfer()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Osc("5;0;#101010;4;#505050"));
        var restored = new TerminalBuffer(10, 3);
        TerminalRuntimeColors? colors;
        buffer.Lock.EnterReadLock();
        try { colors = buffer.ExportRuntimeColors(); }
        finally { buffer.Lock.ExitReadLock(); }
        restored.Lock.EnterWriteLock();
        try { restored.ImportRuntimeColors(colors); }
        finally { restored.Lock.ExitWriteLock(); }
        var restoredParser = new AnsiParser(restored) { DefaultForeground = TermColor.White };
        var replies = new List<string>();
        restoredParser.OnResponse = replies.Add;
        restoredParser.Process(Osc("5;0;?;1;?;4;?"));
        Assert.Equal(new[]
        {
            Osc("5;0;rgb:1010/1010/1010"), Osc("5;1;rgb:ffff/ffff/ffff"), Osc("5;4;rgb:5050/5050/5050")
        }, replies);
    }

    [Fact]
    public void Push_and_pop_save_and_restore_the_special_colors_with_the_rest_of_the_frame()
    {
        var (parser, _, replies) = Create();
        parser.Process(Osc("5;0;#101010"));
        parser.Process(Osc("30001")); // kitty push
        parser.Process(Osc("5;0;#202020;0;?"));
        parser.Process(Osc("30101")); // kitty pop
        parser.Process(Osc("5;0;?"));
        Assert.Equal(new[] { Osc("5;0;rgb:2020/2020/2020"), Osc("5;0;rgb:1010/1010/1010") }, replies);
    }

    [Fact]
    public void Malformed_pairs_change_nothing_and_valid_pairs_still_apply()
    {
        var (parser, _, replies) = Create();
        parser.Process(Osc("5;0"));
        parser.Process(Osc("5;;?;0;not-a-color;0;#101010;0;?"));
        Assert.Equal(new[] { Osc("5;0;rgb:1010/1010/1010") }, replies);
    }
}
