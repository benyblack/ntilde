using Avalonia;
using Avalonia.Controls;

namespace Ntilde.Settings;

/// <summary>
/// The hairline-row look SettingRow draws, for rows built in code (shortcut bindings, snippets).
/// The brush is the palette INSTANCE, so a theme switch recolours these rows too.
/// </summary>
internal static class SettingsRowChrome
{
    /// <summary>Vertical padding of a code-built row; SettingRow's own template uses 14 for its taller rows.</summary>
    internal static readonly Thickness RowPadding = new(0, 10);

    internal static Border CreateRow(Control owner, Control content) => new()
    {
        BorderBrush = owner.FindPaletteBrush("NtHairline"),
        BorderThickness = new Thickness(0, 0, 0, 1),
        Padding = RowPadding,
        Child = content,
    };
}
