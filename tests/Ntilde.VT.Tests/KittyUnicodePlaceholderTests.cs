using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Ntilde.VT.Tests;

/// <summary>Kitty Unicode placeholders (#989): virtual placements, placeholder-cell resolution and what a frame shows.</summary>
public sealed class KittyUnicodePlaceholderTests
{
    private const string Pixel = "/wAA/w==";
    private const string Placeholder = "\U0010EEEE";
    private static string Command(string controls, string payload = "") => $"\x1b_G{controls};{payload}\x1b\\";
    private static string Mark(int number) => char.ConvertFromUtf32(KittyPlaceholders.DiacriticFor(number));

    private static (TerminalBuffer Buffer, AnsiParser Parser, List<string> Replies) Create(int rows = 24, bool retained = false, bool placeholders = false)
    {
        var buffer = new TerminalBuffer(80, rows);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false)
        {
            ImageDecoder = new FixedSizeImageDecoder(1, 1),
            CellWidth = 8,
            CellHeight = 16,
            RetainImageSources = retained,
            RetainedPlaceholders = placeholders
        };
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        return (buffer, parser, replies);
    }

    /// <summary>What the next frame draws: the ordinary placements and the placeholder runs.</summary>
    private static List<RenderImageSnapshot> Frame(TerminalBuffer buffer)
    {
        buffer.Lock.EnterReadLock();
        try { return buffer.GetVisibleImagesSnapshot(buffer.TotalLines - buffer.Rows, buffer.Rows); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    private static List<RenderImageSnapshot> Runs(TerminalBuffer buffer) => Frame(buffer).Where(image => image.GridColumns > 0).ToList();

    private static void Transmit(AnsiParser parser, uint id, string extra = "") =>
        parser.Process(Command($"a=t,f=32,s=1,v=1,i={id}{extra}", Pixel));

    private static void VirtualPlacement(AnsiParser parser, uint id, int columns, int rows, uint placement = 0) =>
        parser.Process(Command($"a=p,U=1,i={id},c={columns},r={rows}{(placement == 0 ? "" : $",p={placement}")}"));

    /// <summary>One placeholder cell: row, column and (optionally) the id's high byte as diacritics.</summary>
    private static string Cell(int? row = null, int? column = null, int? high = null) =>
        Placeholder + (row is { } r ? Mark(r) : "") + (column is { } c ? Mark(c) : "") + (high is { } h ? Mark(h) : "");

    [Fact]
    public void The_diacritic_table_is_kittys_297_numbers_in_ascending_order()
    {
        Assert.Equal(297, KittyPlaceholders.DiacriticCount);
        Assert.Equal(new[] { 0x305, 0x30D, 0x30E, 0x310, 0x312 }, Enumerable.Range(0, 5).Select(KittyPlaceholders.DiacriticFor));
        int[] all = Enumerable.Range(0, 297).Select(KittyPlaceholders.DiacriticFor).ToArray();
        Assert.Equal(all.OrderBy(value => value), all);
        Assert.Equal(all.Length, all.Distinct().Count());
        Assert.Equal(0x1D244, all[^1]);
        Assert.Equal(-1, KittyPlaceholders.DiacriticFor(297));
        Assert.Equal(-1, KittyPlaceholders.DiacriticFor(-1));
    }

    [Fact]
    public void A_virtual_placement_places_nothing_and_leaves_the_cursor_alone()
    {
        var (buffer, parser, replies) = Create();
        parser.Process("\x1b[3;5H");
        Transmit(parser, 42);
        VirtualPlacement(parser, 42, 2, 2);
        Assert.Empty(buffer.Images);
        Assert.Equal(2, buffer.CursorRow);
        Assert.Equal(4, buffer.CursorCol);
        Assert.Equal(new[] { "\x1b_Gi=42;OK\x1b\\", "\x1b_Gi=42;OK\x1b\\" }, replies);
        Assert.Empty(Frame(buffer));
    }

    [Fact]
    public void Transmit_and_virtually_place_in_one_command_draws_nothing_at_the_cursor()
    {
        var (buffer, parser, replies) = Create();
        parser.Process(Command("a=T,U=1,f=32,s=1,v=1,i=7,c=3,r=1", Pixel));
        Assert.Empty(buffer.Images);
        Assert.Equal(0, buffer.CursorCol);
        Assert.Equal("\x1b_Gi=7;OK\x1b\\", Assert.Single(replies));
        parser.Process("\x1b[38;5;7m" + Cell(0, 0) + Cell() + Cell());
        RenderImageSnapshot run = Assert.Single(Runs(buffer));
        Assert.Equal((0, 3), (run.CellX, run.CellWidth));
    }

    [Fact]
    public void Placeholder_cells_of_the_documented_2x2_example_are_two_runs_of_one_grid()
    {
        var (buffer, parser, _) = Create();
        Transmit(parser, 42);
        VirtualPlacement(parser, 42, 2, 2);
        parser.Process($"\x1b[38;5;42m{Cell(0, 0)}{Cell(0, 1)}\x1b[39m\r\n\x1b[38;5;42m{Cell(1, 0)}{Cell(1, 1)}\x1b[39m\r\n");
        List<RenderImageSnapshot> runs = Runs(buffer);
        Assert.Equal(2, runs.Count);
        Assert.Equal(new[] { (0, 0, 2, 0), (0, 1, 2, 1) }, runs.Select(run => (run.CellX, run.CellY, run.CellWidth, run.GridRow)));
        Assert.All(runs, run =>
        {
            Assert.Equal((0, 2, 2, 1), (run.GridColumn, run.GridColumns, run.GridRows, run.CellHeight));
            Assert.True(run.PreserveAspectRatio);
        });
    }

    [Fact]
    public void Omitted_diacritics_are_inherited_from_the_cell_to_the_left()
    {
        var (buffer, parser, _) = Create();
        Transmit(parser, 42);
        VirtualPlacement(parser, 42, 3, 2);
        // Only the first cell of each row says anything: the row. The rest follow from the left neighbour.
        parser.Process($"\x1b[38;5;42m{Cell(0)}{Cell()}{Cell()}\x1b[39m\r\n\x1b[38;5;42m{Cell(1)}{Cell()}{Cell()}\x1b[39m\r\n");
        List<RenderImageSnapshot> runs = Runs(buffer);
        Assert.Equal(new[] { (0, 3, 0), (1, 3, 1) }, runs.Select(run => (run.CellY, run.CellWidth, run.GridRow)));
    }

    [Fact]
    public void A_row_and_column_without_a_high_byte_inherit_it_only_from_the_adjacent_column()
    {
        // 33554474 = 42 + (2 << 24): the first cell names the high byte, the next inherits it, a jump does not.
        var (buffer, parser, _) = Create();
        Transmit(parser, 33554474);
        VirtualPlacement(parser, 33554474, 3, 1);
        parser.Process($"\x1b[38;5;42m{Cell(0, 0, 2)}{Cell(0, 1)}{Cell(0, 1)}\x1b[39m\r\n");
        // Cell 0 (0,0,high 2) and cell 1 (0,1 -> inherits high 2) are the placement; cell 2 repeats column 1, so its
        // high byte is 0 (image 42, which is not transmitted) and it shows nothing.
        RenderImageSnapshot run = Assert.Single(Runs(buffer));
        Assert.Equal((0, 2), (run.CellX, run.CellWidth));
    }

    [Fact]
    public void The_foreground_names_the_image_as_a_palette_index_or_as_24_bit_colour()
    {
        var (buffer, parser, _) = Create();
        Transmit(parser, 42);
        Transmit(parser, 0x010203);
        VirtualPlacement(parser, 42, 1, 1);
        VirtualPlacement(parser, 0x010203, 1, 1);
        parser.Process($"\x1b[38;5;42m{Cell(0, 0)}\x1b[38;2;1;2;3m{Cell(0, 0)}\x1b[38;2;0;0;42m{Cell(0, 0)}\x1b[39m");
        // 38;5;42, 38;2;1;2;3 and 38;2;0;0;42 are three different cells; the first and third name the same image.
        Assert.Equal(new[] { 0, 1, 2 }, Runs(buffer).Select(run => run.CellX));
    }

    [Fact]
    public void The_underline_colour_selects_the_virtual_placement_and_an_unknown_one_shows_nothing()
    {
        var (buffer, parser, _) = Create();
        Transmit(parser, 42);
        VirtualPlacement(parser, 42, 2, 1, placement: 1);
        VirtualPlacement(parser, 42, 3, 1, placement: 2);
        // Placement 2 by its palette colour, placement 1 by 24-bit colour, and a placement that does not exist.
        parser.Process($"\x1b[38;5;42m\x1b[58;5;2m{Cell(0, 0)}\x1b[58;2;0;0;1m{Cell(0, 0)}\x1b[58;5;9m{Cell(0, 0)}\x1b[59m\x1b[39m");
        List<RenderImageSnapshot> runs = Runs(buffer);
        Assert.Equal(new[] { (0, 3), (1, 2) }, runs.Select(run => (run.CellX, run.GridColumns)));
    }

    [Fact]
    public void Without_an_underline_colour_the_newest_virtual_placement_is_used()
    {
        var (buffer, parser, _) = Create();
        Transmit(parser, 42);
        VirtualPlacement(parser, 42, 2, 1, placement: 1);
        VirtualPlacement(parser, 42, 5, 1, placement: 2);
        parser.Process($"\x1b[38;5;42m{Cell(0, 0)}\x1b[39m");
        Assert.Equal(5, Assert.Single(Runs(buffer)).GridColumns);
    }

    [Fact]
    public void Cells_outside_the_grid_or_without_a_foreground_colour_or_for_an_unknown_image_show_nothing()
    {
        var (buffer, parser, _) = Create();
        Transmit(parser, 42);
        VirtualPlacement(parser, 42, 2, 1);
        parser.Process($"\x1b[38;5;42m{Cell(0, 2)}{Cell(1, 0)}\x1b[38;5;43m{Cell(0, 0)}\x1b[39m{Cell(0, 0)}");
        Assert.Empty(Runs(buffer));
    }

    [Fact]
    public void Plain_text_and_other_private_use_characters_are_not_placeholders()
    {
        var (buffer, parser, _) = Create();
        Transmit(parser, 42);
        VirtualPlacement(parser, 42, 1, 1);
        parser.Process("\x1b[38;5;42mA\U0010EEED" + Mark(0) + "\x1b[39m");
        Assert.Empty(Runs(buffer));
    }

    [Fact]
    public void Erasing_the_cells_removes_the_image_and_scrolling_carries_it_with_the_text()
    {
        var (buffer, parser, _) = Create(rows: 4);
        Transmit(parser, 42);
        VirtualPlacement(parser, 42, 1, 1);
        parser.Process($"\x1b[3;1H\x1b[38;5;42m{Cell(0, 0)}\x1b[39m");
        RenderImageSnapshot before = Assert.Single(Runs(buffer));
        int absBefore = before.CellY;
        parser.Process("\x1b[4;1H\r\n\r\n");
        RenderImageSnapshot scrolled = Assert.Single(Runs(buffer));
        Assert.Equal(absBefore, scrolled.CellY);
        parser.Process("\x1b[2J");
        Assert.Empty(Runs(buffer));
    }

    [Fact]
    public void Deleting_by_identity_removes_virtual_placements_and_position_based_deletion_does_not()
    {
        var (buffer, parser, _) = Create();
        Transmit(parser, 42);
        VirtualPlacement(parser, 42, 1, 1, placement: 3);
        parser.Process($"\x1b[38;5;42m\x1b[58;5;3m{Cell(0, 0)}\x1b[59m\x1b[39m");
        Assert.Single(Runs(buffer));

        parser.Process(Command("a=d,d=a"));
        parser.Process(Command("a=d,d=A"));
        Assert.Single(Runs(buffer));

        parser.Process(Command("a=d,d=i,i=42,p=9"));
        Assert.Single(Runs(buffer));

        parser.Process(Command("a=d,d=i,i=42,p=3"));
        Assert.Empty(Runs(buffer));
        // The image itself is still stored and can be given a placement again.
        VirtualPlacement(parser, 42, 1, 1, placement: 3);
        Assert.Single(Runs(buffer));

        parser.Process(Command("a=d,d=I,i=42"));
        Assert.Empty(Runs(buffer));
    }

    [Fact]
    public void Retransmitting_an_image_drops_its_virtual_placements()
    {
        var (buffer, parser, _) = Create();
        Transmit(parser, 42);
        VirtualPlacement(parser, 42, 1, 1);
        parser.Process($"\x1b[38;5;42m{Cell(0, 0)}\x1b[39m");
        Assert.Single(Runs(buffer));
        Transmit(parser, 42);
        Assert.Empty(Runs(buffer));
    }

    [Fact]
    public void A_feature_query_with_U1_is_answered_with_the_keys_it_asked_about()
    {
        var (_, parser, replies) = Create();
        parser.Process(Command("a=q,i=31,e=T,U=1,C=1"));
        parser.Process(Command("a=q,i=32,e=p,U=1"));
        parser.Process(Command("a=q,i=33"));
        parser.Process(Command("a=q,i=34,e=T"));
        Assert.Equal(new[]
        {
            "\x1b_Gi=31,e=T,C=1,U=1;OK\x1b\\",
            "\x1b_Gi=32,e=p,U=1;OK\x1b\\",
            "\x1b_Gi=33;OK\x1b\\",
            "\x1b_Gi=34;OK\x1b\\"
        }, replies);
    }

    [Fact]
    public void Where_images_are_retained_for_snapshots_placeholders_wait_for_policy_5()
    {
        var (buffer, parser, replies) = Create(retained: true, placeholders: false);
        parser.Process(Command("a=q,i=31,e=T,U=1"));
        parser.Process(Command("a=T,U=1,f=32,s=1,v=1,i=5,c=1,r=1", Pixel));
        Assert.Equal("\x1b_Gi=31;ERR\x1b\\", replies[0]);
        Assert.StartsWith("\x1b_Gi=5;ENOTSUP:Unicode placeholders", replies[1], StringComparison.Ordinal);
        Assert.Empty(buffer.Images);
        // The refused command stored nothing, so a placement of the image cannot find it.
        parser.Process(Command("a=p,U=1,i=5,c=1,r=1"));
        Assert.StartsWith("\x1b_Gi=5;ENOTSUP", replies[2], StringComparison.Ordinal);
        // Ordinary images are unaffected.
        parser.Process(Command("a=T,f=32,s=1,v=1,i=6,C=1", Pixel));
        Assert.Equal("\x1b_Gi=6;OK\x1b\\", replies[3]);

        var (_, enabled, enabledReplies) = Create(retained: true, placeholders: true);
        enabled.Process(Command("a=q,i=31,e=T,U=1"));
        Assert.Equal("\x1b_Gi=31,e=T,U=1;OK\x1b\\", Assert.Single(enabledReplies));
    }

    [Fact]
    public void A_placeholder_keeps_the_pens_underline_colour_without_being_underlined_and_other_text_does_not()
    {
        var (buffer, parser, _) = Create();
        parser.Process($"\x1b[58;2;1;2;3m{Cell(0, 0)}A\x1b[59m");
        buffer.Lock.EnterReadLock();
        try
        {
            TerminalRow row = buffer.GetRowAbsolute(0)!;
            CellUnderline kept = Assert.IsType<CellUnderline>(row.GetUnderline(0));
            Assert.Equal(new TermColor(1, 2, 3), kept.Color);
            Assert.False(row.Cells[0].IsUnderline);
            Assert.Null(row.GetUnderline(1));
            Assert.False(row.Cells[1].IsUnderline);
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void The_grid_defaults_to_the_images_size_in_cells_and_a_run_keeps_its_cell_background()
    {
        var (buffer, parser, _) = Create();
        Transmit(parser, 42);
        parser.Process(Command("a=p,U=1,i=42"));
        parser.Process($"\x1b[48;2;9;8;7m\x1b[38;5;42m{Cell(0, 0)}\x1b[0m");
        RenderImageSnapshot run = Assert.Single(Runs(buffer));
        Assert.Equal((1, 1), (run.GridColumns, run.GridRows));
        Assert.Equal(new TermColor(9, 8, 7), run.TileBackground);
    }

    [Fact]
    public void Cells_with_different_backgrounds_are_separate_runs()
    {
        var (buffer, parser, _) = Create();
        Transmit(parser, 42);
        VirtualPlacement(parser, 42, 2, 1);
        parser.Process($"\x1b[38;5;42m\x1b[48;2;1;1;1m{Cell(0, 0)}\x1b[48;2;2;2;2m{Cell(0, 1)}\x1b[0m");
        Assert.Equal(2, Runs(buffer).Count);
    }

    [Fact]
    public void A_placement_id_names_one_placement_whether_it_is_real_or_virtual()
    {
        var (buffer, parser, _) = Create();
        Transmit(parser, 42);
        parser.Process(Command("a=p,i=42,p=7,C=1,c=1,r=1"));
        Assert.Single(buffer.Images);
        VirtualPlacement(parser, 42, 1, 1, placement: 7);
        Assert.Empty(buffer.Images);                       // the virtual placement replaced the one at the cursor
        parser.Process($"\x1b[38;5;42m\x1b[58;5;7m{Cell(0, 0)}\x1b[59m\x1b[39m");
        Assert.Single(Runs(buffer));
        parser.Process(Command("a=p,i=42,p=7,C=1,c=1,r=1"));
        Assert.Single(buffer.Images);
        Assert.Empty(Runs(buffer));                        // and the real one replaced the virtual one
        // A different id is a different placement.
        VirtualPlacement(parser, 42, 1, 1, placement: 8);
        Assert.Single(buffer.Images);
    }

    [Fact]
    public void Replacing_an_image_that_only_placeholders_show_requests_a_redraw()
    {
        var (buffer, parser, _) = Create();
        Transmit(parser, 42);
        VirtualPlacement(parser, 42, 1, 1);
        parser.Process($"\x1b[38;5;42m{Cell(0, 0)}\x1b[39m");
        int redraws = 0;
        buffer.OnInvalidate += () => redraws++;
        Transmit(parser, 42);
        Assert.True(redraws > 0);
    }

    [Fact]
    public void Wake_ups_for_an_animation_shown_only_by_placeholders_follow_the_visible_cells()
    {
        var (buffer, parser, _) = Create(rows: 4);
        long now = 1000;
        buffer.KittyAnimationClock = () => now;
        parser.Process(Command("a=T,U=1,f=32,s=1,v=1,i=42,c=1,r=1", Pixel));
        parser.Process(Command("a=f,i=42,f=32,s=1,v=1,z=100", "AP8A/w=="));
        parser.Process(Command("a=a,i=42,s=3"));
        buffer.AdvanceKittyAnimations(now, out long? hidden, firstVisibleLine: 0);
        Assert.Null(hidden);                                // nothing prints it yet
        parser.Process($"\x1b[38;5;42m{Cell(0, 0)}\x1b[39m");
        buffer.AdvanceKittyAnimations(now, out long? shown, firstVisibleLine: 0);
        Assert.NotNull(shown);
        buffer.AdvanceKittyAnimations(now, out long? scrolledAway, firstVisibleLine: 100);
        Assert.Null(scrolledAway);
        parser.Process("\x1b[2J");
        buffer.AdvanceKittyAnimations(now, out long? erased, firstVisibleLine: 0);
        Assert.Null(erased);
    }

    [Fact]
    public void A_palette_background_is_carried_to_the_renderer_by_index()
    {
        var (buffer, parser, _) = Create();
        Transmit(parser, 42);
        VirtualPlacement(parser, 42, 2, 1);
        parser.Process($"\x1b[38;5;42m\x1b[48;5;196m{Cell(0, 0)}\x1b[48;5;197m{Cell(0, 1)}\x1b[0m");
        List<RenderImageSnapshot> runs = Runs(buffer);
        Assert.Equal(new short?[] { 196, 197 }, runs.Select(run => run.TileBackgroundIndex));
        Assert.All(runs, run => Assert.Null(run.TileBackground));
    }

    [Fact]
    public void An_overline_does_not_cost_the_placeholder_its_underline_colour()
    {
        var (buffer, parser, _) = Create();
        Transmit(parser, 42);
        VirtualPlacement(parser, 42, 1, 1, placement: 1);
        VirtualPlacement(parser, 42, 2, 1, placement: 2);
        parser.Process($"\x1b[53m\x1b[58;5;1m\x1b[38;5;42m{Cell(0, 0)}\x1b[0m");
        Assert.Equal(1, Assert.Single(Runs(buffer)).GridColumns);   // placement 1, not the newest
    }

    [Fact]
    public void A_placeholder_scrolled_into_history_still_shows_its_image_with_its_underline_colour()
    {
        var (buffer, parser, _) = Create(rows: 2);
        Transmit(parser, 42);
        VirtualPlacement(parser, 42, 1, 1, placement: 1);
        VirtualPlacement(parser, 42, 2, 1, placement: 2);
        parser.Process($"\x1b[38;5;42m\x1b[58;5;1m{Cell(0, 0)}\x1b[0m\r\n\r\n");
        Assert.True(buffer.TotalLines > buffer.Rows);
        buffer.Lock.EnterReadLock();
        try
        {
            List<RenderImageSnapshot> history = buffer.GetVisibleImagesSnapshot(0, buffer.Rows).Where(image => image.GridColumns > 0).ToList();
            RenderImageSnapshot run = Assert.Single(history);
            Assert.Equal((0, 1), (run.CellY, run.GridColumns));   // placement 1 by its underline colour, read from the page
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void A_placeholder_cell_is_drawn_without_a_glyph_whether_or_not_it_has_an_image()
    {
        var (buffer, parser, _) = Create(rows: 2);
        // One placeholder scrolled into history and one in the viewport, neither with an image behind it.
        parser.Process($"\x1b[38;5;42m{Cell(0, 0)}\x1b[0m\r\n\r\n\x1b[38;5;42m{Cell(0, 0)}\x1b[0m");
        foreach (int scrollOffset in new[] { 0, 1 })
        {
            using TerminalRenderSnapshot snapshot = buffer.CaptureRenderSnapshot(
                new RenderSnapshotRequest { ViewportRows = 2, ViewportCols = 80, ScrollOffset = scrollOffset, Isolated = true }, out _);
            RenderCellSnapshot cell = snapshot.RowsData.Array[scrollOffset == 0 ? 1 : 0].Cells[0];
            Assert.Null(cell.Text);
            Assert.Equal(' ', cell.Character);
        }
    }
}
