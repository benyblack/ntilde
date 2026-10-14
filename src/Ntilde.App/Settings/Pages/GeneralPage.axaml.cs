using System;
using Avalonia.Controls;
using Ntilde.Shell;

namespace Ntilde.Settings.Pages;

/// <summary>General: update checks and notifications.</summary>
public partial class GeneralPage : UserControl, ISettingsPage
{
    public GeneralPage()
    {
        InitializeComponent();
    }

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
    }

    public SettingsValidationError? Validate() => null;

    public void Collect(TerminalSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.AutomaticUpdateChecks = Toggle("AutomaticUpdateChecksToggle").IsChecked == true;
        target.LongCommandNotificationsEnabled = Toggle("LongCommandNotificationsToggle").IsChecked == true;
        target.Osc9NotificationsEnabled = Toggle("Osc9NotificationsToggle").IsChecked == true;
        target.Osc9ProgressReportingEnabled = Toggle("Osc9ProgressReportingToggle").IsChecked == true;
    }

    private CheckBox Toggle(string name) => this.FindControl<CheckBox>(name)!;
}
