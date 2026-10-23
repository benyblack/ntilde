using Avalonia.Controls;
using Avalonia.Media;

namespace Ntilde.Settings;

internal static class SettingsResourceExtensions
{
    /// <summary>
    /// One of the app-wide Nt* theme token brushes (see ThemeKeys). Rows built in code must hold the
    /// brush INSTANCE so ThemeService's in-place recolours reach them; a copied colour freezes on the
    /// first theme, which is what left the title-bar cards dark navy under light themes.
    /// </summary>
    internal static IBrush? FindPaletteBrush(this Control control, string key) => control.FindResource(key) as IBrush;
}
