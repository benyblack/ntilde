using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ntilde.Shell;
using Ntilde.Shell.Theming;
using Ntilde.Rendering;
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
using Ntilde.Shell.TitleBar;

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
                    // Register Alt (1) + ~ (0xC0). 
                    // MVP Hardcoded. Future: Parse _settings.GlobalHotkey
                    _globalHotkey.Register(1, 0xC0);
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
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                if (e.GetPosition(this).Y <= 36)
                {
                    BeginMoveDrag(e);
                }
            }
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

            var tabs = this.FindControl<TabControl>("Tabs");
            var titleBar = this.FindControl<Grid>("TitleBar");

            if (titleBar != null)
            {
                titleBar.PointerPressed += (s, e) =>
                {
                    if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                        BeginMoveDrag(e);
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


            if (tabs != null)
            {
                tabs.SelectionChanged += (s, e) =>
                {
                    // A reorder commit removes the selected (dragged) tab from Items and
                    // reinserts it at the drop index; TabControl's AlwaysSelected mode
                    // promotes index 0 to SelectedItem for the duration of that window (see
                    // CommitTabReorder). That promotion is an implementation artifact, not a
                    // user selection: skip it entirely (MRU touch, attention clear, focus
                    // handoff) - the restore of the dragged tab right after re-enters here
                    // with the true final selection and runs the full path.
                    if (_isTabReorderCommitting) return;

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    bool updatedSpecific = false;
                    foreach (var removed in e.RemovedItems.OfType<TabItem>())
                    {
                        UpdateTabVisuals(removed);
                        updatedSpecific = true;
                    }

                    if (tabs.SelectedItem is TabItem ti)
                    {
                        GetTabId(ti);
                        if (_suppressMruTouchOnSelection)
                        {
                            _suppressMruTouchOnSelection = false;
                        }
                        else
                        {
                            TouchTabMru(ti);
                        }
                        GetOrCreateTabState(ti);
                        ClearTabAttention(ti);
                        GetOrCreateTabState(ti).Status.NoteSelected();
                        var pane = ResolvePaneForTab(ti);
                        if (pane != null)
                        {
                            UpdateActivePane(pane);
                            FocusPaneTerminal(pane, defer: true);
                        }
                        UpdateTabVisuals(ti);
                        updatedSpecific = true;
                    }
                    if (!updatedSpecific) UpdateTabVisuals();
                    // The window light's "no visible bar" term is relative to
                    // the selected tab, so a tab switch changes its answer with
                    // no attention event behind it: the pane whose segment just
                    // came on screen must stop double-reporting, and a pane
                    // still being read in the tab just left must start.
                    RefreshAgentObserveIndicator();
                    UpdatePaneAutomationLabels();
                    UpdateTabAutomationLabels();
                    UpdateBroadcastIndicator();
                    PopulateTabListMenu();
                    UpdateTabHeaderViewport();
                    // The taskbar mirrors whichever tab is now selected (#271).
                    RefreshTaskbarProgressFromSelection();
                    Dispatcher.UIThread.Post(EnsureSelectedTabHeaderVisible, DispatcherPriority.Background);
                    sw.Stop();
                    RendererStatistics.RecordTabSwitchTime(sw.ElapsedMilliseconds);
                };
            }
            _startup.Checkpoint("MainWindow.AfterCoreUiWireup");

            ApplyThemeToUI();
            _startup.Checkpoint("MainWindow.AfterApplyTheme");

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

            // Global Focus Tracking
            this.AddHandler(GotFocusEvent, (s, e) =>
            {
                var pane = (e.Source as Control)?.FindAncestorOfType<TerminalPane>();
                if (pane != null)
                {
                    UpdateActivePane(pane);
                }
            }, RoutingStrategies.Bubble | RoutingStrategies.Tunnel);

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

            // SetupCommandPalette() is lazy (runs on palette open / settings save), so
            // prime the toolbar shortcut tooltips here for the initial window state.
            UpdateShortcutTooltips();

            // Keyboard Shortcuts
            this.AddHandler(KeyDownEvent, (s, e) =>
            {
                // An in-flight tab reorder drag owns the pointer; Escape aborts it before
                // anything else - in particular before the broadcast-to-panes fallback at
                // the bottom of this handler, which would otherwise also feed the raw ESC
                // byte to the terminals while the user is only canceling a drag. That
                // priority holds only because this handler is registered with
                // RoutingStrategies.Tunnel (the AddHandler call near the bottom of
                // OnOpened): the tunneling window handler runs before the focused
                // TerminalView's bubble-phase Escape handling (which sends "\x1b" to the
                // PTY), so the drag is canceled and the event marked handled before the
                // key could reach the pane.
                if (_isTabReorderDragging && e.Key == Key.Escape)
                {
                    CancelTabReorderDrag();
                    e.Handled = true;
                    return;
                }

                var modifiers = e.KeyModifiers;
                bool isCtrl = (modifiers & KeyModifiers.Control) != 0;
                bool isShift = (modifiers & KeyModifiers.Shift) != 0;

                // The command palette owns Ctrl+Shift+P outright since V2 Phase 3b. It used to try the
                // Command Assist pin first and fall through when the pin declined, which meant whether
                // this chord opened the palette depended on whether an assist row happened to be
                // selected - a shortcut that works most of the time is worse than one that does not.
                // Pin has its own catalogued binding below.
                if (IsShortcut(e, "command_palette", "Ctrl+Shift+P"))
                {
                    ToggleCommandPalette();
                    e.Handled = true;
                    return;
                }

                var overlay = this.FindControl<Grid>("CommandPaletteOverlay");
                if (overlay != null && overlay.IsVisible)
                {
                    // Trap keys when palette is open
                    if (e.Key == Key.Escape) ToggleCommandPalette();
                    // Let TextBox handle the rest, but prevent bubbling to terminal
                    // Actually, if we are focused on the TextBox, we don't need to do much.
                    // But if focus somehow escaped, we want to ensure we don't type in terminal.
                    return;
                }

                if (IsShortcut(e, "command_assist_toggle", "Ctrl+Space"))
                {
                    RecordCommandUsage("command_assist_toggle");
                    _currentPane?.ToggleCommandAssist();
                    e.Handled = true;
                    return;
                }

                if (IsShortcut(e, "command_assist_help", "Ctrl+Shift+H"))
                {
                    if (TryOpenCommandAssistHelp(_currentPane))
                    {
                        RecordCommandUsage("command_assist_help");
                        e.Handled = true;
                        return;
                    }
                }

                if (IsShortcut(e, "command_assist_history", "Ctrl+R"))
                {
                    if (_currentPane?.OpenCommandAssistHistorySearch() == true)
                    {
                        RecordCommandUsage("command_assist_history");
                        e.Handled = true;
                        return;
                    }
                }

                // V2 Phase 3b: pin/unpin on its own chord. Falls through when there is no row to pin,
                // so the key is not dead - it reaches the terminal like any unbound chord would.
                if (IsShortcut(e, "command_assist_pin", "Ctrl+Shift+S"))
                {
                    if (_currentPane?.TryToggleCommandAssistPinShortcut() == true)
                    {
                        RecordCommandUsage("command_assist_pin");
                        e.Handled = true;
                        return;
                    }
                }

                if (IsShortcut(e, "settings", "Ctrl+,"))
                {
                    RecordCommandUsage("settings");
                    _ = OpenSettings(DefaultSettingsTarget());
                    e.Handled = true;
                    return;
                }

                if (IsShortcut(e, "connections", "Ctrl+Shift+K"))
                {
                    RecordCommandUsage("connections");
                    ToggleConnections();
                    e.Handled = true;
                    return;
                }

                if (IsShortcut(e, "toggle_recording", "Ctrl+Shift+R"))
                {
                    RecordCommandUsage("toggle_recording");
                    _currentPane?.ToggleRecording();
                    e.Handled = true;
                    return;
                }

                if (IsShortcut(e, "font_increase", "Ctrl+OemPlus") || IsShortcut(e, "font_increase_alt", "Ctrl+Add"))
                {
                    RecordCommandUsage("font_increase");
                    _settings.FontSize++;
                    ApplySettingsToAllTabs();
                    _settings.Save();
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "font_decrease", "Ctrl+OemMinus") || IsShortcut(e, "font_decrease_alt", "Ctrl+Subtract"))
                {
                    RecordCommandUsage("font_decrease");
                    _settings.FontSize = Math.Max(6, _settings.FontSize - 1);
                    ApplySettingsToAllTabs();
                    _settings.Save();
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "new_tab", "Ctrl+Shift+T"))
                {
                    RecordCommandUsage("new_tab");
                    AddTab();
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "toggle_tab_orientation", "Ctrl+Shift+L"))
                {
                    RecordCommandUsage("toggle_tab_orientation");
                    ToggleTabOrientation();
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "close_tab", "Ctrl+W"))
                {
                    RecordCommandUsage("close_tab");
                    _ = CloseSelectedTabAsync();
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "close_pane", "Ctrl+Shift+W"))
                {
                    RecordCommandUsage("close_pane");
                    CloseActivePane();
                    e.Handled = true;
                    return;
                }
                // Unbound by default: inert until the user assigns a chord.
                if (IsMuxPersistenceActive && IsShortcut(e, ShortcutCatalog.AttachSessionId, ""))
                {
                    RecordCommandUsage(ShortcutCatalog.AttachSessionId);
                    _ = AttachToMuxSessionAsync();
                    e.Handled = true;
                    return;
                }
                if (IsMuxPersistenceActive && IsShortcut(e, ShortcutCatalog.DetachPaneId, ""))
                {
                    RecordCommandUsage(ShortcutCatalog.DetachPaneId);
                    DetachActivePane();
                    e.Handled = true;
                    return;
                }
                if (IsMuxPersistenceActive && IsShortcut(e, ShortcutCatalog.QuitAndCloseAllShellsId, ""))
                {
                    RecordCommandUsage(ShortcutCatalog.QuitAndCloseAllShellsId);
                    _ = QuitAndCloseAllShellsAsync();
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "find", "Ctrl+F") || IsShortcut(e, "find_alt", "Ctrl+Shift+F"))
                {
                    RecordCommandUsage("find");
                    _currentPane?.ToggleSearch();
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "split_vertical", "Ctrl+Shift+D"))
                {
                    RecordCommandUsage("split_vertical");
                    // "Split Vertical" means a vertical divider (side-by-side panes).
                    SplitPane(Avalonia.Layout.Orientation.Horizontal);
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "split_horizontal", "Ctrl+Shift+E"))
                {
                    RecordCommandUsage("split_horizontal");
                    // "Split Horizontal" means a horizontal divider (stacked panes).
                    SplitPane(Avalonia.Layout.Orientation.Vertical);
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "equalize_panes", "Ctrl+Shift+G"))
                {
                    RecordCommandUsage("equalize_panes");
                    EqualizeCurrentSplit();
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "toggle_pane_zoom", "Ctrl+Shift+Z"))
                {
                    RecordCommandUsage("toggle_pane_zoom");
                    TogglePaneZoomForCurrentTab();
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "toggle_broadcast_input", "Ctrl+Shift+B"))
                {
                    RecordCommandUsage("toggle_broadcast_input");
                    ToggleBroadcastForCurrentTab();
                    e.Handled = true;
                    return;
                }
                bool nextTabShortcut = IsShortcut(e, "next_tab", "Ctrl+Tab");
                bool prevTabShortcut = IsShortcut(e, "prev_tab", "Ctrl+Shift+Tab");
                if (nextTabShortcut || prevTabShortcut)
                {
                    RecordCommandUsage(prevTabShortcut ? "prev_tab" : "next_tab");
                    bool switched = SwitchTabByMru(reverse: prevTabShortcut);
                    if (switched)
                    {
                        e.Handled = true;
                        return;
                    }
                }
                bool moveTabPrevShortcut = IsShortcut(e, MoveTabPrevCommandId, "Ctrl+Shift+PageUp");
                bool moveTabNextShortcut = IsShortcut(e, MoveTabNextCommandId, "Ctrl+Shift+PageDown");
                if (moveTabPrevShortcut || moveTabNextShortcut)
                {
                    // Keyboard move is meaningless mid-drag; swallowing the shortcut keeps
                    // reorder mutations single-threaded through the drag path (cf. the
                    // Escape guard above).
                    if (_isTabReorderDragging)
                    {
                        e.Handled = true;
                        return;
                    }
                    RecordCommandUsage(moveTabPrevShortcut ? MoveTabPrevCommandId : MoveTabNextCommandId);
                    MoveSelectedTab(moveTabPrevShortcut ? -1 : 1);
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, TitleBarCatalog.OpenTabListId, "Ctrl+Shift+O"))
                {
                    RecordCommandUsage(TitleBarCatalog.OpenTabListId);
                    PopulateTabListMenu(showFlyout: true);
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "paste", "Ctrl+V"))
                {
                    RecordCommandUsage("paste");
                    _ = PasteFromClipboardAsync();
                    e.Handled = true;
                    return;
                }
                if ((modifiers & KeyModifiers.Alt) != 0)
                {
                    MoveDirection? dir = e.Key switch
                    {
                        Key.Left => MoveDirection.Left,
                        Key.Right => MoveDirection.Right,
                        Key.Up => MoveDirection.Up,
                        Key.Down => MoveDirection.Down,
                        _ => null
                    };
                    if (dir.HasValue && NavigatePane(dir.Value))
                    {
                        e.Handled = true;
                        return;
                    }
                }

                BroadcastKeyToSiblingPanes(e);
            }, RoutingStrategies.Tunnel);

            this.AddHandler(TextInputEvent, (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Text))
                {
                    BroadcastTextToSiblingPanes(e.Text);
                }
            }, RoutingStrategies.Tunnel);

            try
            {
                Vault = new VaultService();
                if (!Vault.PersistenceAvailable)
                {
                    ShowRecordingToast(
                        "Credential storage unavailable",
                        "No system keychain was found, so SSH passwords won't be saved this session.",
                        filePath: null,
                        folderPath: null,
                        autoHide: true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Vault] Init failed: {ex.Message}");
            }

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
