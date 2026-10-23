namespace Ntilde.Controls
{
    /// <summary>
    /// Focus mode on a pane: the window sets <see cref="FocusChromeHidden"/> and each visibility owner
    /// ANDs it in. Nothing here changes what the pane would show outside focus mode.
    /// </summary>
    public partial class TerminalPane
    {
        private bool _focusChromeHidden;
        private bool _markdownPresent;
        private bool _remoteFilesHiddenByFocus;

        internal bool FocusChromeHidden
        {
            get => _focusChromeHidden;
            set
            {
                if (_focusChromeHidden == value) return;
                _focusChromeHidden = value;
                _remoteFilesHiddenByFocus = value && _remoteFilesSidebarViewModel?.IsOpen == true;

                UpdateStatusBarVisibility();
                TermScrollBar.IsVisible = !value;
                AgentOutputToggle.IsVisible = _markdownPresent && !value;
                UpdateAgentOutputPanelVisibility();
                UpdateRemoteFilesSidebarVisibility();
                UpdateRemoteFilesSidebarEntryPointState();
            }
        }

        /// <summary>
        /// Starts the shell of a pane that has not been laid out. A pane normally spawns on first layout, and a
        /// tab that is not selected is never laid out, so an agent-spawned background tab calls this. A no-op
        /// once a session exists.
        /// </summary>
        internal void StartSessionInBackground(int cols, int rows) =>
            InitializeSession(ShellCommand, Profile, cols, rows);

        internal void OnMarkdownPresenceChanged(bool present)
        {
            _markdownPresent = present;
            AgentOutputToggle.IsVisible = present && !_focusChromeHidden;
        }

        internal static bool ShouldShowRemoteFilesSidebar(bool isOpen, bool altScreen, bool supported, bool hiddenByFocus) =>
            isOpen && !altScreen && supported && !hiddenByFocus;

        /// <summary>A toggle on a panel that focus mode hid reveals it; otherwise it flips the open state.</summary>
        internal static (bool IsOpen, bool HiddenByFocus) ResolveRemoteFilesToggle(bool isOpen, bool hiddenByFocus) =>
            isOpen && hiddenByFocus ? (true, false) : (!isOpen, hiddenByFocus);

        /// <summary>
        /// The pane menu's Remote Files entry. A panel focus mode hid is open but not shown, and a click reveals it
        /// (<see cref="ResolveRemoteFilesToggle"/>), so the entry does not offer to hide it.
        /// </summary>
        internal static string RemoteFilesMenuHeader(bool isOpen, bool hiddenByFocus) =>
            isOpen && !hiddenByFocus ? "Hide Remote Files" : "Remote Files";
    }
}
