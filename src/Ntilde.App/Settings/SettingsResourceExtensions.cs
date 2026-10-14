using Avalonia.Controls;
using Avalonia.Media;

namespace Ntilde.Settings;

internal static class SettingsResourceExtensions
{
    /// <summary>
    /// One of the window's Nt* palette brushes. Rows built in code must hold the brush INSTANCE so
    /// ThemePaletteResources' in-place recolours reach them; a copied colour freezes on the first
    /// theme, which is what left the title-bar cards dark navy under light themes.
    /// </summary>
    internal static IBrush? FindPaletteBrush(this Control control, string key) => control.FindResource(key) as IBrush;
}
