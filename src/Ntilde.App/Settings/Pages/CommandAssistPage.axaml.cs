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

public partial class CommandAssistPage : UserControl, ISettingsPage, IDisposable
{
    private const string RowDescStyleClass = "RowDesc";

    private SettingsSession? _session;

    private TextBlock? _snippetsEmptyHint;

    /// <summary>Whether the next "Clear history" click is the confirming one.</summary>
    private bool _isClearCommandAssistHistoryArmed;

    /// <summary>The rules behind the snippet rows. Built on first use from the injected store.</summary>
    private SnippetEditor? _snippetEditor;

    public CommandAssistPage()
    {
        InitializeComponent();
        WireRemoteShellIntegrationRow();
        WireClearCommandAssistHistoryRow();
        WireCommandAssistSnippetsRow();
    }

    public SettingsPage Page => SettingsPage.CommandAssist;

    public Control View => this;

    private SettingsSession Session => _session ?? throw new InvalidOperationException("CommandAssistPage used before Load.");

    /// <summary>Takes this page's handler off the session: the window disposes its pages when it closes.</summary>
    public void Dispose()
    {
        if (_session != null)
        {
            _session.ShortcutsChanged -= UpdateCommandAssistShortcutText;
        }
        GC.SuppressFinalize(this);
    }

    public void Load(SettingsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!ReferenceEquals(_session, session))
        {
            if (_session != null)
            {
                _session.ShortcutsChanged -= UpdateCommandAssistShortcutText;
            }

            session.ShortcutsChanged += UpdateCommandAssistShortcutText;
            _session = session;
        }

        TerminalSettings settings = session.Settings;
        Toggle("CommandAssistToggle").IsChecked = settings.CommandAssistEnabled;
        Toggle("CommandAssistPassiveBubbleToggle").IsChecked = settings.CommandAssistPassiveBubbleEnabled;
        Toggle("CommandAssistHistoryToggle").IsChecked = settings.CommandAssistHistoryEnabled;
        Toggle("CommandAssistShellIntegrationToggle").IsChecked = settings.CommandAssistShellIntegrationEnabled;
        UpdateCommandAssistShortcutText();
    }

    public SettingsValidationError? Validate() => null;

    public void Collect(TerminalSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.CommandAssistEnabled = Toggle("CommandAssistToggle").IsChecked == true;
        target.CommandAssistPassiveBubbleEnabled = Toggle("CommandAssistPassiveBubbleToggle").IsChecked == true;
        target.CommandAssistHistoryEnabled = Toggle("CommandAssistHistoryToggle").IsChecked == true;
        target.CommandAssistShellIntegrationEnabled = Toggle("CommandAssistShellIntegrationToggle").IsChecked == true;
    }

    private CheckBox Toggle(string name) => this.FindControl<CheckBox>(name)!;

    /// <summary>
    /// The descriptions name their chords in prose. The XAML text is written with the
    /// Windows/Linux defaults; this swaps in the binding actually in force, which differs on macOS
    /// (Cmd+R, Cmd+Shift+S) and after a rebind.
    /// </summary>
    private void UpdateCommandAssistShortcutText()
    {
        string toggle = TitleBarShortcuts.Resolve("command_assist_toggle", Session.ShortcutDraft);
        string history = TitleBarShortcuts.Resolve("command_assist_history", Session.ShortcutDraft);
        string pin = TitleBarShortcuts.Resolve("command_assist_pin", Session.ShortcutDraft);

        Replace("CommandAssistBubbleRow", ("Ctrl+Space", toggle), ("Ctrl+R", history));
        Replace("CommandAssistHistoryRow", ("Ctrl+R", history));
        Replace("CommandAssistSnippetsRow", ("Ctrl+Shift+S", pin));

        if (_snippetsEmptyHint != null)
        {
            _snippetsEmptyHint.Text = FormatSnippetsEmptyHint();
        }

        void Replace(string rowName, params (string Written, string Effective)[] chords)
        {
            if (this.FindControl<SettingRow>(rowName) is not { } row)
            {
                return;
            }

            // Tag keeps the XAML original so a second rebind replaces from the source text, not
            // from the previous substitution.
            row.Tag ??= new RowText(row.Description, row.Details);
            var original = (RowText)row.Tag;
            row.Description = original.Description is null ? null : SubstituteChords(original.Description, chords);
            row.Details = original.Details is null ? null : SubstituteChords(original.Details, chords);
        }
    }

    private sealed record RowText(string? Description, string? Details);

    /// <summary>
    /// Replaces each written chord with its effective binding in one pass over
    /// <paramref name="text"/>, so a substituted binding is never itself matched as another
    /// chord (toggle rebound to Ctrl+R must not then be rewritten as the history binding).
    /// </summary>
    internal static string SubstituteChords(string text, params (string Written, string Effective)[] chords)
    {
        if (chords.Length == 0)
        {
            return text;
        }

        // Longest first, so a chord that is a prefix of another cannot shadow it.
        (string Written, string Effective)[] ordered = chords.OrderByDescending(chord => chord.Written.Length).ToArray();
        var result = new System.Text.StringBuilder(text.Length);
        int index = 0;
        while (index < text.Length)
        {
            int start = index;
            (string Written, string Effective) match = ordered.FirstOrDefault(
                chord => string.CompareOrdinal(text, start, chord.Written, 0, chord.Written.Length) == 0);
            if (match.Written is null)
            {
                result.Append(text[index]);
                index++;
                continue;
            }

            result.Append(match.Effective);
            index += match.Written.Length;
        }

        return result.ToString();
    }

    private string FormatSnippetsEmptyHint() =>
        $"No snippets yet. Pin a suggestion with {TitleBarShortcuts.Resolve("command_assist_pin", Session.ShortcutDraft)}, or add one here.";

    /// <summary>
    /// The "Remote shell integration" row (V2 Phase 2b): pick a remote shell, put that snippet on
    /// the clipboard, and show what to do with it.
    /// </summary>
    /// <remarks>
    /// No setting is read or written here - this row is an action, not a preference. Whether Ntilde
    /// consumes remote marks at all is governed by the existing shell-integration setting, and
    /// whether the remote host emits them is governed by whether the user installed the snippet.
    /// Neither is something this row can toggle.
    /// </remarks>
    private void WireRemoteShellIntegrationRow()
    {
        var shellList = this.FindControl<ComboBox>("RemoteShellIntegrationShellList");
        var copyInstallerButton = this.FindControl<Button>("BtnCopyRemoteShellIntegration");
        var copySnippetButton = this.FindControl<Button>("BtnCopyRemoteShellIntegrationSnippet");
        var status = this.FindControl<SettingRow>("RemoteShellIntegrationRow");
        if (shellList == null || copyInstallerButton == null)
        {
            return;
        }

        shellList.ItemsSource = RemoteShellIntegrationSnippets.All
            .Select(RemoteShellIntegrationSnippets.GetDisplayName)
            .ToList();
        shellList.SelectedIndex = 0;

        RemoteShellIntegrationShell SelectedShell()
        {
            int index = Math.Clamp(
                shellList.SelectedIndex,
                0,
                RemoteShellIntegrationSnippets.All.Count - 1);
            return RemoteShellIntegrationSnippets.All[index];
        }

        copyInstallerButton.Click += async (_, _) =>
            await CopyRemoteShellIntegrationInstallerAsync(SelectedShell(), status);

        if (copySnippetButton != null)
        {
            copySnippetButton.Click += async (_, _) =>
                await CopyRemoteShellIntegrationSnippetAsync(SelectedShell(), status);
        }
    }

    /// <summary>
    /// The primary action: one line the user pastes at the remote prompt.
    /// </summary>
    /// <remarks>
    /// The status text describes what the paste does rather than what the user must do next:
    /// the installer writes the snippet and patches the rc file itself, so there is no next
    /// step to describe. It deliberately does not promise the current session becomes
    /// integrated: the installer runs as a child process and never touches the live shell, so
    /// marks arrive with the next session.
    /// </remarks>
    private async System.Threading.Tasks.Task CopyRemoteShellIntegrationInstallerAsync(
        RemoteShellIntegrationShell shell,
        SettingRow? status)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null)
            {
                ShowStatus(status, "Clipboard is not available.");
                return;
            }

            await clipboard.SetTextAsync(RemoteShellIntegrationSnippets.BuildInstallerCommand(shell));

            string? loaderLine = RemoteShellIntegrationSnippets.GetLoaderLine(shell);
            string writeDescription = loaderLine != null
                ? $"It writes {RemoteShellIntegrationSnippets.GetRemotePath(shell)} and adds the loader " +
                  "line to your rc file if it isn't already there."
                : $"It writes {RemoteShellIntegrationSnippets.GetRemotePath(shell)}, which is sourced " +
                  "automatically, so there is nothing else to add.";

            ShowStatus(
                status,
                $"Copied the installer for {RemoteShellIntegrationSnippets.GetDisplayName(shell)}. " +
                "Paste it at the remote prompt and press Enter - one line, one history entry. " +
                writeDescription);
        }
        catch (Exception ex)
        {
            // Reported in the row rather than swallowed: the whole point of the affordance is
            // that the user now has the installer, and silently not having it looks identical.
            ShowStatus(status, $"Could not copy the installer: {ex.Message}");
        }
    }

    private async System.Threading.Tasks.Task CopyRemoteShellIntegrationSnippetAsync(
        RemoteShellIntegrationShell shell,
        SettingRow? status)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null)
            {
                ShowStatus(status, "Clipboard is not available.");
                return;
            }

            await clipboard.SetTextAsync(RemoteShellIntegrationSnippets.Read(shell));
            ShowStatus(
                status,
                RemoteShellIntegrationSnippets.BuildInstallInstructions(shell));
        }
        catch (Exception ex)
        {
            // Reported in the row rather than swallowed: the whole point of the affordance is
            // that the user now has the snippet, and silently not having it looks identical.
            ShowStatus(status, $"Could not copy the snippet: {ex.Message}");
        }
    }

    /// <summary>
    /// The "Clear history" button (V2 Phase 3b task 5): <see cref="IHistoryStore.ClearAsync"/>
    /// finally gets a caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two clicks, not a modal. The first click arms and relabels the button, the second one clears;
    /// clicking anything else, or reopening the window, forgets. A destructive action a user cannot
    /// undo needs a confirmation, and an <c>await</c>-ed dialog here would be the only modal in this
    /// window - the arm-then-confirm pattern is cheaper to reason about and impossible to
    /// double-click through.
    /// </para>
    /// <para>
    /// Goes through the <em>live</em> store rather than constructing one over the same file.
    /// <c>JsonlHistoryStore</c> caches an index and a physical line count and compacts from that
    /// cache, so a second instance would clear the file while the pane's instance carried on
    /// appending against a stale view - see <c>CommandAssistServices.ApplyHistoryRetentionLimit</c>
    /// for the same argument about the retention cap. <c>MainWindow</c> injects the instance it hands
    /// to panes; with no injection the row reports that instead of pretending.
    /// </para>
    /// </remarks>
    private void WireClearCommandAssistHistoryRow()
    {
        var clearButton = this.FindControl<Button>("BtnClearCommandAssistHistory");
        var status = this.FindControl<SettingRow>("CommandAssistHistoryRow");
        if (clearButton == null)
        {
            return;
        }

        clearButton.Click += async (_, _) =>
        {
            if (Session.HistoryStore == null)
            {
                ShowStatus(status, "Command history is not available in this window.");
                return;
            }

            if (!_isClearCommandAssistHistoryArmed)
            {
                _isClearCommandAssistHistoryArmed = true;
                clearButton.Content = "Confirm clear";
                ShowStatus(
                    status,
                    "This deletes every recorded command, including history carried over from an older version. Click again to confirm.");
                return;
            }

            _isClearCommandAssistHistoryArmed = false;
            clearButton.Content = "Clear history";

            try
            {
                await Session.HistoryStore.ClearAsync();

                // Any assist surface still on screen is showing rows that no longer exist (PR #293
                // review, non-blocking 7). The host owns the panes, so it is told rather than
                // reached for from here; with no host wired the clear still reports success, which is
                // true - only the refresh is missing.
                Session.RaiseHistoryCleared();
                ShowStatus(status, "Command history cleared.");
            }
            catch (Exception ex)
            {
                // Reported rather than swallowed, for the same reason as the snippet copy: a clear
                // that silently failed looks exactly like one that worked.
                ShowStatus(status, $"Could not clear history: {ex.Message}");
            }
        };
    }

    /// <summary>
    /// The snippet manager (V2 Phase 4b, Phase 4 task 4): list, edit in place, delete, add.
    /// <see cref="ISnippetStore.RemoveAsync"/> finally gets a caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Handlers are attached during construction; the list is populated when the frame calls
    /// <see cref="ReloadSnippetsAsync"/> on <c>Opened</c> (and after an external reload), because
    /// the store arrives through the session after the constructor has run - the same
    /// injection shape as the history store, and the same reason it is safe: the constructor only
    /// wires, it does not read.
    /// </para>
    /// <para>
    /// <strong>Edits commit on focus loss, not on a Save button.</strong> The window's Save
    /// button writes <c>settings.json</c>, and snippets do not live there - they are their own
    /// store, written by panes at any moment. Routing them through Save would mean either
    /// pretending they are settings or growing a second Save; committing per field keeps the file
    /// and the boxes in step and matches the shortcut editor immediately above, which also has no
    /// per-row confirm.
    /// </para>
    /// </remarks>
    private void WireCommandAssistSnippetsRow()
    {
        var addButton = this.FindControl<Button>("BtnAddCommandAssistSnippet");
        var status = this.FindControl<SettingRow>("CommandAssistSnippetsRow");
        if (addButton == null)
        {
            return;
        }

        addButton.Click += async (_, _) =>
        {
            SnippetEditor? editor = TryGetSnippetEditor(status);
            if (editor == null)
            {
                return;
            }

            try
            {
                // Created blank and immediately focused, rather than opening a dialog: the row
                // the user is about to fill in is the same row they will later edit, so there is
                // no second form to learn. The command placeholder text carries the requirement
                // that an empty command is not saved.
                CommandSnippet? created = await editor.AddAsync("New snippet", "# replace with your command");
                if (created == null)
                {
                    return;
                }

                PopulateCommandAssistSnippetsPanel();
                Session.RaiseSnippetsChanged();
                ShowStatus(status, "Snippet added. Give it a name and a command.");
            }
            catch (Exception ex)
            {
                ShowStatus(status, $"Could not add the snippet: {ex.Message}");
            }
        };
    }

    internal async System.Threading.Tasks.Task ReloadSnippetsAsync()
    {
        var status = this.FindControl<SettingRow>("CommandAssistSnippetsRow");
        SnippetEditor? editor = TryGetSnippetEditor(status: null);
        if (editor == null)
        {
            PopulateCommandAssistSnippetsPanel();
            return;
        }

        try
        {
            await editor.LoadAsync();
        }
        catch (Exception ex)
        {
            ShowStatus(status, $"Could not read your snippets: {ex.Message}");
        }

        PopulateCommandAssistSnippetsPanel();
    }

    private SnippetEditor? TryGetSnippetEditor(SettingRow? status)
    {
        if (Session.SnippetStore == null)
        {
            ShowStatus(status, "Snippets are not available in this window.");
            return null;
        }

        return _snippetEditor ??= new SnippetEditor(Session.SnippetStore);
    }

    private void PopulateCommandAssistSnippetsPanel()
    {
        var panel = this.FindControl<StackPanel>("CommandAssistSnippetsPanel");
        if (panel == null)
        {
            return;
        }

        panel.Children.Clear();

        IReadOnlyList<CommandSnippet> snippets = _snippetEditor?.Snippets ?? Array.Empty<CommandSnippet>();
        _snippetsEmptyHint = null;
        if (snippets.Count == 0)
        {
            var emptyText = new TextBlock
            {
                Text = Session.SnippetStore == null
                    ? "Snippets are not available in this window."
                    : FormatSnippetsEmptyHint(),
                Classes = { RowDescStyleClass },
            };

            // Names the pin chord, so a rebind has to refresh it (UpdateCommandAssistShortcutText).
            if (Session.SnippetStore != null)
            {
                _snippetsEmptyHint = emptyText;
            }

            panel.Children.Add(emptyText);
            return;
        }

        foreach (CommandSnippet snippet in snippets)
        {
            panel.Children.Add(CreateCommandAssistSnippetRow(snippet));
        }
    }

    private Border CreateCommandAssistSnippetRow(CommandSnippet snippet)
    {
        var status = this.FindControl<SettingRow>("CommandAssistSnippetsRow");

        // The row's idea of what is currently saved. Updated in place after every successful
        // commit, because the row outlives the edit: without it a second edit would compare
        // against the values the row was built with and the blank-command restore below would put
        // back a command the user replaced two edits ago.
        CommandSnippet current = snippet;

        var nameEditor = new TextBox
        {
            Text = snippet.Name,
            PlaceholderText = "Name",
            MinWidth = 160,
        };

        var commandEditor = new TextBox
        {
            Text = snippet.CommandText,
            PlaceholderText = "Command (required)",
            FontFamily = new FontFamily("Consolas"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        // Commit on focus loss rather than on every keystroke: a per-keystroke write would put a
        // file rewrite behind every character, and the store re-sorts on write, so the list would
        // reorder under the caret mid-word.
        //
        // The panel is deliberately NOT rebuilt afterwards. Focus loss is usually focus moving to
        // something else in the same row - most often the Delete button - and destroying the row
        // out from under an in-flight click means the click never lands. The boxes already show
        // what was saved; only the sort position can be stale, and it settles on the next open.
        async void Commit()
        {
            CommandSnippet? saved = await CommitCommandAssistSnippetAsync(current, nameEditor, commandEditor, status);
            if (saved != null)
            {
                current = saved;
            }
        }

        nameEditor.LostFocus += (_, _) => Commit();
        commandEditor.LostFocus += (_, _) => Commit();

        var deleteButton = new Button
        {
            Content = "Delete",
            Classes = { "Pill" },
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        deleteButton.Click += async (_, _) =>
        {
            // No arm/confirm here, unlike Clear history: one snippet is a row the user can
            // retype, and the two-click pattern earns its friction against "every command you
            // have ever run", not against a single line the user just looked at.
            SnippetEditor? editor = TryGetSnippetEditor(status);
            if (editor == null)
            {
                return;
            }

            try
            {
                if (await editor.RemoveAsync(current.Id))
                {
                    PopulateCommandAssistSnippetsPanel();
                    Session.RaiseSnippetsChanged();
                    ShowStatus(status, $"Deleted \"{current.Name}\".");
                }
            }
            catch (Exception ex)
            {
                ShowStatus(status, $"Could not delete the snippet: {ex.Message}");
            }
        };

        // Columns are assigned explicitly. Avalonia defaults Grid.Column to 0 for every child, so
        // a Children initializer alone stacks all three controls on top of each other in the
        // first column - which is exactly what the first build of this row did.
        Grid.SetColumn(nameEditor, 0);
        Grid.SetColumn(commandEditor, 1);
        Grid.SetColumn(deleteButton, 2);

        return SettingsRowChrome.CreateRow(this, new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("220,*,Auto"),
            ColumnSpacing = 10,
            Children = { nameEditor, commandEditor, deleteButton },
        });
    }

    /// <summary>
    /// Writes one row's edits, returning the saved snippet or <see langword="null"/> when nothing
    /// was written (unchanged, refused, or no store).
    /// </summary>
    private async System.Threading.Tasks.Task<CommandSnippet?> CommitCommandAssistSnippetAsync(
        CommandSnippet snippet,
        TextBox nameEditor,
        TextBox commandEditor,
        SettingRow? status)
    {
        SnippetEditor? editor = _snippetEditor;
        if (editor == null)
        {
            return null;
        }

        string name = nameEditor.Text ?? string.Empty;
        string command = commandEditor.Text ?? string.Empty;
        if (string.Equals(name, snippet.Name, StringComparison.Ordinal) &&
            string.Equals(command, snippet.CommandText, StringComparison.Ordinal))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            // Refused rather than saved-as-empty, and the box is put back, so the user can see
            // which snippet they were about to blank. Deleting is what the Delete button is for.
            commandEditor.Text = snippet.CommandText;
            ShowStatus(status, "A snippet needs a command. Use Delete to remove it.");
            return null;
        }

        try
        {
            if (!await editor.UpdateAsync(snippet.Id, name, command))
            {
                return null;
            }

            Session.RaiseSnippetsChanged();
            ShowStatus(status, "Snippet saved.");

            CommandSnippet? saved = editor.Snippets
                .FirstOrDefault(x => string.Equals(x.Id, snippet.Id, StringComparison.Ordinal));

            // The name the editor derived may differ from what was typed (a blank name becomes
            // the command's first line), so the box is reconciled with what was actually stored.
            if (saved != null)
            {
                nameEditor.Text = saved.Name;
            }

            return saved;
        }
        catch (Exception ex)
        {
            ShowStatus(status, $"Could not save the snippet: {ex.Message}");
            return null;
        }
    }

    private static void ShowStatus(SettingRow? row, string message)
    {
        if (row != null)
        {
            row.Status = message;
        }
    }
}
