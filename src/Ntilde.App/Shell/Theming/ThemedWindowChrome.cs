using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Ntilde.Shell.Theming;

/// <summary>
/// The surface and text colours of a window built in code (confirm dialogs, SSH prompts, the
/// mux install dialog, About). It holds the app's token brush INSTANCES, which ThemeService
/// recolours in place, so a dialog left open follows a theme switch; before v2 these windows were
/// painted once at open with copied colours. The theme variant is not set here: a window inherits
/// the application's, which ThemeService keeps in step with the tokens.
/// </summary>
public static class ThemedWindowChrome
{
    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (Application.Current is not { } app)
        {
            return;
        }

        if (app.FindResource(ThemeKeys.WindowBg) is IBrush background)
        {
            window.Background = background;
        }

        if (app.FindResource(ThemeKeys.Fg) is IBrush foreground)
        {
            window.Foreground = foreground;
        }
    }
}
