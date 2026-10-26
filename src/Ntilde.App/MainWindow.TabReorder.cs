using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Ntilde.Shell;
using Avalonia;

namespace Ntilde
{
    /// <summary>
    /// Drag-to-reorder of tabs in the strip, and the keyboard move-tab actions.
    /// </summary>
    public partial class MainWindow
    {
        // ---- Tab drag-to-reorder state (handlers near OnTabHeaderPointerPressed) ----
        // A left press on a header host only ARMS a reorder; the drag begins once
        // TabDragModel.ShouldStartDrag sees >= 5 DIP of movement along the strip axis, so a
        // plain click keeps its select-on-press behavior. From that point the pressed header
        // host holds pointer capture and is dimmed, PART_TabInsertIndicator marks the drop
        // gap, and an auto-scroll DispatcherTimer runs near the viewport edges. The reorder
        // itself only moves entries inside Tabs.Items - the same TabItem instances and pane
        // content are reused (ApplyTabLayout's PART_SelectedContentHost ownership contract).
        private bool _isTabReorderDragging;
        private bool _isTabHeaderLeftPressPending;
        private Border? _tabDragPressedHost;
        private IPointer? _tabDragPointer;
        private TabControl? _tabDragTabs;
        private double _tabDragPressAxisPos;
        // Pointer position along the strip axis in the header ScrollViewer's VIEWPORT
        // coordinate space - kept fresh on every move so the auto-scroll tick can evaluate
        // the edge zones without waiting for another pointer event. Viewport space (unlike
        // the ItemsPresenter's content space, which the insert-index math uses) is what
        // stays constant while the pointer parks in an edge zone and only the strip
        // scrolls underneath. NaN = "no position yet"; TabDragModel treats that as no-op.
        private double _tabDragPointerAxisPos = double.NaN;
        private int _tabDragInsertIndex;
        private DispatcherTimer? _tabDragAutoScrollTimer;
        // True only inside the RemoveAt/Insert window of CommitTabReorder. TabControl is
        // AlwaysSelected, so removing the (selected) dragged tab promotes index 0 for the
        // microseconds before selection is restored - the SelectionChanged handler bails on
        // that transient promotion (see the guard at its top).
        private bool _isTabReorderCommitting;
        // Single source of truth for the indicator's thickness (applied along Width in
        // vertical mode, Height in horizontal - the XAML element deliberately sets no
        // size). Brush/alignment live in the template XAML.
        private const double TabInsertIndicatorThickness = 2;
        private const double TabInsertIndicatorMinLength = 16;
        // Command ids for the keyboard move-tab actions (ShortcutCatalog keys + command
        // palette ids + usage recording must all agree).
        private const string MoveTabPrevCommandId = "move_tab_prev";
        private const string MoveTabNextCommandId = "move_tab_next";

        /// <summary>Test-only seam: exposes the mid-reorder-drag state (same pattern as
        /// <see cref="IsTabStripGripDraggingForTest"/>).</summary>
        internal bool IsTabReorderDraggingForTest => _isTabReorderDragging;

        // ---- Tab drag-to-reorder (pure math lives in Shell/TabDragModel.cs) ----
        //
        // Subscribed by both header factories, so the behavior is axis-generic: which
        // coordinate feeds TabDragModel is decided by _isVerticalTabStrip, and header bounds
        // and pointer positions are both measured in the ItemsPresenter's (content)
        // coordinate space so scrolling is accounted for. The reorder only moves entries
        // inside Tabs.Items - TabItem instances and their pane content are never recreated
        // (ApplyTabLayout's ownership contract for PART_SelectedContentHost).
        private void WireTabHeaderReorderDrag(TabItem tab, Border headerHost)
        {
            headerHost.PointerPressed += (_, e) => OnTabReorderPointerPressed(headerHost, e);
            headerHost.PointerMoved += (_, e) => OnTabReorderPointerMoved(headerHost, e);
            headerHost.PointerReleased += (_, e) => OnTabReorderPointerReleased(tab, headerHost, e);
            // Involuntary capture loss (another control steals it, window deactivates
            // mid-drag) must abort without committing - the same non-committing-cleanup
            // contract the sidebar grip's PointerCaptureLost follows.
            headerHost.PointerCaptureLost += (_, _) => EndTabReorderDrag();
        }

        private void OnTabReorderPointerPressed(Border headerHost, PointerPressedEventArgs e)
        {
            // Middle/right presses have already been claimed (handled) by
            // OnTabHeaderPointerPressed for select/close/context-menu; only an unhandled
            // left press can become a reorder drag.
            if (e.Handled || !e.GetCurrentPoint(headerHost).Properties.IsLeftButtonPressed)
            {
                _isTabHeaderLeftPressPending = false;
                return;
            }

            // Arm, don't capture (yet): a plain click must keep its existing behavior, and a
            // capture here would break hover on the other headers before any drag starts.
            _isTabHeaderLeftPressPending = true;
            _tabDragPressedHost = headerHost;
            _tabDragTabs = this.FindControl<TabControl>("Tabs");
            _tabDragPressAxisPos = GetTabStripAxisPosition(e);
        }

        private void OnTabReorderPointerMoved(Border headerHost, PointerEventArgs e)
        {
            if (_isTabReorderDragging)
            {
                if (!ReferenceEquals(e.Pointer.Captured, headerHost)) return;

                // Zombie-drag guard: the release event can be lost outright (quake-mode
                // ToggleVisibility -> Hide() mid-drag), leaving capture set and the drag
                // "alive" on the hidden window - the auto-scroll timer keeps ticking and,
                // after re-show, buttonless hover moves keep driving the drag until some
                // stray click commits an unintended reorder. A move with the button up
                // means the gesture is physically over: cancel - never commit - through
                // the same capture-releasing cleanup Escape uses. Mirrors the armed
                // branch's staleness check below.
                if (!e.GetCurrentPoint(headerHost).Properties.IsLeftButtonPressed)
                {
                    CancelTabReorderDrag();
                    return;
                }

                UpdateTabDrag(e);
                return;
            }

            if (!_isTabHeaderLeftPressPending || !ReferenceEquals(_tabDragPressedHost, headerHost)) return;

            // The button coming up outside this host (nothing captured yet) leaves the armed
            // press stale; the next buttonless move over the host must not graduate it.
            if (!e.GetCurrentPoint(headerHost).Properties.IsLeftButtonPressed)
            {
                _isTabHeaderLeftPressPending = false;
                return;
            }

            // The grip owns the pointer for the duration of a sidebar resize; a header move
            // arriving then is a stray cross-route event, not a reorder gesture. Likewise a
            // single-tab strip can never reorder.
            if (_isTabStripGripDragging || _tabDragTabs == null || _tabDragTabs.Items.Count < 2)
            {
                _isTabHeaderLeftPressPending = false;
                return;
            }

            var axisPos = GetTabStripAxisPosition(e);
            if (!TabDragModel.ShouldStartDrag(_tabDragPressAxisPos, axisPos)) return;

            BeginTabReorderDrag(headerHost, e);
        }

        private void BeginTabReorderDrag(Border headerHost, PointerEventArgs e)
        {
            _isTabHeaderLeftPressPending = false;
            _isTabReorderDragging = true;
            _tabDragPointer = e.Pointer;
            e.Pointer.Capture(headerHost);
            headerHost.Opacity = 0.5;

            UpdateTabDrag(e);

            // Auto-scroll near the viewport edges runs on a timer rather than only on moves:
            // once the pointer parks inside an edge zone, it stops generating PointerMoved
            // events but the strip must keep scrolling.
            _tabDragAutoScrollTimer ??= CreateTabDragAutoScrollTimer();
            _tabDragAutoScrollTimer.Start();
        }

        private DispatcherTimer CreateTabDragAutoScrollTimer()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            timer.Tick += (_, _) => AutoScrollTabDrag();
            return timer;
        }

        private void UpdateTabDrag(PointerEventArgs e)
        {
            var axisPos = GetTabStripAxisPosition(e);
            if (!double.IsFinite(axisPos)) return;

            // Viewport-space pointer position for the auto-scroll tick: content-space minus
            // the current scroll offset (content = viewport + offset).
            var scrollViewer = FindTabHeaderScrollViewer();
            double offset = 0;
            if (scrollViewer != null)
            {
                offset = _isVerticalTabStrip ? scrollViewer.Offset.Y : scrollViewer.Offset.X;
            }

            _tabDragPointerAxisPos = axisPos - offset;

            var bounds = GetTabHeaderBoundsInStrip();
            if (bounds.Count == 0) return;

            _tabDragInsertIndex = TabDragModel.ComputeInsertIndex(HeaderCenters(bounds), axisPos);
            PositionTabInsertIndicator(bounds);
        }

        private static List<double> HeaderCenters(List<(double Start, double End)> bounds)
        {
            var centers = new List<double>(bounds.Count);
            foreach (var extent in bounds)
            {
                centers.Add((extent.Start + extent.End) / 2);
            }

            return centers;
        }

        /// <summary>Pointer position along the strip axis in the ItemsPresenter's (content)
        /// coordinate space - the same space <see cref="GetTabHeaderBoundsInStrip"/> reports,
        /// so scrolling never desynchronizes the two. Non-finite when the presenter or the
        /// position transform is unavailable; TabDragModel's guards treat that as
        /// "no drag start / insert index 0".</summary>
        private double GetTabStripAxisPosition(PointerEventArgs e)
        {
            var presenter = FindTabItemsPresenter();
            if (presenter == null) return double.NaN;
            var position = e.GetPosition(presenter);
            return _isVerticalTabStrip ? position.Y : position.X;
        }

        /// <summary>Along-axis extent of every tab's header container (the TabItem itself, a
        /// realized child of the items panel) in the ItemsPresenter's coordinate space, in
        /// visual (= Items) order. The dragged tab keeps its original slot until commit, so
        /// the list always reflects the pre-reorder order ComputeInsertIndex expects.</summary>
        private List<(double Start, double End)> GetTabHeaderBoundsInStrip()
        {
            var bounds = new List<(double Start, double End)>();
            var presenter = FindTabItemsPresenter();
            var tabs = _tabDragTabs ?? this.FindControl<TabControl>("Tabs");
            if (presenter == null || tabs == null) return bounds;

            foreach (var tab in tabs.Items.Cast<TabItem>())
            {
                var topLeft = tab.TranslatePoint(new Point(0, 0), presenter);
                if (topLeft == null) continue;
                bounds.Add(_isVerticalTabStrip
                    ? (topLeft.Value.Y, topLeft.Value.Y + tab.Bounds.Height)
                    : (topLeft.Value.X, topLeft.Value.X + tab.Bounds.Width));
            }

            return bounds;
        }

        private void PositionTabInsertIndicator(List<(double Start, double End)> bounds)
        {
            var indicator = FindTabTemplatePart<Border>("PART_TabInsertIndicator");
            var sidebar = FindTabTemplatePart<Grid>("PART_TabSidebar");
            if (indicator == null || sidebar == null || bounds.Count == 0) return;

            // Gap along the axis: the near edge of the header currently at the insert index,
            // or the far edge of the last header when dropping at the very end.
            var index = Math.Clamp(_tabDragInsertIndex, 0, bounds.Count);
            double edge = index < bounds.Count ? bounds[index].Start : bounds[bounds.Count - 1].End;

            // Translate the content-space edge into the sidebar Grid's space (the indicator's
            // parent), so auto-scrolling mid-drag keeps the line glued to the on-screen gap.
            var gapInContent = _isVerticalTabStrip ? new Point(0, edge) : new Point(edge, 0);
            var gapInSidebar = FindTabItemsPresenter()?.TranslatePoint(gapInContent, sidebar);
            if (gapInSidebar == null)
            {
                indicator.IsVisible = false;
                return;
            }

            // The bar spans the drop gap like a divider between adjacent tabs: its length
            // runs along the CROSS axis (the dragged header's row width in vertical mode,
            // its column height in horizontal mode) and the 2-DIP thickness runs along the
            // drag axis, straddling the gap edge. Background and alignment are constant,
            // set once in the template XAML - only the per-drag values (thickness axis,
            // length, margin, visibility) are assigned here.
            double crossLength = _isVerticalTabStrip
                ? Math.Max(TabInsertIndicatorMinLength, _tabDragPressedHost?.Bounds.Width ?? 0)
                : Math.Max(TabInsertIndicatorMinLength, _tabDragPressedHost?.Bounds.Height ?? 0);

            if (_isVerticalTabStrip)
            {
                indicator.Height = TabInsertIndicatorThickness;
                indicator.Width = crossLength;
                indicator.Margin = new Thickness(
                    0, gapInSidebar.Value.Y - TabInsertIndicatorThickness / 2, 0, 0);
            }
            else
            {
                indicator.Width = TabInsertIndicatorThickness;
                indicator.Height = crossLength;
                indicator.Margin = new Thickness(
                    gapInSidebar.Value.X - TabInsertIndicatorThickness / 2,
                    Math.Max(0, (sidebar.Bounds.Height - crossLength) / 2), 0, 0);
            }

            indicator.IsVisible = true;
        }

        private void AutoScrollTabDrag()
        {
            if (!_isTabReorderDragging)
            {
                _tabDragAutoScrollTimer?.Stop();
                return;
            }

            var scrollViewer = FindTabHeaderScrollViewer();
            if (scrollViewer == null) return;

            // _tabDragPointerAxisPos is already viewport-space (where the edge zones live),
            // and it stays constant while the pointer parks and the strip scrolls - so the
            // tick keeps scrolling until the extent clamp stops it, rather than the pointer
            // "drifting" out of the zone it is physically still in.
            bool vertical = _isVerticalTabStrip;
            double viewportLength = vertical ? scrollViewer.Viewport.Height : scrollViewer.Viewport.Width;
            double delta = TabDragModel.ComputeAutoScrollDelta(0, viewportLength, _tabDragPointerAxisPos);
            if (delta == 0) return;

            // Clamp to the scrollable extent (Extent - Viewport), mirroring what the
            // ScrollViewer itself would do with an out-of-range Offset assignment.
            double offset = vertical ? scrollViewer.Offset.Y : scrollViewer.Offset.X;
            double maxOffset = vertical
                ? scrollViewer.Extent.Height - scrollViewer.Viewport.Height
                : scrollViewer.Extent.Width - scrollViewer.Viewport.Width;
            double next = Math.Clamp(offset + delta, 0, Math.Max(0, maxOffset));

            // Content scrolled under the parked pointer, so its content-space position
            // (viewport + offset) moved with it: re-derive the insert index instead of
            // letting the drop gap lag behind the headers until the next pointer move.
            //
            // Ordering constraint: assigning Offset only invalidates arrange - the
            // header bounds and the presenter->sidebar transform below still measure
            // PRE-scroll geometry until the next layout pass. So everything here must
            // derive from the PRE-scroll `offset`: pointer content-space =
            // _tabDragPointerAxisPos + offset, the same frame GetTabHeaderBoundsInStrip
            // reports. Mixing post-scroll pointer math (`+ next`) with pre-scroll bounds
            // desynchronizes the indicator and drop index by up to one 12 DIP step, so a
            // drop right after a scroll tick could pick the neighboring slot. Only move
            // the Offset afterwards; layout catches up by the next tick (33ms), which
            // then recomputes from the settled offset.
            var bounds = GetTabHeaderBoundsInStrip();
            if (bounds.Count > 0)
            {
                _tabDragInsertIndex = TabDragModel.ComputeInsertIndex(
                    HeaderCenters(bounds), _tabDragPointerAxisPos + offset);
            }

            PositionTabInsertIndicator(bounds);

            scrollViewer.Offset = vertical
                ? new Vector(scrollViewer.Offset.X, next)
                : new Vector(next, scrollViewer.Offset.Y);
        }

        private void OnTabReorderPointerReleased(TabItem tab, Border headerHost, PointerReleasedEventArgs e)
        {
            if (_isTabReorderDragging && ReferenceEquals(e.Pointer.Captured, headerHost))
            {
                // Releasing capture synchronously raises PointerCaptureLost, which runs the
                // shared non-committing cleanup; commit afterwards, ordered by the final
                // insert index.
                e.Pointer.Capture(null);
                CommitTabReorder(tab);
                return;
            }

            // An armed press that never crossed the drag threshold ends here (a plain
            // click): disarm without touching anything.
            if (_isTabHeaderLeftPressPending && ReferenceEquals(_tabDragPressedHost, headerHost))
            {
                _isTabHeaderLeftPressPending = false;
            }
        }

        private void CommitTabReorder(TabItem draggedTab)
        {
            var tabs = _tabDragTabs ?? this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            int oldIndex = tabs.Items.IndexOf(draggedTab);
            if (oldIndex < 0) return;

            // ComputeInsertIndex ran over the pre-reorder order (the dragged header never
            // leaves its slot during the drag), so a target past the dragged tab collapses by
            // one once the dragged tab is removed from Items.
            int insertIndex = Math.Clamp(_tabDragInsertIndex, 0, tabs.Items.Count);
            int newIndex = insertIndex > oldIndex ? insertIndex - 1 : insertIndex;

            ReorderTabInItems(tabs, draggedTab, newIndex);
        }

        /// <summary>The single reorder primitive, shared by the drag commit
        /// (<see cref="CommitTabReorder"/>) and <see cref="MoveSelectedTab"/>: reinserts
        /// the tab at <paramref name="newIndex"/> inside the guarded
        /// <see cref="_isTabReorderCommitting"/> window (TabControl's AlwaysSelected mode
        /// promotes index 0 to SelectedItem for the microseconds before the restore below -
        /// the SelectionChanged handler skips that transient promotion: no MRU touch, no
        /// attention clear, no focus steal on a tab the user never actually viewed), then
        /// restores the moved tab as the selection, which re-runs the full select path with
        /// the final Items order. The mutation itself is the same
        /// remove-from-live-ItemCollection pattern CloseTab uses: the TabItem and its pane
        /// content are reused, never recreated (ApplyTabLayout's ownership contract).</summary>
        private void ReorderTabInItems(TabControl tabs, TabItem movedTab, int newIndex)
        {
            int oldIndex = tabs.Items.IndexOf(movedTab);
            if (oldIndex < 0) return;

            if (newIndex != oldIndex)
            {
                _isTabReorderCommitting = true;
                try
                {
                    tabs.Items.RemoveAt(oldIndex);
                    tabs.Items.Insert(newIndex, movedTab);
                }
                finally
                {
                    _isTabReorderCommitting = false;
                }
            }

            tabs.SelectedItem = movedTab;
        }

        /// <summary>Moves the selected tab one slot along the strip (delta -1/+1), clamped
        /// at the ends - the keyboard counterpart of drag-to-reorder, committing through the
        /// same <see cref="ReorderTabInItems"/> semantics (pinned/protected tabs move
        /// freely, consistent with the drag path). Internal for headless tests.</summary>
        internal void MoveSelectedTab(int delta)
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs?.SelectedItem is not TabItem selected) return;

            int currentIndex = tabs.Items.IndexOf(selected);
            int newIndex = ComputeMoveTabIndex(currentIndex, tabs.Items.Count, delta);
            if (newIndex < 0 || newIndex == currentIndex) return;

            ReorderTabInItems(tabs, selected, newIndex);
        }

        /// <summary>Pure move-tab index math: the clamped target for moving the tab at
        /// <paramref name="currentIndex"/> by <paramref name="delta"/> slots. Returns -1
        /// when no move is expressible (no tabs, a single tab, or an out-of-range current
        /// index); a clamp back onto <paramref name="currentIndex"/> is a caller no-op.</summary>
        internal static int ComputeMoveTabIndex(int currentIndex, int count, int delta)
        {
            if (count <= 1 || currentIndex < 0 || currentIndex >= count) return -1;
            return Math.Clamp(currentIndex + delta, 0, count - 1);
        }

        /// <summary>Shared non-committing cleanup for drag end, Escape and involuntary
        /// capture loss alike: hide the indicator, restore the dimmed header, stop the
        /// auto-scroll timer. Does not reset <c>_tabDragInsertIndex</c> - the release
        /// handler still needs it for its commit after capture release runs this first.</summary>
        private void EndTabReorderDrag()
        {
            _isTabReorderDragging = false;
            _isTabHeaderLeftPressPending = false;
            if (_tabDragPressedHost != null)
            {
                _tabDragPressedHost.Opacity = 1;
            }

            _tabDragPressedHost = null;
            _tabDragPointer = null;
            _tabDragTabs = null;
            _tabDragAutoScrollTimer?.Stop();
            if (FindTabTemplatePart<Border>("PART_TabInsertIndicator") is { } indicator)
            {
                indicator.IsVisible = false;
            }
        }

        /// <summary>Aborts an in-flight reorder drag without committing (Escape). Dropping
        /// capture raises PointerCaptureLost on the header host, running the shared
        /// <see cref="EndTabReorderDrag"/> cleanup - the same contract as a capture steal.</summary>
        private void CancelTabReorderDrag()
        {
            if (_tabDragPointer?.Captured != null)
            {
                _tabDragPointer.Capture(null);
            }
            else
            {
                EndTabReorderDrag();
            }
        }
    }
}
