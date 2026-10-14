using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Ntilde.Backup;
using Ntilde.Settings;
using Ntilde.Settings.Pages;
using Ntilde.Shell;
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
}
