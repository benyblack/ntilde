using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Ntilde.Backup;
using Ntilde.CommandAssist.Models;
using Ntilde.CommandAssist.Storage;
using Ntilde.Settings.Pages;
using Ntilde.Tests.Backup;
using Xunit;

namespace Ntilde.Tests.Core;

/// <summary>
/// Task 8: the Settings window's "Backup &amp; Restore" page (<see cref="BackupPage"/>): its
/// snapshot list, Restore's confirmation gate, and the reload after an Import or Restore that keeps
/// a later Save from reverting what was just brought in.
/// </summary>
public sealed class SettingsWindowBackupSectionTests
{
    [AvaloniaFact]
    public void BackupTab_NamedControls_AreAllReachableByName()
    {
        using var tree = BackupTestTree.CreateEmpty();
        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new Ntilde.SettingsWindow();

        Assert.NotNull(window.GetPage<BackupPage>().FindControl<Button>("BtnBackupExport"));
        Assert.NotNull(window.GetPage<BackupPage>().FindControl<Button>("BtnBackupImport"));
        Assert.NotNull(window.GetPage<BackupPage>().FindControl<TextBlock>("BackupStatusText"));
        Assert.NotNull(window.GetPage<BackupPage>().FindControl<ListBox>("SnapshotList"));
        Assert.NotNull(window.GetPage<BackupPage>().FindControl<Button>("BtnRestoreSnapshot"));
    }

    [AvaloniaFact]
    public void SnapshotList_IsPopulatedFromDisk_WithReasonAndSizeInTheDisplayText()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root);
        var written = service.Snapshot(SnapshotReason.Auto);
        Assert.NotNull(written);

        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new Ntilde.SettingsWindow();

        var snapshotList = window.GetPage<BackupPage>().FindControl<ListBox>("SnapshotList")!;
        var rows = snapshotList.ItemsSource!.Cast<object>().ToArray();

        Assert.Single(rows);
        string display = GetDisplay(rows[0]);
        Assert.Contains("automatic", display);
    }

    [AvaloniaFact]
    public void RestoreSelected_WithNoSelection_ShowsErrorStatus_AndDoesNotThrow()
    {
        using var tree = BackupTestTree.CreateEmpty();
        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new Ntilde.SettingsWindow();

        var btnRestore = window.GetPage<BackupPage>().FindControl<Button>("BtnRestoreSnapshot")!;
        var status = window.GetPage<BackupPage>().FindControl<TextBlock>("BackupStatusText")!;

        // Safe to click: with nothing selected the handler returns before it ever reaches the
        // confirmation dialog, so this can't hit the ShowDialog hang described below.
        var exception = Record.Exception(() => btnRestore.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)));

        Assert.Null(exception);
        Assert.Equal("Select a snapshot first.", status.Text);
    }

    /// <summary>
    /// Fix round 1 (Important finding, design call): Restore now asks for confirmation before
    /// overwriting live configuration, naming the snapshot's timestamp and reason, stating that it
    /// replaces the categories the snapshot contains, and noting that a pre-restore snapshot is
    /// taken first. This tests that wording directly, via the private
    /// <c>BuildRestoreConfirmationText</c> helper the confirmation dialog is built from.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT click "Restore selected" with a snapshot selected and drive the
    /// confirmation dialog end to end. <c>MainWindowShellExitTests</c> already found, empirically,
    /// that a real <c>Window.ShowDialog</c> with no owner ever shown and no button for anything to
    /// click does not return in this repo's headless test host — the UI thread gets stuck inside
    /// <c>ShowDialog</c> itself, not merely at the awaiting call site, so even a fire-and-forget
    /// click risks hanging the test run. <c>BuildRestoreConfirmationText</c> exists specifically so
    /// the wording requirement can be verified without going anywhere near that call.
    /// </remarks>
    [AvaloniaFact]
    public void RestoreConfirmationText_NamesTheSnapshotAndReason_AndExplainsReplaceAndPreRestoreUndo()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root);
        var written = service.Snapshot(SnapshotReason.Auto);
        Assert.NotNull(written);

        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new Ntilde.SettingsWindow();

        var snapshotList = window.GetPage<BackupPage>().FindControl<ListBox>("SnapshotList")!;
        object row = snapshotList.ItemsSource!.Cast<object>().Single();

        var (headline, body) = BackupPage.BuildRestoreConfirmationText((BackupPage.SnapshotRow)row);

        // Names the snapshot: its timestamp and reason.
        Assert.Contains(written!.CreatedUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), headline);
        Assert.Contains("automatic", headline, StringComparison.OrdinalIgnoreCase);

        // States it replaces the categories the snapshot contains, and that a pre-restore
        // snapshot is taken first so the restore itself can be undone.
        Assert.Contains("replaces", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("taken first", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("undone", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fix round 2 (Important finding): the confirmation gate added in round 1 for
    /// <c>BuildRestoreConfirmationText</c>'s wording left the actual wiring uncovered - does
    /// confirming really trigger the restore? Pins the "confirmed" branch through
    /// <c>RestoreConfirmationOverride</c>, the injectable seam the Backup page's Restore click
    /// handler (<c>BackupPage</c>) falls back from (production always uses the real, untestable
    /// <c>ConfirmRestoreAsync</c>/<c>ShowDialog</c>). Verifies the tracked file actually rolls back
    /// on disk (not just that some outcome was reported) and that a PreRestore snapshot appears.
    /// </summary>
    [AvaloniaFact]
    public void RestoreSelected_WhenConfirmationReturnsTrue_CallsRestore_AndRollsBackTheTrackedFile()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root);
        var snapshot = service.Snapshot(SnapshotReason.Auto);
        Assert.NotNull(snapshot);

        // Drift after the snapshot, so restoring has something real to roll back - proves
        // service.Restore actually ran, not just that some status text changed.
        tree.WriteFile("settings.json", """{"FontSize":99,"ThemeName":"Changed"}""");

        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new Ntilde.SettingsWindow { RestoreConfirmationOverride = _ => Task.FromResult(true) };

        var snapshotList = window.GetPage<BackupPage>().FindControl<ListBox>("SnapshotList")!;
        var btnRestore = window.GetPage<BackupPage>().FindControl<Button>("BtnRestoreSnapshot")!;
        var status = window.GetPage<BackupPage>().FindControl<TextBlock>("BackupStatusText")!;

        snapshotList.SelectedIndex = 0;
        btnRestore.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        // M2: the status text now surfaces BackupService's own outcome message (which is why it
        // says "Restored", not "Imported" - M1's operation-noun fix - and carries the
        // credentials note, since the populated tree's ssh/profiles.json makes Connections one
        // of the restored categories) rather than a generic, hand-composed string.
        Assert.Equal(
            "Restored 6 categories (Replace). Connection passwords are not included in a bundle " +
            "— re-enter them on first connect. Restart Ntilde to pick up all changes.",
            status.Text);

        // The tracked file rolled back to the snapshot's content on disk.
        using var restoredSettings = tree.ReadJson("settings.json");
        Assert.Equal(14, restoredSettings.RootElement.GetProperty("FontSize").GetInt32());

        // Restore forces a pre-restore snapshot before applying.
        var reasons = service.ListSnapshots().Select(s => s.Reason).ToArray();
        Assert.Contains(SnapshotReason.PreRestore, reasons);
    }

    /// <summary>
    /// Fix round 2 (Important finding): the other half of the same gap - does declining actually
    /// prevent the restore? This is the assertion that matters most: proving no PreRestore
    /// snapshot appears is what rules out Cancel being a no-op that silently restores anyway.
    /// </summary>
    [AvaloniaFact]
    public void RestoreSelected_WhenConfirmationReturnsFalse_DoesNotCallRestore_AndAddsNoPreRestoreSnapshot()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root);
        var snapshot = service.Snapshot(SnapshotReason.Auto);
        Assert.NotNull(snapshot);

        tree.WriteFile("settings.json", """{"FontSize":99,"ThemeName":"Changed"}""");

        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new Ntilde.SettingsWindow { RestoreConfirmationOverride = _ => Task.FromResult(false) };

        var snapshotList = window.GetPage<BackupPage>().FindControl<ListBox>("SnapshotList")!;
        var btnRestore = window.GetPage<BackupPage>().FindControl<Button>("BtnRestoreSnapshot")!;
        var status = window.GetPage<BackupPage>().FindControl<TextBlock>("BackupStatusText")!;

        snapshotList.SelectedIndex = 0;
        btnRestore.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        // Declining is a genuine no-op: the handler returns before SetStatus, so the status text
        // set at construction (empty) is untouched.
        Assert.Equal(string.Empty, status.Text);

        // The tracked file was never touched.
        using var currentSettings = tree.ReadJson("settings.json");
        Assert.Equal(99, currentSettings.RootElement.GetProperty("FontSize").GetInt32());

        // The assertion that matters: no PreRestore snapshot was written, proving Restore itself
        // never ran - Cancel is not a no-op that silently restores anyway.
        var reasons = service.ListSnapshots().Select(s => s.Reason).ToArray();
        Assert.DoesNotContain(SnapshotReason.PreRestore, reasons);
        Assert.Single(reasons);
    }

    /// <summary>
    /// I1 (final whole-branch review, Important, user-visible data loss): SettingsWindow loads
    /// <c>_settings</c> once at construction. Restore changes settings.json on disk directly, but
    /// before this fix nothing told the already-open window - clicking Save afterward called
    /// <c>_settings.Save()</c> with the pre-restore snapshot still in memory, silently reverting
    /// the restore that had just completed and telling the user the opposite ("Restart
    /// Ntilde to pick up all changes"). Fully click-driven (real BtnRestoreSnapshot and
    /// BtnSave clicks, via the same <see cref="SettingsWindow.RestoreConfirmationOverride"/> seam
    /// the other Restore tests use) rather than reflecting into a private method, since Restore's
    /// only modal (the confirmation dialog) already has a test seam - unlike Import, which needs
    /// a file picker AND a mode-selection dialog neither of which can run headlessly (see
    /// <see cref="Import_ThenSave_DoesNotRevertTheImportedSettings"/> below for that path, driven
    /// directly instead).
    /// </summary>
    [AvaloniaFact]
    public void RestoreSelected_ThenSave_DoesNotRevertTheRestoredSettings()
    {
        using var tree = BackupTestTree.CreatePopulated();
        tree.WriteFile("settings.json", """{"FontSize":77,"ThemeName":"FromSnapshot"}""");
        var service = new BackupService(tree.Root);
        var snapshot = service.Snapshot(SnapshotReason.Auto);
        Assert.NotNull(snapshot);

        // Drift after the snapshot: this is both what Restore rolls back AND what the already-open
        // window's own _settings (loaded at construction, before this write) reflects.
        tree.WriteFile("settings.json", """{"FontSize":14,"ThemeName":"Changed"}""");

        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new Ntilde.SettingsWindow { RestoreConfirmationOverride = _ => Task.FromResult(true) };

        var snapshotList = window.GetPage<BackupPage>().FindControl<ListBox>("SnapshotList")!;
        var btnRestore = window.GetPage<BackupPage>().FindControl<Button>("BtnRestoreSnapshot")!;
        var btnSave = window.FindControl<Button>("BtnSave")!;

        snapshotList.SelectedIndex = 0;
        btnRestore.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        // Sanity: the restore itself worked (already covered elsewhere; repeated here so a
        // failure below points at Save, not at Restore).
        using (var restored = tree.ReadJson("settings.json"))
        {
            Assert.Equal(77, restored.RootElement.GetProperty("FontSize").GetInt32());
        }

        btnSave.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        using var afterSave = tree.ReadJson("settings.json");
        Assert.Equal(77, afterSave.RootElement.GetProperty("FontSize").GetInt32());
        Assert.Equal("FromSnapshot", afterSave.RootElement.GetProperty("ThemeName").GetString());
    }

    /// <summary>
    /// The same I1 bug, for the scenario the review actually names (Import, Replace mode).
    /// Import's own click handler needs a real file picker AND a mode-selection dialog - neither
    /// drivable headlessly (see <see cref="ConfirmRestoreAsync"/>'s remarks on why a bare
    /// <c>Window.ShowDialog</c> hangs this host) - and unlike Restore's confirmation, no test seam
    /// exists for either. Per the plan's own guidance, this drives the underlying method chain
    /// directly instead: a real <see cref="BackupService.Import"/> call, then the private
    /// <c>ReloadSettingsAfterExternalChangeAsync</c> the click handler calls on success (reached via
    /// reflection, the same established pattern <c>BuildRestoreConfirmationText</c> uses
    /// elsewhere in this file), then a real BtnSave click.
    /// </summary>
    [AvaloniaFact]
    public async Task Import_ThenSave_DoesNotRevertTheImportedSettings()
    {
        using var source = BackupTestTree.CreatePopulated();
        source.WriteFile("settings.json", """{"FontSize":77,"ThemeName":"FromImport"}""");
        string bundle = Path.Combine(source.Root, "import.ntildebackup");
        Assert.True(new BackupService(source.Root).Export(bundle).Success);

        using var target = BackupTestTree.CreatePopulated();
        target.WriteFile("settings.json", """{"FontSize":14,"ThemeName":"Changed"}""");

        using var _ = OverrideAppDataRoot(target.Root);
        var window = new Ntilde.SettingsWindow();

        var targetService = new BackupService(target.Root);
        var outcome = targetService.Import(bundle, ImportMode.Replace);
        Assert.True(outcome.Success, outcome.Message);

        await InvokeReloadAfterExternalChangeAsync(window);

        var btnSave = window.FindControl<Button>("BtnSave")!;
        btnSave.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        using var afterSave = target.ReadJson("settings.json");
        Assert.Equal(77, afterSave.RootElement.GetProperty("FontSize").GetInt32());
        Assert.Equal("FromImport", afterSave.RootElement.GetProperty("ThemeName").GetString());
    }

    /// <summary>
    /// I1 residual, Fix 1 (scoped re-review): <c>PopulateThemes</c> fills the theme combo boxes
    /// from disk once, at construction. <c>ReloadSettingsAfterExternalChangeAsync</c> did not re-run it,
    /// so an import bringing a NEW theme file plus a settings.json naming it left the combo holding
    /// the pre-import theme list — no entry for the imported theme. <c>LoadCurrentSettings</c>'s
    /// theme-selection loop then had nothing to select, left <c>SelectedItem</c> on the stale
    /// pre-import theme, and <c>SaveAndClose</c> would write that stale name back — the original I1
    /// bug, narrowed to one field.
    ///
    /// The pre-state <c>ThemeName</c> here is "Default", deliberately present in the combo both
    /// before and after the import (<c>ThemeManager</c> always synthesizes "Default"). The other
    /// tests in this file use "Changed" as a pre-state, which is absent from every combo — that
    /// leaves <c>SelectedItem</c> null and the `is ComboBoxItem` guard at <c>SaveAndClose</c> skips
    /// the write entirely, which would silently miss this bug. A present pre-state name is what
    /// makes the stale selection observable at Save.
    /// </summary>
    [AvaloniaFact]
    public async Task Import_BringingNewTheme_ThenSave_PersistsTheImportedThemeName()
    {
        using var source = BackupTestTree.CreateEmpty();
        source.WriteFile("settings.json", """{"FontSize":14,"ThemeName":"Imported"}""");
        source.WriteFile(Path.Combine("themes", "imported.json"), """{"name":"Imported"}""");
        string bundle = Path.Combine(source.Root, "import.ntildebackup");
        Assert.True(new BackupService(source.Root).Export(bundle).Success);

        // The populated target tree's settings.json already names ThemeName "Default" — present
        // in the combo both before and after the import (see remarks above).
        using var target = BackupTestTree.CreatePopulated();

        using var _ = OverrideAppDataRoot(target.Root);
        var window = new Ntilde.SettingsWindow();

        var targetService = new BackupService(target.Root);
        var outcome = targetService.Import(bundle, ImportMode.Replace);
        Assert.True(outcome.Success, outcome.Message);

        await InvokeReloadAfterExternalChangeAsync(window);

        var btnSave = window.FindControl<Button>("BtnSave")!;
        btnSave.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        using var afterSave = target.ReadJson("settings.json");
        Assert.Equal("Imported", afterSave.RootElement.GetProperty("ThemeName").GetString());
    }

    /// <summary>
    /// Finding 1 (Codex review round 2, PR #362): the exact same residual as
    /// <see cref="Import_BringingNewTheme_ThenSave_PersistsTheImportedThemeName"/> above, for
    /// fonts. The font list (now built by the session's font choices and the Terminal and Profiles
    /// pages' Load) explicitly seeds the configured font even when it is not installed locally.
    /// Before the fix, the window's reload re-populated the themes but not the fonts, so a bundle
    /// imported from another machine naming a font absent here
    /// left <c>FontList</c> holding the pre-import choices; Load could not
    /// select the imported font, and a later Save wrote the stale font back over it.
    ///
    /// The target tree's pre-import <c>FontFamily</c> is left unset, defaulting to
    /// <c>BundledFontCatalog.DefaultTerminalFontFamily</c> — deliberately present in
    /// <c>FontList</c> both before and after the import (the font list,
    /// <c>SettingsSession.FontChoices</c>, always includes the bundled default), exactly the "present pre-state name" precedent the theme test above
    /// documents: an absent pre-state would leave <c>SelectedItem</c> null and the `is
    /// ComboBoxItem` guard at Save (the page's Collect) would skip the write entirely, silently missing
    /// this bug.
    /// </summary>
    [AvaloniaFact]
    public async Task Import_BringingFontNotInstalledLocally_ThenSave_PersistsTheImportedFontFamily()
    {
        const string importedFont = "TotallyNotARealFont-XYZ123";

        using var source = BackupTestTree.CreateEmpty();
        source.WriteFile("settings.json", $$"""{"FontSize":14,"ThemeName":"Default","FontFamily":"{{importedFont}}"}""");
        string bundle = Path.Combine(source.Root, "import.ntildebackup");
        Assert.True(new BackupService(source.Root).Export(bundle).Success);

        // The populated target tree's settings.json does not set FontFamily, so
        // TerminalSettings.Load() defaults it to the bundled default font — present in FontList
        // both before and after the import (see remarks above).
        using var target = BackupTestTree.CreatePopulated();

        using var _ = OverrideAppDataRoot(target.Root);
        var window = new Ntilde.SettingsWindow();

        var targetService = new BackupService(target.Root);
        var outcome = targetService.Import(bundle, ImportMode.Replace);
        Assert.True(outcome.Success, outcome.Message);

        await InvokeReloadAfterExternalChangeAsync(window);

        var btnSave = window.FindControl<Button>("BtnSave")!;
        btnSave.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        using var afterSave = target.ReadJson("settings.json");
        Assert.Equal(importedFont, afterSave.RootElement.GetProperty("FontFamily").GetString());
    }

    /// <summary>
    /// Sibling of <see cref="Import_BringingFontNotInstalledLocally_ThenSave_PersistsTheImportedFontFamily"/>
    /// and <see cref="Import_BringingNewTheme_ThenSave_PersistsTheImportedThemeName"/>, for the one
    /// populate path the reload did not cover: the Command Assist snippet list.
    ///
    /// Every page's Load runs from the constructor, so the window's reload re-runs them by simply
    /// repeating that sequence. The snippet list is different — <c>CommandAssistPage.ReloadSnippetsAsync</c>
    /// is reached through the frame's <c>Opened += async (_, _) =&gt; await
    /// GetPage&lt;CommandAssistPage&gt;().ReloadSnippetsAsync()</c> handler, because the store arrives by
    /// property assignment after the constructor has run. <c>Opened</c> fires once, the first time the window
    /// is shown; the reload runs on an ALREADY-OPEN window and does not reopen it, so before this fix
    /// nothing refreshed the snippet rows. An import whose bundle carries a different
    /// <c>command-assist/snippets.json</c> (which <c>BackupService</c> replaces wholesale in BOTH
    /// modes) left the visible list showing the pre-import snippets until the window was closed and
    /// reopened.
    ///
    /// Unlike the theme/font siblings, the observable damage is not at Save — snippets do not live in
    /// settings.json and the Save button never writes them — it is the stale rows themselves, which
    /// the user can then edit or delete, acting on a list that no longer describes the file. So this
    /// asserts on the panel's contents directly.
    /// </summary>
    [AvaloniaFact]
    public async Task Import_BringingNewSnippets_RefreshesTheAlreadyOpenSnippetsPanel()
    {
        using var source = BackupTestTree.CreateEmpty();
        source.WriteFile("settings.json", """{"FontSize":14,"ThemeName":"Default"}""");
        await new JsonSnippetStore(SnippetsPathIn(source))
            .UpsertAsync(NewSnippet("imported-1", "Imported snippet", "echo imported"));
        string bundle = Path.Combine(source.Root, "import.ntildebackup");
        Assert.True(new BackupService(source.Root).Export(bundle).Success);

        using var target = BackupTestTree.CreatePopulated();
        // The live store MainWindow.OpenSettings injects — a real JsonSnippetStore over the target
        // tree's own file, so the import below genuinely changes what it reads.
        var targetStore = new JsonSnippetStore(SnippetsPathIn(target));
        await targetStore.UpsertAsync(NewSnippet("local-1", "Local snippet", "echo local"));

        using var _ = OverrideAppDataRoot(target.Root);
        var window = new Ntilde.SettingsWindow { CommandAssistSnippetStore = targetStore };

        // Stands in for the one-shot Opened handler: the window is open and its snippet list has
        // already been populated from the pre-import file.
        await InvokeReloadCommandAssistSnippetsAsync(window);
        Assert.Equal(new[] { "Local snippet" }, SnippetRowNames(window));

        var outcome = new BackupService(target.Root).Import(bundle, ImportMode.Replace);
        Assert.True(outcome.Success, outcome.Message);

        await InvokeReloadAfterExternalChangeAsync(window);

        Assert.Equal(new[] { "Imported snippet" }, SnippetRowNames(window));
    }

    /// <summary>
    /// Codex review (PR #364, P2): refreshing the Settings snippet panel is only half of what a
    /// snippet change owes the app. <c>MainWindow.OpenSettings</c> subscribes
    /// <c>DismissCommandAssistSurfaces</c> to <c>OnCommandAssistSnippetsChanged</c>, and the add,
    /// edit and delete paths all raise it - the rows an open assist bubble or popup is showing are a
    /// snapshot of a ranking pass, and nothing else invalidates them (see that method's own remarks,
    /// written for the identical "Clear history" case).
    ///
    /// An import or restore is the most destructive snippet change there is: <c>BackupService</c>
    /// replaces <c>snippets.json</c> wholesale in BOTH modes, so every snippet the user had can
    /// vanish at once. Without this notification a popup left open behind the Settings dialog goes on
    /// displaying - and accepting - snippets the import just deleted, until some later suggestion
    /// refresh happens to rebuild it.
    ///
    /// Asserts on the event rather than on pane state because the event IS the seam MainWindow wires
    /// to; a pane-level assertion would need a whole MainWindow and would still be testing this same
    /// handoff one indirection later.
    /// </summary>
    [AvaloniaFact]
    public async Task ReloadAfterExternalChange_NotifiesTheHostThatSnippetsChanged()
    {
        // A bundle with no snippets at all: the import removes the local one outright, which is the
        // case where stale rows in an open popup are actively wrong rather than merely out of date.
        using var source = BackupTestTree.CreateEmpty();
        source.WriteFile("settings.json", """{"FontSize":14,"ThemeName":"Default"}""");
        source.WriteFile(Path.Combine("command-assist", "snippets.json"), "[]");
        string bundle = Path.Combine(source.Root, "import.ntildebackup");
        Assert.True(new BackupService(source.Root).Export(bundle).Success);

        using var target = BackupTestTree.CreatePopulated();
        var targetStore = new JsonSnippetStore(SnippetsPathIn(target));
        await targetStore.UpsertAsync(NewSnippet("local-1", "Local snippet", "echo local"));

        using var _ = OverrideAppDataRoot(target.Root);
        var window = new Ntilde.SettingsWindow { CommandAssistSnippetStore = targetStore };

        int notifications = 0;
        window.OnCommandAssistSnippetsChanged += () => notifications++;

        await InvokeReloadCommandAssistSnippetsAsync(window);
        Assert.Equal(new[] { "Local snippet" }, SnippetRowNames(window));

        var outcome = new BackupService(target.Root).Import(bundle, ImportMode.Replace);
        Assert.True(outcome.Success, outcome.Message);

        await InvokeReloadAfterExternalChangeAsync(window);

        // The panel emptying is the precondition; the notification is the point.
        Assert.Empty(SnippetRowNames(window));
        Assert.Equal(1, notifications);
    }

    private static string SnippetsPathIn(BackupTestTree tree)
        => Path.Combine(tree.Root, "command-assist", "snippets.json");

    private static CommandSnippet NewSnippet(string id, string name, string command) => new(
        Id: id,
        Name: name,
        CommandText: command,
        Description: null,
        ShellKind: null,
        WorkingDirectory: null,
        IsPinned: true,
        CreatedAt: DateTimeOffset.UnixEpoch,
        LastUsedAt: null);

    /// <summary>
    /// The names shown by the rows currently in <c>CommandAssistSnippetsPanel</c> — the first
    /// <see cref="TextBox"/> of each row, which <c>CreateCommandAssistSnippetRow</c> seeds with the
    /// snippet's name. Reading the built controls rather than the editor's list is the point: the bug
    /// is precisely that the two can disagree. The empty-list placeholder is not a row and is skipped.
    /// </summary>
    private static string[] SnippetRowNames(Ntilde.SettingsWindow window)
    {
        var panel = window.GetPage<CommandAssistPage>().FindControl<StackPanel>("CommandAssistSnippetsPanel")!;

        // FirstOrDefault, not First: an empty snippet list is a legitimate panel state, and
        // PopulateCommandAssistSnippetsPanel renders it as a single explanatory TextBlock with no
        // TextBox in it. Only children that actually carry an editor are snippet rows.
        return panel.Children
            .Select(row => row.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault())
            .Where(nameEditor => nameEditor != null)
            .Select(nameEditor => nameEditor!.Text ?? "")
            .ToArray();
    }

    private static async Task InvokeReloadCommandAssistSnippetsAsync(Ntilde.SettingsWindow window)
    {
        await window.GetPage<CommandAssistPage>().ReloadSnippetsAsync();
    }

    /// <summary>
    /// Drives the internal <c>ReloadSettingsAfterExternalChangeAsync</c> the Import and Restore click
    /// handlers call on success. Awaited: the method became a <see cref="Task"/> when
    /// the Command Assist snippet reload (genuinely async - it re-reads the store's file) joined the
    /// synchronous repopulation, so invoking without awaiting would race the assertions that follow.
    /// </summary>
    private static async Task InvokeReloadAfterExternalChangeAsync(Ntilde.SettingsWindow window)
    {
        await window.ReloadSettingsAfterExternalChangeAsync();
    }


    /// <summary>
    /// Finding 2 (Codex review round 2, PR #362): the Merge/Replace import prompt says Merge
    /// "keeps items you have locally that the bundle does not contain." That is false for
    /// Snippets — <c>BackupService.BuildPlan</c> replaces <c>snippets.json</c> wholesale in BOTH
    /// modes (a deliberate, spec'd design decision: the file is a flat array with no stable id, so
    /// there is nothing to merge by — see <c>BackupImportTests.Snippets_AlwaysReplacedWholesale</c>).
    /// A user choosing Merge specifically to keep local snippets would lose them with no warning.
    /// Reaches the wording directly via the private <c>BuildImportModeBodyText</c> helper — the
    /// same reflection-free-body/reflection-only-for-the-modal-itself split
    /// <c>RestoreConfirmationText_...</c> above uses for <c>BuildRestoreConfirmationText</c> —
    /// since a real <c>PromptForImportModeAsync</c>'s <c>ShowDialog</c> hangs this repo's headless
    /// host (see that test's remarks).
    /// </summary>
    [Fact]
    public void ImportModeBodyText_MentionsSnippetsReplacedWholesale_OnlyWhenBundleContainsSnippets()
    {
        var withSnippets = new BundleInspection(
            NewManifestForBodyTextTest(),
            new Dictionary<BackupCategory, int>
            {
                [BackupCategory.Settings] = 1,
                [BackupCategory.Snippets] = 1,
            });

        var withoutSnippets = new BundleInspection(
            NewManifestForBodyTextTest(),
            new Dictionary<BackupCategory, int>
            {
                [BackupCategory.Settings] = 1,
                [BackupCategory.Snippets] = 0,
            });

        string withSnippetsText = BackupPage.BuildImportModeBodyText(withSnippets);
        string withoutSnippetsText = BackupPage.BuildImportModeBodyText(withoutSnippets);

        Assert.Contains("replaced entirely", withSnippetsText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("replaced entirely", withoutSnippetsText, StringComparison.OrdinalIgnoreCase);

        // The base guarantee must still hold for a bundle without Snippets — this is a copy fix,
        // not a semantics change.
        Assert.Contains("keeps items you have locally", withoutSnippetsText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An import reloads every page from disk, as a restore does, so it warns the same way.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ImportModeBodyText_SaysUnsavedChangesAreDiscarded(int snippetCount)
    {
        var inspection = new BundleInspection(
            NewManifestForBodyTextTest(),
            new Dictionary<BackupCategory, int>
            {
                [BackupCategory.Settings] = 1,
                [BackupCategory.Snippets] = snippetCount,
            });

        string text = BackupPage.BuildImportModeBodyText(inspection);

        Assert.EndsWith(SettingsPageTests.UnsavedChangesDiscarded, text, StringComparison.Ordinal);
    }

    private static BackupManifest NewManifestForBodyTextTest() => new()
    {
        SchemaVersion = BackupManifest.CurrentSchemaVersion,
        AppVersion = "1.0.0-test",
        CreatedUtc = DateTimeOffset.UnixEpoch,
        Machine = "TEST",
        Categories = new[] { "settings", "snippets" }
    };

    /// <summary>
    /// I1 residual, Fix 2 (scoped re-review): ProfilesPage's <c>_selectedProfile</c> points at an entry of the
    /// PRE-reload <c>_profilesList</c>. The session reload (which re-Loads the page) rebuilds
    /// <c>_profilesList</c> with fresh <c>TerminalProfile</c> instances (freshly deserialized from
    /// the reloaded settings.json) but, before this fix, never re-pointed <c>_selectedProfile</c> —
    /// it was left dangling, referencing an object no longer reachable from <c>_profilesList</c>.
    /// The profile editor's KeyUp handlers are bound to <c>_selectedProfile</c> directly, so an
    /// edit typed right after an import/restore would silently mutate the detached object and be
    /// dropped at Save instead of landing on the profile actually in the list.
    ///
    /// Selects the one profile by a real ListBox selection (which drives the same
    /// SelectionChanged → SwitchSelectedProfile path a user's click would), reloads (simulating
    /// what Import/Restore's success handler does), types a name edit via a real KeyUp on the
    /// name TextBox (exactly how ProfileNameInput's handler is wired), then Saves — proving the
    /// edit landed on the live post-reload profile object, not a detached pre-reload one.
    /// </summary>
    [AvaloniaFact]
    public async Task ReloadAfterExternalChange_ThenEditSelectedProfile_ThenSave_PersistsTheEdit()
    {
        using var tree = BackupTestTree.CreateEmpty();
        const string profileId = "11111111-1111-1111-1111-111111111111";
        tree.WriteFile(
            "settings.json",
            $$"""
            {"FontSize":14,"ThemeName":"Default","DefaultProfileId":"{{profileId}}",
             "Profiles":[{"Id":"{{profileId}}","Name":"Original","Command":"{{ProfileCommandForThisOs}}"}]}
            """);

        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new Ntilde.SettingsWindow();

        var profilesListBox = window.GetPage<ProfilesPage>().FindControl<ListBox>("ProfilesListBox")!;
        Assert.Single(profilesListBox.Items);
        profilesListBox.SelectedIndex = 0;

        // The external change: same profile Id (as a real Import/Restore preserves), but a
        // machine-local field like FontSize also drifted, standing in for whatever else an
        // import brought along.
        tree.WriteFile(
            "settings.json",
            $$"""
            {"FontSize":18,"ThemeName":"Default","DefaultProfileId":"{{profileId}}",
             "Profiles":[{"Id":"{{profileId}}","Name":"Original","Command":"{{ProfileCommandForThisOs}}"}]}
            """);

        await InvokeReloadAfterExternalChangeAsync(window);

        // Edit the profile's name exactly as a user typing after the reload would: set the
        // TextBox's Text, then raise the real KeyUp event ProfileNameInput's handler listens for.
        var nameInput = window.GetPage<ProfilesPage>().FindControl<TextBox>("ProfileNameInput")!;
        nameInput.Text = "EditedAfterReload";
        nameInput.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyUpEvent,
            Key = Key.D,
            Source = nameInput,
        });

        var btnSave = window.FindControl<Button>("BtnSave")!;
        btnSave.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        using var afterSave = tree.ReadJson("settings.json");
        var savedProfiles = afterSave.RootElement.GetProperty("Profiles");
        Assert.Equal(1, savedProfiles.GetArrayLength());
        Assert.Equal("EditedAfterReload", savedProfiles[0].GetProperty("Name").GetString());
    }

    /// <summary>
    /// Fix round 1 (Important finding): the "passwords are not included, re-enter them" copy is
    /// the user-facing half of a guarantee the whole feature is built around. Nothing asserted on
    /// it before this - a future rewording or accidental deletion would still pass every other
    /// test in this file.
    /// </summary>
    [AvaloniaFact]
    public void BackupTab_StatesPasswordsAreNotIncluded_AndMustBeReEntered()
    {
        using var tree = BackupTestTree.CreateEmpty();
        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new Ntilde.SettingsWindow();

        string? passwordCopy = window.GetPage<BackupPage>().GetLogicalDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text)
            .FirstOrDefault(text =>
                text is not null &&
                text.Contains("never included", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("re-enter", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(passwordCopy);
    }

    /// <summary>
    /// Fix round 1 (Important finding): the Backup page's <c>Load</c> runs unconditionally when the
    /// window is built, and had no try/catch around <c>ListSnapshots()</c>, so a locked or inaccessible
    /// backups directory used to propagate an exception straight out of the constructor - the user
    /// could not open Settings at all. Proves the window still constructs, falls back to an empty
    /// snapshot list, and surfaces the failure through the status text instead.
    /// </summary>
    /// <remarks>
    /// The restriction is applied and then verified by actually trying the enumeration (mirroring
    /// <c>RemoteInstallerIntegrationTests</c>' "decide the skip by trying it rather than guessing
    /// the platform") - an elevated or root process can ignore a deny ACL / zeroed Unix mode
    /// entirely, in which case this skips rather than asserting a false negative.
    /// </remarks>
    [AvaloniaFact]
    public void Constructing_WhenSnapshotEnumerationFails_StillOpens_WithAnEmptyListAndAStatusMessage()
    {
        using var tree = BackupTestTree.CreateEmpty();
        string backupsDirectory = Path.Combine(tree.Root, "backups");
        Directory.CreateDirectory(backupsDirectory);
        File.WriteAllText(
            Path.Combine(backupsDirectory, "auto-20260101T000000Z-abc0000000000000.ntildebackup"),
            "not a real bundle - enumeration must fail before this is ever opened");

        bool blocked = TryBlockDirectoryListing(backupsDirectory, out Action restore);
        try
        {
            if (!blocked)
            {
                Assert.Skip("this process can enumerate a directory it just denied itself access to (root, or an unrestricted account)");
            }

            using var _ = OverrideAppDataRoot(tree.Root);

            Ntilde.SettingsWindow? window = null;
            var exception = Record.Exception(() => window = new Ntilde.SettingsWindow());

            Assert.Null(exception);
            Assert.NotNull(window);

            var snapshotList = window!.GetPage<BackupPage>().FindControl<ListBox>("SnapshotList")!;
            var status = window.GetPage<BackupPage>().FindControl<TextBlock>("BackupStatusText")!;

            Assert.Empty(snapshotList.ItemsSource!.Cast<object>());
            Assert.Contains("snapshot", status.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            restore();
        }
    }

    /// <summary>
    /// A command TerminalSettings.Load() recognizes as a native shell for the OS running the
    /// test. Without this, Load()'s cross-platform polish (a Local profile whose command targets
    /// another OS doesn't count as a "native shell found") would silently pad the loaded profile
    /// list with the current OS's default shells, breaking the count assertion in
    /// <see cref="ReloadAfterExternalChange_ThenEditSelectedProfile_ThenSave_PersistsTheEdit"/>.
    /// </summary>
    private static string ProfileCommandForThisOs => OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash";

    private static string GetDisplay(object snapshotRow)
    {
        var property = snapshotRow.GetType().GetProperty("Display", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(property);
        return (string)property!.GetValue(snapshotRow)!;
    }

    /// <summary>
    /// Denies directory-listing access to <paramref name="directory"/> for the current process,
    /// verifying - rather than assuming - that this actually blocks <c>Directory.GetFiles</c>
    /// before reporting success. The returned <paramref name="restore"/> action always undoes the
    /// change, whether or not the block took, so temp-directory cleanup can proceed either way.
    /// </summary>
    private static bool TryBlockDirectoryListing(string directory, out Action restore)
    {
        if (OperatingSystem.IsWindows())
        {
            var dirInfo = new DirectoryInfo(directory);
            var security = dirInfo.GetAccessControl();
            var currentUser = WindowsIdentity.GetCurrent().User!;
            var rule = new FileSystemAccessRule(
                currentUser,
                FileSystemRights.ListDirectory | FileSystemRights.Read,
                AccessControlType.Deny);

            security.AddAccessRule(rule);
            dirInfo.SetAccessControl(security);

            restore = () =>
            {
                try
                {
                    var current = dirInfo.GetAccessControl();
                    current.RemoveAccessRule(rule);
                    dirInfo.SetAccessControl(current);
                }
                catch
                {
                    // Best-effort restore; the temp tree's Dispose is best-effort too.
                }
            };
        }
        else
        {
            File.SetUnixFileMode(directory, UnixFileMode.None);

            restore = () =>
            {
                try
                {
                    File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                catch
                {
                    // Best-effort restore; the temp tree's Dispose is best-effort too.
                }
            };
        }

        try
        {
            Directory.GetFiles(directory, "*.ntildebackup");
            return false; // enumeration still succeeded - the restriction did not take (root, etc.)
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Points <c>AppPaths.RootDirectory</c> (and therefore the Backup page's own
    /// <see cref="BackupService"/>) at a temp tree for the lifetime of the returned scope, so
    /// constructing a <see cref="Ntilde.SettingsWindow"/> in a test never reads or writes the
    /// real user profile's backups directory.
    /// </summary>
    private static IDisposable OverrideAppDataRoot(string root)
    {
        string? previous = Environment.GetEnvironmentVariable("NTILDE_APPDATA_ROOT");
        Environment.SetEnvironmentVariable("NTILDE_APPDATA_ROOT", root);
        return new RestoreEnvVar(previous);
    }

    private sealed class RestoreEnvVar(string? previous) : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable("NTILDE_APPDATA_ROOT", previous);
    }
}
