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
        Assert.Equal(expected, pane.TermView.EnableLigatures);
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
        Assert.False(plain.Pixels().AsSpan().SequenceEqual(ligature));

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
