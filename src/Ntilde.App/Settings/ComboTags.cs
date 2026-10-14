using System;
using System.Linq;
using Avalonia.Controls;

namespace Ntilde.Settings;

/// <summary>
/// Combos that show a readable label but store a fixed value. Each item carries the stored value in
/// Tag and the label in Content. Load and Collect look only at Tag, so relabelling an item can
/// never change what is written to settings.json.
/// </summary>
internal static class ComboTags
{
    /// <summary>
    /// Selects the item whose Tag equals <paramref name="value"/>. With no match, selects
    /// <paramref name="fallbackIndex"/>, but only when nothing is selected yet. That is what the
    /// old Content-matching loops did.
    /// </summary>
    internal static void Select(ComboBox combo, string? value, StringComparison comparison, int fallbackIndex)
    {
        ComboBoxItem? match = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, value, comparison));
        if (match != null)
        {
            combo.SelectedItem = match;
        }
        else if (combo.SelectedItem == null && combo.ItemCount > 0)
        {
            combo.SelectedIndex = fallbackIndex;
        }
    }

    /// <summary>The selected item's stored value; null when nothing is selected.</summary>
    internal static string? SelectedValue(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;
}
