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
    public void ShortcutsPage_ARefusedChord_ShowsTheRowError_ButNotTheSaveMessage()
    {
        var session = SessionFor(new TerminalSettings());
        var page = new ShortcutsPage();
        page.Load(session);
        ShortcutCatalogEntry closeTab = ShortcutCatalog.GetEntries().Single(e => e.CommandId == "close_tab");
        Assert.True(ShortcutMatcher.TryParse(closeTab.DefaultBinding, out Key key, out KeyModifiers modifiers));

        TextBox editor = BindingEditorFor(page, "Settings");
        PressChord(editor, key, modifiers);

        // Nothing was recorded, so nothing blocks Save; the row says why the chord was refused.
        Assert.False(session.ShortcutDraft.ContainsKey("settings"));
        string chord = ShortcutMatcher.Format(key, modifiers);
        Assert.Contains(RowTextsFor(editor), t => t.IsVisible && t.Text == $"{chord} is already assigned to Close Tab.");
        Assert.False(page.FindControl<TextBlock>("ShortcutValidationMessage")!.IsVisible);
    }

    /// <summary>Restore and import reload every page; the rows are rebuilt, so the message from the old draft goes too.</summary>
    [AvaloniaFact]
    public void ShortcutsPage_Reload_HidesTheSaveMessage()
    {
        (ShortcutsPage page, _, TextBlock message) = ShortcutsPageAfterARefusedSave();

        page.Load(SessionFor(new TerminalSettings()));

        Assert.False(message.IsVisible);
    }

    /// <summary>The message describes the draft, so an edit to an unrelated row must not hide it while the duplicate remains.</summary>
    [AvaloniaTheory]
    [InlineData("record")]
    [InlineData("reset")]
    [InlineData("esc")]
    public void ShortcutsPage_WhileTheDraftStillHasADuplicate_AnEditElsewhere_KeepsTheSaveMessage(string edit)
    {
        (ShortcutsPage page, _, TextBlock message) = ShortcutsPageAfterARefusedSave();
        TextBox editor = BindingEditorFor(page, "New Tab");

        switch (edit)
        {
            case "record":
                PressChord(editor, Key.F9, KeyModifiers.Alt);
                break;
            case "reset":
                ResetButtonFor(editor).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                break;
            default:
                PressChord(editor, Key.Escape, KeyModifiers.None);
                break;
        }

        Assert.True(message.IsVisible);
    }

    [AvaloniaFact]
    public void ShortcutsPage_ResettingTheDuplicate_HidesTheSaveMessage()
    {
        (ShortcutsPage page, SettingsSession session, TextBlock message) = ShortcutsPageAfterARefusedSave();

        ResetButtonFor(BindingEditorFor(page, "Settings")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.True(ShortcutBindingResolver.Resolve(ShortcutCatalog.GetDefinitions(), session.ShortcutDraft).IsValid);
        Assert.False(message.IsVisible);
    }

    /// <summary>
    /// A Shortcuts page whose draft binds Settings to Command Palette's chord, as a hand-edited
    /// settings.json can, after Save was refused and showed the message.
    /// </summary>
    private static (ShortcutsPage Page, SettingsSession Session, TextBlock Message) ShortcutsPageAfterARefusedSave()
    {
        var session = SessionFor(new TerminalSettings());
        session.ShortcutDraft["settings"] = ShortcutCatalog.GetEntries().Single(e => e.CommandId == "command_palette").DefaultBinding;
        var page = new ShortcutsPage();
        page.Load(session);
        Assert.NotNull(page.Validate());
        var message = page.FindControl<TextBlock>("ShortcutValidationMessage")!;
        Assert.True(message.IsVisible);
        return (page, session, message);
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

        Button reset = ResetButtonFor(BindingEditorFor(page, "Settings"));
        reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.False(session.ShortcutDraft.ContainsKey("settings"));
        Assert.Equal(1, changes);
    }

    [AvaloniaFact]
    public void ShortcutsPage_EscWithAModifier_IsRecorded_OnAnAppRow()
    {
        var session = SessionFor(new TerminalSettings());
        var page = new ShortcutsPage();
        page.Load(session);

        TextBox editor = BindingEditorFor(page, "Settings");
        PressChord(editor, Key.Escape, KeyModifiers.Shift);

        string expected = ShortcutMatcher.Format(Key.Escape, KeyModifiers.Shift);
        Assert.Equal(expected, editor.Text);
        Assert.Equal(expected, session.ShortcutDraft["settings"]);
    }

    [AvaloniaFact]
    public void ShortcutsPage_PlainEsc_IsRecorded_OnACommandAssistRow()
    {
        string shiftEsc = ShortcutMatcher.Format(Key.Escape, KeyModifiers.Shift);
        var session = SessionFor(new TerminalSettings { Keybindings = new() { ["command_assist_dismiss"] = shiftEsc } });
        var page = new ShortcutsPage();
        page.Load(session);
        int changes = 0;
        session.ShortcutsChanged += () => changes++;

        TextBox editor = BindingEditorFor(page, "Command Assist: Close");
        PressChord(editor, Key.Escape, KeyModifiers.None);

        // Escape is this row's default, so recording it removes the override.
        Assert.Equal("Escape", editor.Text);
        Assert.False(session.ShortcutDraft.ContainsKey("command_assist_dismiss"));
        Assert.Equal(1, changes);
    }

    /// <summary>
    /// These are matched by the window before the terminal sees the key, so a plain-Esc binding
    /// would take Esc away from every program running in it. Command Assist Toggle is one of them,
    /// although it is listed under Command Assist (PR #530 review). Command Assist: Close is moved
    /// off Esc first, so the duplicate check cannot be what stops the press.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Settings", "settings")]
    [InlineData("Close Pane", "close_pane")]
    [InlineData("Command Assist Toggle", "command_assist_toggle")]
    public void ShortcutsPage_PlainEsc_OnARowMatchedBeforeTheTerminal_StillCancels_AndRecordsNothing(string title, string commandId)
    {
        string shiftEsc = ShortcutMatcher.Format(Key.Escape, KeyModifiers.Shift);
        var session = SessionFor(new TerminalSettings { Keybindings = new() { ["command_assist_dismiss"] = shiftEsc } });
        var page = new ShortcutsPage();
        page.Load(session);
        int changes = 0;
        session.ShortcutsChanged += () => changes++;

        TextBox editor = BindingEditorFor(page, title);
        string before = editor.Text!;
        PressChord(editor, Key.Escape, KeyModifiers.None);

        Assert.Equal(before, editor.Text);
        Assert.False(session.ShortcutDraft.ContainsKey(commandId));
        Assert.Equal(0, changes);
    }

    [AvaloniaFact]
    public void ShortcutsPage_PlainEsc_OnAnotherCommandAssistRow_IsRefused_WhileCloseHasIt()
    {
        var session = SessionFor(new TerminalSettings());
        var page = new ShortcutsPage();
        page.Load(session);

        TextBox editor = BindingEditorFor(page, "Command Assist: Previous Suggestion");
        PressChord(editor, Key.Escape, KeyModifiers.None);

        Assert.False(session.ShortcutDraft.ContainsKey("command_assist_selection_up"));
        Assert.Contains(RowTextsFor(editor), t => t.IsVisible && t.Text == "Escape is already assigned to Command Assist: Close.");
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
        var history = page.FindControl<SettingRow>("CommandAssistHistoryRow")!;

        Assert.Contains("Alt+F8", history.Description);
        Assert.DoesNotContain("Ctrl+R", history.Description);

        session.ShortcutDraft["command_assist_history"] = "Alt+F7";
        session.RaiseShortcutsChanged();

        Assert.Contains("Alt+F7", history.Description);
    }

    [AvaloniaFact]
    public void AgentAccessPage_ShowsReadableLabels_AndStillStoresTheOldValues()
    {
        var page = new AgentAccessPage();
        page.Load(SessionFor(new TerminalSettings { AgentIndicatorTabRollup = "All" }));
        var combo = page.FindControl<ComboBox>("AgentIndicatorTabRollupList")!;
        var target = new TerminalSettings();

        page.Collect(target);

        string[] labels = ["Writes only", "All activity"];
        Assert.Equal(labels, combo.Items.OfType<ComboBoxItem>().Select(i => (string)i.Content!).ToArray());
        Assert.Equal("All activity", ((ComboBoxItem)combo.SelectedItem!).Content);
        Assert.Equal("All", target.AgentIndicatorTabRollup);
    }

    [AvaloniaTheory]
    [InlineData("writesonly")]
    [InlineData("Sometimes")]
    public void AgentAccessPage_AnUnknownStoredRollup_OpensOnWritesOnly(string stored)
    {
        var page = new AgentAccessPage();
        page.Load(SessionFor(new TerminalSettings { AgentIndicatorTabRollup = stored }));
        // Seed with a value that is neither the default nor the expected result, so a Load that selects nothing fails.
        var target = new TerminalSettings { AgentIndicatorTabRollup = "All" };

        page.Collect(target);

        Assert.Equal("WritesOnly", target.AgentIndicatorTabRollup);
    }

    [AvaloniaFact]
    public void LongDescriptions_SitBehindMore()
    {
        string[] withMore = ["remember-commands", "remote-shell-integration", "native-ssh", "keybindings", "osc9-notifications", "update-checks"];
        Control[] pages = [new CommandAssistPage(), new SshPage(), new ShortcutsPage(), new GeneralPage()];

        var rows = pages.SelectMany(page => page.GetLogicalDescendants().OfType<SettingRow>()).ToDictionary(row => row.RowId!);

        Assert.All(withMore, id => Assert.False(string.IsNullOrWhiteSpace(rows[id].Details), $"{id} has no Details"));
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
    }

    [AvaloniaFact]
    public void ProfilesPage_AnEditKeepsTheFieldsTheEditorDoesNotShow()
    {
        using var root = SettingsTestRoot.Empty();
        var settings = new TerminalSettings();
        settings.Profiles.Clear();
        settings.Profiles.Add(new TerminalProfile
        {
            Name = "Legacy",
            Type = ConnectionType.Local,
            Command = "/bin/bash",
            CursorStyle = "Block",
            ShellOverride = Ntilde.Platform.ShellOverride.Posix,
            Icon = "Server",
        });
        var page = new ProfilesPage();
        page.Load(SessionFor(settings));
        page.FindControl<ListBox>("ProfilesListBox")!.SelectedIndex = 0;

        var name = page.FindControl<TextBox>("ProfileNameInput")!;
        name.Text = "Renamed";
        name.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.D, Source = name });
        var target = new TerminalSettings();
        page.Collect(target);

        TerminalProfile saved = Assert.Single(target.Profiles);
        Assert.Equal("Renamed", saved.Name);
        Assert.Equal("Block", saved.CursorStyle);
        Assert.Equal(Ntilde.Platform.ShellOverride.Posix, saved.ShellOverride);
        Assert.Equal("Server", saved.Icon);
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
    }

    [AvaloniaFact]
    public void ProfilesPage_HasNoSshEditor()
    {
        var page = new ProfilesPage();

        Assert.Null(page.FindControl<Control>("SshSettingsPanel"));
        Assert.Null(page.FindControl<Control>("ProfileTypeList"));
        Assert.Null(page.FindControl<Control>("ForwardsList"));
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
            SessionPersistence = "Off",
        }));
        var target = new TerminalSettings();

        page.Collect(target);

        Assert.False(target.AutomaticUpdateChecks);
        Assert.True(target.LongCommandNotificationsEnabled);
        Assert.True(target.Osc9NotificationsEnabled);
        Assert.False(target.Osc9ProgressReportingEnabled);
        Assert.Equal("Off", target.SessionPersistence); // the target's default is KeepOnClose
    }

    /// <summary>
    /// The session persistence combo reads the stored value the way main does (trimmed, ignoring case; anything
    /// else is Off). The target starts on the OTHER value so a Load that selects nothing fails.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(" keeponclose ", "KeepOnClose", "Off")]
    [InlineData("bogus", "Off", "KeepOnClose")]
    public void GeneralPage_SessionPersistence_FallsBackLikeMain(string stored, string expected, string targetStart)
    {
        var page = new GeneralPage();
        page.Load(SessionFor(new TerminalSettings { SessionPersistence = stored }));
        var target = new TerminalSettings { SessionPersistence = targetStart };

        page.Collect(target);

        Assert.Equal(expected, target.SessionPersistence);
    }

    [AvaloniaFact]
    public void GeneralPage_QuitLink_FollowsQuitLinkVisible_AndClickRaisesTheEvent()
    {
        var page = new GeneralPage();
        page.Load(SessionFor(new TerminalSettings()));
        var link = page.FindControl<Button>("QuitAndCloseAllShellsLink")!;
        int raised = 0;
        page.QuitAndCloseAllRequested += () => raised++;

        Assert.False(page.QuitLinkVisible);
        Assert.False(link.IsVisible);
        page.QuitLinkVisible = true;
        Assert.True(link.IsVisible);

        link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(1, raised);
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
        var page = new AppearancePage();
        page.Load(SessionFor(new TerminalSettings
        {
            ThemeName = "GitHub Light",
            WindowOpacity = 0.75,
            UiScale = 1.25,
            BlurEffect = "Mica",
            BackgroundImagePath = "/img.png",
            BackgroundImageOpacity = 0.35,
            BackgroundImageStretch = "Uniform",
        }));
        var target = new TerminalSettings();

        page.Collect(target);

        Assert.Equal("GitHub Light", target.ThemeName);
        Assert.Equal(0.75, target.WindowOpacity, precision: 9);
        Assert.Equal(1.25, target.UiScale, precision: 9);
        Assert.Equal("Mica", target.BlurEffect);
        Assert.Equal("/img.png", target.BackgroundImagePath);
        Assert.Equal(0.35, target.BackgroundImageOpacity, precision: 9);
        Assert.Equal("Uniform", target.BackgroundImageStretch);
        Assert.Equal("Fit", ((ComboBoxItem)page.FindControl<ComboBox>("BgImageStretchList")!.SelectedItem!).Content);
    }

    /// <summary>Review focus 1: hand-edited or older values open on today's fallback and save it.</summary>
    [AvaloniaTheory]
    [InlineData("uniform", "UniformToFill")]       // case differs: no match, fall back to Fill (crop)
    [InlineData("Tile", "UniformToFill")]
    [InlineData("None", "None")]
    public void AppearancePage_BackgroundStretch_UnknownValuesFallBackAsBefore(string stored, string saved)
    {
        using var root = SettingsTestRoot.Empty();
        var page = new AppearancePage();
        page.Load(SessionFor(new TerminalSettings { BackgroundImageStretch = stored }));
        // Seed with a value that is neither the default nor the expected result, so a Load that selects nothing fails.
        var target = new TerminalSettings { BackgroundImageStretch = "unset" };

        page.Collect(target);

        Assert.Equal(saved, target.BackgroundImageStretch);
    }

    [AvaloniaTheory]
    [InlineData("Transparent", "Acrylic")]
    [InlineData("mica", "Acrylic")]                 // the old loop compared ordinally
    [InlineData("None", "None")]
    public void AppearancePage_BlurEffect_UnknownValuesFallBackAsBefore(string stored, string saved)
    {
        using var root = SettingsTestRoot.Empty();
        var page = new AppearancePage();
        page.Load(SessionFor(new TerminalSettings { BlurEffect = stored }));
        // Seed with a value that is neither the default nor the expected result, so a Load that selects nothing fails.
        var target = new TerminalSettings { BlurEffect = "unset" };

        page.Collect(target);

        Assert.Equal(saved, target.BlurEffect);
    }

    [AvaloniaTheory]
    [InlineData("Vertical", "Vertical")]
    [InlineData("vertical", "Vertical")]     // the old loop compared case-insensitively too
    [InlineData("Diagonal", "Horizontal")]   // unknown: first item, as before
    public void TabsAndTitleBarPage_TabOrientation_RoundTrips_WithTheOldFallbacks(string stored, string expected)
    {
        var page = new TabsAndTitleBarPage();
        page.Load(SessionFor(new TerminalSettings { TabStripOrientation = stored }));
        // Seed with a value that is neither the default nor the expected result, so a Load that selects nothing fails.
        var target = new TerminalSettings { TabStripOrientation = "unset" };

        page.Collect(target);

        Assert.Equal(expected, target.TabStripOrientation);
    }

    [AvaloniaFact]
    public void TabsAndTitleBarPage_RebuildsTheTitleBarRowsWhenShortcutsChange()
    {
        using var root = SettingsTestRoot.Empty();
        var session = SessionFor(new TerminalSettings());
        var page = new TabsAndTitleBarPage();
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

    /// <summary>The Reset button on the same row as <paramref name="editor"/>.</summary>
    internal static Button ResetButtonFor(TextBox editor)
        => editor.GetLogicalParent()!.GetLogicalChildren().OfType<Button>().Single(b => (string?)b.Content == "Reset");

    /// <summary>The row's own text lines below the editor, among them its error line.</summary>
    internal static TextBlock[] RowTextsFor(TextBox editor)
        => editor.GetLogicalParent()!.GetLogicalParent()!.GetLogicalChildren().OfType<TextBlock>().ToArray();

    internal static void PressChord(Control target, Key key, KeyModifiers modifiers)
        => target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = target });

    /// <summary>Spec section 8: moving the long wording behind "More" must not drop any of it.</summary>
    [AvaloniaFact]
    public void CommandAssistPage_RememberCommands_KeepsTheCompleteHistorySecurityWording()
    {
        var page = new CommandAssistPage();
        var row = page.GetLogicalDescendants().OfType<SettingRow>().Single(r => r.RowId == "remember-commands");
        string text = row.Description + " " + row.Details;

        Assert.Contains("Other secrets typed as part of a visible command are not.", text);
        Assert.Contains("Clear history erases every recorded command", text);
        Assert.Contains("Other secrets typed as part of a visible command are not.", row.Details);
        Assert.Contains("Clear history erases every recorded command", row.Details);
    }

    [AvaloniaFact]
    public void CommandAssistPage_ClearHistoryWithoutAStore_ShowsItsStatusOnTheRow()
    {
        var page = new CommandAssistPage();
        page.Load(SessionFor(new TerminalSettings())); // no history store
        var row = page.GetLogicalDescendants().OfType<SettingRow>().Single(r => r.RowId == "remember-commands");

        page.FindControl<Button>("BtnClearCommandAssistHistory")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal("Command history is not available in this window.", row.Status);
    }
}
