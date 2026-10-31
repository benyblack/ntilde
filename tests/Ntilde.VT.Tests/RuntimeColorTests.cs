using Ntilde.VT;
using Ntilde.Replay;
using System.Text;

namespace Ntilde.VT.Tests;

public sealed class RuntimeColorTests
{
    private const string Esc = "\u001b";
    private static string Osc(string value) => Esc + "]" + value + Esc + "\\";

    [Theory]
    [InlineData("#123456", "1212/3434/5656")]
    [InlineData("#abc", "aaaa/bbbb/cccc")]
    [InlineData("#123456789", "1212/4545/7878")]
    [InlineData("#123456789abc", "1212/5656/9a9a")]
    [InlineData("rgb:1/2/f", "1111/2222/ffff")]
    [InlineData("rgb:1234/5678/9abc", "1212/5656/9a9a")]
    [InlineData("rgbi:1/0/0.5", "ffff/0000/7f7f")]
    public void Dynamic_colors_can_be_set_then_queried(string value, string rgb)
    {
        var parser = new AnsiParser(new TerminalBuffer(10, 3));
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process(Osc("10;" + value) + Osc("10;?"));
        Assert.Equal(new[] { Osc("10;rgb:" + rgb) }, replies);
    }

    [Theory]
    [InlineData("#gg0000")]
    [InlineData("rgb:10000/0/0")]
    [InlineData("rgb:1/2")]
    [InlineData("rgbi:NaN/0/0")]
    [InlineData("rgbi:2/0/0")]
    [InlineData("not-a-color")]
    public void Malformed_colors_leave_the_previous_color_and_cannot_inject_replies(string value)
    {
        var parser = new AnsiParser(new TerminalBuffer(10, 3)) { DefaultForeground = TermColor.White };
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process(Osc("10;" + value) + Osc("10;?"));
        Assert.Equal(new[] { Osc("10;rgb:ffff/ffff/ffff") }, replies);
    }

    [Fact]
    public void Nested_stack_restores_runtime_colors_and_empty_pop_is_silent()
    {
        var parser = new AnsiParser(new TerminalBuffer(10, 3));
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process(Osc("10;#102030") + Osc("30001") + Osc("10;#405060") + Osc("30001") +
            Osc("10;#708090") + Osc("30101") + Osc("10;?") + Osc("30101") + Osc("10;?") + Osc("30101"));
        Assert.Equal(new[] { Osc("10;rgb:4040/5050/6060"), Osc("10;rgb:1010/2020/3030") }, replies);
    }

    [Fact]
    public void Overrides_reach_render_snapshots_and_preserve_truecolor_cells()
    {
        var buffer = new TerminalBuffer(10, 3);
        var parser = new AnsiParser(buffer);
        parser.Process("D" + Esc + "[38;5;200mP" + Esc + "[38;2;1;2;3mT" +
            Osc("10;#112233;#445566;#778899") + Osc("17;#abcdef") + Osc("19;#fedcba") + Osc("4;200;#123456"));
        using var snapshot = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest { ViewportCols = 10, ViewportRows = 3, Isolated = true }, out _);
        Assert.Equal(0xff112233u, snapshot.Theme.Foreground.ToUint());
        Assert.Equal(0xff445566u, snapshot.Theme.Background.ToUint());
        Assert.Equal(0xff778899u, snapshot.Theme.CursorColor.ToUint());
        Assert.Equal(0xffabcdefu, snapshot.Theme.Source!.SelectionBackground!.Value.ToUint());
        Assert.Equal(0xfffedcbau, snapshot.Theme.Source.SelectionForeground!.Value.ToUint());
        Assert.Equal(256, snapshot.Theme.AnsiPalette.Length);
        Assert.Equal(0xff123456u, snapshot.Theme.AnsiPalette[200].ToUint());
        Assert.True(snapshot.RowsData.Array[0].Cells[0].IsDefaultForeground);
        Assert.Equal(200, snapshot.RowsData.Array[0].Cells[1].FgIndex);
        Assert.Equal(0xff010203u, snapshot.RowsData.Array[0].Cells[2].Foreground.ToUint());
        Assert.NotEqual(0xff112233u, buffer.Theme.Foreground.ToUint());
    }

    [Fact]
    public void Palette_and_dynamic_resets_return_to_host_defaults()
    {
        var parser = new AnsiParser(new TerminalBuffer(10, 3)) { DefaultForeground = TermColor.White };
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process(Osc("10;#123456") + Osc("4;200;#abcdef") + Osc("110") + Osc("104;200") + Osc("10;?") + Osc("4;200;?"));
        Assert.Equal(new[] { Osc("10;rgb:ffff/ffff/ffff"), Osc("4;200;rgb:ffff/0000/d7d7") }, replies);
    }

    [Fact]
    public void Serialized_attach_retains_overrides_stack_and_partial_OSC_without_import_notifications()
    {
        var source = new TerminalBuffer(10, 3);
        var parser = new AnsiParser(source);
        parser.Process(Osc("10;#123456") + Osc("30001") + Osc("10;#abcdef") + Esc + "]301");
        var state = source.ExportState(0);
        state.Parser = parser.ExportState();
        var target = new TerminalBuffer(10, 3);
        var restored = new AnsiParser(target) { DefaultForeground = TermColor.White };
        var replies = new List<string>();
        restored.OnResponse = replies.Add;
        TerminalStateTransfer.Restore(target, restored, TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(state)));
        Assert.Empty(replies);
        restored.Process("01" + Esc + "\\" + Osc("10;?"));
        Assert.Equal(new[] { Osc("10;rgb:1212/3434/5656") }, replies);
    }

    [Fact]
    public void Push_pop_saves_every_palette_slot_cursor_and_selection()
    {
        var buffer = new TerminalBuffer(10, 3);
        var parser = new AnsiParser(buffer);
        for (int i = 0; i < 256; i++) parser.Process(Osc($"4;{i};#{i:x2}1234"));
        parser.Process(Osc("10;#123456;#234567;#345678") + Osc("17;#456789") + Osc("19;#56789a") + Osc("30001"));
        parser.Process(Osc("104") + Osc("110") + Osc("111") + Osc("112") + Osc("117") + Osc("119") + Osc("30101"));
        var state = buffer.ExportState(0).Colors!;
        for (int i = 0; i < 256; i++) Assert.Equal(0xff001234u | (uint)(i << 16), state.Current.Palette[i]);
        Assert.Equal(0xff123456u, state.Current.Foreground);
        Assert.Equal(0xff234567u, state.Current.Background);
        Assert.Equal(0xff345678u, state.Current.Cursor);
        Assert.Equal(0xff456789u, state.Current.SelectionBackground);
        Assert.Equal(0xff56789au, state.Current.SelectionForeground);
        Assert.Empty(state.Stack);
    }

    [Fact]
    public void Stack_is_bounded_and_discards_the_oldest_frame()
    {
        var buffer = new TerminalBuffer(10, 3);
        var parser = new AnsiParser(buffer);
        for (int i = 0; i < 100; i++) parser.Process(Osc($"10;#{i:x2}0000") + Osc("30001"));
        Assert.Equal(TerminalRuntimeColors.MaxStackDepth, buffer.ExportState(0).Colors!.Stack.Length);
        for (int i = 99; i >= 84; i--)
        {
            parser.Process(Osc("30101"));
            Assert.Equal(0xff000000u | (uint)(i << 16), buffer.ExportState(0).Colors!.Current.Foreground);
        }
        parser.Process(Osc("30101"));
        Assert.Equal(0xff540000u, buffer.ExportState(0).Colors!.Current.Foreground);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Invalid_color_snapshots_are_rejected_before_any_resize_or_state_change(int kind)
    {
        var source = new TerminalBuffer(20, 4);
        var state = source.ExportState(0);
        state.Colors = new TerminalRuntimeColors();
        switch (kind)
        {
            case 0: state.Colors!.Current.Palette = new uint?[255]; break;
            case 1: state.Colors!.Stack = new TerminalColorOverrides[17]; break;
            case 2: state.Colors!.Current = null!; break;
            case 3: state.Colors!.Stack = null!; break;
            case 4: state.Colors!.Stack = [null!]; break;
        }
        var target = new TerminalBuffer(10, 3);
        var parser = new AnsiParser(target);
        parser.Process(Osc("10;#123456"));
        Assert.Throws<InvalidOperationException>(() => TerminalStateTransfer.Restore(target, parser, state));
        Assert.Equal((10, 3), (target.Cols, target.Rows));
        Assert.Equal(0xff123456u, target.ExportState(0).Colors!.Current.Foreground);
    }

    [Fact]
    public void Legacy_state_clears_overrides_and_exports_do_not_alias_live_arrays()
    {
        var buffer = new TerminalBuffer(10, 3);
        var parser = new AnsiParser(buffer);
        parser.Process(Osc("10;#123456") + Osc("4;200;#abcdef") + Osc("30001"));
        var exported = buffer.ExportState(0);
        exported.Colors!.Current.Foreground = 0xff000000;
        exported.Colors.Stack[0].Palette[200] = 0xff000000;
        Assert.Equal(0xff123456u, buffer.ExportState(0).Colors!.Current.Foreground);
        Assert.Equal(0xffabcdefu, buffer.ExportState(0).Colors!.Stack[0].Palette[200]);
        exported.Colors = null;
        TerminalStateTransfer.Restore(buffer, parser, exported);
        Assert.Null(buffer.ExportState(0).Colors);
    }

    [Theory]
    [InlineData("\u001b[!p", true)]
    [InlineData("\u001bc", false)]
    public void Soft_reset_preserves_colors_but_RIS_clears_the_overrides_and_stack(string reset, bool retained)
    {
        var buffer = new TerminalBuffer(10, 3);
        var parser = new AnsiParser(buffer);
        parser.Process(Osc("10;#123456") + Osc("30001") + Esc + "[?1049h" + reset);
        var colors = buffer.ExportState(0).Colors ?? new TerminalRuntimeColors();
        Assert.Equal(retained ? 0xff123456u : (uint?)null, colors.Current.Foreground);
        Assert.Equal(retained ? 1 : 0, colors.Stack.Length);
    }

    [Fact]
    public void Runtime_background_masks_host_changes_until_reset_and_notifies_only_class_transitions()
    {
        var parser = new AnsiParser(new TerminalBuffer(10, 3)) { DefaultBackground = TermColor.Black };
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process(Esc + "[?2031h" + Osc("30001") + Osc("11;#ffffff") + Osc("11;#ffffff"));
        Assert.Equal(new[] { Esc + "[?997;2n" }, replies);
        replies.Clear();
        parser.DefaultBackground = TermColor.FromRgb(20, 20, 20);
        Assert.Empty(replies);
        parser.Process(Osc("30101") + Esc + "[?996n");
        Assert.Equal(new[] { Esc + "[?997;1n", Esc + "[?997;1n" }, replies);
    }

    [Fact]
    public void Color_mutations_invalidate_row_pictures_and_the_live_snapshot_baseline()
    {
        var buffer = new TerminalBuffer(10, 3);
        var parser = new AnsiParser(buffer);
        parser.Process("Text");
        var request = new RenderSnapshotRequest { ViewportCols = 10, ViewportRows = 3 };
        using var first = buffer.CaptureRenderSnapshot(request, out _);
        uint revision = first.RowsData.Array[0].Revision;
        parser.Process(Osc("4;200;#123456"));
        using var next = buffer.CaptureRenderSnapshot(request, out _);
        Assert.NotEqual(revision, next.RowsData.Array[0].Revision);
        Assert.Equal(3, next.DirtySpans.Length);
        using var unchanged = buffer.CaptureRenderSnapshot(request, out _);
        Assert.Equal(0, unchanged.DirtySpans.Length);
    }

    [Theory]
    [InlineData("\u001b]", "\a")]
    [InlineData("\u001b]", "\u001b\\")]
    [InlineData("\u009d", "\u009c")]
    public void Color_commands_support_string_terminators_and_every_input_split(string osc, string end)
    {
        string input = osc + "10;#123456" + end + osc + "30001" + end + osc + "10;#abcdef" + end + osc + "30101" + end + osc + "10;?" + end;
        for (int cut = 1; cut < input.Length; cut++)
        {
            var parser = new AnsiParser(new TerminalBuffer(10, 3));
            var replies = new List<string>();
            parser.OnResponse = replies.Add;
            parser.Process(input[..cut]);
            parser.Process(input[cut..]);
            Assert.Equal(new[] { Osc("10;rgb:1212/3434/5656") }, replies);
        }
    }


    [Fact]
    [Trait("Category", "Replay")]
    public async Task Recorded_checkpoint_retains_the_palette_and_stack_before_replaying_the_tail()
    {
        string path = Path.GetTempFileName();
        try
        {
            var buffer = new TerminalBuffer(10, 3);
            var parser = new AnsiParser(buffer);
            parser.Process(Osc("10;#123456") + Osc("4;200;#abcdef") + Osc("30001") + Osc("10;#abcdef") + Esc + "[31;");
            using (var recorder = new PtyRecorder(path, 10, 3))
            {
                recorder.RecordSnapshot(buffer, parser);
                byte[] tail = Encoding.UTF8.GetBytes("1mX" + Osc("30101") + Osc("10;?") + Osc("4;200;?"));
                recorder.RecordChunkAt(10, tail, tail.Length);
            }
            var target = new TerminalBuffer(10, 3);
            var restored = new AnsiParser(target) { DefaultForeground = TermColor.White };
            var replies = new List<string>();
            restored.OnResponse = replies.Add;
            bool applied = false;
            await new ReplayReader(path).RunWithResultAsync(
                data => { restored.Process(Encoding.UTF8.GetString(data)); return Task.CompletedTask; },
                onSnapshotCallback: state =>
                {
                    ReplayRunner.ApplySnapshot(target, restored, state);
                    Assert.Empty(replies);
                    applied = true;
                    return Task.CompletedTask;
                }, ct: TestContext.Current.CancellationToken);
            Assert.True(applied);
            Assert.True(target.IsBold);
            Assert.Equal(new[] { Osc("10;rgb:1212/3434/5656"), Osc("4;200;rgb:abab/cdcd/efef") }, replies);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Oversized_color_requests_and_qualified_stack_commands_are_ignored_without_partial_changes()
    {
        var buffer = new TerminalBuffer(10, 3);
        var parser = new AnsiParser(buffer);
        parser.Process(Osc("10;#123456") + Osc("4;1;#abcdef;" + new string(';', 40000)) +
            Osc("10;#000000;" + new string(';', 40000)) + Osc("30001;invalid"));
        var state = buffer.ExportState(0).Colors!;
        Assert.Equal(0xff123456u, state.Current.Foreground);
        Assert.Null(state.Current.Palette[1]);
        Assert.Empty(state.Stack);
    }

}
