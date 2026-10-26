using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Avalonia.Controls.Presenters;
using Ntilde.Shell;
using Ntilde.Shell.Theming;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Layout;
using Ntilde.Shell.TitleBar;

namespace Ntilde
{
    /// <summary>
    /// The tab strip's layout: orientation, the caption-button reserve, the vertical strip's resize grip, the
    /// header viewport, the overflow indicators and the tab list menu.
    /// </summary>
    public partial class MainWindow
    {
        // Reused only when PopulateTabListMenu's anchor is NOT the dedicated open_tab_list button
        // (i.e. that action is Overflow or Hidden, see PopulateTabListMenu's anchor fallback chain).
        // Neither of the two other possible anchors is safe to cache the flyout on directly: the
        // overflow button already owns a different MenuFlyout for its own menu, and the
        // TitleBarItemsHost StackPanel fallback has no Flyout property to hold one at all.
        private MenuFlyout? _tabListFallbackFlyout;
        // Dedicated flyout for the vertical overflow pill (PART_TabOverflowPill): same
        // keep-alive rationale as _tabListFallbackFlyout - the pill is a Button that must
        // NOT carry this as its own Flyout (Button auto-opens an empty flyout on click
        // before the lazy populate could fill it), so MainWindow owns the instance and
        // PopulateTabListMenu(anchorOverride: pill) fills + shows it on demand.
        private MenuFlyout? _tabOverflowPillFlyout;
        internal const double MinimumTabHeaderRightReserve = 440;
        internal const double MacOsTrafficLightReserve = 92;
        internal const double TabHeaderViewportPadding = 16;

        /// <summary>
        /// Breathing room between our last title bar button and the first caption button, and the
        /// whole right reserve when there are no drawn caption buttons to clear. Matches the value
        /// the macOS branch in the constructor has always used.
        /// </summary>
        internal const double CaptionReserveGutter = 8;

        /// <summary>
        /// x:Name of the caption-button strip in NtildeWindowDecorationsTheme (App.axaml). Ours, not
        /// Avalonia's - though the default theme uses the same name, which is where ours was copied
        /// from.
        /// </summary>
        private const string CaptionButtonStripName = "PART_OverlayPanel";

        /// <summary>The caption strip <see cref="WatchCaptionButtonStrip"/> is hooked to, so it hooks once.</summary>
        private Control? _watchedCaptionButtonStrip;

        // True for the duration of a sidebar grip drag (PointerPressed on the grip through its
        // PointerReleased/PointerCaptureLost). UpdateTabHeaderViewport - invoked continuously
        // during a drag from activity-driven paths (QueueTabVisualRefresh on pane output/bell,
        // the 1s tab-status timer) - must not stomp scrollViewer.Width with the stale persisted
        // setting value while this is true, or a live drag gets silently reverted mid-gesture.
        private bool _isTabStripGripDragging;

        /// <summary>Test-only seam: simulates the mid-drag state without needing real pointer
        /// event simulation in a headless test host.</summary>
        internal bool IsTabStripGripDraggingForTest
        {
            get => _isTabStripGripDragging;
            set => _isTabStripGripDragging = value;
        }
        // Estimated height of one vertical tab row: the fallback CountHiddenTabs uses for
        // headers not yet measured (Bounds.Height 0 before the first layout pass) when the
        // vertical overflow pill counts hidden rows. Matches the vertical TabItem MinHeight
        // set in MainWindow.axaml's styles.
        private const double DefaultVerticalTabRowHeight = 44;
        // Tag marking a template part whose one-time wiring has already happened (resize
        // grip, overflow pill), so repeated ApplyTabLayout passes cannot double-subscribe.
        private const string TemplatePartWiredTag = "wired";

        private ItemsPresenter? FindTabItemsPresenter()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return null;

            return tabs.GetVisualDescendants()
                .OfType<ItemsPresenter>()
                .FirstOrDefault(p => p.Name == "PART_ItemsPresenter");
        }

        private ScrollViewer? FindTabHeaderScrollViewer()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return null;

            return tabs.GetVisualDescendants()
                .OfType<ScrollViewer>()
                .FirstOrDefault(s => s.Name == "PART_TabHeaderScrollViewer");
        }

        /// <summary>
        /// How much of the title bar's right edge our own buttons have to keep clear so they do not
        /// sit under the drawn caption buttons.
        ///
        /// This used to be the literal 140 in MainWindow.axaml's <c>Margin="0,4,140,0"</c>: three
        /// 45px buttons plus their 2px spacing plus the 1px border margin. That number is only right
        /// when all three are actually there, and how many are there is not ours to decide - the
        /// theme hides minimize and maximize on <c>:not(:has-minimize)</c> / <c>:not(:has-maximize)</c>,
        /// and X11 sets neither pseudoclass unless the window manager advertises the action. On a
        /// tiling compositor (Hyprland, Sway) only close survives, so ~92px of the reserve was dead
        /// space between our last button and the ✕.
        ///
        /// So measure the strip instead of predicting it. <paramref name="captionStripLeft"/> and
        /// <paramref name="windowWidth"/> are in the same coordinate space, and the gap between them
        /// is the whole reserve: the strip itself plus whatever frame inset sits to its right.
        /// </summary>
        /// <param name="windowWidth">Width of the window, in the coordinate space the caption strip was translated into.</param>
        /// <param name="captionStripLeft">Left edge of the caption strip, in that same space.</param>
        /// <param name="captionStripWidth">
        /// Width of the caption strip. Zero or negative means there is nothing to clear - no drawn
        /// decorations on this platform, or the strip is collapsed (the theme hides it in fullscreen)
        /// - and the reserve collapses to the gutter. <paramref name="captionStripLeft"/> is not
        /// meaningful in that case, so it is not read.
        /// </param>
        internal static double ComputeCaptionButtonReserve(
            double windowWidth,
            double captionStripLeft,
            double captionStripWidth,
            double gutter = CaptionReserveGutter)
        {
            if (captionStripWidth <= 0 || double.IsNaN(captionStripWidth))
            {
                return gutter;
            }

            if (double.IsNaN(windowWidth) || double.IsNaN(captionStripLeft))
            {
                return gutter;
            }

            double reserve = Math.Ceiling(windowWidth - captionStripLeft + gutter);

            // Clamped rather than trusted. A reserve wider than half the window would push our own
            // buttons somewhere absurd, and the inputs come from a live layout pass that can be
            // read mid-transition (a window state change resizes the frame and the strip on
            // different passes). The floor matters for the same reason: a strip briefly measured as
            // hanging past the right edge would otherwise yield a negative margin, which Avalonia
            // honours by letting our buttons overhang the window.
            double ceiling = Math.Max(gutter, windowWidth / 2);
            return Math.Clamp(reserve, gutter, ceiling);
        }
        /// <summary>
        /// Finds the drawn caption-button strip, or null when this window has none.
        ///
        /// It is deliberately NOT a visual descendant of this window: TopLevelHost owns the
        /// decorations and the Window is its child, so the strip is a SIBLING of our whole content
        /// tree and <c>this.GetVisualDescendants()</c> never sees it (verified against 12.0.4 -
        /// the window's own tree ends at PART_ContentPresenter). Hence the hop up to the visual
        /// parent first.
        ///
        /// Returns null in three different situations that all want the same answer - a bare gutter:
        /// a platform drawing native chrome instead (macOS traffic lights), decorations disabled
        /// altogether, and the strip not built yet. The third is why callers must not treat null as
        /// settled while <see cref="Window.IsExtendedIntoWindowDecorations"/> is true.
        /// </summary>
        private Control? FindCaptionButtonStrip()
        {
            return this.GetVisualParent()?
                .GetVisualDescendants()
                .OfType<Control>()
                .FirstOrDefault(c => c.Name == CaptionButtonStripName);
        }

        /// <summary>
        /// Re-measures the drawn caption buttons and reserves exactly their width on the right of
        /// our own title bar strip, replacing the hardcoded 140px the XAML used to carry. See
        /// <see cref="ComputeCaptionButtonReserve"/> for why a constant cannot be right.
        ///
        /// macOS is left alone on purpose. Its caption lives on the LEFT, the constructor already
        /// collapses the right reserve to <see cref="CaptionReserveGutter"/> for it, and that
        /// behaviour is known-good on a platform this change could not be tested on. If macOS turns
        /// out to build a drawn strip too, this measurement would produce the same answer and the
        /// special case can go - but that is a claim to verify there, not to assume here.
        /// </summary>
        private void UpdateCaptionButtonReserve()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return;
            }

            var titleBar = this.FindControl<Grid>("TitleBar");
            if (titleBar == null)
            {
                return;
            }

            var strip = FindCaptionButtonStrip();
            if (strip == null)
            {
                // Extended into the decorations means a strip is coming, so this is "too early",
                // not "there is none". Collapsing the reserve now would park our buttons under the
                // caption buttons for as long as it took some unrelated event to recompute; the
                // triggers wired in WireCaptionButtonReserve run this again once the strip exists.
                if (IsExtendedIntoWindowDecorations)
                {
                    return;
                }

                ApplyCaptionButtonReserve(titleBar, CaptionReserveGutter);
                return;
            }

            WatchCaptionButtonStrip(strip);

            // Translated rather than compared raw: the strip is in TopLevelHost's space and our
            // title bar is in the window's, and with drawn decorations those origins differ by the
            // shadow and frame inset. Null when the two are not connected in the visual tree, which
            // is transient - leave the current reserve and wait for the next trigger.
            var left = strip.TranslatePoint(default, this)?.X;
            if (left == null)
            {
                return;
            }

            ApplyCaptionButtonReserve(
                titleBar,
                ComputeCaptionButtonReserve(Bounds.Width, left.Value, strip.Bounds.Width));
        }

        /// <summary>
        /// Writes the reserve into the title bar's right margin, and only then tells the tab header
        /// to re-measure - <see cref="GetTabHeaderViewportMargin"/> reads that margin, so the order
        /// matters. No-ops on an unchanged value so the SizeChanged and Bounds triggers cannot ping
        /// -pong: setting Margin relayouts the bar, which fires SizeChanged, which lands back here.
        /// </summary>
        private void ApplyCaptionButtonReserve(Grid titleBar, double reserve)
        {
            var current = titleBar.Margin;
            if (Math.Abs(current.Right - reserve) < 0.5)
            {
                return;
            }

            titleBar.Margin = new Thickness(current.Left, current.Top, reserve, current.Bottom);

            // Logged because this is the number a "my buttons overlap the caption buttons" report on
            // some untested window manager turns on, and it is otherwise invisible. Only fires when
            // the value actually changes, so it is a handful of lines per session, not per layout.
            AppLogger.Log($"[TitleBar] caption reserve {current.Right:0.#} -> {reserve:0.#}");
            Dispatcher.UIThread.Post(UpdateTabHeaderViewport, DispatcherPriority.Background);

            // The focus strip's title margin is derived from this right margin.
            if (_isFocusMode) UpdateFocusStrip();
        }

        /// <summary>
        /// Keeps the reserve correct after the first measurement. The strip's width is not fixed for
        /// the life of the window: the theme collapses the whole panel in fullscreen and the caption
        /// set can change with window state, so its Bounds is the authoritative signal. Subscribed
        /// once and only once - this runs from a recompute that is itself triggered by layout, so an
        /// unguarded subscribe would add a handler per pass.
        /// </summary>
        private void WatchCaptionButtonStrip(Control strip)
        {
            if (ReferenceEquals(_watchedCaptionButtonStrip, strip))
            {
                return;
            }

            if (_watchedCaptionButtonStrip != null)
            {
                _watchedCaptionButtonStrip.PropertyChanged -= OnCaptionButtonStripPropertyChanged;
            }

            _watchedCaptionButtonStrip = strip;
            strip.PropertyChanged += OnCaptionButtonStripPropertyChanged;
        }

        /// <summary>
        /// The strip's own Bounds is the authoritative width signal, so only that property is acted
        /// on - the panel raises plenty of others. Posted rather than handled inline because this
        /// fires from inside a layout pass, and <see cref="UpdateCaptionButtonReserve"/> reads
        /// Bounds and writes a Margin.
        /// </summary>
        private void OnCaptionButtonStripPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == BoundsProperty)
            {
                Dispatcher.UIThread.Post(UpdateCaptionButtonReserve, DispatcherPriority.Background);
            }
        }

        /// <summary>
        /// Triggers for <see cref="UpdateCaptionButtonReserve"/>. Called from the constructor.
        ///
        /// Opened is the earliest point the platform window - and therefore the decorations - exists
        /// at all, and even then the strip is built during a layout pass, so the recompute is posted
        /// at Background priority for the same reason RebuildTitleBar posts: Background (-2) drains
        /// after every layout/render priority Avalonia schedules its own passes at. SizeChanged
        /// covers the case where Opened still ran too early, since the first real arrange resizes us.
        /// </summary>
        private void WireCaptionButtonReserve()
        {
            void Recompute() =>
                Dispatcher.UIThread.Post(UpdateCaptionButtonReserve, DispatcherPriority.Background);

            Opened += (_, _) => Recompute();
            this.SizeChanged += (_, _) => Recompute();

            // WindowState changes the caption set on platforms that offer maximize/restore, and
            // fullscreen collapses the strip entirely. IsExtendedIntoWindowDecorations flips once,
            // late, when a platform builds drawn decorations - the transition that takes
            // FindCaptionButtonStrip from "nothing yet" to a real strip.
            this.PropertyChanged += (_, e) =>
            {
                if (e.Property == WindowStateProperty || e.Property == IsExtendedIntoWindowDecorationsProperty)
                {
                    Recompute();
                }
            };
        }
        internal static Thickness GetTabHeaderViewportMargin(
            bool isMacOs,
            double titleBarWidth,
            double titleBarRightMargin,
            double minimumRightReserve = MinimumTabHeaderRightReserve,
            double macLeftReserve = MacOsTrafficLightReserve,
            double viewportPadding = TabHeaderViewportPadding)
        {
            double reservedLeft = isMacOs ? macLeftReserve : 0;

            // Before the title bar has measured, fall back to the static minimum so the first paint
            // doesn't crowd tabs against the buttons. Once we have a real bound, trust it — the floor
            // was sized for Windows (custom buttons + 140px caption reserve) and overshoots on macOS,
            // where the caption lives on the left and titleBarRightMargin is small.
            double reservedRight = titleBarWidth > 0
                ? Math.Ceiling(titleBarWidth + Math.Max(0, titleBarRightMargin) + viewportPadding)
                : minimumRightReserve;

            return new Thickness(reservedLeft, 0, reservedRight, 0);
        }

        /// <summary>
        /// Applies the TabStripOrientation setting. There is exactly ONE TabControl template
        /// (see MainWindow.axaml) - it is never swapped, because swapping Theme/ItemsPanel at
        /// runtime while a tab has live content makes the new template's PART_SelectedContentHost
        /// fight the old one for ownership of that content (Avalonia 12.0.4 throws "already has a
        /// visual parent" - see task-6-report.md). Instead this only flips the "vertical-tabs"
        /// class and defers to UpdateTabHeaderViewport, which reconfigures the existing template
        /// parts (dock side, spacer height, grip visibility, items-panel orientation, sizing) in
        /// place. The same TabItem instances (and their pane content) are reused throughout - a
        /// layout swap must never dispose or recreate sessions.
        /// </summary>
        internal void ApplyTabLayout()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            bool vertical = TabStripLayout.IsVertical(_settings.TabStripOrientation);
            _isVerticalTabStrip = vertical;
            tabs.Classes.Set("vertical-tabs", vertical);

            // ConfigureTabHeader is mode-aware (plain header vs. rich status/title/preview
            // row), so a layout swap must rebuild every tab's header content in place - the
            // same TabItem instances are reused, only their Header content changes.
            foreach (var tab in tabs.Items.Cast<TabItem>())
            {
                ConfigureTabHeader(tab, GetTabHeaderText(tab));

                // A mode swap rebuilds the header content, discarding whatever text the
                // TabPreviewLine TextBlock previously held - mark dirty so the first vertical
                // pass after this repopulates it instead of leaving it blank until the next
                // output/status-timer tick.
                GetOrCreateTabState(tab).PreviewDirty = true;
            }

            if (vertical && _tabStatusTimer == null)
            {
                _tabStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _tabStatusTimer.Tick += (_, _) => RefreshTabStatuses();
            }

            if (_tabStatusTimer != null)
            {
                _tabStatusTimer.IsEnabled = vertical;
            }

            // Sizing/part reconfiguration needs a layout pass to have measured the template
            // parts, so defer - same pattern as RebuildTitleBar.
            Dispatcher.UIThread.Post(() =>
            {
                WireTabStripResizeGrip();
                WireTabOverflowPill();
                UpdateTabVisuals();
            }, DispatcherPriority.Background);
        }

        /// <summary>
        /// Flips <see cref="TerminalSettings.TabStripOrientation"/> between Horizontal and
        /// Vertical, persists it, and re-applies the tab layout - the same effect as changing
        /// the setting from the Settings window, but reachable via shortcut/command palette.
        /// </summary>
        private void ToggleTabOrientation()
        {
            LeaveFocusModeForNavigation();
            _settings.TabStripOrientation = TabStripLayout.IsVertical(_settings.TabStripOrientation)
                ? "Horizontal"
                : "Vertical";
            _settings.Save();
            ApplyTabLayout();
        }

        /// <summary>
        /// Wires the sidebar's resize grip (PART_TabStripResizeGrip). The grip is a permanent
        /// part of the single inline template (see ApplyTabLayout's remarks) - it is never
        /// re-templated in/out, only shown/hidden via IsVisible. So wiring happens once per
        /// window instance (guarded by a "wired" Tag) and survives every mode flip; a hidden
        /// grip in horizontal mode is not hit-tested, so the handlers are inert there. Width is
        /// only persisted to settings on pointer release, not on every PointerMoved.
        /// </summary>
        private void WireTabStripResizeGrip()
        {
            if (!_isVerticalTabStrip) return;

            var grip = this.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(b => b.Name == "PART_TabStripResizeGrip");
            var scrollViewer = FindTabHeaderScrollViewer();
            if (grip == null || scrollViewer == null || Equals(grip.Tag, TemplatePartWiredTag)) return;
            grip.Tag = TemplatePartWiredTag;

            double startWidth = 0;
            double startX = 0;

            grip.PointerPressed += (_, e) =>
            {
                startWidth = scrollViewer.Bounds.Width;
                startX = e.GetPosition(this).X;
                _isTabStripGripDragging = true;
                e.Pointer.Capture(grip);
                e.Handled = true;
            };

            grip.PointerMoved += (_, e) =>
            {
                if (!ReferenceEquals(e.Pointer.Captured, grip)) return;
                scrollViewer.Width = TabStripLayout.ComputeDraggedWidth(startWidth, startX, e.GetPosition(this).X);
            };

            grip.PointerReleased += (_, e) =>
            {
                if (!ReferenceEquals(e.Pointer.Captured, grip)) return;
                e.Pointer.Capture(null);
                _isTabStripGripDragging = false;
                if (double.IsFinite(scrollViewer.Width))
                {
                    _settings.VerticalTabStripWidth = scrollViewer.Width;
                    _settings.Save();
                }
            };

            // Involuntary capture loss (e.g. another control steals it, window deactivates
            // mid-drag) must not leave the flag stuck - that would permanently freeze
            // scrollViewer.Width against future viewport passes. Clear WITHOUT persisting: the
            // next UpdateTabHeaderViewport pass restores the last-persisted width, which is the
            // correct recovery for an aborted drag (mirrors PointerReleased's persist path being
            // skipped, not duplicated).
            grip.PointerCaptureLost += (_, _) =>
            {
                _isTabStripGripDragging = false;
            };
        }

        /// <summary>
        /// Locates a named part inside the (single, never-swapped) Tabs TabControl template.
        /// </summary>
        private T? FindTabTemplatePart<T>(string name) where T : Control
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return null;

            return tabs.GetVisualDescendants()
                .OfType<T>()
                .FirstOrDefault(c => c.Name == name);
        }

        private void UpdateTabHeaderViewport()
        {
            var scrollViewer = FindTabHeaderScrollViewer();
            if (scrollViewer == null) return;

            var sidebar = FindTabTemplatePart<Grid>("PART_TabSidebar");
            var spacer = FindTabTemplatePart<Border>("PART_TitleBandSpacer");
            var grip = FindTabTemplatePart<Border>("PART_TabStripResizeGrip");
            var panel = FindTabItemsPresenter()?.Panel as StackPanel;

            // Focus mode hides the whole tab area. Nothing persisted (width, orientation) is
            // touched, so leaving restores exactly what was there.
            if (_isFocusMode)
            {
                if (sidebar != null) sidebar.IsVisible = false;
                if (spacer != null) spacer.Height = FocusStripHeight;
                return;
            }

            if (sidebar != null) sidebar.IsVisible = true;

            if (_isVerticalTabStrip)
            {
                if (sidebar != null) DockPanel.SetDock(sidebar, Dock.Left);
                if (spacer != null) spacer.Height = 36;
                if (grip != null) grip.IsVisible = true;
                if (panel != null) panel.Orientation = Orientation.Vertical;

                scrollViewer.Margin = new Thickness(0);
                scrollViewer.Height = double.NaN;
                // A live grip drag owns scrollViewer.Width via PointerMoved - a viewport pass
                // firing mid-drag (activity-driven: QueueTabVisualRefresh on pane output/bell,
                // the 1s tab-status timer) must not reset it back to the stale persisted value,
                // or the in-progress drag is silently discarded (visible snap-back, and a
                // release without another move would persist the reverted width).
                //
                // A reorder drag needs no analogous guard: it owns neither Width, Height nor
                // Offset - only the insert indicator (positioned in PART_TabSidebar space) and
                // the dragged header's opacity, none of which this method resets. Rewriting the
                // identical sizing values below is a no-op for an in-flight reorder.
                if (!_isTabStripGripDragging)
                {
                    scrollViewer.Width = TabStripLayout.ClampSidebarWidth(_settings.VerticalTabStripWidth);
                }
                scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                scrollViewer.ClipToBounds = true;

                // Horizontal overflow math is meaningless in a scrolling sidebar; route through
                // UpdateTabOverflowIndicator's own vertical guard so the reset logic (badge,
                // tab-list button tooltip/foreground) lives in one place.
                UpdateTabOverflowIndicator();
                UpdateTabOverflowPill(scrollViewer);
                return;
            }

            if (sidebar != null) DockPanel.SetDock(sidebar, Dock.Top);
            if (spacer != null) spacer.Height = 0;
            if (grip != null) grip.IsVisible = false;
            if (panel != null) panel.Orientation = Orientation.Horizontal;

            scrollViewer.Width = double.NaN;
            scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
            scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            var titleBar = this.FindControl<Grid>("TitleBar");

            scrollViewer.Margin = GetTabHeaderViewportMargin(
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
                titleBar?.Bounds.Width ?? 0,
                titleBar?.Margin.Right ?? 0);
            scrollViewer.Height = 36;
            scrollViewer.ClipToBounds = true;

            UpdateTabOverflowIndicator();
            UpdateTabOverflowPill(scrollViewer);
        }

        private void UpdateTabOverflowIndicator()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            var badge = this.FindControl<TextBlock>("TabOverflowBadge");
            // "open_tab_list" may legitimately be Overflow or Hidden per the user's title bar
            // layout, in which case this button does not exist. The badge itself lives outside
            // TitleBarItemsHost (see RebuildTitleBar_TabOverflowBadge_SurvivesRebuild) so it is NOT
            // cleared by the title bar rebuild that drops the button - without this explicit
            // hide/clear, flipping open_tab_list from Pinned to Overflow/Hidden while tabs are
            // currently clipped left a stale "+N" badge on screen with no adjacent button (Codex P2
            // on PR #342). Hide and clear it before bailing rather than returning silently.
            var button = FindTitleBarButton(TitleBarCatalog.OpenTabListId);
            var scrollViewer = FindTabHeaderScrollViewer();
            if (tabs == null || badge == null || scrollViewer == null) return;

            // The badge can be parented inside TitleBarItemsHost and this runs from paths other
            // than UpdateTabHeaderViewport, so it must stay hidden while the strip replaces the bar.
            if (_isFocusMode)
            {
                badge.IsVisible = false;
                return;
            }

            if (button == null)
            {
                badge.IsVisible = false;
                badge.Text = string.Empty;
                return;
            }

            // Compose onto the factory-generated "Tab List (<shortcut>)" tooltip rather than
            // replacing it: TitleBarViewFactory.Populate already resolved the (possibly
            // user-overridden) shortcut for this button via TitleBarShortcuts, and this method
            // reruns after every layout pass, so a bare "Tab List" or "Tab List (N hidden)"
            // written here would immediately clobber that shortcut (Codex P3 round 5 on PR #342).
            var tabListEntry = TitleBarCatalog.GetEntries()
                .FirstOrDefault(e => e.Id == TitleBarCatalog.OpenTabListId);
            string tabListShortcut = TitleBarShortcuts.Resolve(
                tabListEntry?.ShortcutKey ?? TitleBarCatalog.OpenTabListId, _settings.Keybindings);
            string baseTooltip = TitleBarShortcuts.FormatTooltip(tabListEntry?.Title ?? "Tab List", tabListShortcut);

            // In vertical mode the sidebar scrolls its own overflow, so viewportWidth (≈ sidebar
            // width, not "space for N tabs") is meaningless here. Force it through the same
            // zero-hidden reset branch used when the viewport has no measured width yet, rather
            // than duplicating the badge/tooltip/foreground reset.
            double viewportWidth = _isVerticalTabStrip ? 0 : scrollViewer.Bounds.Width;
            if (viewportWidth <= 0)
            {
                badge.IsVisible = false;
                ToolTip.SetTip(button, baseTooltip);
                button.ClearValue(Button.ForegroundProperty);
                return;
            }

            int hiddenCount = CountHiddenTabs(viewportWidth, tabs.Items.Cast<TabItem>().Select(t => t.Bounds.Width));

            badge.IsVisible = hiddenCount > 0;
            badge.Text = hiddenCount > 0 ? $"+{hiddenCount}" : string.Empty;
            ToolTip.SetTip(button, hiddenCount > 0 ? $"{baseTooltip} \u2014 {hiddenCount} hidden" : baseTooltip);
            // This runs after every layout pass (via UpdateTabVisuals -> PopulateTabListMenu). With
            // tabs hidden the button takes the attention tint; otherwise its local value is cleared
            // so the stock foreground (NtFg) applies.
            if (hiddenCount > 0)
            {
                button.Foreground = ThemeBrush(ThemeKeys.TabChipAttention);
            }
            else
            {
                button.ClearValue(Button.ForegroundProperty);
            }
        }

        internal static int CountHiddenTabs(double viewportWidth, IEnumerable<double> tabWidths, double fallbackTabWidth = 120)
        {
            if (viewportWidth <= 0) return 0;

            double usedWidth = 0;
            int hiddenCount = 0;
            foreach (double width in tabWidths)
            {
                double tabWidth = width > 0 ? width : fallbackTabWidth;
                if (usedWidth + tabWidth <= viewportWidth + 0.5)
                {
                    usedWidth += tabWidth;
                }
                else
                {
                    hiddenCount++;
                }
            }

            return hiddenCount;
        }

        /// <summary>Drives the vertical-mode overflow pill (PART_TabOverflowPill): visible
        /// with "+N more" when tab rows run past the sidebar viewport, hidden otherwise -
        /// including every horizontal pass, where the title-bar TabOverflowBadge owns the
        /// affordance instead (UpdateTabOverflowIndicator, deliberately untouched here).
        /// CountHiddenTabs is axis-generic 1D packing math, so the vertical pass simply
        /// feeds it the viewport height and the header heights.
        ///
        /// Called only from UpdateTabHeaderViewport - deliberately NOT from the
        /// RefreshTabStatuses 1 Hz tick: the hidden count can only change when tabs are
        /// added/removed (AddTab/CloseTab both route through UpdateTabVisuals →
        /// UpdateTabHeaderViewport), the viewport resizes (window/title-bar SizeChanged →
        /// UpdateTabHeaderViewport), or selection shifts (SelectionChanged →
        /// UpdateTabHeaderViewport), all of which already re-run this. Colors are reapplied
        /// on every visible pass so a theme change takes effect without a tab/viewport
        /// change (two small brush allocations - pill background and pill text
        /// foreground - same precedent as UpdateTabVisuals).</summary>
        private void UpdateTabOverflowPill(ScrollViewer scrollViewer)
        {
            var pill = FindTabTemplatePart<Button>("PART_TabOverflowPill");
            if (pill == null) return;

            // The text part is the pill's XAML Content, read straight off the property
            // rather than via a visual-tree lookup: a hidden control is never measured,
            // so while the pill is IsVisible=False its own template (and with it the
            // TextBlock's visual-tree presence) has never been applied - a visual lookup
            // would come back empty and the pill could never become visible at all.
            // Content still holds the TextBlock instance, and setting its text/foreground
            // works pre-materialization: the ContentPresenter shows it with these values
            // on the first pass after visibility turns on.
            if (pill.Content is not TextBlock pillText) return;

            if (!_isVerticalTabStrip)
            {
                pill.IsVisible = false;
                return;
            }

            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null)
            {
                pill.IsVisible = false;
                return;
            }

            // Occlusion semantics: the pill overlays the bottom ~25px of the viewport,
            // so the lowest strictly-fully-visible row may be counted hidden (an
            // undercount of one at most) - the semantic is "row doesn't fully fit
            // above the pill", an accepted approximation matching the title-bar badge.
            int hiddenCount = CountHiddenTabs(
                scrollViewer.Bounds.Height,
                tabs.Items.Cast<TabItem>().Select(t => t.Bounds.Height),
                fallbackTabWidth: DefaultVerticalTabRowHeight);
            if (hiddenCount <= 0)
            {
                pill.IsVisible = false;
                return;
            }

            // Colours are the sidebar's tokens, set in MainWindow.axaml.
            pillText.Text = $"+{hiddenCount} more";
            pill.IsVisible = true;
        }

        /// <summary>Wires the vertical overflow pill (PART_TabOverflowPill) click: opens the
        /// tab-list menu anchored at the pill (PopulateTabListMenu's anchorOverride path),
        /// the same lazy-populate-flyout-on-demand pattern the tab headers' ContextFlyout
        /// uses. The pill is a permanent part of the single inline template (like the resize
        /// grip, see ApplyTabLayout's remarks), so wiring happens once per window instance
        /// (guarded Tag) and survives every mode flip; a hidden pill in horizontal mode is
        /// not hit-tested, so the handler is inert there.</summary>
        private void WireTabOverflowPill()
        {
            var pill = FindTabTemplatePart<Button>("PART_TabOverflowPill");
            if (pill == null || Equals(pill.Tag, TemplatePartWiredTag)) return;
            pill.Tag = TemplatePartWiredTag;

            // Focusable=False (XAML) keeps the click from moving keyboard focus off the
            // terminal; the open flyout takes focus only while open, like any menu.
            pill.Click += (_, _) => PopulateTabListMenu(showFlyout: true, anchorOverride: pill);
        }

        private void EnsureSelectedTabHeaderVisible()
        {
            if (_isVerticalTabStrip)
            {
                (this.FindControl<TabControl>("Tabs")?.SelectedItem as Control)?.BringIntoView();
                return;
            }

            var tabs = this.FindControl<TabControl>("Tabs");
            var scrollViewer = FindTabHeaderScrollViewer();
            if (tabs?.SelectedItem is not TabItem selected || scrollViewer == null) return;

            var tabOrigin = selected.TranslatePoint(new Point(0, 0), scrollViewer);
            if (tabOrigin == null) return;

            double viewportWidth = scrollViewer.Bounds.Width;
            if (viewportWidth <= 0) return;

            double tabLeft = tabOrigin.Value.X;
            double tabRight = tabLeft + selected.Bounds.Width;
            double offsetX = scrollViewer.Offset.X;
            double nextOffset = offsetX;

            if (tabLeft < 0)
            {
                nextOffset = Math.Max(0, offsetX + tabLeft - 12);
            }
            else if (tabRight > viewportWidth)
            {
                nextOffset = Math.Max(0, offsetX + (tabRight - viewportWidth) + 12);
            }

            if (Math.Abs(nextOffset - offsetX) > 0.5)
            {
                scrollViewer.Offset = new Vector(nextOffset, scrollViewer.Offset.Y);
            }
        }

        /// <summary>Resolves where the tab-list menu anchors and which flyout to populate:
        /// an explicit override (the vertical overflow pill), else the pinned Tab List
        /// button (its flyout created and attached on first use), else the "..." overflow
        /// button or the title-bar host panel as a fallback anchor with a dedicated
        /// long-lived flyout. Null when no anchor exists at all.</summary>
        private (Control Anchor, MenuFlyout Flyout)? ResolveTabListMenuHost(Control? anchorOverride)
        {
            if (anchorOverride is not null)
            {
                // Vertical overflow pill path: anchor the menu at the pill instead of the
                // title bar, into the pill-dedicated flyout (see _tabOverflowPillFlyout for
                // why it cannot ride the pill's own Flyout property). The item-building and
                // show logic in PopulateTabListMenu is shared verbatim with the title-bar paths.
                return (anchorOverride, _tabOverflowPillFlyout ??= new MenuFlyout());
            }

            // "open_tab_list" may legitimately be Overflow or Hidden per the user's title bar
            // layout, in which case FindTitleBarButton returns null for its dedicated button - but
            // the action must still be reachable by shortcut and command palette (that is the whole
            // premise of Hidden: still there, just not a dedicated icon). DO NOT simplify this back
            // to the button-only lookup: that was exactly the bug (Codex P2 on PR #342) - with no
            // fallback, setting Tab List to Overflow or Hidden turned the overflow menu item, the
            // Ctrl+Shift+O shortcut, and the command palette entry all into silent no-ops. Fall back
            // to the overflow button when it exists, and finally to the title bar host panel itself,
            // which always exists.
            var button = FindTitleBarButton(TitleBarCatalog.OpenTabListId);
            var host = this.FindControl<StackPanel>("TitleBarItemsHost");
            var overflowButton = host?.Children.OfType<Button>()
                .FirstOrDefault(b => b.Name == TitleBarViewFactory.OverflowButtonName);
            var anchor = (Control?)button ?? (Control?)overflowButton ?? host;
            if (anchor == null) return null;

            // A pinned item's own button starts out with no popup menu attached, so this is
            // where one gets created and attached, the first time it is needed. The overflow ("...")
            // button is different: it already carries its own popup, prebuilt to list whichever
            // actions do not have a dedicated icon right now, and grabbing hold of that same popup
            // here would silently replace those contents the next time someone opens the "..." menu.
            // The panel hosting the title bar buttons cannot carry a popup at all. So whenever the
            // anchor is not a pinned item's own button, fall back to one dedicated menu kept alive
            // across calls purely to support that case.
            if (button is not null)
            {
                if (button.Flyout is not MenuFlyout existing)
                {
                    existing = new MenuFlyout();
                    button.Flyout = existing;
                }

                return (anchor, existing);
            }

            return (anchor, _tabListFallbackFlyout ??= new MenuFlyout());
        }

        /// <summary>
        /// Empties the window-owned tab-list flyouts other than <paramref name="target"/>. Each tab
        /// entry's Click closure captures its TabItem, and through TabItem.Content the whole pane,
        /// so a flyout this method stopped targeting (the fallback once the dedicated button
        /// exists, the overflow pill between clicks) kept every tab it once listed alive after the
        /// tab closed. Both are rebuilt before they are shown, so nothing is lost by emptying them.
        /// An open one is left alone. The dedicated button's own flyout needs no handling: it is
        /// either the title-bar target, rebuilt on every UpdateTabVisuals, or it went away with
        /// its button.
        /// </summary>
        private void ReleaseOtherTabListMenus(MenuFlyout target)
        {
            foreach (var cached in new[] { _tabListFallbackFlyout, _tabOverflowPillFlyout })
            {
                if (cached is not null && !ReferenceEquals(cached, target) && !cached.IsOpen && cached.Items.Count > 0)
                {
                    cached.Items.Clear();
                }
            }
        }

        private void PopulateTabListMenu(bool showFlyout = false, Control? anchorOverride = null)
        {
            // Only an explicit open is navigation; the rest are refreshes that run after every layout pass.
            if (showFlyout) LeaveFocusModeForNavigation();
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            var resolved = ResolveTabListMenuHost(anchorOverride);
            if (resolved == null) return;
            var (anchor, flyout) = resolved.Value;

            ReleaseOtherTabListMenus(flyout);
            flyout.Items.Clear();
            int index = 1;
            foreach (var tab in tabs.Items.Cast<TabItem>())
            {
                var item = new MenuItem
                {
                    Header = GetTabMenuLabel(tab, index),
                    IsChecked = tabs.SelectedItem == tab,
                    ToggleType = MenuItemToggleType.Radio,
                    StaysOpenOnClick = false
                };
                item.Click += (_, __) =>
                {
                    tabs.SelectedItem = tab;
                    flyout.Hide();
                };
                flyout.Items.Add(item);
                index++;
            }

            if (tabs.Items.Count > 0)
            {
                flyout.Items.Add(new Separator());

                var renameItem = new MenuItem { Header = "Rename Current Tab..." };
                renameItem.Click += async (_, __) => await RenameSelectedTabAsync();
                flyout.Items.Add(renameItem);

                var copyTitleItem = new MenuItem { Header = "Copy Tab Title" };
                copyTitleItem.Click += async (_, __) => await CopySelectedTabTitleAsync();
                flyout.Items.Add(copyTitleItem);

                bool canCloseCurrent = tabs.SelectedItem is not TabItem currentTab || CanCloseTab(IsTabProtected(currentTab));
                var closeCurrentItem = new MenuItem
                {
                    Header = "Close Current Tab",
                    IsEnabled = canCloseCurrent
                };
                closeCurrentItem.Click += async (_, __) => await CloseSelectedTabAsync();
                flyout.Items.Add(closeCurrentItem);

                bool hasClosableOthers = tabs.SelectedItem is TabItem selectedForCloseOthers &&
                    tabs.Items.Cast<TabItem>().Any(t => t != selectedForCloseOthers && !ShouldSkipTabWhenClosingOthers(IsTabPinned(t), IsTabProtected(t)));
                var closeOthersItem = new MenuItem
                {
                    Header = "Close Other Tabs",
                    IsEnabled = hasClosableOthers
                };
                closeOthersItem.Click += async (_, __) => await CloseOtherTabsAsync();
                flyout.Items.Add(closeOthersItem);

                if (tabs.SelectedItem is TabItem selectedTab)
                {
                    var selectedState = GetOrCreateTabState(selectedTab);

                    var pinItem = new MenuItem { Header = GetPinTabActionLabel(selectedState.IsPinned) };
                    pinItem.Click += (_, __) => TogglePinSelectedTab();
                    flyout.Items.Add(pinItem);

                    var protectItem = new MenuItem { Header = GetProtectTabActionLabel(selectedState.IsProtected) };
                    protectItem.Click += (_, __) => ToggleProtectSelectedTab();
                    flyout.Items.Add(protectItem);
                }
            }

            if (showFlyout)
            {
                flyout.ShowAt(anchor);
            }

            UpdateTabOverflowIndicator();
        }
    }
}
