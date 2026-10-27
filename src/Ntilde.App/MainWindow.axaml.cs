using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ntilde.Shell;
using Ntilde.Shell.Theming;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Input.Platform;
using Ntilde.Pty;

using Ntilde.Controls;
using Ntilde.Services.Ssh;
using Ntilde.Backup;
using Ntilde.Shell.Shortcuts;

namespace Ntilde
{
    public partial class MainWindow : Window, AgentHost.IAgentActionExecutor
    {
        internal readonly record struct ShellOpenRequest(string FileName, string? Arguments);

        private TerminalPane? _currentPaneValue;
        private TerminalPane? _currentPane
        {
            get => _currentPaneValue;
            set
            {
                if (_currentPaneValue != value)
                {
                    if (_currentPaneValue != null) _currentPaneValue.IsActivePane = false;
                    _currentPaneValue = value;
                    if (_currentPaneValue != null) _currentPaneValue.IsActivePane = true;
                }
            }
        }
        private readonly Dictionary<TabItem, TerminalPane> _activePaneByTab = new();
        private bool _windowIconLoaded;
        private TerminalSettings _settings;
        private GlobalHotkey? _globalHotkey;

        // Lazy. The first Apply can arrive from the startup SelectionChanged — before
        // the window is shown, when TryGetPlatformHandle still returns null — so
        // TaskbarProgress re-reads the handle on each init attempt rather than
        // capturing it at construction.
        private Ntilde.Shell.Native.TaskbarProgress? _taskbarProgress;
        private Ntilde.Shell.Native.TaskbarProgress Taskbar => _taskbarProgress ??= new Ntilde.Shell.Native.TaskbarProgress(this);

        /// <summary>Test-only seam: the window's quake-mode hotkey, so a test can assert that
        /// OnOpened re-running keeps the one instance (inert under headless - no HWND to hook).</summary>
        internal GlobalHotkey? GlobalHotkeyForTest => _globalHotkey;

        // Started at the end of the constructor, not from SetupCommandPalette() - that method is
        // lazy (runs on palette-open / settings-save), so starting the scheduler there would mean
        // automatic snapshots only begin after the user's first palette open. Disposed in
        // PerformAppTeardown alongside the window's other constructor-owned services.
        private SnapshotScheduler? _snapshotScheduler;
        private readonly SshConnectionService _sshConnectionService;
        private readonly ISshInteractionService _sshInteractionService;
        private readonly SshLegacyProfileMigrationService _sshLegacyMigrationService;
        private static readonly TimeSpan BellDebounceWindow = TimeSpan.FromMilliseconds(750);

        /// <summary>
        /// Minimum spacing between preview-line recomputes for a given tab while it is marked
        /// dirty. Recomputing on every batched visual refresh is O(rows*cols) grapheme reads
        /// under the buffer's read lock per tab per pass (contending with the parser's writes),
        /// which gets expensive with several streaming tabs refreshing many times a second. See
        /// <see cref="UpdateVerticalTabExtras"/> for the dirty+throttle gate this backs.
        /// </summary>
        private static readonly TimeSpan PreviewRefreshInterval = TimeSpan.FromMilliseconds(250);
        private bool _isVerticalTabStrip;
        internal bool IsVerticalTabStripActive => _isVerticalTabStrip;

        /// <summary>Shows the "Attach to session…" picker; null = cancelled. A seam so tests choose without a modal.</summary>
        internal Func<IReadOnlyList<Ntilde.Shell.Mux.MuxPickerItem>, Task<Ntilde.Shell.Mux.MuxPickerItem?>> PickMuxSession { get; set; }
        private readonly DispatcherTimer _updateCheckTimer = new() { Interval = TimeSpan.FromSeconds(10) };
        // Guards the OnOpened wiring below against re-entry: quake mode's Hide()/Show() round
        // trip re-raises OnOpened (Avalonia clears _shown on Hide and ShowCore raises it again
        // on Show), and without this flag every re-show would re-arm the timer, double-subscribe
        // the toast buttons, and replace a coordinator that might be holding a staged update.
        private bool _updateChecksStarted;
        // Same once-per-window rule for the daemon warm-up (spec §9: once-per-process mux init is
        // guarded against OnOpened re-raising). WarmUp is itself idempotent, so this is belt and
        // braces: it keeps the warm-up a one-time event even if a caller later moves into
        // OnOpened, which quake Hide()/Show() re-raises.
        private bool _muxWarmupStarted;
        private readonly StartupOrchestrator _startup;

        /// <summary>
        /// The one Command Assist dependency graph, built at the App composition root and handed to
        /// every pane this window creates. Replaces the static <c>CommandAssistInfrastructure</c>.
        /// </summary>
        private readonly CommandAssistServices _commandAssistServices;
        // Not readonly: it follows the SessionPersistence setting (ApplySettingsWindowResult).
        private Ntilde.Pty.ITerminalSessionFactory _sessionFactory;

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            _startup.Mark(StartupPhase.WindowOpened);
            // Once per window, not once per OnOpened: the hotkey's own Hide()/Show() round trip
            // re-raises OnOpened (see _updateChecksStarted). Each GlobalHotkey subclasses the
            // WNDPROC and chains to whatever it replaced, so a second instance would leave the
            // first one's thunk in that chain with nothing rooting it. After the next GC, every
            // window message would call freed memory (the crash GlobalHotkey.RemoveHook
            // describes). Keeping the one instance also lets PerformAppTeardown's Dispose
            // restore the original WNDPROC.
            if (_settings.QuakeModeEnabled && _globalHotkey == null)
            {
                try
                {
                    _globalHotkey = new GlobalHotkey(this);
                    _globalHotkey.OnHotkeyPressed += ToggleVisibility;
                    bool parsed = GlobalHotkey.TryParse(_settings.GlobalHotkey, out uint modifiers, out uint key);
                    if (parsed && !_globalHotkey.Register(modifiers, key) && OperatingSystem.IsWindows())
                    {
                        AppLogger.Log($"[GlobalHotkey] Windows refused '{_settings.GlobalHotkey}' (another program may hold it); using Alt+`.");
                        parsed = false;
                    }
                    else if (!parsed)
                    {
                        AppLogger.Log($"[GlobalHotkey] '{_settings.GlobalHotkey}' is not a hotkey Ntilde can register; using Alt+`.");
                    }

                    if (!parsed) _globalHotkey.Register(GlobalHotkey.DefaultModifiers, GlobalHotkey.DefaultKey);
                }
                catch { /* Ignore P/Invoke errors on non-Windows */ }
            }

            FocusCurrentTerminal(defer: true);

            // Reap leftover clipboard-paste temp images from previous runs (best-effort).
            System.Threading.Tasks.Task.Run(() =>
                Ntilde.Platform.Input.ClipboardImage.CleanUpOldTempImages(TimeSpan.FromHours(24)));

            // OnOpened is re-raised on every quake-mode hide/show, so everything below must run
            // exactly once per process - see _updateChecksStarted's doc comment for why.
            if (!_updateChecksStarted)
            {
                _updateChecksStarted = true;

                var updateToastClose = this.FindControl<Button>("UpdateToastClose");
                if (updateToastClose != null)
                {
                    updateToastClose.Click += (_, __) => HideUpdateToast();
                }

                var updateToastRestart = this.FindControl<Button>("UpdateToastRestart");
                if (updateToastRestart != null)
                {
                    updateToastRestart.Click += (_, __) => ApplyStagedUpdate();
                }

                // The coordinator itself is NOT constructed here - see EnsureUpdateCoordinator's
                // doc comment for why building it belongs off this measured startup path. Only
                // the one-shot timer is armed; its tick is what actually builds the coordinator.
                // async void by way of an async event handler, which is safe here precisely
                // because RunAutomaticCheckSafeAsync catches and logs everything itself: there is
                // no exception left to escape into the void. Awaiting it rather than discarding
                // the task with `_ =` also keeps the continuation on the UI thread.
                _updateCheckTimer.Tick += async (_, __) =>
                {
                    // Once per launch, not every 10 seconds.
                    _updateCheckTimer.Stop();
                    EnsureUpdateCoordinator();
                    if (_updateCoordinator != null)
                    {
                        await RunAutomaticCheckSafeAsync();
                    }
                };
                _updateCheckTimer.Start();
            }
        }

        private void TopLevel_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            // Strictly above: the content starts at the band's height, and its first row is not part of the band.
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
                && e.GetPosition(this).Y < WindowDragBandHeight(_isFocusMode))
            {
                OnTitleBandPressed(e);
            }
        }

        // The last press OnTitleBandPressed acted on, and where on the screen it was.
        private PointerPressedEventArgs? _lastTitleBandPress;
        private PixelPoint _screenPointOfLastTitleBandPress;

        /// <summary>
        /// A left press on the title band (focus mode's strip included): a double-click maximizes or restores the window,
        /// any other press drags it. The band is the window's own content, not the system title bar, so Windows sees no
        /// caption double-click of its own there.
        /// </summary>
        /// <remarks>
        /// Avalonia counts a double-click by the point in the window, and a drag carries that point along with the window:
        /// grabbing the band again right after a quick drag reads as a double-click. A native caption compares points on
        /// the screen, so the second press also has to be within the double-click box of the first there - the same few
        /// pixels of give, so a double-click whose first press nudged the window still counts.
        /// </remarks>
        private void OnTitleBandPressed(PointerPressedEventArgs e)
        {
            // The title bar's own handler and MainRoot's both see a press on the title bar: act on it once.
            if (ReferenceEquals(e, _lastTitleBandPress)) return;
            PixelPoint onScreen = this.PointToScreen(e.GetPosition(this));
            bool sameSpotOnScreen = WithinDoubleClickBox(_screenPointOfLastTitleBandPress, onScreen);
            _lastTitleBandPress = e;
            _screenPointOfLastTitleBandPress = onScreen;

            if (e.ClickCount == 2 && sameSpotOnScreen)
            {
                WindowState = ToggledMaximizeState(WindowState);
                // Once: the title bar's own handler runs first, and this keeps MainRoot's from toggling it back.
                e.Handled = true;
                return;
            }

            BeginMoveDrag(e);
        }

        /// <summary>
        /// Whether two screen points are within the platform's double-click box of each other: the box Avalonia measures a
        /// double-click by inside the window, in physical pixels.
        /// </summary>
        private bool WithinDoubleClickBox(PixelPoint first, PixelPoint second)
        {
            Size box = Application.Current?.PlatformSettings?.GetDoubleTapSize(PointerType.Mouse) ?? new Size(4, 4);
            return Math.Abs(second.X - first.X) <= box.Width * RenderScaling / 2
                && Math.Abs(second.Y - first.Y) <= box.Height * RenderScaling / 2;
        }

        private void ToggleVisibility()
        {
            if (this.IsVisible)
            {
                if (this.IsActive)
                {
                    // Visible and Focused -> Hide
                    this.Hide();
                }
                else
                {
                    // Visible but Blur -> Focus
                    this.Activate();
                    this.Focus();
                    FocusCurrentTerminal(defer: true);
                }
            }
            else
            {
                // Hidden -> Show
                this.Show();
                this.WindowState = WindowState.Normal;
                this.Activate();
                this.Focus();
                FocusCurrentTerminal(defer: true);
            }
        }

        // Designer + legacy-test forwarder. Production callers must use the
        // typed ctor via App.OnFrameworkInitializationCompleted.
        public MainWindow() : this(AppServices.BuildForDesigner())
        {
        }

        public MainWindow(AppServiceBundle services)
        {
            ArgumentNullException.ThrowIfNull(services);
            _startup = services.Startup;
            _commandAssistServices = services.CommandAssist;
            // Assigned here rather than as a field initializer: an instance method group cannot
            // be referenced from a field initializer (CS0236, "this" isn't available yet).
            ConfirmSessionLossForUpdate = ShowUpdateSessionLossConfirmationAsync;
            PickMuxSession = ShowMuxSessionPickerAsync;
            ConfirmSharedClose = ShowSharedCloseDialogAsync;
            ConfirmFirstClose = ShowFirstCloseDialogAsync;
            ConfirmQuitAndCloseAll = ShowQuitAndCloseAllDialogAsync;
            ConfirmMuxRestart = ShowMuxRestartDialogAsync;
            InitializeComponent();
            _startup.Checkpoint("MainWindow.AfterInitializeComponent");
            _settings = services.Settings ?? TerminalSettings.Load();
            // Before the factory choice below, which builds the daemon host through it for KeepOnClose.
            MuxHostFactory = services.MuxHostFactory;
            // Decided here, once _settings exists and long before the first tab (restore or
            // AddTab below) creates a pane: every pane is wired with this factory.
            _sessionFactory = ChooseSessionFactory(services.SessionFactory);
            // Started this early (not at the end of the ctor) so the daemon connect, and a daemon
            // spawn if none is running, overlaps the UI setup below instead of the first pane's
            // Create blocking on all of it.
            StartMuxWarmupOnce();
            // Before anything is shown: the Window theme reads this through a DynamicResource, so
            // every window opened from here on - this one included - lays out at the saved scale.
            UiScale.Apply(_settings.UiScale);
            _startup.Checkpoint("MainWindow.AfterSettingsLoad");
            _commandPaletteUsageStore = new CommandPaletteUsageStore(AppPaths.CommandPaletteUsageFilePath);
            _commandPaletteUsage = new Dictionary<string, CommandPaletteUsageEntry>(_commandPaletteUsageStore.Load(), StringComparer.OrdinalIgnoreCase);
            _sshConnectionService = new SshConnectionService();
            _sshInteractionService = new SshInteractionService(() => this, ThemedWindowChrome.Apply);
            // Read through a lambda when an endpoint is first used, so this (or a test's later
            // replacement) is in place before any pane can ask for a remote host.
            RemoteMuxHostFactory = CreateRemoteMuxHost;
            ShowRemoteMuxInstall = ShowRemoteMuxInstallDialogAsync;
            OpenRemoteMuxInstall = OpenRemoteMuxInstallForProfile;
            _sshLegacyMigrationService = new SshLegacyProfileMigrationService();

            if (_sshLegacyMigrationService.MigrateLegacyProfiles(_settings))
            {
                _settings.Save();
            }
            _startup.Checkpoint("MainWindow.AfterLegacyMigration");

            ConnectAgentHost();

            WireWindowEvents();

            var tabs = this.FindControl<TabControl>("Tabs");
            WireTitleBar();

            if (tabs != null)
            {
                tabs.SelectionChanged += (_, e) => OnTabsSelectionChanged(tabs, e);
            }
            _startup.Checkpoint("MainWindow.AfterCoreUiWireup");

            ApplyThemeToUI();
            _startup.Checkpoint("MainWindow.AfterApplyTheme");

            WireTitleBarMenuItems();

            WireIndicatorAndToastButtons();

            // Global Focus Tracking
            this.AddHandler(GotFocusEvent, (s, e) =>
            {
                var pane = (e.Source as Control)?.FindAncestorOfType<TerminalPane>();
                if (pane != null)
                {
                    UpdateActivePane(pane);
                }
            }, RoutingStrategies.Bubble | RoutingStrategies.Tunnel);

            OpenInitialTabs(tabs, services);

            // SetupCommandPalette() is lazy (runs on palette open / settings save), so
            // prime the toolbar shortcut tooltips here for the initial window state.
            UpdateShortcutTooltips();

            // Keyboard Shortcuts
            this.AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

            this.AddHandler(TextInputEvent, (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Text))
                {
                    BroadcastTextToSiblingPanes(e.Text);
                }
            }, RoutingStrategies.Tunnel);

            InitializeVault();

            // Applies TabStripOrientation for the initial window. Like RebuildTitleBar below,
            // this cannot wait for SetupCommandPalette() — that is lazy and never runs at startup.
            ApplyTabLayout();

            // Built here rather than from SetupCommandPalette(), which is lazy and does not run at
            // startup: the initial window's title bar has to exist before the user opens anything.
            RebuildTitleBar();

            // Snapshot scheduling starts with the app, not with the first palette open - see the
            // field's remarks. Start() is best-effort (never throws even if a watch target is
            // missing), so this can't fail the constructor.
            _snapshotScheduler = new SnapshotScheduler(new BackupService(AppPaths.RootDirectory, log: AppLogger.Log), log: AppLogger.Log);
            _snapshotScheduler.Start();

            _startup.Checkpoint("MainWindow.CtorComplete");
        }

        /// <summary>
        /// Connects this window to the agent host: its settings gates, the act executor and the windowless sessions,
        /// and the attention rollup onto tabs.
        /// </summary>
        private void ConnectAgentHost()
        {
            // Agent-host observe endpoint (docs/agent-host/DIRECTION.md, A1):
            // strictly no-op unless the user opted in via Settings. The replay
            // export sub-gate (A4) and the act gate + SSH allowlist (A3) are pushed
            // alongside so the endpoint checks the current settings on every
            // request. Screenshots (A5) ride the observe toggle alone.
            AgentHost.AgentHostService.Instance.ReplayExportEnabled = _settings.AgentReplayExportEnabled;
            AgentHost.AgentHostService.Instance.ActEnabled = _settings.AgentAccessActEnabled;
            AgentHost.AgentHostService.Instance.SetSshProfileAllowlist(IsSshProfileAgentAllowed);
            AgentHost.AgentHostService.Instance.SetActionExecutor(this);
            AgentHost.AgentHostService.Instance.SetWindowlessSource(WindowlessSessions);
            AgentHost.AgentHostService.Instance.Apply(_settings.AgentAccessObserveEnabled);
            AgentHost.AgentHostService.Instance.ObserveActivityChanged += OnAgentObserveActivityChanged;
            RefreshAgentObserveIndicator();

            // Tab-label rollup: mirror each pane's attention tier onto its
            // owning tab. Subscribe to sessions already registered (a pane can
            // register before MainWindow's constructor reaches this point is
            // not expected today, but costs nothing to handle) and to future
            // registrations via the registry's lifecycle events.
            AgentHost.AgentSessionRegistry.Instance.SessionRegistered += OnAgentSessionRegisteredForAttention;
            AgentHost.AgentSessionRegistry.Instance.SessionUnregistered += OnAgentSessionUnregisteredForAttention;
            foreach (var registration in AgentHost.AgentSessionRegistry.Instance.GetRegistrations())
            {
                // Same wiring the lifecycle event applies, so a session that
                // beat the subscription also gets the window-visibility seed.
                OnAgentSessionRegisteredForAttention(registration);
            }
        }

        /// <summary>
        /// The window's own events: the deferred UI setup once it is loaded, activation, resizing and the recording
        /// toast's timer.
        /// </summary>
        private void WireWindowEvents()
        {
            // Ensure visual tree is ready for initial tab border
            this.Loaded += (s, e) =>
            {
                // Give layout one more tick to settle
                Dispatcher.UIThread.Post(() =>
                {
                    _startup.Checkpoint("MainWindow.LoadedPostStart");
                    UpdateTabVisuals();
                    UpdateTabHeaderViewport();
                    EnsureSelectedTabHeaderVisible();
                    FocusCurrentTerminal(defer: true);
                    SyncRecordingButtonState();
                    PopulateNewTabMenu();
                    InitializeCommandPaletteUI();
                    InitializeTransferCenterUI();
                    _startup.Checkpoint("MainWindow.LoadedPostUiReady");
                    _startup.Mark(StartupPhase.DeferredWorkComplete);
                    Dispatcher.UIThread.Post(EnsureWindowIconLoaded, DispatcherPriority.Background);
                }, DispatcherPriority.Input);
            };
            this.Activated += (s, e) => FocusCurrentTerminal(defer: true);
            // Spec §7.5, second half: back in this window, the focused mux pane's grid wins again
            // over a size another client set meanwhile.
            this.Activated += (_, _) => _currentPane?.ReassertMuxGrid();
            // Window activation feeds the agent attention machines' focus
            // signal (see PushAgentWindowVisibility). Deliberately separate
            // subscriptions rather than folded into the focus handler above:
            // this half must also run on Deactivated, which has no terminal to
            // focus.
            this.Activated += (_, _) => SetAgentWindowActivated(true);
            this.Deactivated += (_, _) => SetAgentWindowActivated(false);
            this.SizeChanged += (_, __) => Dispatcher.UIThread.Post(UpdateTabHeaderViewport, DispatcherPriority.Background);
            _recordingToastTimer.Tick += (_, __) =>
            {
                _recordingToastTimer.Stop();
                HideRecordingToast();
            };
        }

        /// <summary>
        /// The title bar: dragging the window by it, re-fitting the tab headers when it resizes, and its reserve for
        /// the caption buttons.
        /// </summary>
        private void WireTitleBar()
        {
            var titleBar = this.FindControl<Grid>("TitleBar");

            if (titleBar != null)
            {
                titleBar.PointerPressed += (s, e) =>
                {
                    if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                        OnTitleBandPressed(e);
                };
                titleBar.SizeChanged += (_, __) => Dispatcher.UIThread.Post(UpdateTabHeaderViewport, DispatcherPriority.Background);

                // XAML sets Margin="0,4,140,0" as a pre-measurement floor for drawn caption buttons
                // on the right. On macOS the system traffic lights are on the left, so collapse the
                // right reservation so the custom buttons (+, tab list, record, …, settings) sit
                // flush against the edge. Everywhere else UpdateCaptionButtonReserve measures it.
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    var m = titleBar.Margin;
                    titleBar.Margin = new Thickness(m.Left, m.Top, CaptionReserveGutter, m.Bottom);
                }
            }

            // Everywhere else the right reserve is measured off the real caption buttons rather than
            // guessed, which is what makes the XAML default a floor and not the answer.
            WireCaptionButtonReserve();
        }

        /// <summary>
        /// The title bar's menu items: the "+" button's flyout and the context menu's Customize Title Bar.
        /// </summary>
        private void WireTitleBarMenuItems()
        {
            var menuManage = this.FindControl<MenuItem>("MenuManageProfiles");
            if (menuManage != null) menuManage.Click += async (s, e) =>
            {
                await OpenSettings(ManageProfilesSettingsTarget());
            };

            var menuCustomizeTitleBar = this.FindControl<MenuItem>("MenuCustomizeTitleBar");
            if (menuCustomizeTitleBar != null) menuCustomizeTitleBar.Click += async (s, e) =>
            {
                // Opens the Tabs & title bar page, where the title bar layout lives.
                await OpenSettings(CustomizeTitleBarSettingsTarget());
            };

            if (this.FindControl<MenuItem>("MenuExitFocusMode") is { } menuExitFocus)
                menuExitFocus.Click += (s, e) => LeaveFocusMode();
            if (this.FindControl<MenuItem>("MenuTitleBarExitFocusMode") is { } menuTitleBarExitFocus)
                menuTitleBarExitFocus.Click += (s, e) => LeaveFocusMode();

            var menuNewSsh = this.FindControl<MenuItem>("MenuNewSshConnection");
            if (menuNewSsh != null) menuNewSsh.Click += async (s, e) =>
            {
                await ShowNewSshConnectionDialogAsync(null);
            };

            var menuAgentActivity = this.FindControl<MenuItem>("MenuAgentActivity");
            if (menuAgentActivity != null) menuAgentActivity.Click += async (s, e) =>
            {
                await ShowAgentActivityJournalAsync();
            };

            var menuAbout = this.FindControl<MenuItem>("MenuAbout");
            if (menuAbout != null) menuAbout.Click += async (s, e) =>
            {
                await ShowAboutWindowAsync();
            };
        }

        /// <summary>The agent observe indicator's click, and the recording toast's buttons.</summary>
        private void WireIndicatorAndToastButtons()
        {
            // Wired once, here, and never again: PlaceAgentObserveIndicator re-parents this exact
            // instance on every RebuildTitleBar instead of recreating it, so this subscription
            // survives every rebuild. (BtnRecord's old wiring is gone - PR #342 moved Record into
            // the generated title bar, where TitleBarViewFactory drives it through a Command.)
            var agentObserveIndicator = this.FindControl<Button>("AgentObserveIndicator");
            if (agentObserveIndicator != null)
            {
                agentObserveIndicator.Click += async (_, _) => await ShowAgentActivityJournalAsync();
            }

            var recordingToastClose = this.FindControl<Button>("RecordingToastClose");
            if (recordingToastClose != null)
            {
                recordingToastClose.Click += (_, __) => HideRecordingToast();
            }

            var recordingToastOpenFolder = this.FindControl<Button>("RecordingToastOpenFolder");
            if (recordingToastOpenFolder != null)
            {
                recordingToastOpenFolder.Click += (_, __) => OpenRecordingToastFolder();
            }

            var recordingToastAction = this.FindControl<Button>("RecordingToastAction");
            if (recordingToastAction != null)
            {
                recordingToastAction.Click += (_, __) => RunRecordingToastAction();
            }
        }

        /// <summary>
        /// The tabs the window opens with: the restored session or one default tab, then orphaned mux sessions and the
        /// deferred restore.
        /// </summary>
        private void OpenInitialTabs(TabControl? tabs, AppServiceBundle services)
        {
            var defaultProfile = _settings.Profiles.Find(p => p.Id == _settings.DefaultProfileId) ?? _settings.Profiles[0];

            // Attempt to restore session
            NtildeSession? restoredSession = null;
            if (tabs != null)
            {
                if (!TryRestoreStartupSession(tabs, services.SessionsCannotPredateUtc, out NtildeSession? loadedSession))
                {
                    AddTab(defaultProfile);
                    _startup.CompleteWithoutRestore();
                }
                else
                {
                    restoredSession = loadedSession;
                    if (loadedSession!.FocusMode) EnterFocusMode();
                }
            }
            else
            {
                AddTab(defaultProfile);
                _startup.CompleteWithoutRestore();
            }

            // Spec §9 orphans. Runs whether or not a session was restored (a crash before the first
            // save leaves no file at all). Only a restore that went ahead references anything: an
            // aborted one reopens none of its panes, so their daemon sessions are orphans too.
            // The local daemon only: a remote endpoint's sessions are not adopted (Phase 4 spec §5).
            if (_muxHosts is { } startupMuxHosts)
            {
                _ = AdoptOrphanedMuxSessionsAsync(startupMuxHosts.Local, Ntilde.Shell.Mux.MuxOrphans.CollectReferencedIds(restoredSession));
            }

            if (_startup.HasPendingDeferredRestore)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var tabsControl = this.FindControl<TabControl>("Tabs");
                    if (tabsControl == null)
                    {
                        return;
                    }
                    _startup.DrainDeferred(deferredTab => HydrateDeferredStartupTab(tabsControl, deferredTab));
                }, DispatcherPriority.Background);
            }
            _startup.Checkpoint("MainWindow.AfterInitialTabs");
        }

        /// <summary>
        /// Opens the credential vault, and says so when there is no system keychain to keep passwords in.
        /// </summary>
        private void InitializeVault()
        {
            try
            {
                Vault = new VaultService();
                if (!Vault.PersistenceAvailable)
                {
                    ShowUnpromptedToast(
                        "Credential storage unavailable",
                        "No system keychain was found, so SSH passwords won't be saved this session.",
                        "focus:credential-storage");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Vault] Init failed: {ex.Message}");
            }
        }

        public static VaultService? Vault { get; private set; }

        private void EnsureWindowIconLoaded()
        {
            if (_windowIconLoaded)
            {
                return;
            }

            using var iconStream = AssetLoader.Open(new Uri("avares://Ntilde/Assets/ntilde_icon.ico"));
            Icon = new WindowIcon(iconStream);
            _windowIconLoaded = true;
        }

        private async Task PasteFromClipboardAsync()
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel?.Clipboard == null || _currentPane?.Session == null)
                {
                    return;
                }

                var clipboard = topLevel.Clipboard;
                var text = await clipboard.TryGetTextAsync();
                if (!string.IsNullOrEmpty(text))
                {
                    // Normalize line endings to avoid double newlines on paste
                    _currentPane.NotifyCommandAssistPaste(text);
                    text = Ntilde.Platform.Input.TerminalInputSender.PreparePaste(
                        text,
                        _currentPane.Buffer?.Modes.IsBracketedPasteMode == true);

                    _currentPane.ScrollToInputLine();
                    _currentPane.Session.SendInput(text);
                    return;
                }

                // No text on the clipboard. If it holds an image (e.g. a screenshot), save it
                // to a temp PNG and send the path so a running CLI such as Claude Code can read
                // the image. This mirrors Ntilde's existing file-drop behavior.
                var bitmap = await clipboard.TryGetBitmapAsync();
                if (bitmap != null)
                {
                    try
                    {
                        string path = Ntilde.Platform.Input.ClipboardImage.GetTempImagePath(".png");
                        bitmap.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);

                        // In a WSL session the Linux CLI can't resolve a C:\ path, so map it to
                        // its /mnt/<drive> form — mirroring the file-drop path handling.
                        bool isWsl = _currentPane.Session.ShellCommand?.Contains("wsl", StringComparison.OrdinalIgnoreCase) ?? false;
                        string sendPath = isWsl
                            ? Ntilde.Platform.Input.ClipboardImage.ToWslMountPath(path)
                            : path;
                        _currentPane.NotifyExternalInputSent();
                        _currentPane.ScrollToInputLine();
                        _currentPane.Session.SendInput(Ntilde.Platform.Input.ClipboardImage.QuotePathForInput(sendPath));
                    }
                    finally
                    {
                        bitmap.Dispose();
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// One of the app-wide theme token brushes (see <see cref="ThemeKeys"/>). Code-built chrome
        /// holds this INSTANCE, which ThemeService recolours in place, so it follows every theme
        /// switch without being rebuilt and nothing allocates a brush per pass.
        /// </summary>
        private IBrush ThemeBrush(string key) => (IBrush)this.FindResource(key)!;

        /// <summary>
        /// The "+" flyout's "About Ntilde...". The window only renders and reports; the
        /// update machinery stays here so a check started from About shares the coordinator, the
        /// in-flight guard and the announce-once state with the palette's manual check and the
        /// deferred startup check. Wiring is by property, the same way SettingsWindow is.
        /// Also reached from the macOS application menu's "About Ntilde" (see App).
        /// </summary>
        internal async System.Threading.Tasks.Task ShowAboutWindowAsync()
        {
            var about = new UI.About.AboutWindow();
            about.RunUpdateCheck = () => CheckForUpdatesInteractiveAsync(about);
            about.ApplyStagedUpdate = ApplyStagedUpdate;
            about.StagedVersionProvider = () => _updateCoordinator?.StagedVersion;

            ThemedWindowChrome.Apply(about);
            await about.ShowDialog(this);
        }
    }
}
