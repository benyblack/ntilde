using Avalonia.Controls;
using Avalonia.Threading;
using Ntilde.Shell.Theming;
using Avalonia;
using Avalonia.Media;
using Avalonia.Layout;

namespace Ntilde
{
    /// <summary>
    /// How agent activity shows in the window: the tab strip's tiers, the observe indicator, the activity
    /// journal, and the window half of the attention machines' focus signal.
    /// </summary>
    public partial class MainWindow
    {
        // The window half of the agent attention machines' focus signal; see
        // PushAgentWindowVisibility. Two fields because the two inputs arrive
        // from different places (Activated/Deactivated vs. the WindowState
        // property) and neither can be read reliably from inside the other's
        // notification: Avalonia raises Activated *before* it sets IsActive.
        private bool _agentWindowActivated;
        private bool _agentWindowVisible;

        /// <summary>
        /// Whether an attention tier is loud enough for the tab strip under the
        /// given rollup policy. A write always shows: the setting only governs
        /// whether reads do.
        /// </summary>
        internal static bool ShouldShowTierInTabStrip(string? rollupPolicy, AgentHost.AgentAttentionTier tier)
        {
            if (tier == AgentHost.AgentAttentionTier.Wrote) return true;
            if (tier == AgentHost.AgentAttentionTier.Idle) return false;
            return string.Equals(rollupPolicy, "All", StringComparison.Ordinal);
        }

        /// <summary>
        /// Decides the application-level agent light from its three inputs. Pure
        /// so it can be tested without a window and without touching the
        /// process-wide <see cref="AgentHost.AgentHostService.Instance"/>.
        ///
        /// Visible exactly while observe is enabled. "Active" (the watched
        /// styling) covers the two cases this is the only correctly-scoped
        /// surface for: an in-flight waitForEvents long poll, which names no
        /// pane; and a read landing on a pane whose status bar the user cannot
        /// currently see, which would otherwise be invisible everywhere.
        ///
        /// The condition is deliberately per-pane rather than "act is off".
        /// A pane carries a bar only when it is *actable*, and act being on is
        /// not sufficient for that: an SSH pane whose profile lacks
        /// AllowAgentAccess is never actable, so with act on it has no bar, no
        /// tab glyph under the default WritesOnly rollup, and — under the old
        /// global-toggle condition — no window light either. Reading exactly
        /// the panes the user deliberately excluded from act produced no signal
        /// anywhere. Keying on "the watched pane has no visible bar" subsumes
        /// the act-off case: with act off no pane is actable, so every watched
        /// pane is unmarked.
        ///
        /// See <see cref="IsPaneReadInvisibleWithoutWindowLight"/> for the
        /// per-pane half of that decision.
        ///
        /// A read of a windowless session (Phase 5 §3: a daemon session no pane
        /// shows) is the third case: there is no pane to mark at all, so
        /// <paramref name="windowlessWatched"/> (the service's decaying
        /// <see cref="AgentHost.AgentHostService.WindowlessWatched"/>) lights it too.
        /// </summary>
        internal static (bool Visible, bool Active) ComputeObserveIndicatorState(
            bool observeRunning, bool polling, bool anyUnmarkedPaneWatched, bool windowlessWatched)
        {
            if (!observeRunning) return (false, false);
            return (true, polling || anyUnmarkedPaneWatched || windowlessWatched);
        }

        /// <summary>
        /// Whether a read of this pane would be invisible unless the window
        /// light reports it — i.e. the pane has no agent status-bar segment
        /// the user can *currently see*. Pure, so the rule is testable without
        /// a window.
        ///
        /// Three cases say "invisible":
        ///
        /// 1. The pane is not actable, so it carries no agent segment at all.
        /// 2. The pane is actable and therefore has a segment, but it lives in
        ///    a tab that is not selected. A non-selected tab's content is not
        ///    rendered, so that segment is off screen. This is the common case,
        ///    not an edge one — act on plus more than one tab is enough — and
        ///    it fails in the same inverted direction as the SSH-allowlist hole
        ///    this predicate already covers: the more permission a pane is
        ///    granted, the *less* visible reading it becomes. The tier decays
        ///    in ~3 s, so switching to that tab a moment later shows nothing
        ///    either.
        /// 3. The pane has no tab association yet. A registration is created in
        ///    TerminalPane.SetupCommon and only associated with a tab later by
        ///    MainWindow (SetTabAssociation), so this window is real, if brief.
        ///    Unassociated means "cannot be proven on screen", and the whole
        ///    point of this light is that a read is never silent, so the
        ///    unprovable case reports rather than hides. The cost of being
        ///    wrong here is at worst a redundant light next to a visible
        ///    segment; the cost the other way is a silent read.
        /// 4. The pane is actable and in the selected tab, but that tab is
        ///    pane-zoomed onto a *different* pane. Zoom replaces the tab
        ///    content with the single zoomed pane (EnterPaneZoom), so the
        ///    selected-tab test above stops meaning "rendered": every
        ///    non-zoomed sibling still matches the selected tab id while
        ///    showing its segment to nobody. Without this the failure is the
        ///    familiar silent one - a read of the hidden sibling has no bar
        ///    (unrendered), no tab glyph (reads reach the tab strip only under
        ///    the non-default "All" rollup) and no window light, and the
        ///    Watched tier decays in ~3 s, so un-zooming a moment later shows
        ///    nothing either.
        ///
        /// A pane whose segment *is* on screen (actable, in the selected tab,
        /// and either not zoomed away or the zoomed pane itself) returns false:
        /// it already says "agent reading" itself, and lighting the window too
        /// would double-report the same event.
        /// </summary>
        internal static bool IsPaneReadInvisibleWithoutWindowLight(
            bool isAgentActable, Guid? paneTabId, Guid? selectedTabId, bool paneHiddenByZoom)
            => IsPaneReadInvisibleWithoutWindowLight(
                isAgentActable, paneTabId, selectedTabId, paneHiddenByZoom, focusMode: false);

        /// <summary>
        /// As above, with focus mode as a fifth input: focus mode hides every pane status bar, so no
        /// pane's segment is on screen and every read is the window light's to report.
        /// </summary>
        internal static bool IsPaneReadInvisibleWithoutWindowLight(
            bool isAgentActable, Guid? paneTabId, Guid? selectedTabId, bool paneHiddenByZoom, bool focusMode)
        {
            if (focusMode) return true;
            if (!isAgentActable) return true;
            if (!paneTabId.HasValue) return true;
            if (paneTabId.Value != selectedTabId) return true;
            return paneHiddenByZoom;
        }

        /// <summary>
        /// The window-state half of the decision: resolves which tab's content
        /// is actually rendered, and which pane inside it is, then applies the
        /// pure rule above to one registration.
        ///
        /// The selected tab id is resolved the same way every other tab-id
        /// consumer in this file does it, so a pane's stored TabId and this are
        /// comparable by construction. Zoom is read from the same two maps
        /// EnterPaneZoom/ExitPaneZoom maintain.
        ///
        /// A separate method rather than an inline lambda so the zoom wiring
        /// can be tested: RefreshAgentObserveIndicator's own visible output is
        /// gated on AgentHostService.Instance.IsRunning, which a test must not
        /// turn on (it would start a real IPC endpoint in the shared test
        /// process).
        /// </summary>
        internal bool IsPaneReadInvisibleWithoutWindowLight(AgentHost.AgentSessionRegistration registration)
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            var selectedTab = tabs?.SelectedItem as TabItem;
            Guid? selectedTabId = selectedTab != null ? GetPersistentTabId(selectedTab) : null;

            // Pane zoom breaks the "selected tab == rendered" equivalence:
            // EnterPaneZoom swaps the tab content for the single zoomed pane,
            // so the split siblings are off screen while still carrying the
            // selected tab id.
            //
            // zoomedPaneId comes back null only if the two zoom maps ever
            // disagreed, which they are written not to; if they did, every pane
            // in the tab compares unequal and is treated as hidden. That is the
            // direction this feature must fail in - a redundant light, never a
            // silent read.
            bool selectedTabIsZoomed = selectedTab != null && IsPaneZoomActiveForTab(selectedTab);
            Guid? zoomedPaneId = selectedTab != null ? GetZoomedPaneIdForTab(selectedTab) : null;

            return IsPaneReadInvisibleWithoutWindowLight(
                registration.IsAgentActable,
                registration.TabId,
                selectedTabId,
                paneHiddenByZoom: selectedTabIsZoomed && registration.PaneId != zoomedPaneId,
                focusMode: _isFocusMode);
        }

        /// <summary>Applies <see cref="ComputeObserveIndicatorState"/> to the chrome. UI thread.</summary>
        internal void RefreshAgentObserveIndicator()
        {
            var service = AgentHost.AgentHostService.Instance;

            // "Unmarked" = the pane shows no agent segment the user can see
            // right now, so this light is the only place its read can appear.
            bool anyUnmarkedPaneWatched = AgentHost.AgentSessionRegistry.Instance.GetRegistrations()
                .Any(r => IsPaneReadInvisibleWithoutWindowLight(r)
                    && r.AttentionMachine.Snapshot().Tier == AgentHost.AgentAttentionTier.Watched);

            var (visible, active) = ComputeObserveIndicatorState(
                service.IsRunning, service.InFlightPollCount > 0, anyUnmarkedPaneWatched, service.WindowlessWatched);

            ApplyObserveIndicatorVisuals(visible, active, CollectUnseenWriteMarks());
        }

        /// <summary>
        /// Paints the light's controls from the state <see cref="RefreshAgentObserveIndicator"/> computed: visibility,
        /// the token fill and ring of <see cref="ObserveLightAppearance"/>, and the tooltip. Separate from the refresh so
        /// it can be tested without starting the agent host service.
        /// </summary>
        internal void ApplyObserveIndicatorVisuals(bool visible, bool active, IReadOnlyList<FocusWriteMark> marks)
        {
            var indicator = this.FindControl<Button>("AgentObserveIndicator");
            var dot = this.FindControl<Avalonia.Controls.Shapes.Ellipse>("AgentObserveIndicatorDot");
            if (indicator == null || dot == null) return;

            indicator.IsVisible = visible;
            if (!visible) return;
            var (fillKey, ringKey) = ObserveLightAppearance(active, marks.Count > 0);
            dot.Fill = ThemeBrush(fillKey);
            // A write and a read at once: the wrote fill with a watched ring, so neither hides the other.
            dot.Stroke = ringKey is null ? null : ThemeBrush(ringKey);
            dot.StrokeThickness = ringKey is null ? 0 : 1.5;
            // BuildFocusLightTooltip with no marks is the XAML default text, so this restores it outside focus mode.
            ToolTip.SetTip(indicator, BuildFocusLightTooltip(marks, active));
        }

        // Raised on an IPC thread when the in-flight poll count leaves or
        // returns to zero, and on an IPC or the sweep's timer thread when a
        // windowless read lights or decays. Posted through the window's own
        // dispatcher, never the Dispatcher.UIThread static: an off-thread read
        // of that static is what poisoned headless test runs (#81).
        private void OnAgentObserveActivityChanged()
            => Dispatcher.Post(RefreshAgentObserveIndicator);

        /// <summary>
        /// Recomputes each tab's agent marker from the loudest attention tier
        /// among its panes, filtered by the rollup setting, then refreshes the
        /// labels. Tiers are stored on tab state rather than patched onto
        /// labels because UpdateTabVisuals rebuilds every label from scratch.
        /// </summary>
        internal void RefreshTabAgentAttention()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            var loudestByTab = new Dictionary<Guid, AgentHost.AgentAttentionTier>();
            foreach (var registration in AgentHost.AgentSessionRegistry.Instance.GetRegistrations())
            {
                var tabId = registration.TabId;
                if (!tabId.HasValue) continue;
                var tier = registration.AttentionMachine.Snapshot().Tier;
                if (!loudestByTab.TryGetValue(tabId.Value, out var current) || tier > current)
                {
                    loudestByTab[tabId.Value] = tier;
                }
            }

            foreach (TabItem tab in tabs.Items.Cast<TabItem>())
            {
                var state = GetOrCreateTabState(tab);
                var tier = loudestByTab.TryGetValue(GetPersistentTabId(tab), out var found)
                    ? found
                    : AgentHost.AgentAttentionTier.Idle;

                state.AgentTier = ShouldShowTierInTabStrip(_settings.AgentIndicatorTabRollup, tier)
                    ? tier
                    : AgentHost.AgentAttentionTier.Idle;
            }

            UpdateTabVisuals();
            RefreshAgentObserveIndicator();
        }

        /// <summary>
        /// Live-applies the agent-host settings after the Settings dialog is
        /// saved — the observe endpoint itself, the A4 replay-export and A5
        /// screenshot sub-gates, the A3 act gate — and then re-renders every
        /// chrome surface that reads them.
        ///
        /// Both refreshes are mandatory, and the tab one is easy to miss:
        /// <see cref="RefreshTabAgentAttention"/> bakes the rollup policy into
        /// each tab's stored tier, so a runtime change from "All" to
        /// "WritesOnly" leaves an already-displayed read glyph on the tab until
        /// the next attention event on that pane — which may never come. Named
        /// and factored out rather than inlined at the call site so that
        /// invariant is testable without driving the Settings dialog.
        /// </summary>
        internal void ApplyAgentHostSettingsLive()
        {
            AgentHost.AgentHostService.Instance.ReplayExportEnabled = _settings.AgentReplayExportEnabled;
            AgentHost.AgentHostService.Instance.ActEnabled = _settings.AgentAccessActEnabled;
            AgentHost.AgentHostService.Instance.SetSshProfileAllowlist(IsSshProfileAgentAllowed);
            AgentHost.AgentHostService.Instance.SetActionExecutor(this);
            // After ApplySessionPersistenceSetting, which builds the hosts when persistence was just turned on.
            AgentHost.AgentHostService.Instance.SetWindowlessSource(WindowlessSessions);
            AgentHost.AgentHostService.Instance.Apply(_settings.AgentAccessObserveEnabled);
            // RefreshTabAgentAttention already ends by calling
            // RefreshAgentObserveIndicator, so this covers both surfaces.
            RefreshTabAgentAttention();
        }

        /// <summary>
        /// Pushes "the user can actually see this window" to every live
        /// registration, which ANDs it with the pane's own selected-ness to
        /// produce the attention machines' focus signal.
        ///
        /// Focus is what retires the sticky "agent typed" mark, and it is
        /// supposed to mean "the user has plausibly seen it". IsActivePane
        /// alone does not: it means "selected inside the app" and stays true
        /// while Ntilde is minimized or behind another application. An
        /// agent typing into that pane would then have its mark retired by the
        /// periodic tick <see cref="AgentHost.AgentAttentionMachine.WriteFloorSeconds"/>
        /// later, with nobody looking — the one signal built to survive until
        /// seen, disappearing in exactly the scenario it exists for.
        ///
        /// This has to be a push of its own rather than a term inside
        /// <c>UpdateSnapshot</c>: that runs on pane-level changes (title,
        /// profile, selection), and a window losing focus is not one, so
        /// nothing would ever re-evaluate the AND. Only the *window* half is
        /// re-pushed here; each registration keeps its own pane half, so this
        /// does not have to know or recompute which pane is selected where.
        ///
        /// Deduplicated twice — once here on the composed value, once in
        /// <see cref="AgentHost.AgentSessionRegistration.NoteWindowVisibilityChanged"/>
        /// — so the WindowState property notification (which fires for
        /// unrelated reasons) cannot turn into a push storm.
        /// </summary>
        private void PushAgentWindowVisibility()
        {
            bool visible = _agentWindowActivated && this.WindowState != WindowState.Minimized;
            if (visible == _agentWindowVisible) return;
            _agentWindowVisible = visible;

            foreach (var registration in AgentHost.AgentSessionRegistry.Instance.GetRegistrations())
            {
                registration.NoteWindowVisibilityChanged(visible);
            }
        }

        /// <summary>
        /// Records window activation and re-pushes. Separate from
        /// <see cref="PushAgentWindowVisibility"/> because Avalonia raises
        /// <c>Activated</c> before it assigns <c>IsActive</c>, so the handler
        /// cannot recompute activation from the property.
        /// </summary>
        private void SetAgentWindowActivated(bool activated)
        {
            _agentWindowActivated = activated;
            PushAgentWindowVisibility();
        }

        /// <summary>
        /// Minimizing is the other way the window stops being visible while the
        /// selected pane stays selected. On Windows it usually deactivates too,
        /// but that is a platform courtesy, not a guarantee, so the state is
        /// watched directly.
        /// </summary>
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == WindowStateProperty)
            {
                PushAgentWindowVisibility();
            }
        }

        /// <summary>Test seam for the window-visibility push, which no headless test can raise for real.</summary>
        internal void SetAgentWindowActivatedForTesting(bool activated) => SetAgentWindowActivated(activated);

        private void OnAgentSessionRegisteredForAttention(AgentHost.AgentSessionRegistration registration)
        {
            registration.AttentionMachine.Changed += OnAgentAttentionChangedForTabs;

            // A pane can be born while the window is not front — session
            // restore runs before the window is activated, and a split can be
            // made by an agent while the user is in another app. Seed the
            // window half now so the registration's optimistic default never
            // survives into a live window.
            registration.NoteWindowVisibilityChanged(_agentWindowVisible);
        }

        private void OnAgentSessionUnregisteredForAttention(AgentHost.AgentSessionRegistration registration)
        {
            registration.AttentionMachine.Changed -= OnAgentAttentionChangedForTabs;
            Dispatcher.UIThread.Post(RefreshTabAgentAttention);
        }

        // Raised on the endpoint's IPC or timer thread; hop before touching tabs.
        private void OnAgentAttentionChangedForTabs(AgentHost.AgentAttentionSnapshot _)
            => Dispatcher.UIThread.Post(RefreshTabAgentAttention);

        /// <summary>
        /// One line of the Agent Activity dialog. A windowless session's id is its mux session id, so it is called a
        /// session, never a pane; a run of identical reads the journal folded says how many it stands for ("×N"). Pure,
        /// so its wording is testable without a window.
        /// </summary>
        internal static string DescribeAgentActivity(AgentHost.AgentActivityEntry e)
        {
            string when = e.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            string outcome = e.Outcome == "ok" ? "ok" : $"denied: {e.Outcome}";
            string times = e.Count > 1 ? $" ×{e.Count}" : string.Empty;
            string id = e.PaneId is { } paneId ? $" · {(e.Windowless ? "session" : "pane")} {paneId}" : string.Empty;
            return $"{when}  {e.Method}{times}  [{outcome}]  {e.Target}{id}";
        }

        // A3: visible agent activity journal ("nothing is silent"). Read-only
        // snapshot of recent acting attempts (allowed and denied) and reads of
        // windowless sessions (Phase 5 ruling R5), newest first,
        // with a Refresh button. The journal data layer lives in
        // AgentActivityJournal; this is its window.
        internal async Task ShowAgentActivityJournalAsync()
        {
            var dialog = CreateThemedDialogWindow("Agent activity", 720, 460, canResize: true);

            var list = new ItemsControl
            {
                // Bind to the data snapshot with a template; don't materialize
                // controls as items (bypasses recycling, leaks on refresh).
                ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<AgentHost.AgentActivityEntry>((e, _) =>
                    new TextBlock
                    {
                        Text = DescribeAgentActivity(e),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 2, 0, 2),
                    }),
            };
            var scroll = new ScrollViewer
            {
                Content = list,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            };

            var empty = new TextBlock
            {
                Text = "No agent activity recorded yet. Actions and windowless reads by AI agents (typing, opening or closing sessions, reading a session no window shows) appear here \u2014 including attempts that were denied.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
            };

            void Refresh()
            {
                var entries = AgentHost.AgentActivityJournal.Instance.Snapshot();
                if (entries.Count == 0)
                {
                    list.ItemsSource = null;
                    scroll.IsVisible = false;
                    empty.IsVisible = true;
                    return;
                }

                empty.IsVisible = false;
                scroll.IsVisible = true;
                list.ItemsSource = entries; // data snapshot; the ItemTemplate renders each row
            }

            var refreshButton = new Button { Content = "Refresh", Width = 92 };
            refreshButton.Click += (_, __) => Refresh();
            var closeButton = new Button { Content = "Close", Width = 92 };
            closeButton.Click += (_, __) => dialog.Close();

            Refresh();

            dialog.Content = new Border
            {
                Padding = new Thickness(16),
                Child = new DockPanel
                {
                    LastChildFill = true,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Recent actions and windowless reads by AI agents, including attempts that were denied. Acting needs 'Agent access (act)'; a windowless read is a read of a session no window shows. Newest first; the list is in-memory and bounded.",
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 0, 0, 12),
                            [DockPanel.DockProperty] = Dock.Top,
                        },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Margin = new Thickness(0, 12, 0, 0),
                            [DockPanel.DockProperty] = Dock.Bottom,
                            Children = { refreshButton, closeButton },
                        },
                        new Panel { Children = { scroll, empty } },
                    }
                }
            };

            await dialog.ShowDialog(this);
        }
    }
}
