using System;
using System.Collections.Generic;

namespace Ntilde.Shell.Shortcuts;

/// <summary>
/// Adapts the catalogue's default bindings to the platform's primary modifier.
/// </summary>
/// <remarks>
/// <para>
/// Defaults are written once, with Ctrl, as Windows and Linux terminals expect. On macOS the
/// app-level modifier is Cmd: Cmd+V pastes, Cmd+W closes a tab, Cmd+, opens Settings. Moving the
/// defaults there also hands Ctrl+W, Ctrl+R, Ctrl+F and Ctrl+V back to the shell, where a macOS
/// user expects them to act as readline keys rather than app commands.
/// </para>
/// <para>
/// Only defaults are translated. A binding the user wrote into <c>settings.json</c> is taken at its
/// word on every platform.
/// </para>
/// </remarks>
public static class ShortcutPlatform
{
    /// <summary>
    /// Commands whose default keeps Ctrl on macOS because the Cmd form belongs to the system or
    /// means something else there: Cmd+Tab / Cmd+Shift+Tab switch applications (and macOS apps
    /// cycle tabs with Ctrl+Tab anyway), and Cmd+Space opens Spotlight. The Command Assist insert
    /// chord is matched by <c>AssistKeyBindings.Default</c> rather than this catalogue, so its
    /// listed default has to stay what that matcher actually uses.
    /// </summary>
    private static readonly HashSet<string> KeepCtrlOnMacOS = new(StringComparer.OrdinalIgnoreCase)
    {
        "next_tab",
        "prev_tab",
        "command_assist_toggle",
        "command_assist_insert",
    };

    /// <summary>
    /// Commands whose macOS default is not a plain Ctrl-to-Cmd swap. Ctrl+Shift+T carries Shift off
    /// macOS only to stay clear of the shell's Ctrl+T; Cmd has no such clash, and Cmd+T is the tab
    /// chord every macOS app uses.
    /// </summary>
    private static readonly Dictionary<string, string> MacOSOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["new_tab"] = "Cmd+T",
    };

    public static bool IsMacOS => OperatingSystem.IsMacOS();

    /// <summary>The default binding for <paramref name="commandId"/> on the current platform.</summary>
    public static string DefaultBinding(string commandId, string binding) =>
        DefaultBinding(commandId, binding, IsMacOS);

    /// <summary>
    /// The default binding for <paramref name="commandId"/>: <paramref name="binding"/> unchanged off
    /// macOS, and with Ctrl swapped for Cmd on macOS unless the command is exempt or has its own
    /// macOS default.
    /// </summary>
    public static string DefaultBinding(string commandId, string binding, bool isMacOS)
    {
        if (isMacOS && MacOSOverrides.TryGetValue(commandId, out string? macOSBinding))
        {
            return macOSBinding;
        }

        if (!isMacOS || KeepCtrlOnMacOS.Contains(commandId) ||
            !binding.StartsWith("Ctrl+", StringComparison.OrdinalIgnoreCase))
        {
            return binding;
        }

        return ShortcutNormalizer.Normalize("Cmd+" + binding["Ctrl+".Length..]);
    }
}
