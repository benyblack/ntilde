using Ntilde.Shell.Shortcuts;

namespace Ntilde.Tests.Core;

public sealed class SettingsWindowShortcutFilteringTests
{
    [Fact]
    public void FilterShortcutCatalogEntries_MatchesTitleCategoryAndScope()
    {
        IReadOnlyList<ShortcutCatalogEntry> results = SettingsWindow.FilterShortcutCatalogEntries("assist");

        Assert.Contains(results, entry => entry.CommandId == "command_assist_toggle");
        Assert.DoesNotContain(results, entry => entry.CommandId == "settings");
    }

    [Fact]
    public void Mux_entries_are_listed_only_with_session_persistence_on()
    {
        Assert.Contains(SettingsWindow.FilterShortcutCatalogEntries("", sessionPersistence: true), e => e.CommandId == "attach_session");
        Assert.DoesNotContain(SettingsWindow.FilterShortcutCatalogEntries("", sessionPersistence: false), e => e.CommandId is "attach_session" or "detach_pane");
    }

    [Fact]
    public void An_unbound_default_reads_none()
    {
        Assert.Equal("Default none", SettingsWindow.DescribeDefaultBinding(""));
        Assert.Equal("Default Ctrl+,", SettingsWindow.DescribeDefaultBinding("Ctrl+,"));
    }
}
