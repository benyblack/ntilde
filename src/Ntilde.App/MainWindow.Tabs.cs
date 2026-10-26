using Avalonia.Controls;
using Avalonia.Threading;
using Ntilde.Shell;
using Avalonia.Input.Platform;
using Ntilde.Pty;

using Ntilde.Controls;
using Ntilde.Shell.TitleBar;

namespace Ntilde
{
    /// <summary>
    /// Tabs: their identity and runtime state, status refresh, MRU switching, opening a tab, and the tab
    /// commands (copy title, rename, pin, protect, close others).
    /// </summary>
    public partial class MainWindow
    {
        private readonly Dictionary<TabItem, Guid> _tabIds = new();
        private readonly List<TabItem> _tabMru = new();
        private readonly Dictionary<TabItem, TabRuntimeState> _tabStateByTab = new();
        private bool _suppressMruTouchOnSelection;
        private DispatcherTimer? _tabStatusTimer;

        private sealed class TabRuntimeState
        {
            public string? UserTitle { get; set; }
            public bool IsPinned { get; set; }
            public bool IsProtected { get; set; }
            public bool HasActivity { get; set; }
            public bool HasBell { get; set; }
            public DateTime LastBellUtc { get; set; }
            public AgentHost.AgentAttentionTier AgentTier { get; set; }

            /// <summary>A pane in this tab shows a mux session other clients are attached to (spec §7.3).</summary>
            public bool IsShared { get; set; }
            public TabStatusTracker Status { get; } = new();
            public TabTrackerStatus RenderedStatus { get; set; }

            /// <summary>True while any pane in this tab has a command running — the agent-session
            /// status machine's precise per-session state (see <see cref="RefreshTabStatuses"/>),
            /// which survives silent stretches that decay the output-burst heuristic. Feeds
            /// <see cref="TabStatusPresentation.ResolveTabDot"/> via
            /// <see cref="UpdateVerticalTabExtras"/>; vertical mode only.</summary>
            public bool HasRunningCommand { get; set; }
            public TabPreviewTracker Preview { get; } = new();
            public bool PreviewDirty { get; set; }
            public DateTime LastPreviewUpdateUtc { get; set; }

            /// <summary>Latest OSC 9;4 progress report from a pane in this tab (#271);
            /// null = none. Last report wins — the common case is one reporting pane per tab.</summary>
            public TerminalProgressReport? Progress { get; set; }

            /// <summary>The pane whose report <see cref="Progress"/> came from, so a split's
            /// other pane can keep reporting after this one withdraws or closes.</summary>
            public TerminalPane? ProgressPane { get; set; }
        }

        internal enum TabHeaderPointerAction
        {
            None,
            OpenContextMenu,
            CloseTab
        }

        private bool TryGetSelectedTab(out TabItem tabItem)
        {
            tabItem = null!;
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs?.SelectedItem is not TabItem selected) return false;
            tabItem = selected;
            return true;
        }

        private Guid GetTabId(TabItem tab)
        {
            if (_tabIds.TryGetValue(tab, out var id))
            {
                return id;
            }

            if (tab.Tag is TabSession session &&
                !string.IsNullOrWhiteSpace(session.TabId) &&
                Guid.TryParse(session.TabId, out var restoredId))
            {
                id = restoredId;
            }
            else
            {
                id = Guid.NewGuid();
            }

            _tabIds[tab] = id;
            return id;
        }

        internal Guid GetPersistentTabId(TabItem tab)
        {
            return GetTabId(tab);
        }

        private TabRuntimeState GetOrCreateTabState(TabItem tab)
        {
            if (_tabStateByTab.TryGetValue(tab, out var state))
            {
                return state;
            }

            state = new TabRuntimeState();
            if (tab.Tag is TabSession saved)
            {
                state.UserTitle = saved.UserTitle;
                state.IsPinned = saved.IsPinned;
                state.IsProtected = saved.IsProtected;
            }

            _tabStateByTab[tab] = state;
            return state;
        }

        internal TabStatusTracker GetTabStatusTracker(TabItem tab) => GetOrCreateTabState(tab).Status;

        /// <summary>Test-only seam: reads the precise running-command flag
        /// <see cref="RefreshTabStatuses"/> populates from agent-session registrations (same
        /// pattern as <see cref="GetTabStatusTracker"/>).</summary>
        internal bool IsTabRunningCommandForTest(TabItem tab) => GetOrCreateTabState(tab).HasRunningCommand;

        /// <summary>Test-only seam: reads the last status the 1 Hz pass rendered (the heuristic
        /// input ResolveTabDot pairs with <see cref="IsTabRunningCommandForTest"/>).</summary>
        internal TabTrackerStatus GetTabRenderedStatusForTest(TabItem tab) => GetOrCreateTabState(tab).RenderedStatus;

        internal bool GetTabPreviewDirtyForTest(TabItem tab) => GetOrCreateTabState(tab).PreviewDirty;

        internal void SetTabPreviewDirtyForTest(TabItem tab, bool dirty) => GetOrCreateTabState(tab).PreviewDirty = dirty;

        /// <summary>
        /// Test-only seam: backdates the preview recompute throttle so the next
        /// <see cref="UpdateTabVisuals()"/> pass is guaranteed to recompute rather than skip.
        /// </summary>
        /// <remarks>
        /// Waiting the interval out by wall clock does not work, and that is what made
        /// <c>ApplyTabLayout_ModeSwitch_MarksPreviewDirty</c> flaky. A test window runs a real
        /// shell, a prompt that redraws (a clock in it is enough) keeps producing output,
        /// <c>OnPaneOutputReceived</c> marks the tab dirty for each burst, and the visual pass that
        /// then recomputes re-arms this timestamp at a moment the test does not control - so a
        /// sleep can end with the throttle freshly armed and the next pass skipping, leaving the
        /// flag set. Backdating immediately before the pass removes the timing from the picture:
        /// no dispatcher job can run between the two, because the test holds the UI thread.
        /// </remarks>
        internal void BackdateTabPreviewThrottleForTest(TabItem tab)
            => GetOrCreateTabState(tab).LastPreviewUpdateUtc = DateTime.UtcNow - PreviewRefreshInterval;

        /// <summary>
        /// Test-only seam: when the preview was last actually recomputed. Advances only in the
        /// recompute branch of <see cref="UpdateTabVisuals()"/>, so it says "a pass recomputed"
        /// in a way the dirty flag cannot.
        /// </summary>
        /// <remarks>
        /// The flag is the wrong thing for a test to assert on once a pane has a live shell behind
        /// it: <c>OnPaneOutputReceived</c> re-marks it for every output burst, so it can be true
        /// again a moment after a pass legitimately cleared it, and a test that reads it is racing
        /// the shell's prompt. This timestamp is monotone through that - output never touches it.
        /// </remarks>
        internal DateTime GetTabPreviewRecomputedAtForTest(TabItem tab)
            => GetOrCreateTabState(tab).LastPreviewUpdateUtc;

        /// <summary>Test-only seam: sets the marker inputs UpdateVerticalTabExtras resolves
        /// the chip visibilities and dot color from (same pattern as
        /// <see cref="SetTabPreviewDirtyForTest"/>), since driving real bell/agent events in
        /// the headless test host is impractical.</summary>
        internal void SetTabMarkerStateForTest(TabItem tab, bool hasBell, bool hasActivity, AgentHost.AgentAttentionTier agentTier)
        {
            var state = GetOrCreateTabState(tab);
            state.HasBell = hasBell;
            state.HasActivity = hasActivity;
            state.AgentTier = agentTier;
        }

        /// <summary>Timer-driven decay pass: re-evaluates every tab's heuristic status and
        /// precise running-command flag (agent-session registry), queueing a visual refresh
        /// only for tabs where either changed. Vertical mode only.</summary>
        internal void RefreshTabStatuses()
        {
            if (!_isVerticalTabStrip) return;
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            // Precise running state, one pass over the registry snapshot before the per-tab
            // loop: any tab owning a pane whose agent-session status machine reports Running
            // has a command in flight. The output-burst heuristic below (Status.Evaluate in
            // the per-tab loop) decays after 2 quiet seconds, so a silently-thinking agent
            // CLI (60s between tokens is normal) would
            // flip its dot back to idle; the per-session status machine knows better. Same
            // registration→tab mapping as RefreshTabAgentAttention (TabId vs the persistent
            // tab id). Snapshot() is thread-safe; GetRegistrations() hands back a point-in-time
            // array, so no registry lock is held. 1 Hz over at most dozens of registrations:
            // one HashSet per tick is the whole allocation cost.
            var runningTabIds = new HashSet<Guid>();
            foreach (var registration in AgentHost.AgentSessionRegistry.Instance.GetRegistrations())
            {
                var tabId = registration.TabId;
                if (tabId.HasValue
                    && registration.StatusMachine.Snapshot().Kind == AgentHost.AgentSessionStatusKind.Running)
                {
                    runningTabIds.Add(tabId.Value);
                }
            }

            var now = DateTime.UtcNow;
            foreach (TabItem tab in tabs.Items.Cast<TabItem>())
            {
                var state = GetOrCreateTabState(tab);
                var status = state.Status.Evaluate(now, isSelected: tab.IsSelected);
                if (status != state.RenderedStatus)
                {
                    state.RenderedStatus = status;
                    QueueTabVisualRefresh(tab);
                }

                // Same change-driven queueing as RenderedStatus: the flag feeds
                // ResolveTabDot via UpdateVerticalTabExtras, which only runs in vertical
                // mode (horizontal headers never read it - stale-but-unread there, cost
                // zero, which is why this whole method is vertical-only). One benign
                // transient: flipping horizontal→vertical re-enters with up to 1s of
                // stale dot state until the first tick resyncs - self-correcting and
                // cosmetic, so it needs no mode-flip hook here.
                bool running = runningTabIds.Contains(GetPersistentTabId(tab));
                if (running != state.HasRunningCommand)
                {
                    state.HasRunningCommand = running;
                    QueueTabVisualRefresh(tab);
                }
            }
        }

        internal string? GetTabUserTitle(TabItem tab)
        {
            return GetOrCreateTabState(tab).UserTitle;
        }

        internal bool IsTabPinned(TabItem tab)
        {
            return GetOrCreateTabState(tab).IsPinned;
        }

        internal bool IsTabProtected(TabItem tab)
        {
            return GetOrCreateTabState(tab).IsProtected;
        }

        internal static bool CanCloseTab(bool isProtected)
        {
            return !isProtected;
        }

        internal static TabHeaderPointerAction ResolveTabHeaderPointerAction(bool isMiddlePressed, bool isRightPressed)
        {
            if (isMiddlePressed)
            {
                return TabHeaderPointerAction.CloseTab;
            }

            if (isRightPressed)
            {
                return TabHeaderPointerAction.OpenContextMenu;
            }

            return TabHeaderPointerAction.None;
        }

        internal static bool ShouldDeferTabContextMenuOpen(bool wasSelected)
        {
            return !wasSelected;
        }

        internal static bool ShouldSkipTabWhenClosingOthers(bool isPinned, bool isProtected)
        {
            return isPinned || isProtected;
        }

        internal static string GetPinTabActionLabel(bool isPinned)
        {
            return isPinned ? "Unpin Tab" : "Pin Tab";
        }

        internal static string GetProtectTabActionLabel(bool isProtected)
        {
            return isProtected ? "Unprotect Tab" : "Protect Tab";
        }

        private void ClearTabAttention(TabItem tab)
        {
            var state = GetOrCreateTabState(tab);
            state.HasActivity = false;
            state.HasBell = false;
        }

        private void QueueTabVisualRefresh(TabItem tab)
        {
            _pendingVisualRefreshTabs.Add(tab);
            if (_tabVisualRefreshScheduled) return;

            _tabVisualRefreshScheduled = true;
            Dispatcher.UIThread.Post(() =>
            {
                _tabVisualRefreshScheduled = false;
                var toRefresh = _pendingVisualRefreshTabs.ToList();
                _pendingVisualRefreshTabs.Clear();

                // UpdateTabVisuals ignores its specificTab parameter and always does a full
                // all-tabs pass, so calling it once per queued tab makes K queued tabs = K
                // identical full passes. One call per batch is enough.
                if (toRefresh.Count == 0) return;
                UpdateTabVisuals();
            }, DispatcherPriority.Background);
        }

        private void TouchTabMru(TabItem tab)
        {
            _tabMru.Remove(tab);
            _tabMru.Insert(0, tab);
        }

        private void CleanupTabMru(TabControl tabs)
        {
            var liveTabs = tabs.Items.Cast<TabItem>().ToHashSet();
            _tabMru.RemoveAll(t => !liveTabs.Contains(t));
            foreach (var tab in tabs.Items.Cast<TabItem>())
            {
                if (!_tabMru.Contains(tab))
                {
                    _tabMru.Add(tab);
                }
            }
        }

        private bool SwitchTabByMru(bool reverse)
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return false;

            CleanupTabMru(tabs);
            if (tabs.SelectedItem is not TabItem selected) return false;

            if (_tabMru.Count < 2) return false;

            int selectedIndex = _tabMru.IndexOf(selected);
            if (selectedIndex < 0)
            {
                TouchTabMru(selected);
                selectedIndex = 0;
            }

            int targetIndex = GetNextMruIndex(selectedIndex, _tabMru.Count, reverse);
            if (targetIndex < 0) return false;

            var target = _tabMru[targetIndex];
            if (tabs.SelectedItem == target) return false;

            _suppressMruTouchOnSelection = true;
            tabs.SelectedItem = target;
            return true;
        }

        internal static int GetNextMruIndex(int selectedIndex, int mruCount, bool reverse)
        {
            if (mruCount < 2 || selectedIndex < 0 || selectedIndex >= mruCount)
            {
                return -1;
            }

            return reverse
                ? (selectedIndex - 1 + mruCount) % mruCount
                : (selectedIndex + 1) % mruCount;
        }

        private async Task CopySelectedTabTitleAsync()
        {
            if (!TryGetSelectedTab(out var tab)) return;
            await CopyTabTitleAsync(tab);
        }

        private async Task CopyTabTitleAsync(TabItem tab)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard == null) return;

            string title = GetTabPrimaryTitle(tab);
            await topLevel.Clipboard.SetTextAsync(title);
        }

        private async Task RenameSelectedTabAsync()
        {
            if (!TryGetSelectedTab(out var tab)) return;
            await RenameTabAsync(tab);
        }

        private async Task RenameTabAsync(TabItem tab)
        {
            var state = GetOrCreateTabState(tab);
            string current = state.UserTitle ?? GetTabPrimaryTitle(tab);
            var updated = await ShowTextPromptAsync("Rename Tab", "Tab title", current);
            if (updated == null) return;

            state.UserTitle = string.IsNullOrWhiteSpace(updated) ? null : updated.Trim();
            UpdateTabVisuals(tab);
            PopulateTabListMenu();
        }

        private string GetTabSwitchCommandLabel(TabItem tab)
        {
            var pane = ResolvePaneForTab(tab);
            string title = GetTabHeaderText(tab);
            string process = pane?.ShellCommand ?? "shell";
            string cwd = pane?.CurrentWorkingDirectory ?? "";

            if (!string.IsNullOrWhiteSpace(cwd))
            {
                return $"Switch Tab: {title} [{Path.GetFileName(cwd)} | {Path.GetFileName(process)}]";
            }

            return $"Switch Tab: {title} [{Path.GetFileName(process)}]";
        }

        private async Task CloseOtherTabsAsync()
        {
            if (!TryGetSelectedTab(out var selected)) return;
            await CloseOtherTabsAsync(selected);
        }

        private async Task CloseOtherTabsAsync(TabItem selected)
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            var others = tabs.Items.Cast<TabItem>().Where(t => t != selected).ToList();
            foreach (var tab in others)
            {
                if (ShouldSkipTabWhenClosingOthers(IsTabPinned(tab), IsTabProtected(tab))) continue;
                await CloseTabAsync(tab);
            }
        }

        private void TogglePinSelectedTab()
        {
            if (!TryGetSelectedTab(out var tab)) return;
            TogglePinTab(tab);
        }

        private void ToggleProtectSelectedTab()
        {
            if (!TryGetSelectedTab(out var tab)) return;
            ToggleProtectTab(tab);
        }

        private void TogglePinTab(TabItem tab)
        {
            var state = GetOrCreateTabState(tab);
            state.IsPinned = !state.IsPinned;
            UpdateTabVisuals(tab);
            PopulateTabListMenu();
        }

        private void ToggleProtectTab(TabItem tab)
        {
            var state = GetOrCreateTabState(tab);
            state.IsProtected = !state.IsProtected;
            UpdateTabVisuals(tab);
            PopulateTabListMenu();
        }

        /// <summary>
        /// Opens <paramref name="pane"/> (not yet wired or hosted) in a new tab. AddTab's tail, shared
        /// with orphan adoption, which builds its pane around a daemon session and passes
        /// <paramref name="select"/> false: the tab is added in the background, leaving the selected
        /// tab, the current pane, the MRU order and focus where the user had them.
        /// </summary>
        private void AddTabWithPane(TerminalPane pane, string title, bool select = true)
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            WirePane(pane);

            pane.ApplySettings(_settings);
            var tabItem = new TabItem { Content = pane };
            ConfigureTabHeader(tabItem, title);
            tabs.Items.Add(tabItem);
            if (select) tabs.SelectedItem = tabItem;
            GetTabId(tabItem);
            GetOrCreateTabState(tabItem);
            if (select)
            {
                TouchTabMru(tabItem);
                _currentPane = pane;
            }

            _activePaneByTab[tabItem] = pane;
            _paneOwnerTab[pane] = tabItem;
            AgentHost.AgentSessionRegistry.Instance.SetTabAssociation(pane.PaneId, GetPersistentTabId(tabItem));

            // Defer visual update until layout is complete (ensures template is applied)
            EventHandler? layoutHandler = null;
            layoutHandler = (s, e) =>
            {
                tabItem.LayoutUpdated -= layoutHandler;
                Dispatcher.UIThread.Post(() => UpdateTabVisuals(), DispatcherPriority.Input);
            };
            tabItem.LayoutUpdated += layoutHandler;

            // Fallback: Post anyway
            Dispatcher.UIThread.Post(() => UpdateTabVisuals(), DispatcherPriority.Input);
            if (select) Dispatcher.UIThread.Post(() => pane.ActiveControl.Focus());
            UpdatePaneAutomationLabels();
            UpdateBroadcastIndicator();
            RefreshLayoutModelForTab(tabItem);
        }

        private void PopulateNewTabMenu()
        {
            var btnNewTab = this.FindControl<Button>(TitleBarViewFactory.NewTabButtonName);
            var flyout = btnNewTab?.Flyout as MenuFlyout;
            if (flyout == null) return;

            // Clear dynamic items (everything before the separator)
            // Note: Simplest way is to rebuild the Flyout menu items list
            int separatorIndex = -1;
            for (int i = 0; i < flyout.Items.Count; i++)
            {
                if (flyout.Items[i] is Separator) { separatorIndex = i; break; }
            }

            // Keep only the separator and Manage Profiles
            var footerItems = new System.Collections.Generic.List<object>();
            if (separatorIndex != -1)
            {
                for (int i = separatorIndex; i < flyout.Items.Count; i++)
                {
                    var footer = flyout.Items[i];
                    if (footer != null) footerItems.Add(footer);
                }
            }

            flyout.Items.Clear();

            // Add profiles
            foreach (var profile in _settings.Profiles.Where(p => p.Type == ConnectionType.Local))
            {
                // UI Polish: Show all profiles the user has configured.
                // Previously we hid "invalid" ones, but that hides imported WSL profiles if not found in path.
                // Let the user see and fix them if broken.

                var item = new MenuItem { Header = profile.Name };
                item.Click += (s, e) => AddTab(profile);
                flyout.Items.Add(item);
            }

            // Add footer back
            foreach (var footer in footerItems)
                flyout.Items.Add(footer);
        }
    }
}
