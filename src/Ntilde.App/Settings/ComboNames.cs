using System.Collections.Generic;
using Avalonia.Controls;

namespace Ntilde.Settings;

/// <summary>
/// Combos whose items are plain names (font families, themes): each item's Content is the name, and
/// Load and Collect read it back from there. The counterpart of <see cref="ComboTags"/>.
/// </summary>
internal static class ComboNames
{
    /// <summary>Replaces the combo's items with one per name, in the given order.</summary>
    internal static void Fill(ComboBox combo, IEnumerable<string> names)
    {
        combo.Items.Clear();
        foreach (string name in names)
        {
            combo.Items.Add(new ComboBoxItem { Content = name });
        }
    }
}
