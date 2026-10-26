using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Ntilde.Shell;
using Ntilde.Shell.Theming;
using Ntilde.Rendering;
using Avalonia;
using Avalonia.Media;
using Avalonia.Layout;

using Ntilde.Controls;

namespace Ntilde
{
    /// <summary>
    /// Tab header hosts, horizontal and vertical: building them, their pointer handling and context menu, and
    /// keeping their visuals (status dot, chips, preview line) current.
    /// </summary>
    public partial class MainWindow
    {
        private readonly HashSet<TabItem> _pendingVisualRefreshTabs = new();
        private bool _tabVisualRefreshScheduled;

        // The vertical header's status dot geometries, one per TabDotShape, in the dot's 8x8 box.
        // Built once on first use (UpdateVerticalTabExtras swaps which one a dot shows on every
        // pass), and lazily, never in a static initializer: creating a Geometry needs Avalonia's
        // render platform, so an eager field would make MainWindow's type initializer throw for
        // anything that touches a MainWindow static before the platform is up - and a type whose
        // initializer has thrown is unusable for the rest of the process.
        private static Geometry? s_tabDotCircleGeometry;
        private static Geometry? s_tabDotDiamondGeometry;
        private static Geometry? s_tabDotRingGeometry;

        private static Geometry TabDotCircleGeometry => s_tabDotCircleGeometry ??= new EllipseGeometry(new Rect(0, 0, 8, 8));

        private static Geometry TabDotDiamondGeometry => s_tabDotDiamondGeometry ??= Geometry.Parse("M4,0 L8,4 L4,8 L0,4 Z");

        private static Geometry TabDotRingGeometry => s_tabDotRingGeometry ??= new EllipseGeometry(new Rect(0.75, 0.75, 6.5, 6.5));

        private static TextBlock? FindTabHeaderTextBlock(object? header)
        {
            return header switch
            {
                TextBlock tb => tb,
                Border border => FindTabHeaderTextBlock(border.Child),
                ContentControl contentControl => FindTabHeaderTextBlock(contentControl.Content),
                Panel panel => panel.Children.Select(child => FindTabHeaderTextBlock(child)).FirstOrDefault(tb => tb != null),
                Decorator decorator => FindTabHeaderTextBlock(decorator.Child),
                _ => null
            };
        }

        internal string GetTabHeaderText(TabItem tab)
        {
            if (FindTabHeaderTextBlock(tab.Header) is TextBlock tb)
            {
                return string.IsNullOrWhiteSpace(tb.Text) ? "Terminal" : tb.Text;
            }

            return "Terminal";
        }

        private Border CreateTabHeaderHost(TabItem tab, string text)
        {
            // No Foreground: the title inherits the TabItem's (NtTabFg, NtTabFgSelected when
            // selected; see App.axaml), so selection and theme switches recolour it on their own.
            var headerText = new TextBlock
            {
                Text = text,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };

            var headerHost = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(10, 4),
                Child = headerText
            };

            headerHost.ContextFlyout = new MenuFlyout();
            headerHost.PointerPressed += (_, e) => OnTabHeaderPointerPressed(tab, e);
            WireTabHeaderReorderDrag(tab, headerHost);
            ToolTip.SetTip(headerHost, text);
            return headerHost;
        }

        private Border CreateVerticalTabHeaderHost(TabItem tab, string text)
        {
            // A Path rather than an Ellipse: the dot's shape carries meaning too (TabDotShape), so
            // UpdateVerticalTabExtras swaps its geometry as well as its token.
            var statusDot = new Avalonia.Controls.Shapes.Path
            {
                Name = "TabStatusDot",
                Width = 8,
                Height = 8,
                Fill = Brushes.Transparent,
                StrokeThickness = 1.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var headerText = new TextBlock
            {
                Text = text,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };

            var previewText = new TextBlock
            {
                Name = "TabPreviewLine",
                Text = string.Empty,
                Foreground = ThemeBrush(ThemeKeys.TabPreviewFg),
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            };

            // OSC 9;4 progress (#271): a thin bar under the preview line, hidden until
            // UpdateVerticalTabExtras turns it on. ProgressBar (rather than hand-rolled
            // track+fill) so star-sized width and the indeterminate animation come for
            // free; not hit-testable so pointer presses stay on the header host, same
            // contract as the chips.
            var progressBar = new Avalonia.Controls.ProgressBar
            {
                Name = "TabProgressBar",
                Height = 3,
                MinWidth = 0,
                Minimum = 0,
                Maximum = 100,
                IsVisible = false,
                IsHitTestVisible = false,
                Margin = new Thickness(0, 4, 0, 0)
            };

            // Title BEFORE preview (and before the chips): FindTabHeaderTextBlock takes the
            // first TextBlock as the title, and UpdateTabVisuals rewrites that one with the
            // display label.
            var textColumn = new StackPanel { Orientation = Avalonia.Layout.Orientation.Vertical };
            textColumn.Children.Add(headerText);
            textColumn.Children.Add(previewText);
            textColumn.Children.Add(progressBar);

            // Trailing status chips: the compact replacements for the attention-marker
            // suffixes that used to live inside the truncated title text (bell/activity/
            // agent glyphs). Hidden until UpdateVerticalTabExtras turns the tab's marker
            // set back on; not hit-testable so pointer presses stay on the header host
            // (select + context menu + drag-reorder), which also means no per-chip
            // tooltips (a hit-test-invisible control never sees pointer-enter). Chip
            // colours are the NtTabChip* tokens, contrast-fixed for text against the sidebar.
            TextBlock Chip(string name, string glyph, string tokenKey)
            {
                var chip = new TextBlock
                {
                    Name = name,
                    Text = glyph,
                    Foreground = ThemeBrush(tokenKey),
                    FontSize = 10,
                    IsVisible = false,
                    IsHitTestVisible = false,
                };
                return chip;
            }

            var bellChip = Chip("TabBellChip", "\U0001F514", ThemeKeys.TabChipAttention);
            var activityChip = Chip("TabActivityChip", "\u2022", ThemeKeys.TabChipActivity);
            var agentWroteChip = Chip("TabAgentWroteChip", AgentWroteGlyph, ThemeKeys.TabChipAgentWrote);
            var agentWatchedChip = Chip("TabAgentWatchedChip", AgentWatchedGlyph, ThemeKeys.TabChipAgentWatched);
            // Shared has always worn the watched blue; it keeps that family.
            var sharedChip = Chip("TabSharedChip", SharedGlyph, ThemeKeys.TabChipAgentWatched);

            var chipsColumn = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            chipsColumn.Children.Add(bellChip);
            chipsColumn.Children.Add(activityChip);
            chipsColumn.Children.Add(agentWroteChip);
            chipsColumn.Children.Add(agentWatchedChip);
            chipsColumn.Children.Add(sharedChip);

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            Grid.SetColumn(statusDot, 0);
            Grid.SetColumn(textColumn, 1);
            Grid.SetColumn(chipsColumn, 2);
            row.Children.Add(statusDot);
            row.Children.Add(textColumn);
            row.Children.Add(chipsColumn);

            var headerHost = new Border
            {
                Background = Brushes.Transparent,
                // Right padding keeps the chips clear of the header's trailing edge.
                Padding = new Thickness(10, 6, 8, 6),
                Child = row
            };

            headerHost.ContextFlyout = new MenuFlyout();
            headerHost.PointerPressed += (_, e) => OnTabHeaderPointerPressed(tab, e);
            WireTabHeaderReorderDrag(tab, headerHost);
            ToolTip.SetTip(headerHost, text);
            return headerHost;
        }

        /// <summary>Walks a code-built header object graph by part name (constructed headers
        /// have no name scope, so FindControl can't see inside them).</summary>
        internal static T? FindTabHeaderDescendant<T>(object? node, string name) where T : Control
            => node switch
            {
                T match when match.Name == name => match,
                Border border => FindTabHeaderDescendant<T>(border.Child, name),
                Panel panel => panel.Children.Select(c => FindTabHeaderDescendant<T>(c, name)).FirstOrDefault(c => c != null),
                Decorator decorator => FindTabHeaderDescendant<T>(decorator.Child, name),
                ContentControl contentControl => FindTabHeaderDescendant<T>(contentControl.Content, name),
                _ => null,
            };

        private void ConfigureTabHeader(TabItem tab, string text)
        {
            tab.Header = _isVerticalTabStrip
                ? CreateVerticalTabHeaderHost(tab, text)
                : CreateTabHeaderHost(tab, text);
        }

        private void OnTabHeaderPointerPressed(TabItem tab, PointerPressedEventArgs e)
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            bool wasSelected = tabs?.SelectedItem == tab;
            if (tabs != null && tabs.SelectedItem != tab)
            {
                tabs.SelectedItem = tab;
            }

            var properties = e.GetCurrentPoint(this).Properties;
            var action = ResolveTabHeaderPointerAction(
                properties.IsMiddleButtonPressed,
                properties.IsRightButtonPressed);

            if (action == TabHeaderPointerAction.CloseTab)
            {
                e.Handled = true;
                _ = CloseTabAsync(tab);
                return;
            }

            if (action == TabHeaderPointerAction.OpenContextMenu)
            {
                if (FindTabHeaderHost(tab) is Control headerHost &&
                    headerHost.ContextFlyout is MenuFlyout flyout)
                {
                    e.Handled = true;
                    ShowTabContextMenu(tab, headerHost, flyout, ShouldDeferTabContextMenuOpen(wasSelected));
                }
            }
        }

        private void ShowTabContextMenu(TabItem tab, Control headerHost, MenuFlyout flyout, bool defer)
        {
            void open()
            {
                PopulateTabContextMenu(flyout, tab);
                flyout.ShowAt(headerHost);
            }

            if (defer)
            {
                Dispatcher.UIThread.Post(open, DispatcherPriority.Input);
            }
            else
            {
                open();
            }
        }

        private void PopulateTabContextMenu(MenuFlyout flyout, TabItem tab)
        {
            flyout.Items.Clear();
            if (!_tabStateByTab.ContainsKey(tab) && !TryEnsureLiveTab(tab))
            {
                return;
            }

            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs != null && tabs.SelectedItem != tab)
            {
                tabs.SelectedItem = tab;
            }

            var state = GetOrCreateTabState(tab);
            bool hasClosableOthers = tabs?.Items
                .Cast<TabItem>()
                .Any(other => other != tab && !ShouldSkipTabWhenClosingOthers(IsTabPinned(other), IsTabProtected(other))) == true;

            var closeItem = new MenuItem
            {
                Header = "Close",
                IsEnabled = CanCloseTab(state.IsProtected)
            };
            closeItem.Click += async (_, __) => await CloseTabAsync(tab);
            flyout.Items.Add(closeItem);

            var closeOthersItem = new MenuItem
            {
                Header = "Close Others",
                IsEnabled = hasClosableOthers
            };
            closeOthersItem.Click += async (_, __) => await CloseOtherTabsAsync(tab);
            flyout.Items.Add(closeOthersItem);

            flyout.Items.Add(new Separator());

            var renameItem = new MenuItem { Header = "Rename..." };
            renameItem.Click += async (_, __) => await RenameTabAsync(tab);
            flyout.Items.Add(renameItem);

            var copyTitleItem = new MenuItem { Header = "Copy Title" };
            copyTitleItem.Click += async (_, __) => await CopyTabTitleAsync(tab);
            flyout.Items.Add(copyTitleItem);

            flyout.Items.Add(new Separator());

            var pinItem = new MenuItem { Header = GetPinTabActionLabel(state.IsPinned) };
            pinItem.Click += (_, __) => TogglePinTab(tab);
            flyout.Items.Add(pinItem);

            var protectItem = new MenuItem { Header = GetProtectTabActionLabel(state.IsProtected) };
            protectItem.Click += (_, __) => ToggleProtectTab(tab);
            flyout.Items.Add(protectItem);
        }

        private bool TryEnsureLiveTab(TabItem tab)
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            return tabs?.Items.Cast<TabItem>().Contains(tab) == true;
        }

        private static Control? FindTabHeaderHost(TabItem tab)
        {
            return tab.Header as Control;
        }

        private string GetTabMenuLabel(TabItem tab, int index)
        {
            var state = GetOrCreateTabState(tab);
            string icon = state.IsPinned ? "\U0001F4CC " : string.Empty;
            if (state.HasBell) icon += "\U0001F514 ";
            else if (state.HasActivity) icon += "\u2022 ";
            if (state.AgentTier == AgentHost.AgentAttentionTier.Wrote) icon += AgentWroteGlyph + " ";
            else if (state.AgentTier == AgentHost.AgentAttentionTier.Watched) icon += AgentWatchedGlyph + " ";
            string label = GetTabHeaderText(tab);
            return $"{index}. {icon}{label}";
        }

        internal void UpdateTabVisuals(TabItem? specificTab = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _ = specificTab;
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null)
            {
                sw.Stop();
                RendererStatistics.RecordTabVisualUpdateTime(sw.ElapsedMilliseconds);
                return;
            }

            // Token brush instances, resolved per pass but never allocated: the indicator is the
            // softened accent (NtTabIndicator), not the raw ANSI blue.
            var indicatorBrush = ThemeBrush(ThemeKeys.TabIndicator);

            var tabItems = tabs.Items.Cast<TabItem>().ToList();
            // Vertical headers show attention as trailing chips (UpdateVerticalTabExtras),
            // so their title text omits the marker suffix; horizontal headers keep the
            // suffix inside the title as before.
            var labels = BuildTabDisplayLabels(tabItems, 44, includeMarkers: !_isVerticalTabStrip);

            foreach (TabItem ti in tabItems)
            {
                ti.BorderBrush = ti.IsSelected ? indicatorBrush : Brushes.Transparent;

                // Vertical: a constant 3px left thickness on EVERY tab, selected or not, so
                // the BorderBrush line above paints the selected tab a 3px accent bar —
                // constant (rather than only-when-selected) because toggling thickness on
                // selection would shift the title 3px every time. (The selected tab's
                // PART_Border needs the companion re-assert style in MainWindow.axaml's
                // Window.Styles: App.axaml's selected underline setter outranks the template
                // binding.) Horizontal: ClearValue rather than a local Thickness(0), so no
                // leftover local value competes with the App.axaml selected style
                // ("TabItem:selected /template/Border#PART_Border", BorderThickness 0 0 0 2),
                // which keeps driving the underline — hence the asymmetry.
                if (_isVerticalTabStrip)
                {
                    ti.BorderThickness = new Thickness(3, 0, 0, 0);
                }
                else
                {
                    ti.ClearValue(TabItem.BorderThicknessProperty);
                }

                if (FindTabHeaderTextBlock(ti.Header) is TextBlock tb)
                {
                    tb.Text = labels[ti];
                }

                if (ti.Header is Control headerControl)
                {
                    ToolTip.SetTip(headerControl, BuildFullTabLabel(ti));
                }

                if (_isVerticalTabStrip)
                {
                    UpdateVerticalTabExtras(ti, GetOrCreateTabState(ti));
                }
            }

            UpdateTabAutomationLabels();
            PopulateTabListMenu();
            UpdateTabHeaderViewport();
            UpdateFocusStrip();
            sw.Stop();
            RendererStatistics.RecordTabVisualUpdateTime(sw.ElapsedMilliseconds);
        }

        private void UpdateVerticalTabExtras(TabItem tab, TabRuntimeState state)
        {
            // One pure resolve per tab per pass drives both the dot and the marker chips —
            // the vertical replacement for the title-suffix attention markers (which the
            // display-label builder no longer appends in vertical mode).
            var markers = TabStatusPresentation.ResolveTabMarkers(
                state.HasBell, state.HasActivity, state.AgentTier, _settings.AgentIndicatorTabRollup, state.IsShared);
            var dotVisual = TabStatusPresentation.ResolveTabDot(state.RenderedStatus, markers, state.HasRunningCommand);

            if (FindTabHeaderDescendant<Avalonia.Controls.Shapes.Path>(tab.Header, "TabStatusDot") is { } dot)
            {
                IBrush? dotBrush = dotVisual switch
                {
                    TabDotVisual.Working => ThemeBrush(ThemeKeys.TabWorking),
                    TabDotVisual.Attention => ThemeBrush(ThemeKeys.TabAttention),
                    TabDotVisual.AgentWrote => ThemeBrush(ThemeKeys.TabAgentWrote),
                    TabDotVisual.AgentWatched => ThemeBrush(ThemeKeys.TabAgentWatched),
                    _ => null,
                };

                // Filled shapes paint Fill; the ring paints only its Stroke.
                var shape = TabStatusPresentation.ResolveDotShape(dotVisual);
                dot.Data = shape switch
                {
                    TabDotShape.Diamond => TabDotDiamondGeometry,
                    TabDotShape.Ring => TabDotRingGeometry,
                    TabDotShape.Circle => TabDotCircleGeometry,
                    _ => null,
                };
                dot.Fill = shape is TabDotShape.Circle or TabDotShape.Diamond ? dotBrush : Brushes.Transparent;
                dot.Stroke = shape == TabDotShape.Ring ? dotBrush : null;
            }

            SetChipVisibility(tab, "TabBellChip", markers.Bell);
            SetChipVisibility(tab, "TabActivityChip", markers.Activity);
            SetChipVisibility(tab, "TabAgentWroteChip", markers.AgentWrote);
            SetChipVisibility(tab, "TabAgentWatchedChip", markers.AgentWatched);
            SetChipVisibility(tab, "TabSharedChip", markers.Shared);

            // OSC 9;4 progress (#271): geometry from the pure resolver; brush by kind —
            // the Working token for normal, Attention for error, the dim activity chip for
            // paused (the same tokens as the dot and chips above). Gated at render
            // time on the same setting as the taskbar, so an off switch hides existing
            // reports without discarding them.
            if (FindTabHeaderDescendant<Avalonia.Controls.ProgressBar>(tab.Header, "TabProgressBar") is { } progressBar)
            {
                var report = _settings.Osc9ProgressReportingEnabled ? state.Progress : null;
                var (barVisible, barIndeterminate, barValue) = TabProgressPresentation.ResolveBar(report);
                progressBar.IsVisible = barVisible;
                if (barVisible)
                {
                    progressBar.IsIndeterminate = barIndeterminate;
                    progressBar.Value = barValue;
                    progressBar.Foreground = ThemeBrush(report is { Kind: TerminalProgressKind.Error }
                        ? ThemeKeys.TabAttention
                        : report is { Kind: TerminalProgressKind.Paused }
                            ? ThemeKeys.TabChipActivity
                            : ThemeKeys.TabWorking);
                }
            }

            // Preview recompute is gated behind a dirty flag + throttle: with several streaming
            // tabs, this visual-refresh pass can run many times a second, and each recompute is
            // an O(rows*cols) grapheme walk under the buffer's read lock (contending with the
            // parser's writes) plus per-row string allocation. Freshness is therefore best-effort
            // (<= PreviewRefreshInterval behind the latest output) while a tab streams; the
            // 1s tab-status timer's Working -> quiet transition (and selection changes, which
            // also trigger a refresh) always fires at least one more pass after a burst ends, so
            // the final line still lands even if the last throttle window was mid-recompute.
            if (FindTabHeaderDescendant<TextBlock>(tab.Header, "TabPreviewLine") is { } preview)
            {
                if (state.PreviewDirty)
                {
                    var now = DateTime.UtcNow;
                    if (now - state.LastPreviewUpdateUtc >= PreviewRefreshInterval)
                    {
                        preview.Text = ReadPaneLastLine(ResolvePaneForTab(tab), state);
                        state.PreviewDirty = false;
                        state.LastPreviewUpdateUtc = now;
                    }
                    // else: still within the throttle window - skip this pass, stay dirty so a
                    // later pass picks it up.
                }
                // else: nothing new since the last recompute - leave the existing text alone.
            }
        }

        /// <summary>Flips one named marker chip's visibility in a vertical header. No-op when
        /// the chip is absent (e.g. the header was built by the horizontal factory), which
        /// keeps callers uniform across both header kinds.</summary>
        private static void SetChipVisibility(TabItem tab, string chipName, bool visible)
        {
            if (FindTabHeaderDescendant<TextBlock>(tab.Header, chipName) is { } chip)
            {
                chip.IsVisible = visible;
            }
        }

        private static string ReadPaneLastLine(TerminalPane? pane, TabRuntimeState state)
        {
            var buffer = pane?.Buffer;
            if (buffer == null) return string.Empty;

            // GetVisibleRowTexts takes the buffer read lock itself (NoRecursion —
            // do NOT wrap this call in another Lock.EnterReadLock).
            string[] rows = Ntilde.VT.Export.TerminalExporter.GetVisibleRowTexts(buffer);

            // Unwritten mid-row cells can surface as raw NUL graphemes; a NUL reaching the
            // preview TextBlock renders as invisible garbage, so swap it for a space and re-trim
            // (the exporter's own TrimEnd may no longer be meaningful once NULs become spaces).
            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i].IndexOf('\0') >= 0)
                {
                    rows[i] = rows[i].Replace('\0', ' ').TrimEnd();
                }
            }

            return state.Preview.Update(rows);
        }
    }
}
