using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Ntilde.Settings;

namespace Ntilde.Tests.Core;

public sealed class ComboTagsTests
{
    private static ComboBox Combo(params (string Label, string Value)[] items)
    {
        var combo = new ComboBox();
        foreach (var (label, value) in items)
        {
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        }

        return combo;
    }

    [AvaloniaFact]
    public void Select_MatchesTheStoredValue_NotTheLabel()
    {
        var combo = Combo(("Don't stretch", "None"), ("Fill (crop)", "UniformToFill"));

        ComboTags.Select(combo, "UniformToFill", StringComparison.Ordinal, fallbackIndex: 0);

        Assert.Equal("Fill (crop)", ((ComboBoxItem)combo.SelectedItem!).Content);
        Assert.Equal("UniformToFill", ComboTags.SelectedValue(combo));
    }

    [AvaloniaFact]
    public void Select_UnknownValue_FallsBackOnlyWhenNothingIsSelected()
    {
        var combo = Combo(("Horizontal", "Horizontal"), ("Vertical", "Vertical"));

        ComboTags.Select(combo, "Diagonal", StringComparison.Ordinal, fallbackIndex: 0);
        Assert.Equal("Horizontal", ComboTags.SelectedValue(combo));

        combo.SelectedIndex = 1;
        ComboTags.Select(combo, "Diagonal", StringComparison.Ordinal, fallbackIndex: 0);
        Assert.Equal("Vertical", ComboTags.SelectedValue(combo)); // the old loops kept a prior selection too
    }

    [AvaloniaFact]
    public void Select_HonoursTheComparison()
    {
        var combo = Combo(("Horizontal", "Horizontal"), ("Vertical", "Vertical"));

        ComboTags.Select(combo, "vertical", StringComparison.OrdinalIgnoreCase, fallbackIndex: 0);

        Assert.Equal("Vertical", ComboTags.SelectedValue(combo));
    }

    [AvaloniaFact]
    public void Select_NullValue_MatchesNothing_SoTheFallbackApplies()
    {
        var combo = new ComboBox();
        combo.Items.Add(new ComboBoxItem { Content = "No tag", Tag = null });
        combo.Items.Add(new ComboBoxItem { Content = "Number tag", Tag = 5 });
        combo.Items.Add(new ComboBoxItem { Content = "Real", Tag = "Real" });

        ComboTags.Select(combo, null, StringComparison.Ordinal, fallbackIndex: 2);

        Assert.Equal("Real", ComboTags.SelectedValue(combo));
    }

    [AvaloniaFact]
    public void Select_Ordinal_StaysCaseSensitive()
    {
        var combo = Combo(("Horizontal", "Horizontal"), ("Vertical", "Vertical"));

        ComboTags.Select(combo, "vertical", StringComparison.Ordinal, fallbackIndex: 0);

        Assert.Equal("Horizontal", ComboTags.SelectedValue(combo));
    }

    [AvaloniaFact]
    public void Select_OnAnEmptyCombo_DoesNotThrow_AndSelectsNothing()
    {
        var combo = new ComboBox();

        Assert.Null(Record.Exception(() => ComboTags.Select(combo, "x", StringComparison.Ordinal, fallbackIndex: 0)));

        Assert.Null(combo.SelectedItem);
        Assert.Null(ComboTags.SelectedValue(combo));
    }

    [AvaloniaFact]
    public void Select_OutOfRangeFallbackIndex_DoesNotThrow()
    {
        var combo = Combo(("Horizontal", "Horizontal"));

        Assert.Null(Record.Exception(() => ComboTags.Select(combo, "Diagonal", StringComparison.Ordinal, fallbackIndex: 7)));
    }
}
