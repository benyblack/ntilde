using Ntilde.Settings.Pages;
using Ntilde.Shell.Shortcuts;

namespace Ntilde.Tests.Core;

public sealed class SettingsWindowShortcutFilteringTests
{
    [Fact]
    public void FilterShortcutCatalogEntries_MatchesTitleCategoryAndScope()
    {
        IReadOnlyList<ShortcutCatalogEntry> results = ShortcutsPage.FilterShortcutCatalogEntries("assist");

        Assert.Contains(results, entry => entry.CommandId == "command_assist_toggle");
        Assert.DoesNotContain(results, entry => entry.CommandId == "settings");
    }
}
