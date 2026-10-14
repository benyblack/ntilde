using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Ntilde.Backup;
using Ntilde.Settings;
using Ntilde.Settings.Pages;
using Ntilde.Shell;
using Ntilde.Shell.Shortcuts;
using Ntilde.Tests.Backup;

namespace Ntilde.Tests.Core;

/// <summary>
/// Each page on its own: Load shows the session's values and Collect writes them back. The
/// window-level round trip (SettingsWindowRoundTripTests) checks the same thing for all pages at
/// once; these tests point at the page that broke.
/// </summary>
public sealed class SettingsPageTests
{
    internal static readonly Func<IEnumerable<string>> NoSystemFonts = () => Array.Empty<string>();

    internal static SettingsSession SessionFor(TerminalSettings settings) => new(settings, NoSystemFonts);

    [AvaloniaFact]
    public void AgentAccessPage_LoadsAndCollectsItsFourSettings()
    {
        var page = new AgentAccessPage();
        page.Load(SessionFor(new TerminalSettings
        {
            AgentAccessObserveEnabled = true,
            AgentReplayExportEnabled = true,
            AgentAccessActEnabled = true,
            AgentIndicatorTabRollup = "All",
        }));
        var target = new TerminalSettings();

        page.Collect(target);

        Assert.True(target.AgentAccessObserveEnabled);
        Assert.True(target.AgentReplayExportEnabled);
        Assert.True(target.AgentAccessActEnabled);
        Assert.Equal("All", target.AgentIndicatorTabRollup);
        Assert.Null(page.Validate());
    }

    [AvaloniaFact]
    public void AgentAccessPage_UnknownRollup_FallsBackToTheFirstItem_AsBefore()
    {
        var page = new AgentAccessPage();
        page.Load(SessionFor(new TerminalSettings { AgentIndicatorTabRollup = "Sometimes" }));
        // Seed with a value that is neither the default nor the expected fallback, so a Load that selects nothing fails.
        var target = new TerminalSettings { AgentIndicatorTabRollup = "All" };

        page.Collect(target);

        Assert.Equal("WritesOnly", target.AgentIndicatorTabRollup);
    }

    [AvaloniaFact]
    public void SshPage_LoadsAndCollectsTheNativeBackendToggle()
    {
        var page = new SshPage();
        page.Load(SessionFor(new TerminalSettings { ExperimentalNativeSshEnabled = false }));
        var target = new TerminalSettings { ExperimentalNativeSshEnabled = true };

        page.Collect(target);

        Assert.False(target.ExperimentalNativeSshEnabled);
    }

    [AvaloniaFact]
    public void BackupPage_Load_ListsTheSnapshotsOnDisk()
    {
        using var root = SettingsTestRoot.Empty();
        Assert.NotNull(new BackupService(root.Root).Snapshot(SnapshotReason.Auto));
        var page = new BackupPage();

        page.Load(SessionFor(new TerminalSettings()));

        var rows = page.FindControl<ListBox>("SnapshotList")!.ItemsSource!.Cast<BackupPage.SnapshotRow>().ToArray();
        Assert.Single(rows);
        Assert.Contains("automatic", rows[0].Display);
    }

    [AvaloniaFact]
    public void BackupPage_StatesPasswordsAreNotIncluded_AndMustBeReEntered()
    {
        using var root = SettingsTestRoot.Empty();
        var page = new BackupPage();

        Assert.Contains(
            page.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text),
            text => text is not null
                && text.Contains("never included", StringComparison.OrdinalIgnoreCase)
                && text.Contains("re-enter", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BackupPage_RestoreConfirmationText_NamesTheSnapshotAndExplainsTheUndo()
    {
        var created = new DateTimeOffset(2026, 10, 13, 9, 30, 0, TimeSpan.Zero);
        var row = new BackupPage.SnapshotRow("id", "display", SnapshotReason.Auto, created);

        var (headline, body) = BackupPage.BuildRestoreConfirmationText(row);

        Assert.Contains(created.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), headline);
        Assert.Contains("automatic", headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("taken first", body, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void ShortcutsPage_WithADuplicateBinding_RefusesToValidate_AndSaysWhy()
    {
        var session = SessionFor(new TerminalSettings());
        var app = ShortcutCatalog.GetEntries()
            .Where(e => e.Scope == ShortcutScope.App && !string.IsNullOrEmpty(e.DefaultBinding))
            .ToList();
        session.ShortcutDraft[app[1].CommandId] = app[0].DefaultBinding;
        Assert.False(ShortcutBindingResolver.Resolve(ShortcutCatalog.GetDefinitions(), session.ShortcutDraft).IsValid);
        var page = new ShortcutsPage();
        page.Load(session);

        SettingsValidationError? error = page.Validate();

        Assert.NotNull(error);
        Assert.Equal(SettingsPage.Shortcuts, error!.Page);
        var message = page.FindControl<TextBlock>("ShortcutValidationMessage")!;
        Assert.True(message.IsVisible);
        Assert.Equal("Resolve duplicate shortcuts before saving.", message.Text);
    }

    [AvaloniaFact]
    public void ShortcutsPage_RecordingAChord_UpdatesTheDraft_AndTellsOtherPages()
    {
        var session = SessionFor(new TerminalSettings());
        var page = new ShortcutsPage();
        page.Load(session);
        int changes = 0;
        session.ShortcutsChanged += () => changes++;

        TextBox editor = BindingEditorFor(page, "Settings");
        PressChord(editor, Key.F9, KeyModifiers.Alt);

        string expected = ShortcutMatcher.Format(Key.F9, KeyModifiers.Alt);
        Assert.Equal(expected, editor.Text);
        Assert.Equal(expected, session.ShortcutDraft["settings"]);
        Assert.Equal(1, changes);
        Assert.Contains(ShortcutsPage.CaptureClass, editor.Classes);
    }

    [AvaloniaFact]
    public void ShortcutsPage_Reset_RemovesTheOverride_AndTellsOtherPages()
    {
        var session = SessionFor(new TerminalSettings { Keybindings = new() { ["settings"] = "Alt+F9" } });
        var page = new ShortcutsPage();
        page.Load(session);
        int changes = 0;
        session.ShortcutsChanged += () => changes++;

        Button reset = BindingEditorFor(page, "Settings").GetLogicalParent()!
            .GetLogicalChildren().OfType<Button>().Single(b => (string?)b.Content == "Reset");
        reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.False(session.ShortcutDraft.ContainsKey("settings"));
        Assert.Equal(1, changes);
    }

    /// <summary>The binding editor on the Shortcuts page row titled <paramref name="title"/>.</summary>
    internal static TextBox BindingEditorFor(ShortcutsPage page, string title)
    {
        var panel = page.FindControl<StackPanel>("ShortcutBindingsPanel")!;
        Border row = panel.Children.OfType<Border>().Single(b => b.GetLogicalDescendants()
            .OfType<TextBlock>().Any(t => t.Classes.Contains("RowLabel") && t.Text == title));
        return row.GetLogicalDescendants().OfType<TextBox>().Single();
    }

    internal static void PressChord(Control target, Key key, KeyModifiers modifiers)
        => target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = target });
}
