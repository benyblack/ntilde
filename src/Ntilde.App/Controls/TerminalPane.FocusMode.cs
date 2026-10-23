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
            }
        }

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
    }
}
