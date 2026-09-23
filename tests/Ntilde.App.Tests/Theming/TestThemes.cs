using Ntilde.VT;

namespace Ntilde.Tests.Theming;

/// <summary>
/// Inline theme fixtures, so theme tests do not depend on which files the test's theme directory
/// holds. Values match the shipped JSON for the colors the tests read.
/// </summary>
internal static class TestThemes
{
    public static TerminalTheme Dracula => new()
    {
        Name = "Dracula",
        Background = TermColor.FromRgb(0x28, 0x2A, 0x36),
        Foreground = TermColor.FromRgb(0xF8, 0xF8, 0xF2),
        Blue = TermColor.FromRgb(0xBD, 0x93, 0xF9),
    };

    public static TerminalTheme Nord => new()
    {
        Name = "Nord",
        Background = TermColor.FromRgb(0x2E, 0x34, 0x40),
        Foreground = TermColor.FromRgb(0xD8, 0xDE, 0xE9),
        Blue = TermColor.FromRgb(0x81, 0xA1, 0xC1),
    };

    public static TerminalTheme GitHubLight => new()
    {
        Name = "GitHub Light",
        Background = TermColor.FromRgb(0xFF, 0xFF, 0xFF),
        Foreground = TermColor.FromRgb(0x24, 0x29, 0x2F),
        Blue = TermColor.FromRgb(0x09, 0x69, 0xDA),
    };
}
