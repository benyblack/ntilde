using Ntilde.VT.Tests.StateTransfer;

namespace Ntilde.VT.Tests;

// Issue #831: DECSCNM (CSI ? 5 h/l), the mode terminfo's flash= toggles for the visual bell. The
// buffer owns the flag; the render snapshot carries the presented (swapped) default colors as data.
public class ReverseVideoModeTests
{
    private static readonly TermColor Fg = TermColor.FromRgb(0xEE, 0xEE, 0xEE);
    private static readonly TermColor Bg = TermColor.FromRgb(0x11, 0x11, 0x11);
    private static readonly TermColor Cursor = TermColor.FromRgb(0xFF, 0x00, 0x00);

    private static (TerminalBuffer Buffer, AnsiParser Parser, List<string> Responses) Terminal(TerminalTheme? theme = null)
    {
        var buffer = new TerminalBuffer(cols: 20, rows: 4);
        if (theme != null) buffer.Theme = theme;
        var parser = new AnsiParser(buffer);
        var responses = new List<string>();
        parser.OnResponse = responses.Add;
        return (buffer, parser, responses);
    }

    private static TerminalTheme Theme(TermColor? cursor = null, TermColor? cursorText = null) => new()
    {
        Foreground = Fg,
        Background = Bg,
        CursorColor = cursor ?? Cursor,
        CursorText = cursorText,
    };

    private static string Query(AnsiParser parser, List<string> responses)
    {
        responses.Clear();
        parser.Process("\u001b[?5$p");
        return Assert.Single(responses);
    }

    private static uint Row0Revision(TerminalBuffer buffer)
    {
        buffer.Lock.EnterReadLock();
        try { return buffer.GetRowAbsolute(0)!.Revision; }
        finally { buffer.Lock.ExitReadLock(); }
    }

    private static TerminalCell Cell(TerminalBuffer buffer, int col)
    {
        buffer.Lock.EnterReadLock();
        try { return buffer.GetCell(col, 0); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    private static RenderThemeSnapshot CaptureTheme(TerminalBuffer buffer, bool isolated = false)
    {
        using var snapshot = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest
        {
            ViewportRows = buffer.Rows,
            ViewportCols = buffer.Cols,
            Isolated = isolated,
        }, out _);
        return snapshot.Theme;
    }

    [Fact]
    public void Mode_is_off_by_default_and_follows_set_and_reset()
    {
        var (buffer, parser, responses) = Terminal();
        Assert.False(buffer.Modes.IsReverseVideo);
        Assert.Equal("\u001b[?5;2$y", Query(parser, responses));

        parser.Process("\u001b[?5h");
        Assert.True(buffer.Modes.IsReverseVideo);
        Assert.Equal("\u001b[?5;1$y", Query(parser, responses));

        parser.Process("\u001b[?5l");
        Assert.False(buffer.Modes.IsReverseVideo);
        Assert.Equal("\u001b[?5;2$y", Query(parser, responses));
    }

    [Fact]
    public void Mode_shares_one_DECSET_with_other_modes_and_survives_screen_switches()
    {
        var (buffer, parser, _) = Terminal();
        parser.Process("\u001b[?5;25h");
        Assert.True(buffer.Modes.IsReverseVideo);

        // Screen-wide, not per screen buffer: the alternate screen keeps it, and so does leaving it.
        parser.Process("\u001b[?1049h");
        Assert.True(buffer.Modes.IsReverseVideo);
        parser.Process("\u001b[?1049l");
        Assert.True(buffer.Modes.IsReverseVideo);
        Assert.True(buffer.Modes.Clone().IsReverseVideo);
    }

    [Fact]
    public void RIS_returns_to_normal_video_and_DECSTR_keeps_it()
    {
        var (buffer, parser, _) = Terminal();
        parser.Process("\u001b[?5h\u001b[!p");
        Assert.True(buffer.Modes.IsReverseVideo); // VT510/xterm: DECSTR does not reset DECSCNM

        parser.Process("\u001bc");
        Assert.False(buffer.Modes.IsReverseVideo);
        Assert.False(CaptureTheme(buffer).IsReverseVideo);
    }

    [Fact]
    public void Snapshot_theme_presents_swapped_defaults_and_keeps_the_palette()
    {
        var (buffer, parser, _) = Terminal(Theme());
        RenderThemeSnapshot normal = CaptureTheme(buffer);
        Assert.False(normal.IsReverseVideo);
        Assert.Equal((Fg, Bg), (normal.Foreground, normal.Background));

        parser.Process("\u001b[?5h");
        RenderThemeSnapshot reversed = CaptureTheme(buffer);
        Assert.True(reversed.IsReverseVideo);
        Assert.Equal((Bg, Fg), (reversed.Foreground, reversed.Background));
        Assert.Equal((Bg, Fg), (reversed.Source!.Foreground, reversed.Source.Background));
        Assert.Equal(Cursor, reversed.CursorColor); // an explicit cursor color is kept
        Assert.Equal(normal.AnsiPalette, reversed.AnsiPalette);

        // The host theme object itself is never edited.
        Assert.Equal((Fg, Bg), (buffer.Theme.Foreground, buffer.Theme.Background));

        // Isolated captures (agent screenshots, exports) present the same colors.
        RenderThemeSnapshot isolated = CaptureTheme(buffer, isolated: true);
        Assert.Equal((Bg, Fg, true), (isolated.Foreground, isolated.Background, isolated.IsReverseVideo));

        parser.Process("\u001b[?5l");
        RenderThemeSnapshot restored = CaptureTheme(buffer);
        Assert.Equal((Fg, Bg, false), (restored.Foreground, restored.Background, restored.IsReverseVideo));
        Assert.Same(normal.Source, restored.Source);
    }

    [Fact]
    public void A_cursor_that_tracked_a_default_color_follows_the_swap()
    {
        var (buffer, parser, _) = Terminal(Theme(cursor: Fg, cursorText: Bg));
        parser.Process("\u001b[?5h");
        RenderThemeSnapshot reversed = CaptureTheme(buffer);
        // Drawn on the new (light) background, the cursor takes the old background, and the glyph
        // under a block cursor the old foreground: both keep the contrast they had.
        Assert.Equal(Bg, reversed.CursorColor);
        Assert.Equal(Fg, reversed.Source!.CursorText);
    }

    [Fact]
    public void Runtime_default_color_overrides_are_swapped_with_their_override_flags()
    {
        var (buffer, parser, _) = Terminal(Theme());
        var overrideFg = TermColor.FromRgb(0x12, 0x34, 0x56);
        parser.Process("\u001b]10;#123456\u0007\u001b[?5h");
        RenderThemeSnapshot reversed = CaptureTheme(buffer);
        Assert.Equal((Bg, overrideFg), (reversed.Foreground, reversed.Background));
        Assert.False(reversed.HasForegroundOverride);
        Assert.True(reversed.HasBackgroundOverride);
    }

    [Fact]
    public void Reversed_theme_is_cached_until_the_host_theme_changes()
    {
        var (buffer, parser, _) = Terminal(Theme());
        parser.Process("\u001b[?5h");
        TerminalTheme first = CaptureTheme(buffer).Source!;
        Assert.Same(first, CaptureTheme(buffer).Source);

        // An in-place host theme edit is detected, as the effective-theme cache detects it.
        buffer.Theme.Foreground = TermColor.FromRgb(0xAA, 0xBB, 0xCC);
        TerminalTheme edited = CaptureTheme(buffer).Source!;
        Assert.NotSame(first, edited);
        Assert.Equal(TermColor.FromRgb(0xAA, 0xBB, 0xCC), edited.Background);
    }

    [Fact]
    public void A_mode_change_repaints_every_row_once_and_setting_the_same_value_does_not()
    {
        var (buffer, parser, _) = Terminal(Theme());
        parser.Process("one\r\ntwo\r\nthree");
        var request = new RenderSnapshotRequest { ViewportRows = buffer.Rows, ViewportCols = buffer.Cols };
        buffer.CaptureRenderSnapshot(request, out _).Dispose();
        using (var quiet = buffer.CaptureRenderSnapshot(request, out _)) Assert.Equal(0, quiet.DirtySpans.Length);

        uint revision = Row0Revision(buffer);
        int invalidations = 0;
        buffer.OnInvalidate += () => invalidations++;
        parser.Process("\u001b[?5h");
        Assert.Equal(1, invalidations);
        Assert.NotEqual(revision, Row0Revision(buffer)); // row-picture caches key on it

        using (var flipped = buffer.CaptureRenderSnapshot(request, out _))
        {
            Assert.Equal(buffer.Rows, flipped.DirtySpans.Length);
            for (int i = 0; i < flipped.DirtySpans.Length; i++)
            {
                DirtySpan span = flipped.DirtySpans.Array[i];
                Assert.Equal((0, buffer.Cols), (span.ColStart, span.ColEnd));
            }
        }

        revision = Row0Revision(buffer);
        parser.Process("\u001b[?5h");
        Assert.Equal(1, invalidations);
        Assert.Equal(revision, Row0Revision(buffer));
        using (var unchanged = buffer.CaptureRenderSnapshot(request, out _)) Assert.Equal(0, unchanged.DirtySpans.Length);
    }

    [Fact]
    public void Cells_keep_their_own_colors_and_inverse_flags_under_the_mode()
    {
        var (buffer, parser, _) = Terminal(Theme());
        parser.Process("\u001b[?5hA\u001b[7mB\u001b[0;31mC\u001b[38;2;1;2;3mD");
        var a = Cell(buffer, 0);
        var b = Cell(buffer, 1);
        var c = Cell(buffer, 2);
        var d = Cell(buffer, 3);
        Assert.True(a.IsDefaultForeground && a.IsDefaultBackground && !a.IsInverse);
        Assert.True(b.IsInverse); // the renderer's SGR 7 swap of the presented defaults shows it normal
        Assert.Equal(1, c.FgIndex);
        Assert.Equal(TermColor.FromRgb(1, 2, 3), d.Foreground);
    }

    [Fact]
    public void State_transfer_round_trips_the_mode_and_repaints_on_import()
    {
        var (source, sourceParser, _) = Terminal();
        sourceParser.Process("\u001b[?5hreversed");
        var snapshot = source.ExportState(0);
        snapshot.Parser = sourceParser.ExportState();
        Assert.True(snapshot.Modes.IsReverseVideo);
        snapshot = TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(snapshot));

        var (target, targetParser, responses) = Terminal();
        TerminalStateTransfer.Restore(target, targetParser, snapshot);
        Assert.True(target.Modes.IsReverseVideo);
        TerminalStateAssert.AssertEquivalent("decscnm", source, sourceParser, target, targetParser);
        Assert.True(CaptureTheme(target).IsReverseVideo);
        Assert.Equal("\u001b[?5;1$y", Query(targetParser, responses));
    }

    [Fact]
    public void A_payload_without_the_field_imports_as_normal_video()
    {
        var (source, sourceParser, _) = Terminal();
        var snapshot = source.ExportState(0);
        snapshot.Parser = sourceParser.ExportState();
        string json = System.Text.Encoding.UTF8.GetString(TerminalStateSerializer.ToBytes(snapshot));
        Assert.Contains("\"decscnm\":false", json, StringComparison.Ordinal);
        var legacy = TerminalStateSerializer.FromBytes(System.Text.Encoding.UTF8.GetBytes(json.Replace("\"decscnm\":false,", "", StringComparison.Ordinal)));

        var (target, targetParser, _) = Terminal();
        targetParser.Process("\u001b[?5h");
        TerminalStateTransfer.Restore(target, targetParser, legacy);
        Assert.False(target.Modes.IsReverseVideo);
    }

    [Fact]
    public void The_shared_parity_oracle_detects_different_reverse_video_modes()
    {
        var expected = new TerminalBuffer(10, 5);
        var actual = new TerminalBuffer(10, 5);
        expected.SetReverseVideo(true);
        var error = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            TerminalStateAssert.AssertEquivalent("reverse video", expected, new AnsiParser(expected), actual, new AnsiParser(actual)));
        Assert.Contains("modes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Presented_default_foreground_follows_the_mode()
    {
        var (buffer, parser, _) = Terminal(Theme());
        Assert.Equal(Fg, buffer.PresentedDefaultForeground);
        parser.Process("\u001b[?5h");
        Assert.Equal(Bg, buffer.PresentedDefaultForeground);
    }
}
