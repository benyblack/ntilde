using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Controls.Presenters;
using Ntilde.VT;
using Ntilde.Rendering;
using Avalonia.Automation;

using Ntilde.Controls;

namespace Ntilde
{
    /// <summary>
    /// Which pane is active and which tab owns it, moving focus between panes, and the automation labels that
    /// screen readers announce.
    /// </summary>
    public partial class MainWindow
    {
        private readonly Dictionary<TerminalPane, TabItem> _paneOwnerTab = new();

        private static IEnumerable<TerminalPane> EnumeratePanes(Control? control)
        {
            if (control == null) yield break;

            if (control is TerminalPane pane)
            {
                yield return pane;
                yield break;
            }

            if (control is Panel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is not Control cc) continue;
                    foreach (var nested in EnumeratePanes(cc)) yield return nested;
                }

                yield break;
            }

            if (control is ContentControl contentControl && contentControl.Content is Control content)
            {
                foreach (var nested in EnumeratePanes(content)) yield return nested;
            }
        }

        private void UpdatePaneAutomationLabels()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            int tabCount = tabs.Items.Count;
            for (int tabIndex = 0; tabIndex < tabCount; tabIndex++)
            {
                if (tabs.Items[tabIndex] is not TabItem tabItem) continue;
                var panes = EnumeratePanes(tabItem.Content as Control).ToList();
                if (panes.Count == 0) continue;

                var activeForTab = ResolvePaneForTab(tabItem);
                for (int paneIndex = 0; paneIndex < panes.Count; paneIndex++)
                {
                    var pane = panes[paneIndex];
                    bool isActive = pane == activeForTab || pane == _currentPane;
                    string activeText = isActive ? " active" : string.Empty;
                    string label = $"Tab {tabIndex + 1} pane {paneIndex + 1} of {panes.Count}{activeText}";
                    AutomationProperties.SetName(pane, label);
                    AutomationProperties.SetName(pane.ActiveControl, label);
                }
            }
        }

        /// <summary>
        /// The trailing attention-marker suffix for a tab's screen-reader
        /// automation label, spoken as words rather than the glyphs
        /// <see cref="GetAttentionMarkerSuffix"/> renders for sighted users.
        /// Order and wording preserve exactly what the previous inline
        /// ternary chain in <c>UpdateTabAutomationLabels</c> produced: bell
        /// beats mere activity, and the agent tier (independent of either)
        /// follows.
        /// </summary>
        private static string GetAttentionAnnouncementSuffix(TabRuntimeState state)
        {
            string attention;
            if (state.HasBell)
            {
                attention = " bell";
            }
            else if (state.HasActivity)
            {
                attention = " activity";
            }
            else
            {
                attention = string.Empty;
            }

            string agent;
            if (state.AgentTier == AgentHost.AgentAttentionTier.Wrote)
            {
                agent = " agent-typed";
            }
            else if (state.AgentTier == AgentHost.AgentAttentionTier.Watched)
            {
                agent = " agent-reading";
            }
            else
            {
                agent = string.Empty;
            }

            return attention + agent + (state.IsShared ? " shared" : string.Empty);
        }

        private void UpdateTabAutomationLabels()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null)
            {
                sw.Stop();
                RendererStatistics.RecordTabAutomationUpdateTime(sw.ElapsedMilliseconds);
                return;
            }

            int count = tabs.Items.Count;
            for (int i = 0; i < count; i++)
            {
                if (tabs.Items[i] is not TabItem tab) continue;
                var state = GetOrCreateTabState(tab);
                bool active = tabs.SelectedItem == tab;
                string label = $"Tab {i + 1} of {count}: {GetTabHeaderText(tab)}{(active ? " active" : "")}{GetAttentionAnnouncementSuffix(state)}";
                AutomationProperties.SetName(tab, label);
            }
            sw.Stop();
            RendererStatistics.RecordTabAutomationUpdateTime(sw.ElapsedMilliseconds);
        }

        private enum MoveDirection { Left, Right, Up, Down }
        private bool NavigatePane(MoveDirection dir) => NavigatePaneRecursive(_currentPane, dir);
        private bool NavigatePaneRecursive(Control? start, MoveDirection dir)
        {
            if (start == null || start.Parent is not Grid parentGrid) return false;
            int r = Grid.GetRow(start);
            int c = Grid.GetColumn(start);

            int rowStep = 0;
            int colStep = 0;
            switch (dir)
            {
                case MoveDirection.Left: colStep = -1; break;
                case MoveDirection.Right: colStep = 1; break;
                case MoveDirection.Up: rowStep = -1; break;
                case MoveDirection.Down: rowStep = 1; break;
            }

            int probeR = r + rowStep;
            int probeC = c + colStep;
            while (probeR >= 0 && probeC >= 0)
            {
                Control? sibling = parentGrid.Children
                    .OfType<Control>()
                    .FirstOrDefault(x => Grid.GetRow(x) == probeR && Grid.GetColumn(x) == probeC);
                if (sibling == null) break;
                if (sibling is GridSplitter)
                {
                    probeR += rowStep;
                    probeC += colStep;
                    continue;
                }

                var before = _currentPane;
                FocusFirstPane(sibling);
                return _currentPane != null && _currentPane != before;
            }
            if (parentGrid.Parent is Control grandParent && (grandParent is Grid || grandParent is ContentPresenter || grandParent is TabItem))
                return NavigatePaneRecursive(parentGrid, dir);
            return false;
        }

        private void FocusFirstPane(Control control)
        {
            var pane = FindFirstPane(control);
            if (pane != null)
            {
                UpdateActivePane(pane);
                FocusPaneTerminal(pane, defer: true);
            }
        }

        private void RegisterPaneOwners(TabItem tabItem, Control control)
        {
            if (control is TerminalPane pane)
            {
                _paneOwnerTab[pane] = tabItem;
                AgentHost.AgentSessionRegistry.Instance.SetTabAssociation(pane.PaneId, GetPersistentTabId(tabItem));
                return;
            }

            if (control is Panel panel)
            {
                foreach (var child in panel.Children.OfType<Control>())
                {
                    RegisterPaneOwners(tabItem, child);
                }

                return;
            }

            if (control is Decorator decorator && decorator.Child is Control childControl)
            {
                RegisterPaneOwners(tabItem, childControl);
                return;
            }

            if (control is ContentControl contentControl && contentControl.Content is Control content)
            {
                RegisterPaneOwners(tabItem, content);
            }
        }

        private TabItem? ResolveOwningTabForPane(TerminalPane pane)
        {
            if (_paneOwnerTab.TryGetValue(pane, out var cachedTab))
            {
                return cachedTab;
            }

            var visualTab = pane.FindAncestorOfType<TabItem>();
            if (visualTab != null)
            {
                _paneOwnerTab[pane] = visualTab;
                AgentHost.AgentSessionRegistry.Instance.SetTabAssociation(pane.PaneId, GetPersistentTabId(visualTab));
                return visualTab;
            }

            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null)
            {
                return null;
            }

            foreach (var item in tabs.Items.Cast<TabItem>())
            {
                if (item.Content is Control content)
                {
                    RegisterPaneOwners(item, content);
                    if (_paneOwnerTab.TryGetValue(pane, out cachedTab))
                    {
                        return cachedTab;
                    }
                }
            }

            return null;
        }

        private void UpdateActivePane(TerminalPane pane)
        {
            var ownerTab = ResolveOwningTabForPane(pane);
            if (ownerTab != null) _activePaneByTab[ownerTab] = pane;

            if (_currentPane == pane) return;

            // Unsubscribe from old pane
            if (_currentPane != null)
            {
                _currentPane.RecordingStateChanged -= OnRecordingStateChanged;
                _currentPane.RecordingNotification -= OnRecordingNotification;
            }

            _currentPane = pane;

            // Subscribe to new pane
            if (_currentPane != null)
            {
                _currentPane.RecordingStateChanged += OnRecordingStateChanged;
                _currentPane.RecordingNotification += OnRecordingNotification;
            }

            // Initial UI sync
            OnRecordingStateChanged(_currentPane?.IsRecording ?? false);
            UpdatePaneAutomationLabels();
            if (ownerTab != null)
            {
                RefreshLayoutModelForTab(ownerTab);
                PublishPaneEvent(ownerTab, pane, PaneAuditEventKind.FocusChanged);
            }
        }

        private static TerminalPane? FindFirstPane(Control? control)
        {
            if (control == null) return null;
            if (control is TerminalPane pane) return pane;

            if (control is Panel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is Control cc)
                    {
                        var found = FindFirstPane(cc);
                        if (found != null) return found;
                    }
                }
            }
            else if (control is ContentControl contentControl)
            {
                return FindFirstPane(contentControl.Content as Control);
            }

            return null;
        }

        private TerminalPane? ResolvePaneForTab(TabItem tabItem)
        {
            // Ignore stale mappings when pane was removed/disposed.
            //
            // The logical tree answers this, not the visual one (#319): MainWindow.axaml's
            // TabControl template hosts content in PART_SelectedContentHost, a sibling of the
            // header presenter, so a TabItem is never a *visual* ancestor of its own content
            // and the old check compared null == tabItem — always false. Every call therefore
            // discarded the cache and fell through to FindFirstPane below, so a split tab
            // resolved its FIRST pane instead of the active one: leaving such a tab and
            // returning activated the wrong pane, and CloseTabAsync attributed the close to
            // the wrong pane in the agent journal.
            //
            // Zoom needs no special case: while a tab is zoomed its Content *is* the zoomed
            // pane, so that pane validates here, and a cached non-zoomed pane correctly reads
            // as stale — the fallback then returns the zoomed pane, which is the right answer
            // while zoomed.
            if (_activePaneByTab.TryGetValue(tabItem, out var pane)
                && pane.FindLogicalAncestorOfType<TabItem>() == tabItem)
            {
                return pane;
            }

            if (tabItem.Content is Control content)
            {
                var first = FindFirstPane(content);
                if (first != null)
                {
                    _activePaneByTab[tabItem] = first;
                    return first;
                }
            }

            return null;
        }

        private bool IsFocusOverlayVisible()
        {
            var paletteOverlay = this.FindControl<Grid>("CommandPaletteOverlay");
            if (paletteOverlay?.IsVisible == true) return true;

            var transferOverlay = this.FindControl<Border>("TransferOverlay");
            if (transferOverlay?.IsVisible == true) return true;

            return false;
        }

        private void FocusPaneTerminal(TerminalPane pane, bool defer)
        {
            if (IsFocusOverlayVisible()) return;

            void FocusNow() => pane.ActiveControl.Focus();

            if (defer)
            {
                Dispatcher.UIThread.Post(FocusNow, DispatcherPriority.Input);
                Dispatcher.UIThread.Post(FocusNow, DispatcherPriority.Loaded);
            }
            else
            {
                FocusNow();
            }
        }

        private void FocusCurrentTerminal(bool defer)
        {
            if (_currentPane == null) return;
            FocusPaneTerminal(_currentPane, defer);
        }
    }
}
