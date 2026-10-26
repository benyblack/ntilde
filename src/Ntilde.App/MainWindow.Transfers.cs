using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Ntilde.Shell;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform.Storage;

using Ntilde.Controls;
using Ntilde.Models;

namespace Ntilde
{
    /// <summary>
    /// SFTP transfers started from a pane or the remote files sidebar, and the floating transfer center.
    /// </summary>
    public partial class MainWindow
    {
        private bool _isDraggingTransferOverlay;
        private Point _transferOverlayDragStart;
        private Point _transferOverlayOffsetStart;
        private TranslateTransform? _transferOverlayTransform;
        private TransferCenter? _transferCenterControl;

        private async Task InitiateSftpTransfer(TerminalPane? explicitPane, TransferDirection direction, TransferKind kind)
        {
            var pane = explicitPane ?? _currentPane;
            if (pane == null || pane.Profile == null || pane.Profile.Type != ConnectionType.SSH)
            {
                // Only for SSH sessions
                return;
            }

            // Phase 5 spec R8: a persisted remote tab's transfers run as a plain tab's - native SFTP through its registration
            // (its daemon session id, with its host's password scope), or scp in batch mode on its own connection (OpenSSH).
            if (RefusedOnARetargetedHost(pane)) return;

            var profile = pane.Profile;
            var sessionId = pane.Session?.Id ?? Guid.Empty;
            await InitiateSftpTransferAsync(profile, sessionId, direction, kind, pane);
        }

        internal Task InitiateSftpTransferForTest(
            TerminalProfile profile,
            Guid sessionId,
            TransferDirection direction,
            TransferKind kind)
        {
            ArgumentNullException.ThrowIfNull(profile);
            return InitiateSftpTransferAsync(profile, sessionId, direction, kind);
        }

        internal Task StartSidebarDownloadForTest(
            TerminalProfile profile,
            Guid sessionId,
            string selectedRemotePath,
            TransferKind kind)
        {
            ArgumentNullException.ThrowIfNull(profile);
            return InitiateSidebarSftpTransferAsync(
                profile,
                sessionId,
                TransferDirection.Download,
                kind,
                selectedRemotePath);
        }

        internal Task StartSidebarUploadForTest(
            TerminalProfile profile,
            Guid sessionId,
            string remoteDirectory,
            TransferKind kind)
        {
            ArgumentNullException.ThrowIfNull(profile);
            return InitiateSidebarSftpTransferAsync(
                profile,
                sessionId,
                TransferDirection.Upload,
                kind,
                remoteDirectory);
        }

        /// <param name="pane">
        /// The tab the transfer is for, checked again once its dialog is answered (<see cref="RefusedOnARetargetedHost"/>):
        /// the job reads the stored profile as it starts, at once, so a profile retargeted while the dialog was open would
        /// send it to the new destination. Null: no tab to check (tests).
        /// </param>
        private async Task InitiateSftpTransferAsync(
            TerminalProfile profile,
            Guid sessionId,
            TransferDirection direction,
            TransferKind kind,
            TerminalPane? pane = null)
        {
            var request = TransferDialogRequest.ForAction(
                direction,
                kind,
                profile.DefaultRemoteDir ?? "~",
                profile.Id,
                sessionId);

            TransferDialogResult? result = await ShowTransferDialogAsync(request);
            if (result is not { IsConfirmed: true } || (pane is not null && RefusedOnARetargetedHost(pane)))
            {
                return;
            }

            var job = new TransferJob
            {
                SessionId = sessionId,
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                Direction = direction,
                Kind = kind,
                LocalPath = result.LocalPath,
                RemotePath = result.RemotePath
            };

            EnqueueTransferJob(job);
        }

        private Task InitiateSidebarSftpTransfer(
            TerminalPane srcPane,
            TransferDirection direction,
            TransferKind kind,
            string remotePath)
        {
            if (srcPane.Profile == null || srcPane.Session == null)
            {
                return Task.CompletedTask;
            }

            return InitiateSidebarSftpTransferAsync(
                srcPane.Profile,
                srcPane.Session.Id,
                direction,
                kind,
                remotePath,
                srcPane);
        }

        /// <param name="pane">As for <see cref="InitiateSftpTransferAsync"/>: checked again once a local path is picked.</param>
        private async Task InitiateSidebarSftpTransferAsync(
            TerminalProfile profile,
            Guid sessionId,
            TransferDirection direction,
            TransferKind kind,
            string remotePath,
            TerminalPane? pane = null)
        {
            if (direction == TransferDirection.Upload)
            {
                string? localPath = kind == TransferKind.File
                    ? await PickLocalUploadFilePathAsync()
                    : await PickLocalUploadFolderPathAsync();
                if (string.IsNullOrWhiteSpace(localPath) || (pane is not null && RefusedOnARetargetedHost(pane)))
                {
                    return;
                }

                var uploadJob = new TransferJob
                {
                    SessionId = sessionId,
                    ProfileId = profile.Id,
                    ProfileName = profile.Name,
                    Direction = direction,
                    Kind = kind,
                    LocalPath = localPath,
                    RemotePath = remotePath
                };

                EnqueueTransferJob(uploadJob);
                return;
            }

            string? localDownloadPath = kind == TransferKind.File
                ? await PickLocalDownloadFilePathAsync(ResolveSuggestedDownloadFileName(remotePath))
                : await PickLocalDownloadFolderPathAsync();
            if (string.IsNullOrWhiteSpace(localDownloadPath) || (pane is not null && RefusedOnARetargetedHost(pane)))
            {
                return;
            }

            var job = new TransferJob
            {
                SessionId = sessionId,
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                Direction = direction,
                Kind = kind,
                LocalPath = localDownloadPath,
                RemotePath = remotePath
            };

            EnqueueTransferJob(job);
        }

        internal virtual async Task<TransferDialogResult?> ShowTransferDialogAsync(TransferDialogRequest request)
        {
            var dialog = new TransferDialog(request);
            return await dialog.ShowDialog<TransferDialogResult?>(this);
        }

        internal virtual async Task<string?> PickLocalUploadFilePathAsync()
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null)
            {
                return null;
            }

            IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select File to Upload",
                AllowMultiple = false
            });

            return files.Count > 0 ? files[0].Path.LocalPath : null;
        }

        internal virtual async Task<string?> PickLocalUploadFolderPathAsync()
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null)
            {
                return null;
            }

            IReadOnlyList<IStorageFolder> folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Folder to Upload",
                AllowMultiple = false
            });

            return folders.Count > 0 ? folders[0].Path.LocalPath : null;
        }

        internal virtual async Task<string?> PickLocalDownloadFilePathAsync(string suggestedFileName)
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null)
            {
                return null;
            }

            IStorageFile? file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Select Local Destination",
                SuggestedFileName = suggestedFileName
            });

            return file?.Path.LocalPath;
        }

        internal virtual async Task<string?> PickLocalDownloadFolderPathAsync()
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null)
            {
                return null;
            }

            IReadOnlyList<IStorageFolder> folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Local Destination Folder",
                AllowMultiple = false
            });

            return folders.Count > 0 ? folders[0].Path.LocalPath : null;
        }

        internal virtual void EnqueueTransferJob(TransferJob job)
        {
            SftpService.Instance.AddJob(job, Dispatcher);
            ShowTransferCenter();
        }

        private static string ResolveSuggestedDownloadFileName(string remotePath)
        {
            string trimmed = remotePath.Trim().TrimEnd('/', '\\');
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return "download";
            }

            string fileName = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(fileName)
                ? "download"
                : fileName;
        }

        private TransferCenter? EnsureTransferCenterControl()
        {
            if (_transferCenterControl != null)
            {
                return _transferCenterControl;
            }

            var host = this.FindControl<ContentControl>("TransferCenterHost");
            if (host == null)
            {
                return null;
            }

            _transferCenterControl = new TransferCenter();
            host.Content = _transferCenterControl;
            return _transferCenterControl;
        }

        private void ToggleTransferCenter()
        {
            var overlay = this.FindControl<Border>("TransferOverlay");
            if (overlay != null)
            {
                if (!overlay.IsVisible)
                {
                    _ = EnsureTransferCenterControl();
                }

                overlay.IsVisible = !overlay.IsVisible;
            }
        }

        private void ShowTransferCenter()
        {
            void show()
            {
                var overlay = this.FindControl<Border>("TransferOverlay");
                if (overlay != null)
                {
                    _ = EnsureTransferCenterControl();
                    overlay.IsVisible = true;
                }
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                show();
            }
            else
            {
                Dispatcher.UIThread
                    .InvokeAsync(show, DispatcherPriority.Send)
                    .GetAwaiter()
                    .GetResult();
            }
        }

        private void InitializeTransferCenterUI()
        {
            var btnClose = this.FindControl<Button>("BtnCloseTransfers");
            var overlay = this.FindControl<Border>("TransferOverlay");
            var titleBar = this.FindControl<Grid>("TransferTitleBar");

            if (btnClose != null) btnClose.Click += (s, e) => ToggleTransferCenter();

            if (overlay != null)
            {
                _transferOverlayTransform = overlay.RenderTransform as TranslateTransform;
                if (_transferOverlayTransform == null)
                {
                    _transferOverlayTransform = new TranslateTransform();
                    overlay.RenderTransform = _transferOverlayTransform;
                }
            }

            if (titleBar != null)
            {
                titleBar.PointerPressed += OnTransferTitleBarPointerPressed;
                titleBar.PointerMoved += OnTransferTitleBarPointerMoved;
                titleBar.PointerReleased += OnTransferTitleBarPointerReleased;
                titleBar.PointerCaptureLost += OnTransferTitleBarPointerCaptureLost;
            }
        }

        private void OnTransferTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Control titleBar || e.Source is Button)
            {
                return;
            }

            if (!e.GetCurrentPoint(titleBar).Properties.IsLeftButtonPressed)
            {
                return;
            }

            _isDraggingTransferOverlay = true;
            _transferOverlayDragStart = e.GetPosition(this);
            _transferOverlayOffsetStart = new Point(
                _transferOverlayTransform?.X ?? 0,
                _transferOverlayTransform?.Y ?? 0);
            e.Pointer.Capture(titleBar);
            e.Handled = true;
        }

        private void OnTransferTitleBarPointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_isDraggingTransferOverlay || _transferOverlayTransform == null)
            {
                return;
            }

            Point current = e.GetPosition(this);
            Vector delta = current - _transferOverlayDragStart;
            _transferOverlayTransform.X = _transferOverlayOffsetStart.X + delta.X;
            _transferOverlayTransform.Y = _transferOverlayOffsetStart.Y + delta.Y;
        }

        private void OnTransferTitleBarPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            StopTransferTitleBarDrag(sender, e);
        }

        private void OnTransferTitleBarPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            _isDraggingTransferOverlay = false;
        }

        private void StopTransferTitleBarDrag(object? sender, PointerReleasedEventArgs e)
        {
            if (sender is Control titleBar)
            {
                e.Pointer.Capture(null);
            }

            _isDraggingTransferOverlay = false;
        }
    }
}
