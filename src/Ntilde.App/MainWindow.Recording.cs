using Avalonia.Controls;
using Avalonia.Threading;
using Ntilde.Shell;
using Ntilde.Shell.Theming;
using Avalonia.Media;
using Avalonia.Platform.Storage;

using Ntilde.Controls;

namespace Ntilde
{
    /// <summary>
    /// Session recording: the record button, opening and revealing recordings, and the toast shown when one is
    /// saved.
    /// </summary>
    public partial class MainWindow
    {
        private readonly DispatcherTimer _recordingToastTimer = new() { Interval = TimeSpan.FromSeconds(6) };
        private string? _recordingToastFolderPath;
        private string? _recordingToastFilePath;
        private PersistenceNoticeAction? _recordingToastAction; // UI thread: what the toast's action button runs, while it is shown

        protected virtual Task ExecuteOpenRecordingCommandAsync()
        {
            return OpenRecordingAsync();
        }

        private async Task OpenRecordingAsync()
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null)
            {
                await ShowSimpleMessageDialogAsync("Open Recording", "File picker is not available in the current window.");
                return;
            }

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Replay File",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Ntilde Recordings") { Patterns = new[] { "*.rec", "*.cast" } } }
            });

            if (files.Count < 1)
            {
                return;
            }

            var path = files[0].Path.LocalPath;
            var replayWin = new Ntilde.UI.Replay.ReplayWindow(path, _settings.ActiveTheme);
            replayWin.Show();
        }

        private void OpenRecordingsFolder()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.RecordingsDirectory);
                OpenPathInShell(new ShellOpenRequest(AppPaths.RecordingsDirectory, null));
            }
            catch
            {
                ShowRecordingToast(
                    "Unable to open recordings folder",
                    AppPaths.RecordingsDirectory,
                    null,
                    AppPaths.RecordingsDirectory,
                    autoHide: true);
            }
        }

        private void OpenRecordingToastFolder()
        {
            if (string.IsNullOrWhiteSpace(_recordingToastFolderPath))
            {
                OpenRecordingsFolder();
                return;
            }

            try
            {
                Directory.CreateDirectory(_recordingToastFolderPath);
                var request = ResolveRecordingRevealRequest(_recordingToastFilePath, _recordingToastFolderPath, OperatingSystem.IsWindows());
                OpenPathInShell(request);
            }
            catch
            {
                ShowRecordingToast(
                    "Unable to open recordings folder",
                    _recordingToastFolderPath,
                    _recordingToastFilePath,
                    _recordingToastFolderPath,
                    autoHide: true);
            }
        }

        private static void OpenPathInShell(ShellOpenRequest request)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = request.FileName,
                Arguments = request.Arguments,
                UseShellExecute = true
            });
        }

        internal static ShellOpenRequest ResolveRecordingRevealRequest(string? filePath, string recordingsDirectory, bool isWindows)
        {
            if (isWindows && !string.IsNullOrWhiteSpace(filePath))
            {
                return new ShellOpenRequest("explorer.exe", $"/select,\"{filePath}\"");
            }

            return new ShellOpenRequest(recordingsDirectory, null);
        }

        private void OnRecordingStateChanged(bool isRecording)
        {
            Dispatcher.UIThread.Post(() =>
            {
                bool changed = isRecording
                    ? _activeTitleBarToggles.Add("toggle_recording")
                    : _activeTitleBarToggles.Remove("toggle_recording");

                if (changed)
                {
                    // Surfaces an overflowed Record button into the bar while recording and drops
                    // it back into the … flyout when it stops. RebuildTitleBar re-syncs the button
                    // colouring itself, so no separate UpdateRecordButtonUi call belongs here.
                    RebuildTitleBar();
                }
                else
                {
                    UpdateRecordButtonUi(isRecording);
                }
            });
        }

        private void OnRecordingNotification(RecordingNotificationEventArgs notification)
        {
            Dispatcher.UIThread.Post(() =>
            {
                switch (notification.Kind)
                {
                    case RecordingNotificationKind.Started:
                        ShowRecordingToast(
                            "Recording started",
                            BuildRecordingToastMessage(notification),
                            notification.FilePath,
                            notification.RecordingsDirectory,
                            autoHide: true);
                        break;
                    case RecordingNotificationKind.Stopped:
                        ShowRecordingToast(
                            "Recording saved",
                            BuildRecordingToastMessage(notification),
                            notification.FilePath,
                            notification.RecordingsDirectory,
                            autoHide: true);
                        break;
                    case RecordingNotificationKind.Failed:
                        ShowRecordingToast(
                            "Recording failed",
                            notification.ErrorMessage ?? "Unable to start recording.",
                            notification.FilePath,
                            notification.RecordingsDirectory,
                            autoHide: true);
                        break;
                }
            });
        }

        private static string BuildRecordingToastMessage(RecordingNotificationEventArgs notification)
        {
            if (!string.IsNullOrWhiteSpace(notification.FilePath))
            {
                return notification.FilePath!;
            }

            return notification.RecordingsDirectory;
        }

        /// <param name="action">
        /// The toast's one generic action button (Phase 4 spec §7.5), shown only when there is one: a
        /// click hides the toast and runs it. Every toast without one hides the button.
        /// </param>
        private void ShowRecordingToast(string title, string message, string? filePath, string? folderPath, bool autoHide, PersistenceNoticeAction? action = null)
        {
            var toast = this.FindControl<Border>("RecordingToast");
            var titleBlock = this.FindControl<TextBlock>("RecordingToastTitle");
            var messageBlock = this.FindControl<TextBlock>("RecordingToastMessage");
            if (toast == null || titleBlock == null || messageBlock == null)
            {
                return;
            }

            _shownPersistenceNotices = []; // replaced: ShowPersistenceNotices says again what it shows
            _recordingToastFilePath = filePath;
            _recordingToastFolderPath = folderPath;
            _recordingToastAction = action;
            titleBlock.Text = title;
            messageBlock.Text = message;

            // The folder button only makes sense for toasts that have one
            // (recordings). Non-file toasts (long-command completion) would
            // otherwise offer a button that opens an unrelated folder.
            var openFolderButton = this.FindControl<Button>("RecordingToastOpenFolder");
            if (openFolderButton != null)
            {
                openFolderButton.IsVisible = folderPath != null || filePath != null;
            }

            var actionButton = this.FindControl<Button>("RecordingToastAction");
            if (actionButton != null)
            {
                actionButton.Content = action?.Label;
                actionButton.IsVisible = action != null;
            }

            toast.IsVisible = true;

            _recordingToastTimer.Stop();
            if (autoHide)
            {
                _recordingToastTimer.Start();
            }
        }

        private void HideRecordingToast()
        {
            _recordingToastTimer.Stop();
            // A hidden toast offers nothing: a stray click must not run an action it no longer shows.
            _recordingToastAction = null;
            _shownPersistenceNotices = [];
            var toast = this.FindControl<Border>("RecordingToast");
            if (toast != null)
            {
                toast.IsVisible = false;
            }
        }

        /// <summary>UI thread. The toast's action button: the toast goes first, then its action runs.</summary>
        private void RunRecordingToastAction()
        {
            PersistenceNoticeAction? action = _recordingToastAction;
            HideRecordingToast();
            if (action is null) return;
            try
            {
                action.Run();
            }
            catch (Exception ex)
            {
                // A click handler's throw would take the app down; the action's own UI is the place to say more.
                AppLogger.Log($"[MainWindow] the toast action '{action.Label}' failed: {ex.Message}");
            }
        }

        private void SyncRecordingButtonState()
        {
            UpdateRecordButtonUi(_currentPane?.IsRecording ?? false);
        }

        private void UpdateRecordButtonUi(bool isRecording)
        {
            var btnRecord = FindTitleBarButton("toggle_recording");
            var iconRecord = btnRecord?.Content as PathIcon;

            // Absent whenever Record is hidden, or overflowed and not currently active — both
            // legitimate configurations, so this is a quiet no-op rather than a failure.
            if (btnRecord == null || iconRecord == null)
            {
                return;
            }

            if (isRecording)
            {
                btnRecord.Foreground = ThemeBrush(ThemeKeys.Recording);
                iconRecord.Foreground = ThemeBrush(ThemeKeys.Recording);
                btnRecord.Background = ThemeBrush(ThemeKeys.ErrorDim);
            }
            else
            {
                // Back to the stock foreground (NtFg) rather than a copied colour.
                btnRecord.ClearValue(Button.ForegroundProperty);
                iconRecord.ClearValue(PathIcon.ForegroundProperty);
                btnRecord.Background = Brushes.Transparent;
            }
            var recordingShortcut = GetEffectiveShortcutBinding("toggle_recording", "Ctrl+Shift+R");
            ToolTip.SetTip(btnRecord, isRecording
                ? $"Stop Recording ({recordingShortcut})"
                : $"Record Session ({recordingShortcut})");
        }
    }
}
