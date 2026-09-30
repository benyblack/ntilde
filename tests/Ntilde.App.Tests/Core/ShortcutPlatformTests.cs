using System.Linq;
using Ntilde.Shell.Shortcuts;

namespace Ntilde.Tests.Core;

public sealed class ShortcutPlatformTests
{
    [Theory]
    [InlineData("paste", "Ctrl+V", "Cmd+V")]
    [InlineData("settings", "Ctrl+,", "Cmd+,")]
    [InlineData("new_tab", "Ctrl+Shift+T", "Cmd+T")]
    [InlineData("close_pane", "Ctrl+Shift+W", "Cmd+Shift+W")]
    [InlineData("font_increase", "Ctrl++", "Cmd+OemPlus")]
    // No Ctrl to swap.
    [InlineData("command_assist_dismiss", "Escape", "Escape")]
    // Cmd+Tab / Cmd+Space belong to macOS; Ctrl+Enter is matched outside this catalogue.
    [InlineData("next_tab", "Ctrl+Tab", "Ctrl+Tab")]
    [InlineData("prev_tab", "Ctrl+Shift+Tab", "Ctrl+Shift+Tab")]
    [InlineData("command_assist_toggle", "Ctrl+Space", "Ctrl+Space")]
    [InlineData("command_assist_insert", "Ctrl+Enter", "Ctrl+Enter")]
    public void DefaultBinding_OnMacOS_SwapsCtrlForCmd_ExceptSystemChords(string commandId, string written, string expected)
    {
        Assert.Equal(expected, ShortcutPlatform.DefaultBinding(commandId, written, isMacOS: true));
    }

    [Fact]
    public void DefaultBinding_OffMacOS_IsUnchanged()
    {
        Assert.Equal("Ctrl+Shift+T", ShortcutPlatform.DefaultBinding("new_tab", "Ctrl+Shift+T", isMacOS: false));
    }

    [Fact]
    public void Catalog_OnMacOS_UsesCmdForPasteAndSettings()
    {
        var entries = ShortcutCatalog.GetEntries(isMacOS: true).ToDictionary(entry => entry.CommandId);

        Assert.Equal("Cmd+V", entries["paste"].DefaultBinding);
        Assert.Equal("Cmd+,", entries["settings"].DefaultBinding);
        Assert.Equal("Ctrl+Tab", entries["next_tab"].DefaultBinding);
    }

    [Fact]
    public void Catalog_OnMacOS_DefaultsAreAlreadyNormalized()
    {
        // The Settings recorder compares a recorded chord to the default with an ordinal compare,
        // so a default not in canonical form would never read as "back to default".
        Assert.All(
            ShortcutCatalog.GetEntries(isMacOS: true).Where(entry => entry.DefaultBinding.StartsWith("Cmd+")),
            entry => Assert.Equal(ShortcutNormalizer.Normalize(entry.DefaultBinding), entry.DefaultBinding));
    }
}
