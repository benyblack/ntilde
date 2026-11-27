using System;
using System.IO;
using Avalonia.Headless.XUnit;
using Ntilde.Controls;
using Ntilde.Shell;

namespace Ntilde.Tests.RenderTests;

public sealed class FontLigatureRenderTests
{
    [AvaloniaTheory]
    [InlineData(true, null, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    public void Pane_applies_the_profile_override_or_global_setting(bool global, bool? profile, bool expected)
    {
        using var pane = new TerminalPane(new TerminalProfile { EnableLigatures = profile });
        pane.ApplySettings(new TerminalSettings { EnableLigatures = global });
        Assert.Equal(expected, Assert.IsType<TerminalView>(pane.ActiveControl).EnableLigatures);
    }

    [AvaloniaTheory]
    [InlineData("Fixtures", "FiraCode-Regular.ttf", true)]
    [InlineData("Assets", "JetBrainsMonoNL-Regular.ttf", false)]
    public void Dirty_span_baseline_tracks_ligature_capability_and_row_content(string directory, string face, bool requiresFullRepaint)
    {
        string font = Path.Combine(AppContext.BaseDirectory, directory, "Fonts", face);
        using var cached = new DrawPathHarness(20, 4, fontPath: font) { EnableLigatures = true };
        using var uncached = new DrawPathHarness(20, 4, withRowCache: false, fontPath: font) { EnableLigatures = true };
        var cache = Assert.IsType<Ntilde.Rendering.RowImageCache>(cached.RowCache);

        cached.Parser.Process("\x1b[?25lL=>R");
        cached.DrawFrame();
        long rowId;
        cached.Buffer.Lock.EnterReadLock();
        try
        {
            rowId = Assert.IsType<Ntilde.VT.TerminalRow>(cached.Buffer.GetRowAbsolute(0)).Id;
        }
        finally
        {
            cached.Buffer.Lock.ExitReadLock();
        }
        Assert.True(cache.TryGetLatestByRowId(rowId, out _, out _, out bool firstRequiresFullRepaint));
        Assert.Equal(requiresFullRepaint, firstRequiresFullRepaint);

        cached.Parser.Process("\x1b[1;2H\x1b[2X");
        cached.DrawFrame();
        uncached.Parser.Process("\x1b[?25lL  R");
        uncached.DrawFrame();
        Assert.Equal(uncached.Pixels(), cached.Pixels());
        Assert.True(cache.TryGetLatestByRowId(rowId, out _, out _, out bool secondRequiresFullRepaint));
        Assert.False(secondRequiresFullRepaint);
    }

    [AvaloniaTheory]
    [InlineData("=>", 0)]
    [InlineData("=>", 1)]
    [InlineData("!=", 0)]
    [InlineData("!=", 1)]
    public void One_cell_selection_breaks_the_substitution_and_removal_reuses_the_unselected_row(string pair, int cell)
    {
        string font = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", "FiraCode-Regular.ttf");
        using var plain = new DrawPathHarness(20, 4, fontPath: font);
        using var shaped = new DrawPathHarness(20, 4, fontPath: font) { EnableLigatures = true };
        string fixture = "\x1b[?25lL" + pair + "R";
        plain.Parser.Process(fixture);
        shaped.Parser.Process(fixture);

        plain.DrawFrame();
        shaped.DrawFrame();
        byte[] ligature = shaped.Pixels();
        byte[] ordinary = plain.Pixels();
        Assert.False(ordinary.AsSpan().SequenceEqual(ligature));
        var grid = TerminalDrawOperation.CreatePixelGrid(DrawPathHarness.Metrics, 1);
        for (int y = 0; y < grid.CellHeightPx; y++)
        {
            int rowStart = y * plain.PixelWidth * 4;
            int left = grid.XForCol(1) * 4, right = grid.XForCol(3) * 4;
            Assert.True(ordinary.AsSpan(rowStart, left).SequenceEqual(ligature.AsSpan(rowStart, left)));
            int rightBytes = plain.PixelWidth * 4 - right;
            Assert.True(ordinary.AsSpan(rowStart + right, rightBytes).SequenceEqual(ligature.AsSpan(rowStart + right, rightBytes)));
        }

        foreach (var harness in new[] { plain, shaped })
        {
            harness.Selection.Start = (0, cell + 1);
            harness.Selection.End = (0, cell + 1);
            harness.Selection.IsActive = true;
            harness.DrawFrame();
        }
        Assert.Equal(plain.Pixels(), shaped.Pixels());

        shaped.Selection.IsActive = false;
        shaped.DrawFrame();
        Assert.Equal(ligature, shaped.Pixels());
    }
}
