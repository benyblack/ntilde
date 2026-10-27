using System;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Ntilde.Shell.Shortcuts;

namespace Ntilde.Tests.Infra
{
    /// <summary>
    /// Presses a command's default shortcut as this platform binds it. Defaults are written with
    /// Ctrl, but on macOS most of them are Cmd (and New Tab is Cmd+T), so a test that presses the
    /// Ctrl form hits nothing there.
    /// </summary>
    internal static class ShortcutKeys
    {
        public static void PressShortcut(this Window window, string commandId, string writtenDefault)
        {
            string binding = ShortcutPlatform.DefaultBinding(commandId, writtenDefault);
            if (!ShortcutMatcher.TryParse(binding, out Key key, out KeyModifiers modifiers))
            {
                throw new ArgumentException($"'{binding}' (the default for {commandId}) does not parse.", nameof(commandId));
            }

            var raw = RawInputModifiers.None;
            if (modifiers.HasFlag(KeyModifiers.Control)) raw |= RawInputModifiers.Control;
            if (modifiers.HasFlag(KeyModifiers.Shift)) raw |= RawInputModifiers.Shift;
            if (modifiers.HasFlag(KeyModifiers.Alt)) raw |= RawInputModifiers.Alt;
            if (modifiers.HasFlag(KeyModifiers.Meta)) raw |= RawInputModifiers.Meta;

            // Key.Enter is an alias of Key.Return, whose name PhysicalKey lacks.
            var physical = key == Key.Return ? PhysicalKey.Enter : Enum.Parse<PhysicalKey>(key.ToString());
            window.KeyPress(key, raw, physical, null);
        }
    }
}
