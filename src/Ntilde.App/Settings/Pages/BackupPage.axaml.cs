using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Ntilde.Shell;
using Ntilde.Platform;
using Ntilde.Pty;
using Ntilde.VT;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using Avalonia.Threading;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using Avalonia.Styling;
using Ntilde.CommandAssist.Application;
using Ntilde.CommandAssist.Domain;
using Ntilde.CommandAssist.Models;
using Ntilde.CommandAssist.ShellIntegration.Remote;
using Ntilde.Backup;
using Ntilde.Services.Ssh;
using Ntilde.Shell.Shortcuts;
using Ntilde.Shell.TitleBar;
using Ntilde.Settings;

namespace Ntilde.Settings.Pages;

/// <summary>Backup &amp; Restore: export, import and restore snapshots. Every action takes effect immediately.</summary>
public partial class BackupPage : UserControl, ISettingsPage
{
    private readonly BackupService _service = new(AppPaths.RootDirectory, log: AppLogger.Log);
    private readonly TextBlock? _status;
    private readonly ListBox? _snapshotList;
    private SettingsSession? _session;

    public BackupPage()
    {
        InitializeComponent();

        var btnExport = this.FindControl<Button>("BtnBackupExport");
        var btnImport = this.FindControl<Button>("BtnBackupImport");
        var btnRestore = this.FindControl<Button>("BtnRestoreSnapshot");
        _status = this.FindControl<TextBlock>("BackupStatusText");
        _snapshotList = this.FindControl<ListBox>("SnapshotList");

        if (_snapshotList is not null)
        {
            // ListBox has no WPF-style DisplayMemberBinding in Avalonia; a FuncDataTemplate
            // is the established pattern here (see MainWindow.ShowAgentActivityJournalAsync's
            // agent-activity ItemsControl).
            _snapshotList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<SnapshotRow>((row, _) =>
                new TextBlock { Text = row?.Display ?? string.Empty, Margin = new Thickness(4, 2) });
        }

        if (btnExport != null)
        {
            btnExport.Click += async (_, _) =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel is null) return;

                var file = await topLevel.StorageProvider.SaveFilePickerAsync(
                    new Avalonia.Platform.Storage.FilePickerSaveOptions
                    {
                        Title = "Export Ntilde configuration",
                        SuggestedFileName = $"ntilde-{DateTime.Now:yyyy-MM-dd}{BackupService.BundleExtension}",
                        DefaultExtension = BackupService.BundleExtension.TrimStart('.')
                    });

                if (file is null) return;

                var outcome = _service.Export(file.Path.LocalPath);
                SetStatus(outcome.Success ? $"Exported to {file.Name}." : outcome.Message, outcome.Success);
            };
        }

        if (btnImport != null)
        {
            btnImport.Click += async (_, _) =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel is null) return;

                var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                    new Avalonia.Platform.Storage.FilePickerOpenOptions
                    {
                        Title = "Import Ntilde configuration",
                        AllowMultiple = false
                    });

                if (files.Count == 0) return;
                string path = files[0].Path.LocalPath;

                // Inspect first so the confirmation names what is about to change.
                var inspection = _service.Inspect(path);
                if (!inspection.Success)
                {
                    SetStatus(inspection.Message, success: false);
                    return;
                }

                var mode = await PromptForImportModeAsync(inspection.Inspection!);
                if (mode is null) return;

                var outcome = _service.Import(path, mode.Value);
                if (outcome.Success)
                {
                    // I1: a successful Import already changed settings.json (and possibly
                    // profiles.json, keybindings, etc.) on disk. _settings and everything
                    // derived from it at construction time are now a stale snapshot of the
                    // PRE-import state - if this window's Save is clicked afterward, it would
                    // silently overwrite the very change Import just made with that stale
                    // snapshot. Reload before anything else can touch _settings again.
                    await Session.ReloadAfterExternalChangeAsync();
                }

                // M2: surface the service's own outcome message rather than a generic one -
                // it carries the "connection passwords are not included" note when
                // Connections was among the imported categories, the only place a Settings
                // window user ever sees that warning.
                SetStatus(
                    outcome.Success
                        ? $"{outcome.Message} Restart Ntilde to pick up all changes."
                        : outcome.Message,
                    outcome.Success);
                RefreshSnapshots();
            };
        }

        if (btnRestore != null)
        {
            btnRestore.Click += async (_, _) =>
            {
                if (_snapshotList?.SelectedItem is not SnapshotRow row)
                {
                    SetStatus("Select a snapshot first.", success: false);
                    return;
                }

                // Restore overwrites live configuration immediately, and unlike Import (which
                // offers Merge/Replace) it previously asked nothing at all — the more
                // surprising of the two destructive actions on this page got the weaker gate.
                // Cancel must mean no restore; there is no default "yes" other than the
                // affirmative button. RestoreConfirmationOverride is a test seam (see its own
                // doc comment) - production always falls through to the real ConfirmRestoreAsync.
                Func<SnapshotRow, System.Threading.Tasks.Task<bool>> confirm = RestoreConfirmationOverride ?? ConfirmRestoreAsync;
                bool confirmed = await confirm(row);
                if (!confirmed) return;

                var outcome = _service.Restore(row.Id);
                if (outcome.Success)
                {
                    // I1: same reasoning as the Import handler above - Restore just changed
                    // settings.json (and possibly more) on disk, and this window's in-memory
                    // state must not be allowed to stomp it on a later Save.
                    await Session.ReloadAfterExternalChangeAsync();
                }

                SetStatus(
                    outcome.Success
                        ? $"{outcome.Message} Restart Ntilde to pick up all changes."
                        : outcome.Message,
                    outcome.Success);
                RefreshSnapshots();
            };
        }
    }

    public SettingsPage Page => SettingsPage.Backup;

    public Control View => this;

    private SettingsSession Session => _session ?? throw new InvalidOperationException("BackupPage used before Load.");

    public void Load(SettingsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;

        // Here rather than in the constructor: SetStatus colours the message with the palette, and
        // the page has the window's resources only once it has joined the window.
        RefreshSnapshots();
    }

    public SettingsValidationError? Validate() => null;

    public void Collect(TerminalSettings target)
    {
        // Nothing to collect: export, import and restore act immediately.
    }

    private void SetStatus(string message, bool success)
    {
        if (_status is null) return;
        _status.Text = message;
        _status.Foreground = success
            ? (IBrush?)this.FindResource("NtGreen")
            : (IBrush?)this.FindResource("NtRed");
    }

    private void RefreshSnapshots()
    {
        if (_snapshotList is null) return;

        // WireBackupSection runs unconditionally from the constructor, so a locked
        // backups directory, a permissions error, or a transient antivirus lock during
        // Directory.GetFiles must not propagate out of here — that would stop Settings
        // from opening at all. Fall back to an empty list and say so instead.
        try
        {
            var rows = _service.ListSnapshots()
                .Select(s => new SnapshotRow(
                    s.Id,
                    $"{s.CreatedUtc.LocalDateTime:yyyy-MM-dd HH:mm}  ·  {ReasonLabel(s.Reason)}  ·  {s.SizeBytes / 1024.0:N0} KB",
                    s.Reason,
                    s.CreatedUtc))
                .ToArray();

            _snapshotList.ItemsSource = rows;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _snapshotList.ItemsSource = Array.Empty<SnapshotRow>();
            SetStatus($"Could not read snapshots: {ex.Message}", success: false);
        }
    }

    /// <summary>
    /// Test seam for the "confirm before restoring" gate. Null (the default, and always the
    /// case in production) uses the real <see cref="ConfirmRestoreAsync"/>, which shows an
    /// actual modal <see cref="Window.ShowDialog"/> - not drivable headlessly without risking
    /// a hang (see <see cref="ConfirmRestoreAsync"/>'s remarks). Tests substitute a synchronous
    /// fake here instead, so both the "confirmed" and "declined" branches of the Restore click
    /// handler are covered by something that actually runs in CI, without ever touching
    /// ShowDialog. Internal rather than public: this exists solely so
    /// Ntilde.App.Tests (an InternalsVisibleTo friend) can reach it - it is a test seam,
    /// not new public API.
    /// </summary>
    internal Func<SnapshotRow, System.Threading.Tasks.Task<bool>>? RestoreConfirmationOverride { get; set; }

    private static string ReasonLabel(SnapshotReason reason) => reason switch
    {
        SnapshotReason.Auto => "automatic",
        SnapshotReason.PreImport => "before import",
        SnapshotReason.PreRestore => "before restore",
        _ => "automatic"
    };

    internal sealed record SnapshotRow(string Id, string Display, SnapshotReason Reason, DateTimeOffset CreatedUtc);

    /// <summary>
    /// The restore confirmation dialog's headline and body text for <paramref name="row"/>.
    /// Factored out of <see cref="ConfirmRestoreAsync"/> so the wording — which must name the
    /// snapshot being restored and describe what happens — is unit-testable on its own.
    /// </summary>
    /// <remarks>
    /// The dialog itself (<see cref="ConfirmRestoreAsync"/>, like <see cref="PromptForImportModeAsync"/>)
    /// is not: a real modal <c>Window.ShowDialog</c> with no owner ever shown and no button for
    /// anything to click does not return in this repo's headless test host — confirmed
    /// previously in <c>MainWindowShellExitTests</c> (a declined-confirmation branch behind the
    /// same pattern was deleted rather than risk the hang). This split keeps the requirement —
    /// name the snapshot's timestamp and reason, state that it replaces the categories the
    /// snapshot contains, and note the pre-restore snapshot taken first — covered by a test
    /// without driving the modal itself.
    /// </remarks>
    internal static (string Headline, string Body) BuildRestoreConfirmationText(SnapshotRow row)
    {
        string when = $"{row.CreatedUtc.LocalDateTime:yyyy-MM-dd HH:mm}";
        string headline = $"Restore the snapshot from {when} ({ReasonLabel(row.Reason)})?";
        string body =
            "This replaces the categories the snapshot contains with their state at that time. " +
            "A snapshot of your current configuration is taken first, so this restore itself can be undone.";
        return (headline, body);
    }

    /// <summary>
    /// Confirms before restoring a snapshot. Restore is the more surprising destructive action
    /// on this page (Import at least offers Merge/Replace); this brings it in line. Returns
    /// false when the user cancels — there is no default "yes" other than the affirmative
    /// button.
    /// </summary>
    private async System.Threading.Tasks.Task<bool> ConfirmRestoreAsync(SnapshotRow row)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return false;

        var (headline, body) = BuildRestoreConfirmationText(row);

        bool confirmed = false;

        var dialog = new Window
        {
            Title = "Restore configuration",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };
        UiScale.FitWindow(dialog);

        var restoreButton = new Button { Content = "Restore", Classes = { "Pill" } };
        var cancelButton = new Button { Content = "Cancel", Classes = { "Pill" } };

        restoreButton.Click += (_, _) => { confirmed = true; dialog.Close(); };
        cancelButton.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = headline,
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = body,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.75
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancelButton, restoreButton }
                }
            }
        };

        await dialog.ShowDialog(owner);
        return confirmed;
    }

    /// <summary>
    /// The Merge/Replace prompt's explanatory body text for a bundle whose contents are
    /// <paramref name="inspection"/>. Factored out of <see cref="PromptForImportModeAsync"/> so
    /// the wording is unit-testable without going anywhere near <c>ShowDialog</c> - the same
    /// split <see cref="BuildRestoreConfirmationText"/> uses for the Restore confirmation.
    /// </summary>
    /// <remarks>
    /// (P2, Codex review round 2, PR #362): the base sentence — "Merge keeps items you have
    /// locally that the bundle does not contain" — is false for Snippets. <c>BackupService</c>'s
    /// <c>BuildPlan</c> replaces <c>snippets.json</c> wholesale in BOTH modes (a deliberate
    /// design decision, spec'd: the file is a flat array with no stable id, so there is nothing
    /// to merge by — see <c>Snippets_AlwaysReplacedWholesale</c>). A user choosing Merge
    /// specifically to keep local snippets would lose them with no warning. This fixes the
    /// copy, not the semantics: the caveat only appears when the bundle actually contains the
    /// Snippets category (an <see cref="BundleInspection.ItemCounts"/> lookup — <c>Inspect</c>
    /// already gives the caller this for free), so a bundle without Snippets sees the same
    /// wording as before.
    /// </remarks>
    internal static string BuildImportModeBodyText(BundleInspection inspection)
    {
        bool bundleHasSnippets = inspection.ItemCounts.TryGetValue(BackupCategory.Snippets, out int snippetCount)
            && snippetCount > 0;

        string snippetsCaveat = bundleHasSnippets
            ? " Snippets are always replaced entirely in either mode — Merge does not apply to them."
            : string.Empty;

        return "Merge keeps items you have locally that the bundle does not contain. " +
               "Replace makes the bundle the truth for the categories above. " +
               "A snapshot is taken first either way, so you can roll back." +
               snippetsCaveat;
    }

    /// <summary>
    /// Asks whether to merge or replace, showing what the bundle contains. Returns null
    /// when the user cancels. Import is destructive, so there is no default —
    /// the user must pick.
    /// </summary>
    private async System.Threading.Tasks.Task<ImportMode?> PromptForImportModeAsync(BundleInspection inspection)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return null;

        string summary = string.Join(
            ", ",
            inspection.ItemCounts
                .Where(pair => pair.Value > 0)
                .Select(pair => $"{pair.Value} {pair.Key.ToString().ToLowerInvariant()}"));

        string body = BuildImportModeBodyText(inspection);

        ImportMode? choice = null;

        var dialog = new Window
        {
            Title = "Import configuration",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };
        UiScale.FitWindow(dialog);

        var mergeButton = new Button { Content = "Merge", Classes = { "Pill" } };
        var replaceButton = new Button { Content = "Replace", Classes = { "Pill" } };
        var cancelButton = new Button { Content = "Cancel", Classes = { "Pill" } };

        mergeButton.Click += (_, _) => { choice = ImportMode.Merge; dialog.Close(); };
        replaceButton.Click += (_, _) => { choice = ImportMode.Replace; dialog.Close(); };
        cancelButton.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = $"This bundle contains: {summary}.",
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = body,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.75
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancelButton, mergeButton, replaceButton }
                }
            }
        };

        await dialog.ShowDialog(owner);
        return choice;
    }
}
