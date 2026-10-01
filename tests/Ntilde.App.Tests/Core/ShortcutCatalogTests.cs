using Ntilde.Shell.Shortcuts;

namespace Ntilde.Tests.Core;

public sealed class ShortcutCatalogTests
{
    [Fact]
    public void GetDefinitions_IncludesSettingsPaneAndCommandAssistBindings()
    {
        IReadOnlyList<ShortcutDefinition> definitions = ShortcutCatalog.GetDefinitions();

        Assert.Contains(definitions, definition => definition.CommandId == "settings" && definition.Scope == ShortcutScope.App);
        Assert.Contains(definitions, definition => definition.CommandId == "command_assist_toggle" && definition.Scope == ShortcutScope.CommandAssist);
        Assert.Contains(definitions, definition => definition.CommandId == "find" && definition.Scope == ShortcutScope.Pane);
    }

    [Fact]
    public void GetEntries_ExposesDisplayMetadataForSettingsBinding()
    {
        ShortcutCatalogEntry settingsEntry = Assert.Single(
            ShortcutCatalog.GetEntries(),
            entry => entry.CommandId == "settings");

        Assert.Equal("Settings", settingsEntry.Title);
        Assert.Equal("General", settingsEntry.Category);
        Assert.Equal("Ctrl+,", settingsEntry.DefaultBinding);
    }

    [Fact]
    public void GetEntries_IncludesMoveTabShortcuts_WithPageKeyDefaults()
    {
        ShortcutCatalogEntry prev = Assert.Single(
            ShortcutCatalog.GetEntries(),
            entry => entry.CommandId == "move_tab_prev");
        ShortcutCatalogEntry next = Assert.Single(
            ShortcutCatalog.GetEntries(),
            entry => entry.CommandId == "move_tab_next");

        Assert.Equal("Tab: Move Previous", prev.Title);
        Assert.Equal("Tab: Move Next", next.Title);
        Assert.Equal("General", prev.Category);
        Assert.Equal("General", next.Category);
        Assert.Equal(ShortcutScope.App, prev.Scope);
        Assert.Equal(ShortcutScope.App, next.Scope);
        Assert.Equal("Ctrl+Shift+PageUp", prev.DefaultBinding);
        Assert.Equal("Ctrl+Shift+PageDown", next.DefaultBinding);
    }

    [Fact]
    public void The_mux_entries_exist_with_no_default_chord()
    {
        ShortcutCatalogEntry attach = Assert.Single(ShortcutCatalog.GetEntries(), e => e.CommandId == "attach_session");
        ShortcutCatalogEntry detach = Assert.Single(ShortcutCatalog.GetEntries(), e => e.CommandId == "detach_pane");

        Assert.Equal(("Session: Attach to Session…", "", ShortcutScope.App, true), (attach.Title, attach.DefaultBinding, attach.Scope, attach.RequiresSessionPersistence));
        Assert.Equal(("Pane: Detach", "", ShortcutScope.Pane, true), (detach.Title, detach.DefaultBinding, detach.Scope, detach.RequiresSessionPersistence));
        Assert.All(ShortcutCatalog.GetDefinitions().Where(d => d.CommandId is "attach_session" or "detach_pane"), d => Assert.True(d.IsUnbound));
    }
}
