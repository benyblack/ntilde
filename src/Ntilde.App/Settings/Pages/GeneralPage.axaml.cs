using System;
using Avalonia.Controls;
using Ntilde.Shell;
using Ntilde.Shell.Mux;

namespace Ntilde.Settings.Pages;

/// <summary>General: update checks, notifications and whether shells keep running when the window closes.</summary>
public partial class GeneralPage : UserControl, ISettingsPage
{
    public GeneralPage()
    {
        InitializeComponent();
        QuitLink.Click += (_, _) => QuitAndCloseAllRequested?.Invoke();
    }

    /// <summary>
    /// Whether the "Quit and close all shells…" link under the session persistence row is shown. The owning window
    /// sets it (only a window with session persistence on offers the quit).
    /// </summary>
    internal bool QuitLinkVisible
    {
        get => QuitLink.IsVisible;
        set => QuitLink.IsVisible = value;
    }

    /// <summary>Raised when the "Quit and close all shells…" link is clicked; the owning window closes without saving.</summary>
    internal event Action? QuitAndCloseAllRequested;

    public SettingsPage Page => SettingsPage.General;

    public Control View => this;

    public void Load(SettingsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        TerminalSettings settings = session.Settings;
        Toggle("AutomaticUpdateChecksToggle").IsChecked = settings.AutomaticUpdateChecks;
        Toggle("LongCommandNotificationsToggle").IsChecked = settings.LongCommandNotificationsEnabled;
        Toggle("Osc9NotificationsToggle").IsChecked = settings.Osc9NotificationsEnabled;
        Toggle("Osc9ProgressReportingToggle").IsChecked = settings.Osc9ProgressReportingEnabled;

        // Read the way main does (trimmed, ignoring case; anything else is Off). The value is normalized
        // first, so one of the two Tags always matches and ComboTags' fallback never has to fire.
        string persistence = SessionPersistenceMode.IsKeepOnClose(settings.SessionPersistence)
            ? SessionPersistenceMode.KeepOnClose
            : SessionPersistenceMode.Off;
        ComboTags.Select(PersistenceCombo, persistence, StringComparison.Ordinal, fallbackIndex: 0);
    }

    public SettingsValidationError? Validate() => null;

    public void Collect(TerminalSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.AutomaticUpdateChecks = Toggle("AutomaticUpdateChecksToggle").IsChecked == true;
        target.LongCommandNotificationsEnabled = Toggle("LongCommandNotificationsToggle").IsChecked == true;
        target.Osc9NotificationsEnabled = Toggle("Osc9NotificationsToggle").IsChecked == true;
        target.Osc9ProgressReportingEnabled = Toggle("Osc9ProgressReportingToggle").IsChecked == true;
        if (ComboTags.SelectedValue(PersistenceCombo) is { } persistence)
        {
            target.SessionPersistence = persistence;
        }
    }

    private ComboBox PersistenceCombo => this.FindControl<ComboBox>("SessionPersistenceList")!;

    private Button QuitLink => this.FindControl<Button>("QuitAndCloseAllShellsLink")!;

    private CheckBox Toggle(string name) => this.FindControl<CheckBox>(name)!;
}
