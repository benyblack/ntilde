using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Ntilde.Shell;
using Ntilde.Shell.Theming;
using Avalonia.Media;
using Ntilde.Shell.TitleBar;

namespace Ntilde
{
    /// <summary>
    /// The title bar: its buttons and their placement, the theme applied to the window chrome, and the
    /// transparency hints.
    /// </summary>
    public partial class MainWindow
    {
        // Ids of stateful title bar toggles that are currently ON. An overflowed toggle in this set
        // is auto-surfaced into the bar by TitleBarLayoutResolver, which is how Record stays visible
        // while recording without being permanently pinned.
        private readonly HashSet<string> _activeTitleBarToggles = new(StringComparer.OrdinalIgnoreCase);

        private void ApplyThemeToUI()
        {
            var theme = _settings.ActiveTheme;

            // App-wide tokens, the Fluent palette and the variant. Every themed surface, this
            // window's chrome included, updates from this one call; what follows is only the
            // window-specific part the tokens do not cover (spec section 3, "Consumers").
            ThemeService.Apply(theme, _settings.WindowOpacity);

            // The window's own backdrop: the terminal background at the window opacity, or the
            // background image. Not a token: it is the terminal surface, not UI chrome.
            var bgBrush = new SolidColorBrush(theme.Background.ToAvaloniaColor(), _settings.WindowOpacity);

            this.Background = Brushes.Transparent;
            var bgGrid = this.FindControl<Grid>("WindowBackground");
            if (bgGrid != null)
            {
                bgGrid.Background = bgBrush;
                if (!string.IsNullOrEmpty(_settings.BackgroundImagePath) && System.IO.File.Exists(_settings.BackgroundImagePath))
                {
                    try
                    {
                        var bitmap = new Avalonia.Media.Imaging.Bitmap(_settings.BackgroundImagePath);
                        bgGrid.Background = new ImageBrush(bitmap)
                        {
                            Stretch = Enum.TryParse<Stretch>(_settings.BackgroundImageStretch, out var s) ? s : Stretch.UniformToFill,
                            Opacity = _settings.BackgroundImageOpacity
                        };
                    }
                    catch { }
                }
            }

            // The window's Foreground (NtFg in XAML), the title-bar buttons (Fluent's ButtonForeground,
            // aliased to NtFg) and the theme variant (inherited from the application, which
            // ThemeService sets) all follow the tokens on their own. Only the record button's
            // recording state needs re-asserting.
            SyncRecordingButtonState();

            // The tab tokens recolour in place; this pass refreshes what depends on settings
            // that a theme apply can travel with (labels, preview lines, the overflow state).
            UpdateTabVisuals();

            var titleBar = this.FindControl<Grid>("TitleBar");
            if (titleBar != null) titleBar.Background = Brushes.Transparent;

            UpdateFocusStrip();
        }


        private void UpdateTransparencyHints()
        {
            var hints = new List<WindowTransparencyLevel>();
            switch (_settings.BlurEffect)
            {
                case "Mica": hints.Add(WindowTransparencyLevel.Mica); break;
                case "Acrylic": case "Blur": hints.Add(WindowTransparencyLevel.AcrylicBlur); hints.Add(WindowTransparencyLevel.Blur); break;
                case "None": hints.Add(WindowTransparencyLevel.Transparent); break;
                default: hints.Add(WindowTransparencyLevel.AcrylicBlur); break;
            }
            this.TransparencyLevelHint = hints;
        }

        /// <summary>
        /// Catalog id to action. Deliberately not sourced from CommandRegistry: SetupCommandPalette()
        /// is lazy — it runs on palette-open and settings-save, never at startup (see the comment
        /// near line 2207) — so a title bar reading the registry would come up dead on a cold start.
        /// </summary>
        private Dictionary<string, Action> BuildTitleBarHandlers()
        {
            return new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase)
            {
                ["new_tab"] = () => AddTab(),
                [TitleBarCatalog.OpenTabListId] = () => PopulateTabListMenu(showFlyout: true),
                ["connections"] = () => ToggleConnections(),
                ["settings"] = () => _ = OpenSettings(DefaultSettingsTarget()),
                ["toggle_recording"] = () => _currentPane?.ToggleRecording(),
                ["command_palette"] = () => ToggleCommandPalette(),
                ["find"] = () => _currentPane?.ToggleSearch(),
                ["split_vertical"] = () => SplitPane(Avalonia.Layout.Orientation.Horizontal),
                ["split_horizontal"] = () => SplitPane(Avalonia.Layout.Orientation.Vertical),
                ["sftp_remote_files"] = () => _currentPane?.ToggleRemoteFilesSidebar(),
                ["sftp_transfers"] = () => ToggleTransferCenter(),
                ["agent_activity"] = () => _ = ShowAgentActivityJournalAsync(),
            };
        }

        /// <summary>
        /// Finds a generated title bar button by catalog id. TitleBarViewFactory creates these at
        /// runtime, so they are not in the window's compile-time NameScope and FindControl can never
        /// see them — it would return null forever, silently. Scanning the host panel is the only
        /// lookup that works. Returns null when the action is set to Overflow or Hidden, which is a
        /// legitimate configuration and must stay a quiet no-op at every call site.
        /// </summary>
        private Button? FindTitleBarButton(string catalogId)
        {
            var host = this.FindControl<StackPanel>("TitleBarItemsHost");
            if (host == null)
            {
                return null;
            }

            string name = TitleBarViewFactory.ButtonName(catalogId);
            return host.Children.OfType<Button>().FirstOrDefault(b => b.Name == name);
        }

        private void RebuildTitleBar()
        {
            var host = this.FindControl<StackPanel>("TitleBarItemsHost");
            if (host == null)
            {
                return;
            }

            var layout = TitleBarLayoutResolver.Resolve(
                _settings.TitleBarItems,
                _settings.TitleBarOrder,
                _activeTitleBarToggles);

            TitleBarViewFactory.Populate(
                host,
                layout,
                _settings.Keybindings,
                BuildTitleBarHandlers(),
                this.FindControl<Button>(TitleBarViewFactory.NewTabButtonName),
                id => AppLogger.Log($"[TitleBar] no handler wired for catalog id '{id}'; skipping"));

            PlaceTabOverflowBadge(host);

            // Must run after PlaceTabOverflowBadge: the indicator is appended to the end of
            // the host, and the badge insert would otherwise land after it.
            PlaceAgentObserveIndicator(host);

            // The record button is recreated by every rebuild, so its active colouring has to be
            // reapplied against the new instance. The other generated buttons need nothing: their
            // foreground is Fluent's ButtonForeground, which follows the NtFg token.
            SyncRecordingButtonState();

            // Populate() just replaced TitleBarItemsHost's children synchronously, but that does not
            // itself update TitleBar.Bounds.Width - Avalonia only recomputes Bounds during the next
            // arrange pass. Calling UpdateTabHeaderViewport() synchronously right here would read the
            // STALE pre-rebuild width and recompute the same wrong margin, fixing nothing (Codex P2
            // round 7 on PR #342: saving a layout with a different pinned count, or Record
            // auto-surfacing via OnRecordingStateChanged above, could leave tabs overlapping a newly
            // widened bar - or a stale empty gap - until some unrelated tab/layout action happened to
            // trigger a recompute). Posting at Background priority is the same idiom already used by
            // the titleBar.SizeChanged and this.SizeChanged handlers wired in the constructor for this
            // identical margin: DispatcherPriority.Background (-2) sits below every layout/render
            // priority Avalonia schedules its own passes at (Loaded/UiThreadRender/Render/BeforeRender,
            // all positive), so any layout work Populate() just queued always drains first, and by the
            // time this runs TitleBar.Bounds.Width reflects the rebuilt bar. Unlike hooking
            // LayoutUpdated, a Dispatcher.Post holds no persistent delegate reference for anything to
            // leak - each call queues one self-contained, one-shot action that the dispatcher discards
            // the moment it runs, so back-to-back rebuilds (e.g. a settings save immediately followed
            // by Record auto-surfacing) cannot accumulate subscriptions. It also cannot loop: this is a
            // single one-shot callback, not a persistent handler, and even the pre-existing
            // titleBar.SizeChanged hookup it parallels only calls UpdateTabHeaderViewport when
            // TitleBar's Bounds actually changed - once the margin here is set to match the new width,
            // recomputing again from the same width is a no-op that changes nothing and triggers
            // nothing further.
            Dispatcher.UIThread.Post(UpdateTabHeaderViewport, DispatcherPriority.Background);

            // Last: the generated items above are all fresh and visible.
            ApplyFocusModeToTitleBar();
        }

        /// <summary>
        /// Re-anchors TabOverflowBadge next to the Tab List (open_tab_list) button after every
        /// Populate() call. TitleBarViewFactory stays deliberately unaware of the badge - it is not
        /// one of the catalog actions it knows how to build - so MainWindow has to own this
        /// placement itself, and has to redo it on every RebuildTitleBar: Populate() unconditionally
        /// clears TitleBarItemsHost's children, which would otherwise orphan a badge left inside it,
        /// and a fixed position outside the host can't track the button through a user reorder
        /// (Codex P2 round 2 on PR #342 - see the parking-slot XAML comment in MainWindow.axaml for
        /// the full history).
        /// </summary>
        private void PlaceTabOverflowBadge(Panel host)
        {
            var badge = this.FindControl<TextBlock>("TabOverflowBadge");
            if (badge == null)
            {
                return;
            }

            // Detach before inserting anywhere: Avalonia throws when a control that still has a
            // logical parent is added to a different one. The badge always has a parent at this
            // point - either the parking slot (unpinned, or first run), or TitleBarItemsHost from a
            // previous rebuild (Populate's Clear() above already null'd that one out, so this is a
            // no-op in that case, but the parking-slot case still needs the explicit removal).
            if (badge.Parent is Panel currentParent)
            {
                currentParent.Children.Remove(badge);
            }

            string tabListButtonName = TitleBarViewFactory.ButtonName(TitleBarCatalog.OpenTabListId);
            var tabListButton = host.Children.OfType<Button>()
                .FirstOrDefault(b => b.Name == tabListButtonName);

            if (tabListButton is null)
            {
                // Tab List is Overflow or Hidden - no button to sit beside. Hide it and send it back
                // to the parking slot so the next rebuild has a consistent parent to detach it from,
                // and so it renders nothing and can't overlap or intercept clicks on any other
                // button while unpinned.
                badge.IsVisible = false;
                badge.Text = string.Empty;
                var parkingSlot = this.FindControl<Panel>("TitleBarBadgeParkingSlot");
                parkingSlot?.Children.Add(badge);
                return;
            }

            host.Children.Insert(host.Children.IndexOf(tabListButton) + 1, badge);
        }

        /// <summary>
        /// Re-appends AgentObserveIndicator as the last child of TitleBarItemsHost after every
        /// Populate() call, which unconditionally clears that host.
        ///
        /// The indicator is locked into the bar the same way BtnNewTab is - the XAML-declared
        /// instance is re-inserted, never rebuilt, so the Click handler wired once in the
        /// constructor and the FindControl&lt;Button&gt;/FindControl&lt;Ellipse&gt; lookups in
        /// RefreshAgentObserveIndicator keep resolving - but unlike BtnNewTab it is deliberately
        /// NOT a TitleBarCatalog entry. Catalog items are user-pinnable and therefore
        /// user-removable, and this is a safety surface with no off switch: the only way to have
        /// no indicator is to disable agent access itself (docs/mcp/security.md). Keeping it out
        /// of the catalog is what makes that unconditional, so it cannot be routed through
        /// TitleBarViewFactory's layout loop the way the + button is, and MainWindow owns the
        /// placement here instead - the same division of labour PlaceTabOverflowBadge uses for the
        /// other non-catalog control in this bar.
        ///
        /// Position: last, after everything Populate emitted including the overflow button. That is
        /// the opposite choice from the badge, on purpose. The badge annotates the Tab List button
        /// and so has to follow it wherever the user moves it; this indicator annotates the window,
        /// so anchoring it to any catalog button would hand its position to the user's layout -
        /// the same class of problem as letting them remove it. TitleBarItemsHost is right-aligned,
        /// so the final slot is also the only one whose on-screen location does not shift when
        /// items are pinned, unpinned, reordered, or auto-surfaced (Record).
        /// </summary>
        private void PlaceAgentObserveIndicator(Panel host)
        {
            var indicator = this.FindControl<Button>("AgentObserveIndicator");
            if (indicator == null)
            {
                return;
            }

            // Same detach-before-insert rule as PlaceTabOverflowBadge: Avalonia throws when a
            // control that still has a logical parent is added to another one. Populate's Clear()
            // has already null'd the parent in the steady state, so this is normally a no-op; it
            // matters on the very first rebuild, when the indicator is still the XAML-declared
            // child of a host that... has also just been cleared. Kept anyway so any future caller
            // that places it elsewhere cannot crash the window.
            if (indicator.Parent is Panel currentParent)
            {
                currentParent.Children.Remove(indicator);
            }

            host.Children.Add(indicator);
        }
    }
}
