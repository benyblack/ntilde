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
using Ntilde.Shell.TitleBar;
using Ntilde.Tests.Backup;
using Ntilde.VT;

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

    [AvaloniaFact]
    public void CommandAssistPage_LoadsAndCollectsItsFourToggles()
    {
        var page = new CommandAssistPage();
        page.Load(SessionFor(new TerminalSettings
        {
            CommandAssistEnabled = true,
            CommandAssistPassiveBubbleEnabled = false,
            CommandAssistHistoryEnabled = false,
            CommandAssistShellIntegrationEnabled = false,
        }));
        var target = new TerminalSettings();

        page.Collect(target);

        Assert.True(target.CommandAssistEnabled);
        Assert.False(target.CommandAssistPassiveBubbleEnabled);
        Assert.False(target.CommandAssistHistoryEnabled);
        Assert.False(target.CommandAssistShellIntegrationEnabled);
    }

    [AvaloniaFact]
    public void CommandAssistPage_NamesTheEffectiveChords_AndFollowsLaterRebinds()
    {
        var session = SessionFor(new TerminalSettings { Keybindings = new() { ["command_assist_history"] = "Alt+F8" } });
        var page = new CommandAssistPage();
        page.Load(session);
        var history = page.FindControl<TextBlock>("CommandAssistHistoryDesc")!;

        Assert.Contains("Alt+F8", history.Text);
        Assert.DoesNotContain("Ctrl+R", history.Text);

        session.ShortcutDraft["command_assist_history"] = "Alt+F7";
        session.RaiseShortcutsChanged();

        Assert.Contains("Alt+F7", history.Text);
    }

    private const string LocalProfileId = "33333333-3333-3333-3333-333333333333";

    private static TerminalSettings SettingsWithOneLocalAndOneSshProfile() => new()
    {
        Profiles =
        [
            new TerminalProfile { Id = Guid.Parse(LocalProfileId), Name = "Local", Command = "cmd.exe" },
            new TerminalProfile { Name = "Box", Type = ConnectionType.SSH, SshHost = "box" },
        ],
        DefaultProfileId = Guid.Parse(LocalProfileId),
    };

    [AvaloniaFact]
    public void ProfilesPage_ShowsOnlyLocalProfiles_AndCollectWritesThemBack()
    {
        using var root = SettingsTestRoot.Empty();
        var page = new ProfilesPage();
        page.Load(SessionFor(SettingsWithOneLocalAndOneSshProfile()));
        var target = new TerminalSettings();

        page.Collect(target);

        Assert.Single(page.FindControl<ListBox>("ProfilesListBox")!.Items);
        TerminalProfile only = Assert.Single(target.Profiles);
        Assert.Equal("Local", only.Name);
        page.Dispose();
    }

    /// <summary>
    /// With two local profiles and the second the default, Collect keeps the second. (With one
    /// profile this was vacuous: a fresh target resolves to the only profile anyway.) The page
    /// writes the default into the session's settings and Save collects into that same object, so
    /// the target here is that object.
    /// </summary>
    [AvaloniaFact]
    public void ProfilesPage_Collect_KeepsASecondProfileAsDefault()
    {
        using var root = SettingsTestRoot.Empty();
        Guid second = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var settings = new TerminalSettings
        {
            Profiles =
            [
                new TerminalProfile { Id = Guid.Parse(LocalProfileId), Name = "Local", Command = "cmd.exe" },
                new TerminalProfile { Id = second, Name = "Second", Command = "cmd.exe" },
            ],
            DefaultProfileId = second,
        };
        var page = new ProfilesPage();
        page.Load(SessionFor(settings));

        page.Collect(settings);

        Assert.Equal(second, settings.DefaultProfileId);
        Assert.Equal(2, settings.Profiles.Count);
        page.Dispose();
    }

    [AvaloniaFact]
    public void ProfilesPage_AfterAReload_EditsLandOnTheFreshProfile()
    {
        using var root = SettingsTestRoot.Empty();
        var page = new ProfilesPage();
        page.Load(SessionFor(SettingsWithOneLocalAndOneSshProfile()));
        var list = page.FindControl<ListBox>("ProfilesListBox")!;
        list.SelectedIndex = 0;

        page.Load(SessionFor(SettingsWithOneLocalAndOneSshProfile())); // what an import/restore reload does
        var name = page.FindControl<TextBox>("ProfileNameInput")!;
        name.Text = "Renamed after reload";
        name.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.D, Source = name });
        var target = new TerminalSettings();
        page.Collect(target);

        Assert.Equal("Renamed after reload", Assert.Single(target.Profiles).Name);
        page.Dispose();
    }

    [AvaloniaFact]
    public void ProfilesPage_OverrideFontList_OffersTheSessionsFontChoices()
    {
        using var root = SettingsTestRoot.Empty();
        var settings = SettingsWithOneLocalAndOneSshProfile();
        settings.FontFamily = "Session Mono";
        var page = new ProfilesPage();

        page.Load(SessionFor(settings));

        Assert.Contains(
            page.FindControl<ComboBox>("OverrideFontList")!.Items.OfType<ComboBoxItem>(),
            item => (string?)item.Content == "Session Mono");
        page.Dispose();
    }

    [AvaloniaFact]
    public void GeneralPage_LoadsAndCollectsUpdatesAndNotifications()
    {
        var page = new GeneralPage();
        page.Load(SessionFor(new TerminalSettings
        {
            AutomaticUpdateChecks = false,
            LongCommandNotificationsEnabled = true,
            Osc9NotificationsEnabled = true,
            Osc9ProgressReportingEnabled = false,
        }));
        var target = new TerminalSettings();

        page.Collect(target);

        Assert.False(target.AutomaticUpdateChecks);
        Assert.True(target.LongCommandNotificationsEnabled);
        Assert.True(target.Osc9NotificationsEnabled);
        Assert.False(target.Osc9ProgressReportingEnabled);
    }

    [AvaloniaFact]
    public void TerminalPage_LoadsAndCollectsFontsAndScrollback()
    {
        var page = new TerminalPage();
        page.Load(new SettingsSession(
            new TerminalSettings { FontFamily = "Page Mono", FontSize = 17, EnableLigatures = true, EnableComplexShaping = false, MaxHistory = 25000 },
            () => ["Page Mono"]));
        var target = new TerminalSettings();

        page.Collect(target);

        Assert.Equal("Page Mono", target.FontFamily);
        Assert.Equal(17, target.FontSize);
        Assert.True(target.EnableLigatures);
        Assert.False(target.EnableComplexShaping);
        Assert.Equal(25000, target.MaxHistory);
    }

    [AvaloniaFact]
    public void TerminalPage_PreviewsFontChangesThroughTheSession()
    {
        var session = new SettingsSession(new TerminalSettings(), () => ["One Mono", "Two Mono"]);
        var page = new TerminalPage();
        page.Load(session);
        string? previewed = null;
        session.Preview.FontChanged += family => previewed = family;

        var fonts = page.FindControl<ComboBox>("FontList")!;
        fonts.SelectedItem = fonts.Items.OfType<ComboBoxItem>().Single(i => (string?)i.Content == "Two Mono");

        Assert.Equal("Two Mono", previewed);
    }

    [AvaloniaFact]
    public void AppearancePage_CollectsEveryFieldItLoaded()
    {
        using var root = SettingsTestRoot.Empty();
        root.CopyShippedTheme("GitHubLight.json");
        var seed = new TerminalSettings
        {
            ThemeName = "GitHub Light",
            WindowOpacity = 0.75,
            BlurEffect = "Mica",
            UiScale = 1.25,
            TabStripOrientation = "Vertical",
            BackgroundImagePath = "/img.png",
            BackgroundImageOpacity = 0.35,
            BackgroundImageStretch = "Uniform",
        };
        var page = new AppearancePage();
        page.Load(new SettingsSession(seed, () => ["Page Mono"]));
        var target = new TerminalSettings();

        page.Collect(target);

        Assert.Equal("GitHub Light", target.ThemeName);
        Assert.Equal(0.75, target.WindowOpacity, precision: 9);
        Assert.Equal("Mica", target.BlurEffect);
        Assert.Equal(1.25, target.UiScale, precision: 9);
        Assert.Equal("Vertical", target.TabStripOrientation);
        Assert.Equal("/img.png", target.BackgroundImagePath);
        Assert.Equal(0.35, target.BackgroundImageOpacity, precision: 9);
        Assert.Equal("Uniform", target.BackgroundImageStretch);
    }

    /// <summary>
    /// Review focus 1 for Appearance: a settings.json value outside a combo's set must open on
    /// today's fallback item and never throw. Blur and stretch compare case-sensitively, tab
    /// orientation case-insensitively, as the old loops did. The target starts on sentinels so a
    /// Load that selects nothing (and a Collect that therefore skips the field) fails.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Transparent", "sideways", "uniform", "Acrylic", "Horizontal", "UniformToFill")]
    [InlineData("mica", "vertical", "UNIFORM", "Acrylic", "Vertical", "UniformToFill")] // orientation matches ignoring case, the others fall back
    public void AppearancePage_UnknownComboValues_FallBackAsBefore(
        string blur, string orientation, string stretch, string expectedBlur, string expectedOrientation, string expectedStretch)
    {
        using var root = SettingsTestRoot.Empty();
        var page = new AppearancePage();
        page.Load(SessionFor(new TerminalSettings { BlurEffect = blur, TabStripOrientation = orientation, BackgroundImageStretch = stretch }));
        var target = new TerminalSettings { BlurEffect = "unset", TabStripOrientation = "unset", BackgroundImageStretch = "unset" };

        page.Collect(target);

        Assert.Equal(expectedBlur, target.BlurEffect);
        Assert.Equal(expectedOrientation, target.TabStripOrientation);
        Assert.Equal(expectedStretch, target.BackgroundImageStretch);
    }

    [AvaloniaFact]
    public void AppearancePage_RebuildsTheTitleBarRowsWhenShortcutsChange()
    {
        using var root = SettingsTestRoot.Empty();
        var session = SessionFor(new TerminalSettings());
        var page = new AppearancePage();
        page.Load(session);

        session.ShortcutDraft["settings"] = "Alt+F9";
        session.RaiseShortcutsChanged();

        Assert.Contains(
            page.FindControl<StackPanel>("TitleBarItemsPanel")!.GetLogicalDescendants().OfType<TextBlock>(),
            t => t.Text == "Alt+F9");
    }

    [AvaloniaFact]
    public void AppearancePage_SelectingATheme_PreviewsAndRepaintsThroughTheSession()
    {
        using var root = SettingsTestRoot.Empty();
        root.CopyShippedTheme("GitHubLight.json");
        var session = SessionFor(new TerminalSettings());
        var page = new AppearancePage();
        page.Load(session);
        string? previewed = null;
        TerminalTheme? repainted = null;
        session.Preview.ThemeChanged += name => previewed = name;
        session.Preview.RepaintRequested += theme => repainted = theme;

        var themes = page.FindControl<ComboBox>("ThemeList")!;
        themes.SelectedItem = themes.Items.OfType<ComboBoxItem>().Single(i => (string?)i.Content == "GitHub Light");

        Assert.Equal("GitHub Light", previewed);
        Assert.Equal("GitHub Light", repainted?.Name);
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
