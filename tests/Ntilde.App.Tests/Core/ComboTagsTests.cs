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
}
