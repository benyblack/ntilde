using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Ntilde.Settings;
using Ntilde.Settings.Pages;
using Ntilde.Shell.Shortcuts;
using Ntilde.Shell.TitleBar;

namespace Ntilde.Tests.Core;

/// <summary>
/// A shortcut edit reaches every page that prints a chord through
/// <see cref="SettingsSession.ShortcutsChanged"/>, so the title-bar rows and the Command Assist
/// descriptions show the new binding without reopening Settings. A title-bar placement change made
/// earlier in the same session survives the rows' rebuild.
/// </summary>
public sealed class SettingsWindowTitleBarShortcutRefreshTests
{
    [AvaloniaFact]
    public void ShortcutsChanged_RefreshesTheTitleBarRows_AndKeepsAnEarlierPlacementChange()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        SettingsSession session = window.Session;

        // "find" defaults to Overflow, so pinning it is a real change made before the rebind.
        Assert.True(session.TitleBarDraft.TrySetState("find", TitleBarItemState.Pinned));
        session.ShortcutDraft["settings"] = "Ctrl+Alt+S";
        session.RaiseShortcutsChanged();

        Assert.Equal("Ctrl+Alt+S", ShortcutShownFor(window, "settings"));
        Assert.Equal(TitleBarItemState.Pinned, session.TitleBarDraft.GetState("find"));
        Assert.Equal("IN THE BAR", SettingsTitleBarEditorTests.GroupOf(window.GetPage<TabsAndTitleBarPage>(), "find"));
    }

    [AvaloniaFact]
    public void RebindingOnTheShortcutsPage_UpdatesTheTitleBarRowsAndTheCommandAssistText()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        var shortcuts = window.GetPage<ShortcutsPage>();

        SettingsPageTests.PressChord(SettingsPageTests.BindingEditorFor(shortcuts, "Settings"), Key.F9, KeyModifiers.Alt);
        SettingsPageTests.PressChord(SettingsPageTests.BindingEditorFor(shortcuts, "Command Assist History"), Key.F8, KeyModifiers.Alt);

        Assert.Equal(ShortcutMatcher.Format(Key.F9, KeyModifiers.Alt), ShortcutShownFor(window, "settings"));
        string history = window.GetPage<CommandAssistPage>().FindControl<SettingRow>("CommandAssistHistoryRow")!.Description!;
        Assert.Contains(ShortcutMatcher.Format(Key.F8, KeyModifiers.Alt), history);
        Assert.DoesNotContain("Ctrl+R", history);
    }

    private static string ShortcutShownFor(Ntilde.SettingsWindow window, string id)
        => SettingsTitleBarEditorTests.Row(window.GetPage<TabsAndTitleBarPage>(), id)
            .GetLogicalDescendants().OfType<TextBlock>().Single(t => (string?)t.Tag == "shortcut").Text!;
}
