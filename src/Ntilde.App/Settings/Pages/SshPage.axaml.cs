using System;
using Avalonia.Controls;
using Ntilde.Shell;

namespace Ntilde.Settings.Pages;

/// <summary>SSH: global SSH behaviour. Per-connection options live in the connection manager.</summary>
public partial class SshPage : UserControl, ISettingsPage
{
    public SshPage()
    {
        InitializeComponent();
    }

    public SettingsPage Page => SettingsPage.Ssh;

    public Control View => this;

    public void Load(SettingsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        this.FindControl<CheckBox>("NativeSshToggle")!.IsChecked = session.Settings.ExperimentalNativeSshEnabled;
    }

    public SettingsValidationError? Validate() => null;

    public void Collect(TerminalSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.ExperimentalNativeSshEnabled = this.FindControl<CheckBox>("NativeSshToggle")!.IsChecked == true;
    }
}
