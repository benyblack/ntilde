using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Controls.Presenters;
using Ntilde.Shell;
using Ntilde.VT;
using System.Runtime.InteropServices;
using Avalonia.Media;
using Ntilde.Pty;

using Ntilde.Controls;
using Ntilde.Platform.Ssh.Launch;

namespace Ntilde
{
    /// <summary>
    /// The pane layout of a tab: splitting, replacing a pane, zoom, splitters, broadcast input, and the layout
    /// model published to agents.
    /// </summary>
    public partial class MainWindow
    {
        private const string SplitterHoverClass = "splitter-hover";
        private const string SplitterDraggingClass = "splitter-dragging";
        private readonly Dictionary<TabItem, PaneZoomState> _paneZoomStateByTab = new();
        private readonly Dictionary<TabItem, Guid> _zoomedPaneIdByTab = new();
        private readonly HashSet<TabItem> _broadcastEnabledTabs = new();
        private readonly Dictionary<TabItem, PaneLayoutModel> _layoutModelByTab = new();

        private sealed class PaneZoomState
        {
            public required Control OriginalRoot { get; init; }
            public required Control Placeholder { get; init; }
        }

        internal Guid? GetActivePaneIdForTab(TabItem tabItem)
        {
            return ResolvePaneForTab(tabItem)?.PaneId;
        }

        /// <summary>
        /// Whether this tab is currently pane-zoomed - i.e. its content is one
        /// pane rather than its split layout, so every other pane in the tab is
        /// unrendered. Sits beside <see cref="GetZoomedPaneIdForTab"/> because
        /// the agent observe light needs both halves of that answer.
        /// </summary>
        internal bool IsPaneZoomActiveForTab(TabItem tabItem) => _paneZoomStateByTab.ContainsKey(tabItem);

        internal Guid? GetZoomedPaneIdForTab(TabItem tabItem)
        {
            if (_zoomedPaneIdByTab.TryGetValue(tabItem, out var paneId))
            {
                return paneId;
            }

            return null;
        }

        internal bool IsBroadcastEnabledForTab(TabItem tabItem)
        {
            return _broadcastEnabledTabs.Contains(tabItem);
        }

        internal Control? GetLayoutRootForTab(TabItem tabItem)
        {
            if (_paneZoomStateByTab.TryGetValue(tabItem, out var zoomState))
            {
                return zoomState.OriginalRoot;
            }

            return tabItem.Content as Control;
        }

        private void PublishPaneEvent(TabItem tabItem, TerminalPane? pane, PaneAuditEventKind kind, string details = "")
        {
            PaneEventStream.Publish(new PaneAuditEvent
            {
                TimestampUtc = DateTime.UtcNow,
                Kind = kind,
                TabId = GetTabId(tabItem),
                PaneId = pane?.PaneId,
                Details = details
            });
        }

        private void RefreshLayoutModelForTab(TabItem tabItem)
        {
            var root = GetLayoutRootForTab(tabItem);
            if (root == null)
            {
                _layoutModelByTab.Remove(tabItem);
                return;
            }

            _layoutModelByTab[tabItem] = PaneLayoutModel.FromControl(
                root,
                GetActivePaneIdForTab(tabItem),
                GetZoomedPaneIdForTab(tabItem),
                IsBroadcastEnabledForTab(tabItem));
        }

        private void RefreshAllLayoutModels()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            foreach (var item in tabs.Items.Cast<TabItem>())
            {
                RefreshLayoutModelForTab(item);
            }
        }

        private TerminalPane? FindPaneById(Control? control, Guid paneId)
        {
            return EnumeratePanes(control).FirstOrDefault(p => p.PaneId == paneId);
        }

        private static void CopyGridPlacement(Control from, Control to)
        {
            Grid.SetRow(to, Grid.GetRow(from));
            Grid.SetColumn(to, Grid.GetColumn(from));
            Grid.SetRowSpan(to, Grid.GetRowSpan(from));
            Grid.SetColumnSpan(to, Grid.GetColumnSpan(from));
        }

        private bool EnterPaneZoom(TabItem tabItem, TerminalPane pane, bool publishEvent)
        {
            if (_paneZoomStateByTab.ContainsKey(tabItem)) return false;
            if (tabItem.Content is not Control root) return false;
            if (ReferenceEquals(root, pane)) return false;

            var placeholder = new Border { IsVisible = false };
            CopyGridPlacement(pane, placeholder);

            if (pane.Parent is Panel panel)
            {
                int index = panel.Children.IndexOf(pane);
                if (index < 0) return false;
                panel.Children.RemoveAt(index);
                panel.Children.Insert(index, placeholder);
            }
            else if (pane.Parent is ContentPresenter presenter)
            {
                presenter.Content = placeholder;
            }
            else if (pane.Parent is ContentControl contentControl)
            {
                contentControl.Content = placeholder;
            }
            else
            {
                return false;
            }

            _paneZoomStateByTab[tabItem] = new PaneZoomState
            {
                OriginalRoot = root,
                Placeholder = placeholder
            };
            _zoomedPaneIdByTab[tabItem] = pane.PaneId;

            tabItem.Content = pane;
            UpdateActivePane(pane);
            FocusPaneTerminal(pane, defer: true);
            UpdatePaneAutomationLabels();
            RefreshLayoutModelForTab(tabItem);
            // Zoom changes which panes are on screen, so it changes the window
            // light's answer with no attention event behind it - exactly like a
            // tab switch. Without this, a sibling being read while it is zoomed
            // away keeps its now-invisible segment as its only report: the
            // light stays dark and, under the default WritesOnly rollup, the
            // tab glyph is suppressed too, so the rest of that read is shown
            // nowhere and the Watched tier decays in ~3 s.
            RefreshAgentObserveIndicator();

            if (publishEvent)
            {
                PublishPaneEvent(tabItem, pane, PaneAuditEventKind.ZoomToggled, "on");
            }

            return true;
        }

        /// <param name="forTeardown">
        /// Whether this exit is unwinding a tab that is about to be disposed, in which case the
        /// presentation half below is skipped. It is not merely wasted there, it is harmful:
        /// <see cref="FocusPaneTerminal"/> with <c>defer</c> queues two
        /// <c>Dispatcher.UIThread.Post</c> jobs that focus a pane the caller is about to dispose,
        /// and they sit in the queue the whole assembly shares until something runs them. In the
        /// headless lane that something is another test calling <c>RunJobs()</c>, which is how a
        /// teardown here reddens a rendering test in a later class.
        /// </param>
        /// <param name="activate">
        /// False leaves the user's active pane and keyboard where they are: the zoomed pane becomes only the tab's
        /// remembered pane. For a close in a tab the user is not on, and for a tab that is closing.
        /// </param>
        private bool ExitPaneZoom(TabItem tabItem, bool publishEvent, bool forTeardown = false, bool activate = true)
        {
            if (!_paneZoomStateByTab.TryGetValue(tabItem, out var state)) return false;
            if (tabItem.Content is not TerminalPane zoomedPane) return false;

            tabItem.Content = state.OriginalRoot;

            var placeholder = state.Placeholder;
            if (placeholder.Parent is Panel panel)
            {
                int index = panel.Children.IndexOf(placeholder);
                if (index >= 0)
                {
                    panel.Children.RemoveAt(index);
                    CopyGridPlacement(placeholder, zoomedPane);
                    panel.Children.Insert(index, zoomedPane);
                }
            }
            else if (placeholder.Parent is ContentPresenter presenter)
            {
                presenter.Content = zoomedPane;
            }
            else if (placeholder.Parent is ContentControl contentControl)
            {
                contentControl.Content = zoomedPane;
            }
            else
            {
                return false;
            }

            _paneZoomStateByTab.Remove(tabItem);
            _zoomedPaneIdByTab.Remove(tabItem);

            if (forTeardown)
            {
                // The structural half is all a teardown wants: the root is back in Content, so the
                // walk that follows can reach every pane. Making a doomed pane the active one and
                // queueing focus at it are the parts that outlive the tab.
                return true;
            }

            if (activate)
            {
                UpdateActivePane(zoomedPane);
                FocusPaneTerminal(zoomedPane, defer: true);
            }
            else
            {
                _activePaneByTab[tabItem] = zoomedPane;
            }

            UpdatePaneAutomationLabels();
            RefreshLayoutModelForTab(tabItem);
            // The other direction, and it matters just as much: the siblings
            // that come back on screen carry their own segments again, so a
            // light still lit for them would double-report.
            RefreshAgentObserveIndicator();

            if (publishEvent)
            {
                PublishPaneEvent(tabItem, zoomedPane, PaneAuditEventKind.ZoomToggled, "off");
            }

            return true;
        }

        private void TogglePaneZoomForCurrentTab()
        {
            if (!TryGetSelectedTab(out var tabItem)) return;

            if (_paneZoomStateByTab.ContainsKey(tabItem))
            {
                ExitPaneZoom(tabItem, publishEvent: true);
                return;
            }

            var pane = ResolvePaneForTab(tabItem);
            if (pane != null)
            {
                EnterPaneZoom(tabItem, pane, publishEvent: true);
            }
        }

        /// <summary>
        /// Encodes a key for broadcast to sibling panes in the tab.
        /// </summary>
        /// <remarks>
        /// Non-blocking review note (#277): this builds its own legacy-only encoding
        /// independent of <see cref="TerminalInputModeEncoder.EncodeKittyKey"/> and
        /// <see cref="TerminalView.HandleKeyDownCore"/>. Broadcast targets therefore always
        /// receive legacy sequences (e.g. bare "\r" for Enter, never "\x1b[13;2u" for
        /// Shift+Enter) regardless of whether a broadcast target's own <c>ModeState.KittyKeyboard</c>
        /// has the disambiguate tier pushed. This is deliberate scoping, not an oversight: the
        /// broadcast path only ever needs to reach TUIs that also work with plain legacy input,
        /// and duplicating the kitty-aware ordering here would put the same AltGr/kill-switch
        /// hazards behind a second, harder-to-audit code path. See the scope note in
        /// docs/vt_coverage_matrix.md for the matching matrix entry.
        /// </remarks>
        private bool TryMapBroadcastKey(KeyEventArgs e, TerminalBuffer? buffer, out string? sequence)
        {
            sequence = null;
            bool isAlt = (e.KeyModifiers & KeyModifiers.Alt) != 0;
            bool isShift = (e.KeyModifiers & KeyModifiers.Shift) != 0;

            if (isAlt) return false;

            switch (e.Key)
            {
                case Key.Enter: sequence = "\r"; return true;
                case Key.Back: sequence = "\x7f"; return true;
                case Key.Tab: sequence = isShift ? "\x1b[Z" : "\t"; return true;
                case Key.Escape: sequence = "\x1b"; return true;
            }

            // Modifiers go through for the same reason the focused pane's TerminalView passes them:
            // Ctrl+Left has to word-jump in every broadcast pane, not word-jump in the focused one
            // and move one character in its siblings, which would leave their command lines
            // diverged from that point on.
            sequence = TerminalInputModeEncoder.EncodeSpecialKey(e.Key, e.KeyModifiers, buffer?.Modes);
            if (sequence != null)
            {
                return true;
            }

            // Same legacy C0 table the focused pane's TerminalView sends (Ctrl+letters plus
            // Ctrl+\, Ctrl+], Ctrl+_ and the digit row), so a broadcast Ctrl+\ reaches every pane.
            sequence = TerminalInputModeEncoder.EncodeLegacyControlKey(e.Key, e.KeyModifiers);
            return sequence != null;
        }

        private void BroadcastKeyToSiblingPanes(KeyEventArgs e)
        {
            if (IsFocusOverlayVisible()) return;
            if (!TryGetSelectedTab(out var tabItem)) return;
            if (!_broadcastEnabledTabs.Contains(tabItem)) return;
            if (_currentPane == null) return;
            if (!TryMapBroadcastKey(e, _currentPane.Buffer, out var sequence) || string.IsNullOrEmpty(sequence)) return;

            foreach (var pane in EnumeratePanes(tabItem.Content as Control))
            {
                if (pane == _currentPane) continue;
                // Broadcast bytes never went through the receiving pane's key handling, so its
                // markless submission accumulator cannot model them.
                pane.NotifyExternalInputSent();
                pane.ScrollToInputLine();
                pane.Session?.SendInput(sequence);
            }
        }

        private void BroadcastTextToSiblingPanes(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (IsFocusOverlayVisible()) return;
            if (!TryGetSelectedTab(out var tabItem)) return;
            if (!_broadcastEnabledTabs.Contains(tabItem)) return;
            if (_currentPane == null) return;

            foreach (var pane in EnumeratePanes(tabItem.Content as Control))
            {
                if (pane == _currentPane) continue;
                pane.NotifyExternalInputSent();
                pane.ScrollToInputLine();
                pane.Session?.SendInput(text);
            }
        }

        private void ToggleBroadcastForCurrentTab()
        {
            if (!TryGetSelectedTab(out var tabItem)) return;

            bool enabled;
            if (_broadcastEnabledTabs.Contains(tabItem))
            {
                _broadcastEnabledTabs.Remove(tabItem);
                enabled = false;
            }
            else
            {
                _broadcastEnabledTabs.Add(tabItem);
                enabled = true;
            }

            UpdateBroadcastIndicator();
            RefreshLayoutModelForTab(tabItem);
            PublishPaneEvent(tabItem, ResolvePaneForTab(tabItem), PaneAuditEventKind.BroadcastToggled, enabled ? "on" : "off");
        }

        private void UpdateBroadcastIndicator()
        {
            if (TryGetSelectedTab(out var tabItem) && _broadcastEnabledTabs.Contains(tabItem))
            {
                Title = "Ntilde [Broadcast: Tab]";
            }
            else
            {
                Title = "Ntilde";
            }
        }

        /// <summary>Every pane in every tab, including a zoomed tab's stashed root.</summary>
        internal IReadOnlyList<TerminalPane> AllPanesForTest() => AllPanes();

        private List<TerminalPane> AllPanes() =>
            this.FindControl<TabControl>("Tabs")?.Items.OfType<TabItem>().SelectMany(PanesInTab).ToList() ?? [];

        /// <summary>
        /// Every pane of one tab. While the tab is pane-zoomed, the layout root is the stash that no longer
        /// holds the zoomed pane (it is the tab's content), so that pane is added back.
        /// </summary>
        private IEnumerable<TerminalPane> PanesInTab(TabItem tab)
        {
            foreach (var pane in EnumeratePanes(GetLayoutRootForTab(tab))) yield return pane;
            if (_paneZoomStateByTab.ContainsKey(tab) && tab.Content is TerminalPane zoomed) yield return zoomed;
        }

        private static bool IsSplitGrid(Grid grid)
        {
            int paneChildren = grid.Children.OfType<Control>().Count(c => c is not GridSplitter);
            return paneChildren >= 2;
        }

        private Grid? FindNearestSplitGrid(Control start)
        {
            Control? current = start;
            while (current != null)
            {
                if (current.Parent is Grid grid && IsSplitGrid(grid))
                {
                    return grid;
                }

                current = current.Parent as Control;
            }

            return null;
        }

        private static bool IsSplitterColumn(Grid grid, int column)
        {
            return grid.Children.OfType<GridSplitter>().Any(s => Grid.GetColumn(s) == column);
        }

        private static bool IsSplitterRow(Grid grid, int row)
        {
            return grid.Children.OfType<GridSplitter>().Any(s => Grid.GetRow(s) == row);
        }

        private void EqualizeCurrentSplit()
        {
            if (_currentPane == null) return;

            var splitGrid = FindNearestSplitGrid(_currentPane);
            if (splitGrid == null) return;

            EqualizeSplitGrid(splitGrid);
            InvalidateMeasure();
            InvalidateArrange();
            if (TryGetSelectedTab(out var selected))
            {
                RefreshLayoutModelForTab(selected);
                PublishPaneEvent(selected, _currentPane, PaneAuditEventKind.Equalized);
            }
        }

        private void EqualizeSplitGrid(Grid splitGrid)
        {
            if (!IsSplitGrid(splitGrid)) return;

            bool byColumns = splitGrid.ColumnDefinitions.Count > 1;
            if (byColumns)
            {
                for (int i = 0; i < splitGrid.ColumnDefinitions.Count; i++)
                {
                    if (IsSplitterColumn(splitGrid, i)) continue;
                    splitGrid.ColumnDefinitions[i].Width = new GridLength(1, GridUnitType.Star);
                }
            }
            else
            {
                for (int i = 0; i < splitGrid.RowDefinitions.Count; i++)
                {
                    if (IsSplitterRow(splitGrid, i)) continue;
                    splitGrid.RowDefinitions[i].Height = new GridLength(1, GridUnitType.Star);
                }
            }
        }

        private void WireSplitter(GridSplitter splitter, Grid ownerGrid)
        {
            splitter.Tag = ownerGrid;
            splitter.PointerEntered -= OnSplitterPointerEntered;
            splitter.PointerExited -= OnSplitterPointerExited;
            splitter.PointerPressed -= OnSplitterPointerPressed;
            splitter.PointerReleased -= OnSplitterPointerReleased;
            splitter.PointerCaptureLost -= OnSplitterPointerCaptureLost;
            splitter.DoubleTapped -= OnSplitterDoubleTapped;
            splitter.PointerEntered += OnSplitterPointerEntered;
            splitter.PointerExited += OnSplitterPointerExited;
            splitter.PointerPressed += OnSplitterPointerPressed;
            splitter.PointerReleased += OnSplitterPointerReleased;
            splitter.PointerCaptureLost += OnSplitterPointerCaptureLost;
            splitter.DoubleTapped += OnSplitterDoubleTapped;
        }

        private void OnSplitterPointerEntered(object? sender, PointerEventArgs e)
        {
            if (sender is GridSplitter splitter)
            {
                SetSplitterStateClass(splitter, SplitterHoverClass, true);
            }
        }

        private void OnSplitterPointerExited(object? sender, PointerEventArgs e)
        {
            if (sender is GridSplitter splitter && !splitter.Classes.Contains(SplitterDraggingClass))
            {
                SetSplitterStateClass(splitter, SplitterHoverClass, false);
            }
        }

        private void OnSplitterPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is GridSplitter splitter && e.GetCurrentPoint(splitter).Properties.IsLeftButtonPressed)
            {
                SetSplitterStateClass(splitter, SplitterHoverClass, true);
                SetSplitterStateClass(splitter, SplitterDraggingClass, true);
            }
        }

        private void OnSplitterPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (sender is GridSplitter splitter)
            {
                SetSplitterStateClass(splitter, SplitterDraggingClass, false);
                SetSplitterStateClass(splitter, SplitterHoverClass, splitter.IsPointerOver);
            }
        }

        private void OnSplitterPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            if (sender is GridSplitter splitter)
            {
                SetSplitterStateClass(splitter, SplitterDraggingClass, false);
                SetSplitterStateClass(splitter, SplitterHoverClass, splitter.IsPointerOver);
            }
        }

        private static void SetSplitterStateClass(GridSplitter splitter, string className, bool isActive)
        {
            if (isActive)
            {
                splitter.Classes.Add(className);
                return;
            }

            splitter.Classes.Remove(className);
        }

        private void OnSplitterDoubleTapped(object? sender, RoutedEventArgs e)
        {
            if (sender is GridSplitter splitter && splitter.Tag is Grid ownerGrid)
            {
                EqualizeSplitGrid(ownerGrid);
                InvalidateMeasure();
                InvalidateArrange();
                if (TryGetSelectedTab(out var tabItem))
                {
                    RefreshLayoutModelForTab(tabItem);
                    PublishPaneEvent(tabItem, _currentPane, PaneAuditEventKind.Equalized, "splitter-double-tap");
                }
                e.Handled = true;
            }
        }

        private static bool ReplacePaneInVisualTree(TerminalPane sourcePane, TerminalPane replacementPane)
        {
            switch (sourcePane.Parent)
            {
                case Grid grid:
                    CopyGridPlacement(sourcePane, replacementPane);
                    int gridIndex = grid.Children.IndexOf(sourcePane);
                    if (gridIndex < 0)
                    {
                        return false;
                    }

                    grid.Children.RemoveAt(gridIndex);
                    grid.Children.Insert(gridIndex, replacementPane);
                    return true;

                case ContentPresenter presenter:
                    presenter.Content = replacementPane;
                    return true;

                case TabItem tabItem:
                    tabItem.Content = replacementPane;
                    return true;

                case Panel panel:
                    CopyGridPlacement(sourcePane, replacementPane);
                    int panelIndex = panel.Children.IndexOf(sourcePane);
                    if (panelIndex < 0)
                    {
                        return false;
                    }

                    panel.Children.RemoveAt(panelIndex);
                    panel.Children.Insert(panelIndex, replacementPane);
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>Opens a tab and returns its pane - for a template-rule tab, the template's active pane - or null when
        /// none was created.
        /// <paramref name="select"/> false opens it in the background; <paramref name="leaveFocusMode"/> false is for
        /// callers that are not the user (the agent), which must never end focus mode. <paramref name="openedByAgent"/> marks
        /// the pane as the agent's (<see cref="TerminalPane.OpenedByAgent"/>): its SSH connection shows no dialogs (#610).</summary>
        TerminalPane? AddTab(TerminalProfile? profile = null, SshDiagnosticsLevel sshDiagnostics = SshDiagnosticsLevel.None,
            bool select = true, bool leaveFocusMode = true, bool openedByAgent = false)
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return null;
            if (leaveFocusMode) LeaveFocusModeForNavigation();

            // Fallback to default if null
            if (profile == null)
            {
                profile = _settings.Profiles.Find(p => p.Id == _settings.DefaultProfileId) ?? _settings.Profiles[0];
            }
            else
            {
                if (profile.Type == ConnectionType.SSH)
                {
                    // SSH profiles are store-backed and independent from TerminalSettings.Profiles.
                    TerminalProfile? sshProfile = _sshConnectionService.GetConnectionProfile(profile.Id);
                    if (sshProfile != null)
                    {
                        profile = sshProfile;
                    }
                }
                else
                {
                    // Refresh local profile from settings to pick up latest overrides.
                    var freshProfile = _settings.Profiles.Find(p => p.Id == profile.Id);
                    if (freshProfile != null) profile = freshProfile;
                }
            }

            // Substitute only a command written for another OS - the same predicate the restore
            // path uses, so opening a profile and restoring one agree about it. They did not before:
            // this asked whether the command existed, so a profile carrying inline arguments or a
            // quoted path was reset here while restore kept it.
            //
            // Copied before it is changed, exactly as SessionManager.ResolveProfileForThisPlatform
            // does. The local branch above hands back the instance living in _settings.Profiles, so
            // assigning to its Command edited the user's stored profile - and any later
            // _settings.Save() persisted that. Several ordinary actions save (font size, cursor
            // style, bell), so opening a /bin/bash profile once on Windows could rewrite it to
            // pwsh.exe with its arguments dropped, in the settings.json that travels back to the
            // Linux machine. That is the cross-machine corruption this whole change exists to stop.
            //
            // The SSH branch below mutates too, and does not need this: GetConnectionProfile returns
            // a freshly converted runtime profile, not the stored instance.
            //
            // One consequence worth knowing, since the copy makes it observable where it was a no-op
            // before: ApplySettingsRecursive re-binds a pane to the stored profile by id on any
            // settings re-apply, so after e.g. a font change pane.Profile.Command reads the original
            // foreign command again while the shell running is the substitute. Verified harmless -
            // nothing respawns from Profile.Command (Reconnect passes ShellCommand, and TerminalPane
            // never reads Profile.Command), and session capture writes the pane's actual command.
            if (profile.Type == ConnectionType.Local && ShellHelper.IsCommandForAnotherPlatform(profile.Command))
            {
                profile = profile.ShallowCopy();
                profile.Command = ShellHelper.GetDefaultShell();
                profile.Arguments = ""; // The old arguments belonged to the command just replaced.
            }

            // Construct command if it's an SSH connection
            if (profile.Type == ConnectionType.SSH)
            {
                profile.Command = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ssh.exe" : "ssh";
                profile.Arguments = string.Empty;
            }

            // A background open never applies a template rule. A template tab can hold several panes, and a pane
            // starts its shell on its first layout, which a background tab never gets: an agent's background spawn
            // starts only the pane it is handed back.
            if (select && TryApplyTemplateRuleForProfile(profile, out TerminalPane? templatePane))
            {
                return templatePane;
            }

            // Marked before the tab is added: the pane starts its session on its first layout, and reads the mark then.
            var pane = new TerminalPane(profile, sshDiagnostics) { OpenedByAgent = openedByAgent };
            AddTabWithPane(pane, profile.Name, select);
            return pane;
        }

        private void SplitPane(Avalonia.Layout.Orientation orientation)
        {
            if (_currentPane == null) return;
            if (TryGetSelectedTab(out var selectedTab) && _paneZoomStateByTab.ContainsKey(selectedTab))
            {
                ExitPaneZoom(selectedTab, publishEvent: true);
            }

            var originalPane = _currentPane;
            var parent = originalPane.Parent as Panel;
            var (minPaneWidth, minPaneHeight) = originalPane.GetMinimumPaneSize();

            // CAPTURE coordinates before we reset them for the new nested grid!
            int oldRow = Grid.GetRow(originalPane);
            int oldCol = Grid.GetColumn(originalPane);

            TerminalPane newPane;
            if (originalPane.Profile != null)
            {
                // Create a copy of the profile for the new split pane
                var profile = originalPane.Profile;
                newPane = new TerminalPane(profile, SshDiagnosticsLevel.None);
            }
            else
            {
                newPane = new TerminalPane(originalPane.ShellCommand);
            }

            WirePane(newPane);
            newPane.ApplySettings(_settings);
            if (TryGetSelectedTab(out var splitOwnerTab))
            {
                AgentHost.AgentSessionRegistry.Instance.SetTabAssociation(newPane.PaneId, GetPersistentTabId(splitOwnerTab));
            }
            newPane.MinWidth = Math.Max(newPane.MinWidth, minPaneWidth);
            newPane.MinHeight = Math.Max(newPane.MinHeight, minPaneHeight);
            originalPane.MinWidth = Math.Max(originalPane.MinWidth, minPaneWidth);
            originalPane.MinHeight = Math.Max(originalPane.MinHeight, minPaneHeight);
            var grid = new Grid { Background = Brushes.Transparent, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch };

            if (orientation == Avalonia.Layout.Orientation.Horizontal)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = minPaneWidth });
                grid.ColumnDefinitions.Add(new ColumnDefinition(3, GridUnitType.Pixel)); // 3px hit area
                grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = minPaneWidth });

                var splitter = new GridSplitter
                {
                    Width = 3,
                    ResizeDirection = GridResizeDirection.Columns,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                    Focusable = false
                };
                WireSplitter(splitter, grid);
                Grid.SetColumn(splitter, 1);
                grid.Children.Add(splitter);

                Grid.SetRow(originalPane, 0); Grid.SetColumn(originalPane, 0);
                Grid.SetRow(newPane, 0); Grid.SetColumn(newPane, 2);
            }
            else
            {
                grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star) { MinHeight = minPaneHeight });
                grid.RowDefinitions.Add(new RowDefinition(3, GridUnitType.Pixel)); // 3px hit area
                grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star) { MinHeight = minPaneHeight });

                var splitter = new GridSplitter
                {
                    Height = 3,
                    ResizeDirection = GridResizeDirection.Rows,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    Focusable = false
                };
                WireSplitter(splitter, grid);
                Grid.SetRow(splitter, 1);
                grid.Children.Add(splitter);

                Grid.SetRow(originalPane, 0); Grid.SetColumn(originalPane, 0);
                Grid.SetRow(newPane, 2); Grid.SetColumn(newPane, 0);
            }

            if (parent != null)
            {
                Grid.SetRow(grid, oldRow);
                Grid.SetColumn(grid, oldCol);
                int index = parent.Children.IndexOf(originalPane);
                parent.Children.RemoveAt(index);
                parent.Children.Insert(index, grid);
                grid.Children.Add(originalPane);
                grid.Children.Add(newPane);
            }
            else if (originalPane.Parent is ContentPresenter cp)
            {
                cp.Content = grid;
                grid.Children.Add(originalPane);
                grid.Children.Add(newPane);
            }
            else if (originalPane.Parent is TabItem tab)
            {
                tab.Content = grid;
                grid.Children.Add(originalPane);
                grid.Children.Add(newPane);
            }
            _currentPane = newPane;
            Dispatcher.UIThread.Post(() => { newPane.ActiveControl.Focus(); this.InvalidateMeasure(); this.InvalidateArrange(); }, DispatcherPriority.Loaded);
            UpdatePaneAutomationLabels();
            if (TryGetSelectedTab(out selectedTab))
            {
                RefreshLayoutModelForTab(selectedTab);
                PublishPaneEvent(selectedTab, newPane, PaneAuditEventKind.Split,
                    orientation == Avalonia.Layout.Orientation.Horizontal ? "vertical-divider" : "horizontal-divider");
            }
        }
    }
}
