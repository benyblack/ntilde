using System;
using System.Linq;
using Avalonia.Controls;
using Ntilde.Shell;

namespace Ntilde.Settings.Pages;

/// <summary>Agent Access: what AI agents on this machine may read or do.</summary>
public partial class AgentAccessPage : UserControl, ISettingsPage
{
    public AgentAccessPage()
    {
        InitializeComponent();
    }

    public SettingsPage Page => SettingsPage.AgentAccess;

    public Control View => this;

    public void Load(SettingsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        TerminalSettings settings = session.Settings;
        Toggle("AgentAccessObserveToggle").IsChecked = settings.AgentAccessObserveEnabled;
        Toggle("AgentReplayExportToggle").IsChecked = settings.AgentReplayExportEnabled;
        Toggle("AgentAccessActToggle").IsChecked = settings.AgentAccessActEnabled;

        var rollup = this.FindControl<ComboBox>("AgentIndicatorTabRollupList")!;
        rollup.SelectedItem = rollup.Items
            .Cast<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Content?.ToString(), settings.AgentIndicatorTabRollup, StringComparison.Ordinal));
        if (rollup.SelectedItem == null)
        {
            rollup.SelectedIndex = 0;
        }
    }

    public SettingsValidationError? Validate() => null;

    public void Collect(TerminalSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.AgentAccessObserveEnabled = Toggle("AgentAccessObserveToggle").IsChecked == true;
        target.AgentReplayExportEnabled = Toggle("AgentReplayExportToggle").IsChecked == true;
        target.AgentAccessActEnabled = Toggle("AgentAccessActToggle").IsChecked == true;
        if (this.FindControl<ComboBox>("AgentIndicatorTabRollupList")!.SelectedItem is ComboBoxItem rollupItem)
        {
            target.AgentIndicatorTabRollup = rollupItem.Content?.ToString() ?? "WritesOnly";
        }
    }

    private CheckBox Toggle(string name) => this.FindControl<CheckBox>(name)!;
}
