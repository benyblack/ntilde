using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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

        var panel = TitleBarRowsPanel(window);
        var (title, shortcut) = FindRowByTitle(panel, "Settings");
        Assert.Equal("Settings", title.Text);
        Assert.Equal("Ctrl+Alt+S", shortcut.Text);
        Assert.Equal(TitleBarItemState.Pinned, session.TitleBarDraft.GetState("find"));
        Assert.Equal("Find in Terminal", FindRowByTitle(panel, "Find in Terminal").Title.Text);
    }

    [AvaloniaFact]
    public void RebindingOnTheShortcutsPage_UpdatesTheTitleBarRowsAndTheCommandAssistText()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        var shortcuts = window.GetPage<ShortcutsPage>();

        SettingsPageTests.PressChord(SettingsPageTests.BindingEditorFor(shortcuts, "Settings"), Key.F9, KeyModifiers.Alt);
        SettingsPageTests.PressChord(SettingsPageTests.BindingEditorFor(shortcuts, "Command Assist History"), Key.F8, KeyModifiers.Alt);

        Assert.Equal(ShortcutMatcher.Format(Key.F9, KeyModifiers.Alt), FindRowByTitle(TitleBarRowsPanel(window), "Settings").Shortcut.Text);
        string history = window.GetPage<CommandAssistPage>().FindControl<SettingRow>("CommandAssistHistoryRow")!.Description!;
        Assert.Contains(ShortcutMatcher.Format(Key.F8, KeyModifiers.Alt), history);
        Assert.DoesNotContain("Ctrl+R", history);
    }

    /// <summary>Where the title-bar rows live. PR 3 moves them to Tabs & title bar; PR 4 rebuilds them.</summary>
    private static StackPanel TitleBarRowsPanel(Ntilde.SettingsWindow window)
        => window.GetPage<AppearancePage>().FindControl<StackPanel>("TitleBarItemsPanel")!;

    private static (TextBlock Title, TextBlock Shortcut) FindRowByTitle(StackPanel panel, string title)
    {
        foreach (var child in panel.Children)
        {
            if (child is not Border { Child: Grid grid })
            {
                continue;
            }

            var labels = grid.Children.OfType<StackPanel>().FirstOrDefault();
            var textBlocks = labels?.Children.OfType<TextBlock>().ToList();
            if (textBlocks is { Count: >= 2 } && textBlocks[0].Text == title)
            {
                return (textBlocks[0], textBlocks[1]);
            }
        }

        throw new Xunit.Sdk.XunitException($"No title bar row found with title '{title}'.");
    }
}
