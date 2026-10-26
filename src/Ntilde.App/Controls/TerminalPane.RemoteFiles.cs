using Ntilde.Shell;
using Avalonia.Styling;
using Avalonia.Controls;
using System.ComponentModel;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Models;
using Ntilde.Services.Ssh;
using Ntilde.ViewModels.Ssh;

namespace Ntilde.Controls
{
    /// <summary>
    /// The remote files sidebar of an SSH pane: building it, following the remote working directory, and the
    /// transfers it starts.
    /// </summary>
    public partial class TerminalPane
    {
        private IRemoteDirectoryBrowserService _remoteDirectoryBrowserService = new RemoteDirectoryBrowserService();
        private RemoteFilesSidebarViewModel? _remoteFilesSidebarViewModel;
        private RemoteFilesSidebar? _remoteFilesSidebarHost;
        private bool _isRemoteFilesSidebarTestServiceConfigured;

        public void ToggleRemoteFilesSidebar()
        {
            _ = ToggleRemoteFilesSidebarAsync();
        }

        private void InitializeRemoteFilesSidebar()
        {
            SetRemoteFilesSidebarService(_remoteDirectoryBrowserService);

            if (MenuToggleRemoteFilesSidebar != null)
            {
                MenuToggleRemoteFilesSidebar.Click += async (_, _) => await ToggleRemoteFilesSidebarAsync();
            }

            UpdateRemoteFilesSidebarHostIdentity();
            UpdateRemoteFilesSidebarCurrentDirectoryState();
            UpdateRemoteFilesSidebarVisibility();
            UpdateRemoteFilesSidebarEntryPointState();
        }

        private RemoteFilesSidebar EnsureRemoteFilesSidebarHost()
        {
            if (_remoteFilesSidebarHost != null)
            {
                return _remoteFilesSidebarHost;
            }

            var host = new RemoteFilesSidebar
            {
                IsVisible = false,
                DataContext = _remoteFilesSidebarViewModel
            };

            if (host.FindControl<Button>("BtnUploadFile") is Button uploadFileButton)
            {
                uploadFileButton.Click += (_, _) => RequestRemoteFilesSidebarUploadForCurrentDirectory(TransferKind.File);
            }

            if (host.FindControl<Button>("BtnUploadFolder") is Button uploadFolderButton)
            {
                uploadFolderButton.Click += (_, _) => RequestRemoteFilesSidebarUploadForCurrentDirectory(TransferKind.Folder);
            }

            if (host.FindControl<Button>("BtnDownloadSelected") is Button downloadSelectedButton)
            {
                downloadSelectedButton.Click += (_, _) => RequestRemoteFilesSidebarTransferForSelectedEntry();
            }

            if (RemoteFilesSidebarPresenter != null)
            {
                RemoteFilesSidebarPresenter.Content = host;
                RemoteFilesSidebarPresenter.IsVisible = false;
            }

            _remoteFilesSidebarHost = host;
            UpdateRemoteFilesSidebarHostIdentity();
            return host;
        }

        private void SetRemoteFilesSidebarService(IRemoteDirectoryBrowserService directoryBrowserService)
        {
            ArgumentNullException.ThrowIfNull(directoryBrowserService);

            if (_remoteFilesSidebarViewModel != null)
            {
                _remoteFilesSidebarViewModel.PropertyChanged -= OnRemoteFilesSidebarViewModelPropertyChanged;
            }

            _remoteDirectoryBrowserService = directoryBrowserService;
            _remoteFilesSidebarViewModel = new RemoteFilesSidebarViewModel(new RetargetGuardedDirectoryBrowser(this, directoryBrowserService));
            _remoteFilesSidebarViewModel.PropertyChanged += OnRemoteFilesSidebarViewModelPropertyChanged;

            if (_remoteFilesSidebarHost != null)
            {
                _remoteFilesSidebarHost.DataContext = _remoteFilesSidebarViewModel;
            }

            UpdateRemoteFilesSidebarHostIdentity();
        }

        /// <summary>
        /// The sidebar's listings, each checked on its own (Task 28 fix round 1): the first, a navigation, a refresh. A sidebar
        /// opened before its profile was retargeted (<see cref="IsOnRetargetedHost"/>) would otherwise go on listing the
        /// profile's new destination, not where this tab's shell runs. A refused listing reaches nothing: the sidebar closes and
        /// the toast says why, as at its open (<see cref="ToggleRemoteFilesSidebarAsync"/>). Called on the UI thread, by the
        /// sidebar's view model.
        /// </summary>
        private sealed class RetargetGuardedDirectoryBrowser(TerminalPane pane, IRemoteDirectoryBrowserService inner) : IRemoteDirectoryBrowserService
        {
            public Task<RemoteSidebarListingResult> ListDirectoryAsync(Guid profileId, Guid sessionId, string remotePath, CancellationToken cancellationToken)
            {
                if (!pane.IsOnRetargetedHost) return inner.ListDirectoryAsync(profileId, sessionId, remotePath, cancellationToken);
                pane.RaisePersistenceNotice(RemoteFilesUnavailableNoticeTitle, RemoteFilesRetargetedMessage);
                // Posted: the view model is in the middle of this load, and closing resets what it is about to read.
                pane.Dispatcher.Post(() =>
                {
                    if (!Volatile.Read(ref pane._disposed)) pane.CloseRemoteFilesSidebar();
                });
                return Task.FromResult(RemoteSidebarListingResult.Failure(remotePath, RemoteFilesRetargetedMessage));
            }
        }

        private void OnRemoteFilesSidebarViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            UpdateRemoteFilesSidebarCurrentDirectoryState();
            UpdateRemoteFilesSidebarVisibility();
            UpdateRemoteFilesSidebarEntryPointState();
        }

        private bool IsRemoteFilesSidebarSupported()
        {
            return Profile?.Type == ConnectionType.SSH &&
                   Profile.SshBackendKind == SshBackendKind.Native;
        }

        private void UpdateRemoteFilesSidebarEntryPointState()
        {
            if (MenuToggleRemoteFilesSidebar == null)
            {
                return;
            }

            bool isSupported = IsRemoteFilesSidebarSupported();
            bool isBlockedByAltScreen = Buffer?.IsAltScreenActive == true;
            bool isSidebarOpen = _remoteFilesSidebarViewModel?.IsOpen == true;
            bool isSidebarDisconnected = _remoteFilesSidebarViewModel?.IsDisconnected == true;
            bool canOpen = (isSidebarOpen && !isSidebarDisconnected) || Session?.IsProcessRunning == true;
            MenuToggleRemoteFilesSidebar.IsVisible = isSupported;
            MenuToggleRemoteFilesSidebar.IsEnabled = isSupported && !isBlockedByAltScreen && canOpen;
            MenuToggleRemoteFilesSidebar.Header = isSidebarOpen
                ? "Hide Remote Files"
                : "Remote Files";
        }

        private void UpdateRemoteFilesSidebarVisibility()
        {
            bool shouldShow =
                _remoteFilesSidebarViewModel?.IsOpen == true &&
                !(Buffer?.IsAltScreenActive ?? false) &&
                IsRemoteFilesSidebarSupported();

            if (shouldShow)
            {
                EnsureRemoteFilesSidebarHost().IsVisible = true;
            }
            else if (_remoteFilesSidebarHost != null)
            {
                _remoteFilesSidebarHost.IsVisible = false;
            }

            if (RemoteFilesSidebarPresenter != null)
            {
                RemoteFilesSidebarPresenter.IsVisible = shouldShow;
            }
        }

        private void UpdateRemoteFilesSidebarHostIdentity()
        {
            _remoteFilesSidebarHost?.SetHostIdentity(
                string.IsNullOrWhiteSpace(Profile?.Name) ? null : Profile.Name,
                BuildRemoteFilesSidebarSubtitle(Profile));
        }

        private static string? BuildRemoteFilesSidebarSubtitle(TerminalProfile? profile)
        {
            if (profile?.Type != ConnectionType.SSH)
            {
                return null;
            }

            string host = profile.SshHost?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(host))
            {
                return null;
            }

            string subtitle = string.IsNullOrWhiteSpace(profile.SshUser)
                ? host
                : $"{profile.SshUser.Trim()}@{host}";

            if (profile.SshPort > 0 && profile.SshPort != 22)
            {
                subtitle = $"{subtitle}:{profile.SshPort}";
            }

            return subtitle;
        }

        private void UpdateRemoteFilesSidebarCurrentDirectoryState()
        {
            if (_remoteFilesSidebarViewModel == null)
            {
                return;
            }

            if (!_remoteFilesSidebarViewModel.IsOpen)
            {
                _remoteFilesSidebarViewModel.SetJumpToCurrentDirectoryCandidate(null);
                return;
            }

            string? currentWorkingDirectory = string.IsNullOrWhiteSpace(CurrentWorkingDirectory)
                ? null
                : RemoteSidebarStartPathResolver.NormalizeUncStylePath(CurrentWorkingDirectory.Trim());
            string? jumpTarget = string.Equals(
                currentWorkingDirectory,
                _remoteFilesSidebarViewModel.CurrentPath,
                StringComparison.Ordinal)
                ? null
                : currentWorkingDirectory;
            _remoteFilesSidebarViewModel.SetJumpToCurrentDirectoryCandidate(jumpTarget);
        }

        private async Task ToggleRemoteFilesSidebarAsync()
        {
            if (_remoteFilesSidebarViewModel == null)
            {
                return;
            }

            if (_remoteFilesSidebarViewModel.IsOpen)
            {
                CloseRemoteFilesSidebar();
                return;
            }

            if (!IsRemoteFilesSidebarSupported() ||
                Profile == null ||
                Session == null ||
                Session.Id == Guid.Empty ||
                Buffer?.IsAltScreenActive == true)
            {
                return;
            }

            if (IsOnRetargetedHost)
            {
                // Spec §15 (codex D2): the listing would go where the profile points now, not where this tab's shell runs.
                RaisePersistenceNotice(RemoteFilesUnavailableNoticeTitle, RemoteFilesRetargetedMessage);
                return;
            }

            // A persisted remote tab lists as a plain one does (Phase 5 spec R8): over a connection of its own, through its
            // registration (RegisterRemoteMuxSession).
            await OpenRemoteFilesSidebarAsync(Profile.Id, Session.Id);
        }

        private async Task OpenRemoteFilesSidebarAsync(Guid profileId, Guid sessionId)
        {
            if (_remoteFilesSidebarViewModel == null)
            {
                return;
            }

            string startPath = RemoteSidebarStartPathResolver.Resolve(
                CurrentWorkingDirectory,
                Profile?.DefaultRemoteDir);
            await _remoteFilesSidebarViewModel.OpenAsync(
                profileId,
                sessionId,
                startPath,
                CancellationToken.None);
            UpdateRemoteFilesSidebarCurrentDirectoryState();
            UpdateRemoteFilesSidebarVisibility();
            UpdateRemoteFilesSidebarEntryPointState();
        }

        private void CloseRemoteFilesSidebar()
        {
            _remoteFilesSidebarViewModel?.Close();
            UpdateRemoteFilesSidebarVisibility();
            UpdateRemoteFilesSidebarEntryPointState();
        }

        private void RequestRemoteFilesSidebarTransferForSelectedEntry()
        {
            if (_remoteFilesSidebarViewModel?.SelectedEntry is not { } selectedEntry)
            {
                return;
            }

            TransferKind kind = selectedEntry.IsDirectory
                ? TransferKind.Folder
                : TransferKind.File;
            RequestRemoteFilesSidebarTransfer?.Invoke(
                this,
                new SidebarTransferRequest(
                    TransferDirection.Download,
                    kind,
                    selectedEntry.FullPath));
        }

        private void RequestRemoteFilesSidebarUploadForCurrentDirectory(TransferKind kind)
        {
            string remoteDirectory = _remoteFilesSidebarViewModel?.CurrentPath
                ?? CurrentWorkingDirectory
                ?? Profile?.DefaultRemoteDir
                ?? "~";
            if (string.IsNullOrWhiteSpace(remoteDirectory))
            {
                return;
            }

            RequestRemoteFilesSidebarTransfer?.Invoke(
                this,
                new SidebarTransferRequest(
                    TransferDirection.Upload,
                    kind,
                    remoteDirectory));
        }

        internal void ConfigureRemoteFilesSidebarForTest(IRemoteDirectoryBrowserService directoryBrowserService)
        {
            _isRemoteFilesSidebarTestServiceConfigured = true;
            SetRemoteFilesSidebarService(directoryBrowserService);
            UpdateRemoteFilesSidebarCurrentDirectoryState();
            UpdateRemoteFilesSidebarVisibility();
            UpdateRemoteFilesSidebarEntryPointState();
        }

        internal void ShowRemoteFilesSidebarForTest()
        {
            if (!_isRemoteFilesSidebarTestServiceConfigured)
            {
                ConfigureRemoteFilesSidebarForTest(new TestRemoteDirectoryBrowserService());
            }

            if (!IsRemoteFilesSidebarSupported())
            {
                Profile = new TerminalProfile
                {
                    Name = "Test Native SSH",
                    Type = ConnectionType.SSH,
                    SshBackendKind = SshBackendKind.Native,
                    SshHost = "test.example",
                    SshUser = "nova"
                };
            }

            OpenRemoteFilesSidebarAsync(Profile?.Id ?? Guid.NewGuid(), Session?.Id ?? Guid.NewGuid())
                .GetAwaiter()
                .GetResult();
        }

        internal bool IsRemoteFilesSidebarVisibleForTest()
        {
            return RemoteFilesSidebarPresenter?.IsVisible == true;
        }

        internal bool IsRemoteFilesSidebarEntryAvailableForTest()
        {
            UpdateRemoteFilesSidebarEntryPointState();
            return MenuToggleRemoteFilesSidebar?.IsVisible == true &&
                   MenuToggleRemoteFilesSidebar.IsEnabled;
        }

        internal IReadOnlyList<string> GetSftpContextMenuItemNamesForTest()
        {
            if (RootGrid.ContextMenu?.Items is not IEnumerable<object> items)
            {
                return Array.Empty<string>();
            }

            MenuItem? sftpMenu = items.OfType<MenuItem>().FirstOrDefault(m => (string?)m.Header == "SFTP");
            if (sftpMenu?.Items is null)
            {
                return Array.Empty<string>();
            }

            return sftpMenu.Items
                .OfType<MenuItem>()
                .Select(item => item.Name ?? string.Empty)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();
        }

        internal string GetRemoteFilesSidebarCurrentPathForTest()
        {
            return _remoteFilesSidebarViewModel?.CurrentPath ?? string.Empty;
        }

        internal string? GetRemoteFilesSidebarJumpTargetForTest()
        {
            return _remoteFilesSidebarViewModel?.JumpToCurrentDirectoryPath;
        }

        internal bool IsRemoteFilesSidebarDisconnectedForTest()
        {
            return _remoteFilesSidebarViewModel?.IsDisconnected == true;
        }

        /// <summary>Tests: the sidebar's Refresh, as its button runs it.</summary>
        internal Task RefreshRemoteFilesSidebarForTest() => _remoteFilesSidebarViewModel?.RefreshAsync() ?? Task.CompletedTask;

        private sealed class TestRemoteDirectoryBrowserService : IRemoteDirectoryBrowserService
        {
            public Task<RemoteSidebarListingResult> ListDirectoryAsync(
                Guid profileId,
                Guid sessionId,
                string remotePath,
                CancellationToken cancellationToken)
            {
                string resolvedPath = string.IsNullOrWhiteSpace(remotePath) ? "~" : remotePath;
                return Task.FromResult(RemoteSidebarListingResult.Success(
                    resolvedPath,
                    Array.Empty<RemoteSidebarEntry>()));
            }
        }
    }
}
