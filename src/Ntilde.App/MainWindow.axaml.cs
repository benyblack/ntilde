using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Controls.Presenters;
using Ntilde.Shell;
using Ntilde.Platform;
using Ntilde.VT;
using Ntilde.Rendering;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Layout;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Automation;
using Avalonia.Input.Platform;
using SkiaSharp;
using Ntilde.Pty;

using Ntilde.Controls;
using Ntilde.Services.Ssh;
using Ntilde.Backup;
using Ntilde.Platform.Ssh.Launch;
using Ntilde.Shell.Shortcuts;
using Ntilde.Models;
using Ntilde.ViewModels.Ssh;
using Ntilde.Views.Ssh;
using Ntilde.Pty;
using Ntilde.Shell.TitleBar;

namespace Ntilde
{
    public partial class MainWindow : Window, AgentHost.IAgentActionExecutor
    {
        internal readonly record struct ShellOpenRequest(string FileName, string? Arguments);
        private const string SplitterHoverClass = "splitter-hover";
        private const string SplitterDraggingClass = "splitter-dragging";

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
        private readonly Dictionary<TabItem, PaneZoomState> _paneZoomStateByTab = new();
        private readonly Dictionary<TabItem, Guid> _zoomedPaneIdByTab = new();
        private readonly HashSet<TabItem> _broadcastEnabledTabs = new();
        private readonly Dictionary<TabItem, Guid> _tabIds = new();
        private readonly Dictionary<TerminalPane, TabItem> _paneOwnerTab = new();
        private readonly Dictionary<TabItem, PaneLayoutModel> _layoutModelByTab = new();
        private readonly List<TabItem> _tabMru = new();
        private bool _windowIconLoaded;

        // The window half of the agent attention machines' focus signal; see
        // PushAgentWindowVisibility. Two fields because the two inputs arrive
        // from different places (Activated/Deactivated vs. the WindowState
        // property) and neither can be read reliably from inside the other's
        // notification: Avalonia raises Activated *before* it sets IsActive.
        private bool _agentWindowActivated;
        private bool _agentWindowVisible;
        private readonly Dictionary<TabItem, TabRuntimeState> _tabStateByTab = new();
        private readonly HashSet<TabItem> _pendingVisualRefreshTabs = new();
        private bool _suppressMruTouchOnSelection;
        private bool _tabVisualRefreshScheduled;
        private DispatcherTimer? _tabStatusTimer;
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
        // Ids of stateful title bar toggles that are currently ON. An overflowed toggle in this set
        // is auto-surfaced into the bar by TitleBarLayoutResolver, which is how Record stays visible
        // while recording without being permanently pinned.
        private readonly HashSet<string> _activeTitleBarToggles = new(StringComparer.OrdinalIgnoreCase);
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
        private bool _closePaneInProgress;
        private bool _closeTabInProgress;
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

        /// <summary>Tab-label marker for "an agent typed into a pane in this tab".</summary>
        internal const string AgentWroteGlyph = "\u2328";  // keyboard

        /// <summary>Tab-label marker for "an agent is reading a pane in this tab".</summary>
        internal const string AgentWatchedGlyph = "\U0001F441";  // eye

        /// <summary>Tab-label marker for "a pane in this tab is attached from another window too".</summary>
        internal const string SharedGlyph = "\u29C9";  // two joined squares: one shell, several windows
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

        // Hoisted out of UpdateVerticalTabExtras: that method runs per tab per visual-refresh
        // pass, and allocating a new SolidColorBrush per tab per pass adds up.
        private static readonly IBrush TabAttentionBrush = new ImmutableSolidColorBrush(Color.Parse("#FFD25A"));

        // Agent-aware tab status colors (dot + marker chips). They intentionally match the
        // in-pane agent segment (TerminalPane.ApplyAgentAttention: dot #E8A33D/#4FB0D4, text
        // #F0C07A/#7FC3DC) and the existing tab attention constant above, so the same tier
        // reads as one color everywhere it appears.
        private static readonly IBrush TabAgentWroteDotBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xFF, 0xE8, 0xA3, 0x3D));
        private static readonly IBrush TabAgentWatchedDotBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xFF, 0x4F, 0xB0, 0xD4));
        private static readonly IBrush TabBellChipBrush = TabAttentionBrush; // #FFD25A
        private static readonly IBrush TabActivityChipBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        private static readonly IBrush TabAgentWroteChipBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xFF, 0xF0, 0xC0, 0x7A));
        private static readonly IBrush TabAgentWatchedChipBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xFF, 0x7F, 0xC3, 0xDC));
        private static readonly IBrush TabSharedChipBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xFF, 0x4F, 0xB0, 0xD4));
        private bool _isVerticalTabStrip;
        internal bool IsVerticalTabStripActive => _isVerticalTabStrip;

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
        // Estimated height of one vertical tab row: the fallback CountHiddenTabs uses for
        // headers not yet measured (Bounds.Height 0 before the first layout pass) when the
        // vertical overflow pill counts hidden rows. Matches the vertical TabItem MinHeight
        // set in MainWindow.axaml's styles.
        private const double DefaultVerticalTabRowHeight = 44;
        private const double TabInsertIndicatorMinLength = 16;
        // Tag marking a template part whose one-time wiring has already happened (resize
        // grip, overflow pill), so repeated ApplyTabLayout passes cannot double-subscribe.
        private const string TemplatePartWiredTag = "wired";
        // Command ids for the keyboard move-tab actions (ShortcutCatalog keys + command
        // palette ids + usage recording must all agree).
        private const string MoveTabPrevCommandId = "move_tab_prev";
        private const string MoveTabNextCommandId = "move_tab_next";

        /// <summary>Test-only seam: exposes the mid-reorder-drag state (same pattern as
        /// <see cref="IsTabStripGripDraggingForTest"/>).</summary>
        internal bool IsTabReorderDraggingForTest => _isTabReorderDragging;

        private bool _isDraggingTransferOverlay;
        private Point _transferOverlayDragStart;
        private Point _transferOverlayOffsetStart;
        private TranslateTransform? _transferOverlayTransform;
        private readonly DispatcherTimer _recordingToastTimer = new() { Interval = TimeSpan.FromSeconds(6) };
        private string? _recordingToastFolderPath;
        private string? _recordingToastFilePath;
        private PersistenceNoticeAction? _recordingToastAction; // UI thread: what the toast's action button runs, while it is shown
        private Ntilde.Update.UpdateCoordinator? _updateCoordinator;

        /// <summary>Test seam: installs a coordinator over a fake IUpdateService.</summary>
        internal void SetUpdateCoordinatorForTest(Ntilde.Update.UpdateCoordinator? coordinator) => _updateCoordinator = coordinator;

        /// <summary>
        /// Test seam: how <see cref="ApplyStagedUpdateAsync"/> checks for a live daemon before
        /// applying an update. The default never spawns one - a daemon that is not already up
        /// does not need to be asked about.
        /// </summary>
        internal Func<CancellationToken, Task<Ntilde.Mux.MuxClient?>> MuxProbeForUpdate { get; set; } =
            ct => Ntilde.Mux.Daemon.MuxDaemonLauncher.CreateDefault(AppLogger.Log, Ntilde.Shell.Mux.MuxCommand.ServeArguments).TryConnectExistingAsync(ct);

        /// <summary>Test seam: the daemon's descriptor, read just before <c>shutdown</c> is sent for an update.</summary>
        internal Func<Ntilde.Mux.Contracts.MuxEndpointDescriptor?> MuxReadDescriptorForUpdate { get; set; } =
            () => Ntilde.Mux.Contracts.MuxDiscovery.TryReadDescriptor(Ntilde.Mux.Contracts.MuxDiscovery.GetDescriptorPath(), out var d) ? d : null;

        /// <summary>
        /// Test seam: after <c>shutdown</c> was sent for an update, completes with true once the daemon
        /// named by the descriptor has exited, or false after 5 s. Runs off the UI thread.
        /// </summary>
        internal Func<Ntilde.Mux.Contracts.MuxEndpointDescriptor, Task<bool>> MuxWaitForDaemonExitForUpdate { get; set; } =
            before => Task.Run(() => Ntilde.Mux.Daemon.MuxDaemonExit.WaitForExit(
                Ntilde.Mux.Contracts.MuxDiscovery.GetDescriptorPath(), before, TimeSpan.FromSeconds(5), Environment.ProcessId));

        /// <summary>Test seam: the confirmation shown when an update would close running mux sessions.</summary>
        internal Func<string, Task<bool>> ConfirmSessionLossForUpdate { get; set; }

        /// <summary>Shows the "Attach to session…" picker; null = cancelled. A seam so tests choose without a modal.</summary>
        internal Func<IReadOnlyList<Ntilde.Shell.Mux.MuxSessionPickerRow>, Task<Guid?>> PickMuxSession { get; set; }

        /// <summary>The shared-close prompt (spec §7.4), given how many other clients show the shell. A seam so tests answer without a modal.</summary>
        internal Func<int, Task<Ntilde.Shell.Mux.SharedCloseChoice>> ConfirmSharedClose { get; set; }

        /// <summary>The first-close question's three outcomes (spec R1): Escape or the dialog's X is <see cref="FirstCloseAction.Cancel"/>.</summary>
        internal enum FirstCloseAction { Cancel, Keep, Close }

        /// <summary>An answer to the first-close question; <paramref name="Remember"/> is its "Don't ask again".</summary>
        internal readonly record struct FirstCloseAnswer(FirstCloseAction Action, bool Remember);

        /// <summary>
        /// The first-close question (spec R1), given how many local shells the close would leave running. A seam so
        /// tests answer without a modal; the constructor assigns <see cref="ShowFirstCloseDialogAsync"/>.
        /// </summary>
        internal Func<int, Task<FirstCloseAnswer>> ConfirmFirstClose { get; set; }

        /// <summary>
        /// The "Quit and close all shells" confirmation (Task 17), given how many shells the local daemon is running
        /// (-1 when that is unknown). A seam so tests answer without a modal; the constructor assigns
        /// <see cref="ShowQuitAndCloseAllDialogAsync"/>.
        /// </summary>
        internal Func<int, Task<bool>> ConfirmQuitAndCloseAll { get; set; }

        /// <summary>Where "Don't ask again" keeps the first-close answer (R1). A seam so tests use a scratch root.</summary>
        internal Ntilde.Shell.Mux.MuxCloseChoiceStore MuxCloseChoiceStore { get; set; } = Ntilde.Shell.Mux.MuxCloseChoiceStore.Default;

        /// <summary>Test seam: runs inside <see cref="PerformAppTeardown"/> right after its one-shot guard.</summary>
        internal Action? TeardownFaultForTest { get; set; }
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
        // Prevents a manual "Check for updates" from racing the automatic check (or a second
        // manual invocation) into the same staging directory - UpdateCoordinator.RunCheckAsync
        // has no serialization of its own.
        private bool _updateCheckInFlight;
        // Guards ApplyStagedUpdateAsync against re-entrancy: the toast Restart button, the palette
        // command and About's button all reach it, and nothing else stops a second click landing
        // while the first is still probing the daemon or awaiting the confirmation dialog.
        private bool _applyStagedUpdateInProgress;
        private ConnectionManagerWindow? _connectionManagerWindow;
        private TransferCenter? _transferCenterControl;
        private readonly CommandPaletteUsageStore _commandPaletteUsageStore;
        private Dictionary<string, CommandPaletteUsageEntry> _commandPaletteUsage = new(StringComparer.OrdinalIgnoreCase);
        private readonly StartupOrchestrator _startup;

        /// <summary>
        /// The one Command Assist dependency graph, built at the App composition root and handed to
        /// every pane this window creates. Replaces the static <c>CommandAssistInfrastructure</c>.
        /// </summary>
        private readonly CommandAssistServices _commandAssistServices;
        // Not readonly: it follows the SessionPersistence setting (ApplySettingsWindowResult).
        private Ntilde.Pty.ITerminalSessionFactory _sessionFactory;

        // One daemon connection per endpoint, shared by every mux pane on it (spec §6, Phase 4 spec §5):
        // the local daemon's, plus a remote one per SSH endpoint once a pane uses it. Null while
        // persistence has never been on. Outlives a switch back to Off so the mux panes already open
        // keep working.
        private Ntilde.Shell.Mux.MuxConnectionHosts? _muxHosts;

        /// <summary>The local daemon connection, when session persistence is (or was) on. Tests, startup reattach, updates.</summary>
        internal Ntilde.Shell.Mux.MuxConnectionHost? MuxHost => _muxHosts?.Local;

        /// <summary>Every endpoint's connection (Phase 4 spec §5); null while persistence has never been on.</summary>
        internal Ntilde.Shell.Mux.MuxConnectionHosts? MuxHosts => _muxHosts;

        private Ntilde.Shell.Mux.MuxWindowlessSessions? _windowlessSessions;

        /// <summary>
        /// The agent host's windowless sessions of this window (Phase 5 spec §3, ruling R4): its daemons' sessions that no
        /// pane of it shows. Null while persistence has never been on (no hosts, so nothing to list); built once with the
        /// hosts, which are never replaced. UI thread.
        /// </summary>
        internal AgentHost.IWindowlessSessionSource? WindowlessSessions =>
            _windowlessSessions ??= _muxHosts is { } hosts
                ? new Ntilde.Shell.Mux.MuxWindowlessSessions(hosts, MuxSessionsShownHereAsync, AppLogger.Log)
                : null;

        private sealed class PaneZoomState
        {
            public required Control OriginalRoot { get; init; }
            public required Control Placeholder { get; init; }
        }

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

        private sealed class SessionRestoreAbortedException : Exception
        {
            public SessionRestoreAbortedException(string message) : base(message) { }
        }

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

        private void ToggleConnections()
        {
            // The modal dialog disables MainWindow, so a second shortcut cannot normally arrive
            // while it is open; the guard just makes a stale reference activate the existing
            // window instead of stacking a second one.
            if (_connectionManagerWindow != null)
            {
                _connectionManagerWindow.Activate();
                return;
            }

            _ = OpenConnectionManagerAsync();
        }

        /// <summary>
        /// Opens Connection Manager as a real modal window, the way <see cref="OpenSettings"/>
        /// opens Settings. The manager surface and its event wiring are recreated per open:
        /// the dialog is now the surface's only lifetime, whereas the old in-window overlay
        /// kept one control alive inside MainWindow and refreshed it in place.
        /// </summary>
        private async Task OpenConnectionManagerAsync()
        {
            var window = new ConnectionManagerWindow();
            var connManager = window.Manager;
            if (connManager == null)
            {
                return;
            }

            connManager.ApplyTheme(_settings.ActiveTheme);
            connManager.SavedPasswordAccess = Vault;
            connManager.OnQuickOpenRequested += (profile, target, diagnosticsLevel) =>
            {
                HandleSshQuickOpen(profile, target, diagnosticsLevel);
                window.Close();
            };
            connManager.OnCopyLaunchCommandRequested += (profile, diagnosticsLevel) =>
            {
                _ = CopySshLaunchCommandAsync(profile, diagnosticsLevel, window);
            };
            connManager.OnConnectionDetailsRequested += (profile, diagnosticsLevel) =>
            {
                _ = ShowSshConnectionDetailsAsync(profile, diagnosticsLevel, window);
            };
            connManager.OnProfilesChanged += () =>
            {
                _sshConnectionService.SaveConnectionProfiles(connManager.GetAllProfiles());
            };
            connManager.OnSyncRequested += HandleSshSync;
            connManager.OnNewConnectionRequested += async () =>
            {
                await ShowNewSshConnectionDialogAsync(null, window);
            };
            connManager.OnEditProfile += async (profile) =>
            {
                await ShowNewSshConnectionDialogAsync(profile, window);
            };
            connManager.OnDeleteProfileRequested += async (profile) =>
            {
                await DeleteSshProfileAsync(profile, window);
            };

            // Same "focus search on open" the overlay toggle did. Focus set before the window
            // is shown does not stick, so it waits for Opened.
            window.Opened += (_, _) => connManager.FindControl<TextBox>("SearchInput")?.Focus();

            _connectionManagerWindow = window;
            window.Closed += (_, _) => _connectionManagerWindow = null;
            window.LoadProfiles(_sshConnectionService.GetConnectionProfiles());

            await window.ShowDialog(this);

            // Same focus return the overlay's hide branch did.
            _currentPane?.ActiveControl.Focus();
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

        private bool IsShortcut(KeyEventArgs e, string id, string fallback)
        {
            return ShortcutMatcher.Matches(e, GetEffectiveShortcutBinding(id, fallback));
        }

        private string GetEffectiveShortcutBinding(string id, string fallback)
        {
            if (_settings.Keybindings != null &&
                _settings.Keybindings.TryGetValue(id, out var custom) &&
                !string.IsNullOrWhiteSpace(custom))
            {
                return custom;
            }

            // Fallbacks are written with Ctrl; on macOS the default is the Cmd form.
            return ShortcutPlatform.DefaultBinding(id, fallback);
        }

        internal static bool TryOpenCommandAssistHelp(TerminalPane? pane)
        {
            return pane?.OpenCommandAssistHelp() == true;
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

        private string GetTabHeaderText(TabItem tab)
        {
            if (FindTabHeaderTextBlock(tab.Header) is TextBlock tb)
            {
                return string.IsNullOrWhiteSpace(tb.Text) ? "Terminal" : tb.Text;
            }

            return "Terminal";
        }

        private Border CreateTabHeaderHost(TabItem tab, string text)
        {
            var headerText = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
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
            var statusDot = new Avalonia.Controls.Shapes.Ellipse
            {
                Name = "TabStatusDot",
                Width = 8,
                Height = 8,
                Fill = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var headerText = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };

            var previewText = new TextBlock
            {
                Name = "TabPreviewLine",
                Text = string.Empty,
                Foreground = TabActivityChipBrush,
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
            // colors match the in-pane agent segment and the existing tab attention
            // constant (see the Tab*ChipBrush declarations above).
            TextBlock Chip(string name, string glyph, IBrush foreground)
            {
                var chip = new TextBlock
                {
                    Name = name,
                    Text = glyph,
                    Foreground = foreground,
                    FontSize = 10,
                    IsVisible = false,
                    IsHitTestVisible = false,
                };
                return chip;
            }

            var bellChip = Chip("TabBellChip", "\U0001F514", TabBellChipBrush);
            var activityChip = Chip("TabActivityChip", "\u2022", TabActivityChipBrush);
            var agentWroteChip = Chip("TabAgentWroteChip", AgentWroteGlyph, TabAgentWroteChipBrush);
            var agentWatchedChip = Chip("TabAgentWatchedChip", AgentWatchedGlyph, TabAgentWatchedChipBrush);
            var sharedChip = Chip("TabSharedChip", SharedGlyph, TabSharedChipBrush);

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
                button.Foreground = TitleBarContrastForeground();
                return;
            }

            int hiddenCount = CountHiddenTabs(viewportWidth, tabs.Items.Cast<TabItem>().Select(t => t.Bounds.Width));

            badge.IsVisible = hiddenCount > 0;
            badge.Text = hiddenCount > 0 ? $"+{hiddenCount}" : string.Empty;
            ToolTip.SetTip(button, hiddenCount > 0 ? $"{baseTooltip} \u2014 {hiddenCount} hidden" : baseTooltip);
            // This runs after every layout pass (via UpdateTabVisuals -> PopulateTabListMenu), so
            // the resting color here must be the theme's contrast foreground, not hardcoded white:
            // white was invisible against light themes and kept re-stomping the foreground
            // ApplyThemeToUI had applied.
            button.Foreground = hiddenCount > 0
                ? new SolidColorBrush(Color.FromRgb(255, 210, 90))
                : TitleBarContrastForeground();
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

            var theme = _settings.ActiveTheme;
            pill.Background = new SolidColorBrush(theme.Background.ToAvaloniaColor());
            // Same luminance-contrast pick as UpdateTabVisuals' header text, via the
            // shared GetContrastForeground helper, so the pill reads on both light
            // and dark theme backgrounds.
            pillText.Foreground = new SolidColorBrush(theme.GetContrastForeground().ToAvaloniaColor());
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

        internal static string TruncateTabLabel(string value, int maxLength = 40)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
            if (maxLength < 5) return value.Substring(0, maxLength);
            return value.Substring(0, maxLength - 1) + "\u2026";
        }

        internal static string TruncateTabLabelWithSuffix(string value, int maxLength, string suffix)
        {
            if (string.IsNullOrEmpty(suffix))
            {
                return TruncateTabLabel(value, maxLength);
            }

            if (maxLength <= suffix.Length)
            {
                return suffix.Substring(0, maxLength);
            }

            int available = maxLength - suffix.Length;
            string prefix = value;
            if (prefix.Length > available)
            {
                prefix = available < 5
                    ? prefix.Substring(0, available)
                    : prefix.Substring(0, available - 1) + "\u2026";
            }

            return prefix + suffix;
        }

        private string GetTabPrimaryTitle(TabItem tab)
        {
            var state = GetOrCreateTabState(tab);
            var pane = ResolvePaneForTab(tab);
            return ResolveTabPrimaryTitle(state.UserTitle, pane?.GetBaseTabTitle(), null);
        }

        internal static string ResolveTabPrimaryTitle(string? userTitle, string? paneBaseTitle, string? fallbackHeader)
        {
            if (!string.IsNullOrWhiteSpace(userTitle))
            {
                return userTitle;
            }

            if (!string.IsNullOrWhiteSpace(paneBaseTitle))
            {
                return paneBaseTitle;
            }

            if (!string.IsNullOrWhiteSpace(fallbackHeader))
            {
                return fallbackHeader;
            }

            return "Terminal";
        }

        internal string GetTabPersistedTitle(TabItem tab)
        {
            var state = GetOrCreateTabState(tab);
            var pane = ResolvePaneForTab(tab);
            return ResolveTabPrimaryTitle(state.UserTitle, pane?.GetBaseTabTitle(), GetTabHeaderText(tab));
        }

        /// <summary>
        /// The tab label without its trailing attention marker (bell, activity,
        /// or agent tier): primary title, forwarding badge, and the pinned/
        /// protected prefixes. Split out from <see cref="BuildFullTabLabel"/>
        /// so <see cref="BuildTabDisplayLabels"/> can truncate this part alone
        /// and append the marker afterwards — the marker must never land in
        /// the truncated region, or it silently disappears from a long tab's
        /// visible header (it would still show in the tooltip, since that
        /// reads the untruncated <see cref="BuildFullTabLabel"/> result, but
        /// the always-visible header text is the surface that matters).
        /// </summary>
        private string BuildBaseTabLabel(TabItem tab)
        {
            var state = GetOrCreateTabState(tab);
            var pane = ResolvePaneForTab(tab);
            string label = GetTabPrimaryTitle(tab);

            if (pane?.Profile != null)
            {
                var forwards = pane.Profile.Forwards;
                int activeCount = forwards.Count(f => f.Status == ForwardingStatus.Active);
                int startingCount = forwards.Count(f => f.Status == ForwardingStatus.Starting);
                bool hasFailed = forwards.Any(f => f.Status == ForwardingStatus.Failed);

                if (activeCount > 0 || startingCount > 0)
                {
                    string badge = activeCount.ToString();
                    if (startingCount > 0) badge += $" ({startingCount})";
                    label = $"{label} \U0001F501 {badge}";
                }
                else if (hasFailed)
                {
                    label = $"{label} \u26A0\uFE0F";
                }
            }

            if (state.IsPinned)
            {
                label = "\U0001F4CC " + label;
            }
            if (state.IsProtected)
            {
                label = "\U0001F512 " + label;
            }

            return label;
        }

        /// <summary>
        /// The trailing attention-marker suffix for a tab: bell and activity
        /// are mutually exclusive (a bell always wins over mere activity), and
        /// the agent tier is independent of both and can accompany either —
        /// preserving exactly the precedence and combination rules the old,
        /// inline version of this logic had in <c>BuildFullTabLabel</c>.
        /// </summary>
        private string GetAttentionMarkerSuffix(TabRuntimeState state)
        {
            string suffix = string.Empty;

            if (state.HasBell)
            {
                suffix += " \U0001F514";
            }
            else if (state.HasActivity)
            {
                suffix += " \u2022";
            }

            if (state.AgentTier == AgentHost.AgentAttentionTier.Wrote)
            {
                suffix += " " + AgentWroteGlyph;
            }
            else if (state.AgentTier == AgentHost.AgentAttentionTier.Watched)
            {
                suffix += " " + AgentWatchedGlyph;
            }

            if (state.IsShared) suffix += " " + SharedGlyph;
            // OSC 9;4 progress (#271) rides the same trailing-suffix contract in
            // horizontal mode (" 42%", " ✖ 42%", " ⋯"); vertical mode renders it as the
            // header's thin bar instead (UpdateVerticalTabExtras). Gated at render time
            // so the setting hides existing reports without discarding them — the stored
            // report survives and reappears when the toggle comes back on.
            suffix += TabProgressPresentation.FormatMarkerSuffix(
                _settings.Osc9ProgressReportingEnabled ? state.Progress : null);

            return suffix;
        }

        private string BuildFullTabLabel(TabItem tab)
        {
            var state = GetOrCreateTabState(tab);
            return BuildBaseTabLabel(tab) + GetAttentionMarkerSuffix(state);
        }

        /// <summary>
        /// Truncates every tab's label to <paramref name="maxLength"/> for the
        /// visible header, keeping the attention marker (bell/activity/agent)
        /// out of the truncated region by treating it as a suffix reserved
        /// ahead of time — the same mechanism <see cref="TruncateTabLabelWithSuffix"/>
        /// already used for the collision-disambiguation hint below, just
        /// applied unconditionally instead of only once tabs collide.
        ///
        /// When a truncated label collides with another tab's, the
        /// disambiguation hint is appended *after* the marker rather than
        /// before it: the marker is content about the tab's own live state
        /// (matches its position in the untruncated <see cref="BuildFullTabLabel"/>,
        /// immediately after the title), while the hint is a synthetic
        /// disambiguator bolted on only when two tabs would otherwise render
        /// identically. Putting the hint last also means the one case that
        /// forces both to compete for space (an extremely small maxLength)
        /// degrades by dropping hint characters before marker characters —
        /// <see cref="TruncateTabLabelWithSuffix"/>'s degenerate-case branch
        /// returns the front of the combined suffix, and the marker occupies
        /// the front.
        ///
        /// <paramref name="includeMarkers"/> false is the vertical-header mode:
        /// attention renders as trailing chips there (UpdateVerticalTabExtras),
        /// so the marker suffix is neither appended nor reserved during
        /// truncation — the prefixes (pinned/protected/forwarding) live in the
        /// base label and stay in both modes. The tooltip
        /// (<see cref="BuildFullTabLabel"/>), tab-list flyout
        /// (<see cref="GetTabMenuLabel"/>) and automation labels keep their
        /// markers regardless of mode.
        /// </summary>
        private Dictionary<TabItem, string> BuildTabDisplayLabels(IReadOnlyList<TabItem> tabs, int maxLength, bool includeMarkers = true)
            => ResolveTabDisplayLabels(
                tabs,
                t => BuildBaseTabLabel(t),
                t => GetAttentionMarkerSuffix(GetOrCreateTabState(t)),
                t => "~" + GetTabId(t).ToString("N").Substring(0, 4),
                maxLength,
                includeMarkers);

        /// <summary>
        /// Pure core of <see cref="BuildTabDisplayLabels"/>: same truncation, marker
        /// reservation, and collision-disambiguation behavior, with the per-tab string
        /// sources injected so tests can drive it as plain facts without a window (the
        /// instance method only contributes state-derived strings).
        /// </summary>
        internal static Dictionary<TTab, string> ResolveTabDisplayLabels<TTab>(
            IReadOnlyList<TTab> tabs,
            Func<TTab, string> baseLabelOf,
            Func<TTab, string> markerSuffixOf,
            Func<TTab, string> collisionHintOf,
            int maxLength,
            bool includeMarkers)
        {
            var baseLabels = tabs.ToDictionary(t => t, baseLabelOf);
            var markers = includeMarkers
                ? tabs.ToDictionary(t => t, markerSuffixOf)
                : tabs.ToDictionary(t => t, static _ => string.Empty);
            var truncated = tabs.ToDictionary(t => t, t => TruncateTabLabelWithSuffix(baseLabels[t], maxLength, markers[t]));

            var collisions = tabs
                .GroupBy(t => truncated[t], StringComparer.Ordinal)
                .Where(g => g.Count() > 1);

            foreach (var group in collisions)
            {
                foreach (var tab in group)
                {
                    truncated[tab] = TruncateTabLabelWithSuffix(baseLabels[tab], maxLength, markers[tab] + collisionHintOf(tab));
                }
            }

            return truncated;
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

        private async Task<string?> ShowTextPromptAsync(string title, string prompt, string defaultValue)
        {
            string? result = null;
            var dialog = CreateThemedDialogWindow(title, 520, 190, canResize: false);

            var input = new TextBox
            {
                Text = defaultValue,
                Margin = new Thickness(0, 0, 0, 12)
            };

            var cancelButton = new Button { Content = "Cancel", Width = 92 };
            cancelButton.Click += (_, __) => dialog.Close();

            var applyButton = new Button { Content = "Apply", Width = 92 };
            applyButton.Click += (_, __) =>
            {
                result = input.Text;
                dialog.Close();
            };

            dialog.Content = new Border
            {
                Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = prompt },
                        input,
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { cancelButton, applyButton }
                        }
                    }
                }
            };

            await dialog.ShowDialog(this);
            return result;
        }

        private Window CreateThemedDialogWindow(string title, double width, double height, bool canResize)
        {
            var dialog = new Window
            {
                Title = title,
                Width = width,
                Height = height,
                CanResize = canResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            UiScale.FitWindow(dialog);

            ApplyThemeToDialogWindow(dialog);
            return dialog;
        }

        private void ApplyThemeToDialogWindow(Window dialog)
        {
            var theme = _settings.ActiveTheme;
            var contrast = theme.GetContrastForeground();
            dialog.Background = new SolidColorBrush(theme.Background.ToAvaloniaColor());
            dialog.Foreground = new SolidColorBrush(contrast.ToAvaloniaColor());
            dialog.RequestedThemeVariant = contrast == TermColor.Black ? ThemeVariant.Light : ThemeVariant.Dark;
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

        private void ResetTabCollections()
        {
            _activePaneByTab.Clear();
            _paneZoomStateByTab.Clear();
            _zoomedPaneIdByTab.Clear();
            _broadcastEnabledTabs.Clear();
            _tabIds.Clear();
            _layoutModelByTab.Clear();
            _tabMru.Clear();
            _tabStateByTab.Clear();
            _pendingVisualRefreshTabs.Clear();
        }

        /// <summary>
        /// Disposes every pane this window owns, and with them the PTY and child shell behind
        /// each one. For tests, which build a real window and then abandon it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Nothing else reclaims them. <c>Window.Close()</c> runs <see cref="PerformAppTeardown"/>,
        /// which saves the session and stops timers but never walks the tabs, and a test that only
        /// drops its reference leaves the shells running - a full run finished with 53 of them
        /// alive. Tests cannot call <c>Close()</c> instead: that path also stops the process-wide
        /// <c>AgentHostService</c> singleton, which the next test would inherit.
        /// </para>
        /// <para>
        /// Zoom is why this cannot be a walk of <c>ti.Content</c> from outside. Zooming moves the
        /// tab's real root off the visual tree into <c>_paneZoomStateByTab</c> and puts the zoomed
        /// pane in its place, so disposing the content of a zoomed tab reaches exactly one pane and
        /// abandons its siblings. <see cref="CloseTab"/> exits zoom first for the same reason, and
        /// this mirrors it.
        /// </para>
        /// </remarks>
        internal void DisposeAllPanesForTest()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            foreach (var ti in tabs.Items.Cast<TabItem>().ToList())
            {
                if (_paneZoomStateByTab.ContainsKey(ti))
                {
                    ExitPaneZoom(ti, publishEvent: false, forTeardown: true);
                }

                if (ti.Content is Control content)
                {
                    DisposeControlTree(content);
                }
            }

            // Whatever the walk above did not reach is still registered here, because
            // DisposeControlTree unwires every pane it disposes and unwiring is what removes it
            // from this map. That is how a pane whose tab has left the collection is still found:
            // MainWindowStartupTests calls tabs.Items.Clear() to build its own strip, and the
            // constructor's live tab - shell and all - goes with it.
            foreach (var orphan in _paneOwnerTab.Keys.ToList())
            {
                DisposeControlTree(orphan);
            }
        }

        /// <summary>
        /// Disposes every pane of every tab, for the paths that replace the whole strip.
        /// </summary>
        /// <remarks>
        /// Zoom is exited first, for the reason <c>CloseTab</c> does it: <see cref="EnterPaneZoom"/>
        /// moves the tab's real root off the visual tree into <c>_paneZoomStateByTab</c> and leaves
        /// only the zoomed pane in <c>Content</c>, so disposing the content of a zoomed tab reaches
        /// one pane and abandons its siblings. Here that abandonment is permanent rather than merely
        /// untidy: <see cref="ResetTabCollections"/> runs on the very next line of
        /// <see cref="ApplySessionSnapshot"/> and clears the map that held the off-tree root, after
        /// which nothing can reach those panes at all and their shells outlive the workspace that
        /// owned them. Zoom survives a save/restore cycle, and every caller of this is a user action
        /// - open a workspace bundle, load a workspace, apply a template - that a zoomed tab is
        /// perfectly free to be in the middle of.
        /// </remarks>
        private void DisposeAllTabs(TabControl tabs)
        {
            foreach (var item in tabs.Items.Cast<TabItem>().ToList())
            {
                if (_paneZoomStateByTab.ContainsKey(item))
                {
                    ExitPaneZoom(item, publishEvent: false, forTeardown: true);
                }

                if (item.Content is Control content)
                {
                    DisposeControlTree(content);
                }
            }
        }

        private void ApplySessionSnapshot(NtildeSession session)
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            // A workspace, template or bundle is a layout, not a live session: rebuilt panes start
            // fresh shells. A snapshot saved before its ids were stripped at capture (or a bundle
            // from elsewhere) must not make the rebuilt panes claim daemon sessions.
            session = SessionManager.WithoutMuxIds(session);
            DisposeAllTabs(tabs);
            ResetTabCollections();
            SessionManager.RestoreSession(this, tabs, _settings, session);
            if (tabs.Items.Count > 0)
            {
                InitializeRestoredTabs(tabs);
            }
            SetupCommandPalette();
        }

        /// <param name="sessionsCannotPredateUtc">Read only when there is a session to restore (an OS call).</param>
        private bool TryRestoreStartupSession(TabControl tabs, Func<DateTime?> sessionsCannotPredateUtc, out NtildeSession? loadedSession)
        {
            loadedSession = null;
            if (!SessionManager.TryLoadSavedSession(out NtildeSession? session, out DateTime? savedUtc) ||
                session == null ||
                session.Tabs.Count == 0)
            {
                return false;
            }
            loadedSession = session;
            // Spec R2, decided once per launch: the file predates the boot (or, on Windows, this logon), so a reboot or
            // a logoff ended the local daemon sessions it names. The mark goes on the loaded nodes, before any tab is
            // built from them, so deferred tabs carry it too - and so does the session file until each pane spawns.
            DateTime? boundary = sessionsCannotPredateUtc();
            if (Ntilde.Shell.Mux.MuxRestoreExpectations.SessionsEndedByReboot(savedUtc, boundary))
            {
                TerminalLogger.Log($"TryRestoreStartupSession: the session file ({savedUtc:o}) predates this boot or logon ({boundary:o}); its local shells start fresh, quietly");
                SessionManager.MarkLocalMuxSessionsEndedByReboot(session);
            }

            _startup.Checkpoint("StartupRestore.AfterSessionLoad");

            try
            {
                _startup.BeginSessionRestore(session, immediate =>
                {
                    tabs.Items.Clear();

                    for (int index = 0; index < session.Tabs.Count; index++)
                    {
                        TabSession tabSession = session.Tabs[index];
                        TabItem? tabItem = index == immediate.OriginalIndex
                            ? SessionManager.CreateRestoredTabItem(tabSession, _settings)
                            : CreateStartupPlaceholderTab(tabSession);

                        if (tabItem != null)
                        {
                            tabs.Items.Add(tabItem);
                        }
                    }
                    _startup.Checkpoint("StartupRestore.AfterTabMaterialization");

                    if (tabs.Items.Count == 0)
                    {
                        throw new SessionRestoreAbortedException(
                            "Session restore produced no tab items; aborting restore.");
                    }

                    if (immediate.OriginalIndex >= 0 && immediate.OriginalIndex < tabs.Items.Count)
                    {
                        tabs.SelectedIndex = immediate.OriginalIndex;
                    }

                    InitializeRestoredTabs(tabs);
                    _startup.Checkpoint("StartupRestore.AfterInitializeRestoredTabs");
                });
            }
            catch (SessionRestoreAbortedException ex)
            {
                TerminalLogger.Log($"TryRestoreStartupSession: aborted ({ex.Message})");
                return false;
            }

            return true;
        }

        private TabItem CreateStartupPlaceholderTab(TabSession tabSession)
        {
            return new TabItem
            {
                Header = new TextBlock
                {
                    Text = tabSession.Title,
                    Foreground = Brushes.White,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(10, 4)
                },
                Content = new Border { Background = Brushes.Transparent },
                Tag = tabSession
            };
        }

        private void HydrateDeferredStartupTab(TabControl tabs, StartupRestoreTab deferredTab)
        {
            // Find the placeholder by identity rather than by OriginalIndex (#326 review). Restore
            // materializes every saved tab up front and hydrates the placeholders on a later
            // background dispatcher pass; anything that removes a tab in between shifts every later
            // index by one, and an index lookup then either writes this tab's content into its
            // neighbour's placeholder or falls out of range and leaves a permanently blank tab whose
            // null root can be persisted over the saved session. That became reachable without any
            // user action when ShellExitPolicy started defaulting to "Graceful": the restored
            // selected tab's shell can exit 0 during startup and close its own pane, and the exit is
            // posted at normal dispatcher priority while this pass is posted at background priority,
            // so the close always gets there first.
            //
            // Placeholders carry their TabSession in Tag (CreateStartupPlaceholderTab) and the
            // deferred entry carries the same instance out of NtildeSession.Tabs, so reference
            // equality names the right tab no matter where it has drifted to. OriginalIndex stays on
            // the record: it is what the deferred plan is built and logged against.
            TabItem? tabItem = null;
            foreach (object? item in tabs.Items)
            {
                if (item is TabItem candidate && ReferenceEquals(candidate.Tag, deferredTab.Tab))
                {
                    tabItem = candidate;
                    break;
                }
            }

            if (tabItem == null)
            {
                return;
            }

            Control? content = SessionManager.CreateRestoredTabContent(deferredTab.Tab, _settings);
            if (content == null)
            {
                return;
            }

            tabItem.Content = content;
            tabItem.Tag = deferredTab.Tab;
            InitializeRestoredTabs(tabs);
        }

        private async Task SaveWorkspaceInteractiveAsync()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            string suggested = $"workspace-{DateTime.Now:yyyyMMdd-HHmm}";
            var name = await ShowTextPromptAsync("Save Workspace", "Workspace name", suggested);
            if (string.IsNullOrWhiteSpace(name)) return;

            var snapshot = SessionManager.CaptureSession(this, tabs);
            if (WorkspaceManager.SaveWorkspace(name.Trim(), snapshot))
            {
                SetupCommandPalette();
            }
        }

        private async Task LoadWorkspaceInteractiveAsync()
        {
            var names = WorkspaceManager.ListWorkspaceNames();
            if (names.Count == 0) return;

            var first = names[0];
            var name = await ShowTextPromptAsync("Load Workspace", "Workspace name", first);
            if (string.IsNullOrWhiteSpace(name)) return;
            LoadWorkspaceByName(name.Trim());
        }

        private async Task SaveWorkspaceTemplateInteractiveAsync()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            string suggested = $"template-{DateTime.Now:yyyyMMdd-HHmm}";
            var name = await ShowTextPromptAsync("Save Workspace Template", "Template name", suggested);
            if (string.IsNullOrWhiteSpace(name)) return;

            var snapshot = SessionManager.CaptureSession(this, tabs);
            if (WorkspaceManager.SaveWorkspaceTemplate(name.Trim(), snapshot))
            {
                SetupCommandPalette();
            }
        }

        private async Task LoadWorkspaceTemplateInteractiveAsync()
        {
            var names = WorkspaceManager.ListWorkspaceTemplateNames();
            if (names.Count == 0) return;

            var first = names[0];
            var name = await ShowTextPromptAsync("Apply Workspace Template", "Template name", first);
            if (string.IsNullOrWhiteSpace(name)) return;
            ApplyWorkspaceTemplateByName(name.Trim());
        }

        private async Task ExportWorkspaceBundleInteractiveAsync()
        {
            if (!WorkspacePolicyManager.Current.AllowWorkspaceBundleExport)
            {
                System.Diagnostics.Debug.WriteLine("[Workspace] Export bundle blocked by policy.");
                return;
            }

            var names = WorkspaceManager.ListWorkspaceNames();
            if (names.Count == 0) return;

            string defaultName = names[0];
            string? name = await ShowTextPromptAsync("Export Workspace Bundle", "Workspace name", defaultName);
            if (string.IsNullOrWhiteSpace(name)) return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;

            string suggestedFileName = WorkspaceBundleNaming.SuggestedFileName(name);
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Workspace Bundle",
                SuggestedFileName = suggestedFileName,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType(WorkspaceBundleNaming.PickerDisplayName) { Patterns = WorkspaceBundleNaming.PickerPatterns }
                }
            });

            if (file == null) return;

            bool ok = WorkspaceManager.ExportWorkspaceBundle(name.Trim(), file.Path.LocalPath, Environment.UserName);
            if (!ok)
            {
                System.Diagnostics.Debug.WriteLine("[Workspace] Export bundle failed.");
            }
        }

        private async Task ExportCurrentSessionBundleInteractiveAsync()
        {
            if (!WorkspacePolicyManager.Current.AllowWorkspaceBundleExport)
            {
                System.Diagnostics.Debug.WriteLine("[Workspace] Export bundle blocked by policy.");
                return;
            }

            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            string suggested = $"session-{DateTime.Now:yyyyMMdd-HHmm}";
            string? label = await ShowTextPromptAsync("Export Session Bundle", "Bundle name", suggested);
            if (string.IsNullOrWhiteSpace(label)) return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;

            string suggestedFileName = WorkspaceBundleNaming.SuggestedFileName(label);
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Session Bundle",
                SuggestedFileName = suggestedFileName,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType(WorkspaceBundleNaming.PickerDisplayName) { Patterns = WorkspaceBundleNaming.PickerPatterns }
                }
            });

            if (file == null) return;

            var snapshot = SessionManager.CaptureSession(this, tabs);
            bool ok = WorkspaceManager.ExportWorkspaceBundle(label.Trim(), snapshot, file.Path.LocalPath, Environment.UserName);
            if (!ok)
            {
                System.Diagnostics.Debug.WriteLine("[Workspace] Export current session bundle failed.");
            }
        }

        private async Task ImportWorkspaceBundleInteractiveAsync()
        {
            if (!WorkspacePolicyManager.Current.AllowWorkspaceBundleImport)
            {
                System.Diagnostics.Debug.WriteLine("[Workspace] Import bundle blocked by policy.");
                return;
            }

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import Workspace Bundle",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(WorkspaceBundleNaming.PickerDisplayName) { Patterns = WorkspaceBundleNaming.PickerPatterns }
                }
            });

            if (files.Count == 0) return;

            string bundlePath = files[0].Path.LocalPath;
            string suggestedName = WorkspaceBundleNaming.SuggestedWorkspaceName(bundlePath);

            string? name = await ShowTextPromptAsync("Import Workspace Bundle", "Workspace name", suggestedName);
            if (string.IsNullOrWhiteSpace(name)) return;

            // Import only STORES the bundle; its commands are confirmed at execution time
            // in LoadWorkspaceByName. Confirming here would be a TOCTOU (the file could
            // change between this read and ImportWorkspaceBundle's re-read) and wouldn't
            // cover later loads — so the single confirmation gate lives at execution (#171).
            if (WorkspaceManager.ImportWorkspaceBundle(bundlePath, name.Trim(), out var error))
            {
                SetupCommandPalette();
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[Workspace] Import bundle failed: {error}");
        }

        private async Task OpenWorkspaceBundleInteractiveAsync()
        {
            if (!WorkspacePolicyManager.Current.AllowWorkspaceBundleImport)
            {
                System.Diagnostics.Debug.WriteLine("[Workspace] Open bundle blocked by policy.");
                return;
            }

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Workspace Bundle",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(WorkspaceBundleNaming.PickerDisplayName) { Patterns = WorkspaceBundleNaming.PickerPatterns }
                }
            });

            if (files.Count == 0) return;

            string bundlePath = files[0].Path.LocalPath;
            bool ok = WorkspaceManager.LoadWorkspaceBundleSession(bundlePath, out var _workspaceName, out var snapshot, out var error);
            if (ok && snapshot != null)
            {
                // This spawns the bundle's stored commands immediately — confirm first
                // for a foreign bundle (.ntildews.json or legacy .novaws.json) opened from disk (#171).
                if (!await ConfirmBundleCommandsAsync(snapshot, _workspaceName ?? "workspace"))
                {
                    return;
                }
                ApplySessionSnapshot(snapshot);
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[Workspace] Open bundle failed: {error}");
        }

        private async void LoadWorkspaceByName(string name)
        {
            var snapshot = WorkspaceManager.LoadWorkspace(name);
            if (snapshot == null) return;

            // Confirm ad-hoc commands against the exact stored snapshot that is about to
            // run — this is the single execution-time gate (no TOCTOU), and it covers
            // imported bundles whichever way they're loaded (#171). Profile-only
            // workspaces collect no commands and skip the prompt.
            if (!await ConfirmBundleCommandsAsync(snapshot, name))
            {
                return;
            }
            ApplySessionSnapshot(snapshot);
        }

        private void ApplyWorkspaceTemplateByName(string name)
        {
            var snapshot = WorkspaceManager.LoadWorkspaceTemplate(name);
            if (snapshot == null) return;
            ApplySessionSnapshot(snapshot);
        }

        internal static TabTemplateRule? FindTabTemplateRule(IEnumerable<TabTemplateRule>? rules, Guid profileId)
        {
            if (rules == null) return null;
            return rules.FirstOrDefault(r =>
                r != null &&
                r.Enabled &&
                r.ProfileId == profileId &&
                !string.IsNullOrWhiteSpace(r.TemplateName));
        }

        internal static bool UpsertTabTemplateRule(List<TabTemplateRule> rules, Guid profileId, string templateName)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            if (string.IsNullOrWhiteSpace(templateName)) return false;

            var existing = rules.FirstOrDefault(r => r.ProfileId == profileId);
            if (existing == null)
            {
                rules.Add(new TabTemplateRule
                {
                    ProfileId = profileId,
                    TemplateName = templateName.Trim(),
                    Enabled = true
                });
                return true;
            }

            existing.TemplateName = templateName.Trim();
            existing.Enabled = true;
            return true;
        }

        internal static bool RemoveTabTemplateRule(List<TabTemplateRule> rules, Guid profileId)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            int before = rules.Count;
            rules.RemoveAll(r => r.ProfileId == profileId);
            return rules.Count != before;
        }

        private async Task SetTemplateRuleForCurrentPaneProfileAsync()
        {
            var profile = _currentPane?.Profile;
            if (profile == null) return;

            var templateNames = WorkspaceManager.ListWorkspaceTemplateNames();
            if (templateNames.Count == 0) return;

            string suggested = FindTabTemplateRule(_settings.TabTemplateRules, profile.Id)?.TemplateName ?? templateNames[0];
            string? name = await ShowTextPromptAsync(
                "Set Tab Template Rule",
                $"Template for profile '{profile.Name}'",
                suggested);
            if (string.IsNullOrWhiteSpace(name)) return;

            string trimmed = name.Trim();
            if (WorkspaceManager.LoadWorkspaceTemplate(trimmed) == null)
            {
                System.Diagnostics.Debug.WriteLine($"[Workspace] Template rule not set: missing template '{trimmed}'.");
                return;
            }

            if (UpsertTabTemplateRule(_settings.TabTemplateRules, profile.Id, trimmed))
            {
                _settings.Save();
                SetupCommandPalette();
            }
        }

        private void ClearTemplateRuleForCurrentPaneProfile()
        {
            var profile = _currentPane?.Profile;
            if (profile == null) return;

            if (RemoveTabTemplateRule(_settings.TabTemplateRules, profile.Id))
            {
                _settings.Save();
                SetupCommandPalette();
            }
        }

        private bool TryApplyTemplateRuleForProfile(TerminalProfile profile)
        {
            var rule = FindTabTemplateRule(_settings.TabTemplateRules, profile.Id);
            if (rule == null) return false;

            var template = WorkspaceManager.LoadWorkspaceTemplate(rule.TemplateName);
            if (template == null || template.Tabs.Count == 0) return false;

            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return false;

            var current = SessionManager.CaptureSession(this, tabs);
            var templateTab = CloneTabSession(template.Tabs[0]);
            current.Tabs.Add(templateTab);
            current.ActiveTabIndex = current.Tabs.Count - 1;
            ApplySessionSnapshot(current);
            return true;
        }

        private static TabSession CloneTabSession(TabSession source)
        {
            return new TabSession
            {
                TabId = source.TabId,
                Title = source.Title,
                UserTitle = source.UserTitle,
                IsPinned = source.IsPinned,
                IsProtected = source.IsProtected,
                ActivePaneId = source.ActivePaneId,
                ZoomedPaneId = source.ZoomedPaneId,
                BroadcastInputEnabled = source.BroadcastInputEnabled,
                Root = ClonePaneNode(source.Root)
            };
        }

        private static PaneNode? ClonePaneNode(PaneNode? source)
        {
            if (source == null) return null;

            return new PaneNode
            {
                Type = source.Type,
                SplitOrientation = source.SplitOrientation,
                ProfileId = source.ProfileId,
                PaneId = source.PaneId,
                Command = source.Command,
                Arguments = source.Arguments,
                Sizes = source.Sizes.ToList(),
                Children = source.Children.Select(ClonePaneNode).Where(c => c != null).Select(c => c!).ToList()
            };
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
        private bool ExitPaneZoom(TabItem tabItem, bool publishEvent, bool forTeardown = false)
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

            UpdateActivePane(zoomedPane);
            FocusPaneTerminal(zoomedPane, defer: true);
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
            _sshInteractionService = new SshInteractionService(() => this, ApplyThemeToDialogWindow);
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
            var dragBorder = this.FindControl<Border>("DragBorder");

            if (dragBorder != null)
            {
                dragBorder.PointerPressed += (s, e) =>
                {
                    if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                        BeginMoveDrag(e);
                };
            }

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
                await OpenSettings(1);
            };

            var menuCustomizeTitleBar = this.FindControl<MenuItem>("MenuCustomizeTitleBar");
            if (menuCustomizeTitleBar != null) menuCustomizeTitleBar.Click += async (s, e) =>
            {
                // Codex round 6, PR #342: this entry point exists purely for discoverability - the
                // TITLE BAR section on Appearance isn't independently findable - so it must land
                // scrolled to that section, not just on Appearance's default (top) scroll position.
                // The plain gear button / Ctrl+, path (and every other OpenSettings caller) keeps
                // calling the two-arg overload, which defaults to SettingsSection.None.
                var (tabIndex, section) = CustomizeTitleBarSettingsTarget();
                await OpenSettings(tabIndex, section: section);
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
                    _ = OpenSettings(0);
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

        /// <summary>
        /// Spec §9. An injected factory wins (a mux one brings its host along: the test path);
        /// otherwise KeepOnClose builds the daemon connection and the persistent factory.
        /// </summary>
        private Ntilde.Pty.ITerminalSessionFactory ChooseSessionFactory(Ntilde.Pty.ITerminalSessionFactory? injected)
        {
            if (injected is Ntilde.Shell.Mux.MuxTerminalSessionFactory mux)
            {
                _muxHosts = mux.Hosts;
                return mux;
            }

            if (injected is not null) return injected;
            return Ntilde.Shell.Mux.SessionPersistenceMode.IsKeepOnClose(_settings.SessionPersistence)
                ? CreatePersistentSessionFactory()
                : Ntilde.Shell.DefaultTerminalSessionFactory.Instance;
        }

        /// <summary>Only the local daemon is warmed: a remote endpoint connects when a pane first needs it.</summary>
        private void StartMuxWarmupOnce()
        {
            if (_muxWarmupStarted || _muxHosts is null) return;
            _muxWarmupStarted = true;
            _muxHosts.Local.WarmUp();
        }

        /// <summary>
        /// Builds the local daemon connection for KeepOnClose: <see cref="AppServiceBundle.MuxHostFactory"/>, whose
        /// designer version refuses, so a designer or test window never launches a daemon. A test may replace it.
        /// </summary>
        internal Func<Ntilde.Shell.Mux.MuxConnectionHost> MuxHostFactory { get; set; }

        /// <summary>
        /// Builds the connection for a remote endpoint the first time a pane uses it (Phase 4 spec §5), or
        /// returns null to decline (the profile is gone; one that does not persist remote sessions still gets a
        /// connection, to deliver a closed pane's kill: codex C2). Runs outside
        /// the registry's lock, on whichever thread asked, and may race another ask for the same endpoint
        /// (the loser is disposed unused): it may look things up, but must not connect. The constructor
        /// sets <see cref="CreateRemoteMuxHost"/>; a seam for tests.
        /// </summary>
        internal Func<Ntilde.Shell.Mux.MuxEndpointId, Ntilde.Shell.Mux.MuxConnectionHost?> RemoteMuxHostFactory { get; set; } = _ => null;

        /// <summary>
        /// Opens the ntilde-mux install flow (Phase 4 spec §9) for an SSH profile's id: what the unavailable
        /// toast's "Install ntilde-mux…" / "Update ntilde-mux…" runs. The constructor sets
        /// <see cref="OpenRemoteMuxInstallForProfile"/>; while it is null, no notice offers an install or update.
        /// </summary>
        internal Action<Guid>? OpenRemoteMuxInstall { get; set; }

        /// <summary>
        /// Shows the install dialog for a saved profile over an owner window and returns how it ended
        /// (<see cref="RemoteMuxInstallDialog.ShowAsync"/>): after a success the profile's MuxOptions hold what
        /// was installed, and the flag when the user kept "Keep remote sessions running" ticked. The caller
        /// saves. The constructor sets <see cref="ShowRemoteMuxInstallDialogAsync"/>; a seam for tests.
        /// </summary>
        internal Func<Window, Ntilde.Platform.Ssh.Models.SshProfile, Task<Ntilde.Shell.Mux.Remote.RemoteMuxInstallResult?>> ShowRemoteMuxInstall { get; set; } =
            static (_, _) => Task.FromResult<Ntilde.Shell.Mux.Remote.RemoteMuxInstallResult?>(null);

        /// <summary>One client for every install's release download: it follows GitHub's redirects to its CDN.</summary>
        private static readonly Lazy<System.Net.Http.HttpClient> MuxReleaseHttp =
            new(Ntilde.Shell.Mux.Remote.GitHubReleaseMuxAssetSource.CreateHttpClient);

        /// <summary>
        /// The connection editor's "Install ntilde-mux on this host…" (Phase 4 spec §9): makes the editor's
        /// command run <see cref="InstallRemoteMuxFromEditorAsync"/> with the editor window as the dialog's owner.
        /// </summary>
        internal void WireRemoteMuxInstall(NewSshConnectionViewModel vm, Window editor)
        {
            ArgumentNullException.ThrowIfNull(vm);
            ArgumentNullException.ThrowIfNull(editor);
            vm.InstallRemoteMux = () => InstallRemoteMuxFromEditorAsync(vm, editor);
        }

        /// <summary>
        /// The editor's install: saves the pending edits first, through the editor's own
        /// <see cref="SshConnectionService.SaveProfile(NewSshConnectionViewModel)"/> - the install runs over the
        /// profile's stored host, auth and backend - then shows the dialog. An editor that does not validate shows
        /// why, and opens nothing.
        /// </summary>
        /// <remarks>
        /// After a success only the four mux fields change, in the store
        /// (<see cref="SshConnectionService.RecordRemoteMuxInstall"/>) and in the editor
        /// (<see cref="NewSshConnectionViewModel.ApplyRemoteMuxInstall"/>, whose status line follows). The editor is not
        /// saved a second time: anything in it the first save did not write stays the user's to save or discard.
        /// (The dialog is modal over the editor, so nothing can be typed meanwhile, but the flow does not rely on it.)
        /// </remarks>
        internal async Task InstallRemoteMuxFromEditorAsync(NewSshConnectionViewModel vm, Window editor)
        {
            if (!vm.Validate()) return;

            Ntilde.Platform.Ssh.Models.SshProfile saved;
            try
            {
                saved = _sshConnectionService.SaveProfile(vm);
            }
            catch (Exception ex)
            {
                vm.ValidationError = ex.Message;
                return;
            }

            // A new profile keeps this id from now on, so the editor's own save updates it rather than adding a copy.
            vm.ProfileId = saved.Id;
            RefreshProfileUIs();

            bool wasPersisting = saved.MuxOptions.PersistRemoteSessions;
            Ntilde.Shell.Mux.Remote.RemoteMuxInstallResult? result = await ShowRemoteMuxInstall(editor, saved);
            if (result is not { Success: true, Installed: { } installed }) return;

            // The dialog recorded the install in `saved`; the editor takes those four fields, whatever happens below.
            vm.ApplyRemoteMuxInstall(saved.MuxOptions);
            try
            {
                bool turnOn = !wasPersisting && saved.MuxOptions.PersistRemoteSessions;
                if (_sshConnectionService.RecordRemoteMuxInstall(saved.Id, installed, turnOn) is null)
                {
                    vm.ValidationError = "ntilde-mux was installed, but this connection is no longer saved; Save keeps it.";
                    return;
                }

                RefreshProfileUIs();
            }
            catch (Exception ex)
            {
                vm.ValidationError = $"ntilde-mux was installed, but recording it in the connection failed: {ex.Message}";
            }
        }

        /// <summary>The production <see cref="OpenRemoteMuxInstall"/>: fire and forget, so every failure is logged here.</summary>
        private void OpenRemoteMuxInstallForProfile(Guid profileId) => _ = OpenRemoteMuxInstallForProfileAsync(profileId);

        /// <summary>
        /// The toast's install (spec §7.5, §9): the stored profile, the dialog over this window, and after a success
        /// only the four mux fields recorded in the profile as stored then
        /// (<see cref="SshConnectionService.RecordRemoteMuxInstall"/>): the user never opened it, so nothing else of
        /// it may change.
        /// </summary>
        private async Task OpenRemoteMuxInstallForProfileAsync(Guid profileId)
        {
            try
            {
                Ntilde.Platform.Ssh.Models.SshProfile? profile = _sshConnectionService.GetStoredProfile(profileId);
                if (profile is null)
                {
                    EnqueueNotice("Install ntilde-mux", "That SSH connection no longer exists.");
                    return;
                }

                bool wasPersisting = profile.MuxOptions.PersistRemoteSessions;
                Ntilde.Shell.Mux.Remote.RemoteMuxInstallResult? result = await ShowRemoteMuxInstall(this, profile);
                if (result is not { Success: true, Installed: { } installed }) return;

                bool turnOn = !wasPersisting && profile.MuxOptions.PersistRemoteSessions;
                if (_sshConnectionService.RecordRemoteMuxInstall(profileId, installed, turnOn) is null)
                {
                    EnqueueNotice("Install ntilde-mux", "ntilde-mux was installed, but its SSH connection was deleted meanwhile.");
                    return;
                }

                RefreshProfileUIs();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[MainWindow] ntilde-mux install for SSH profile {profileId:N} failed: {ex.Message}");
                EnqueueNotice("Install ntilde-mux", $"ntilde-mux could not be installed: {ex.Message}");
            }
        }

        /// <summary>The production <see cref="ShowRemoteMuxInstall"/>: the dialog, themed like this window's dialogs.</summary>
        private Task<Ntilde.Shell.Mux.Remote.RemoteMuxInstallResult?> ShowRemoteMuxInstallDialogAsync(Window owner, Ntilde.Platform.Ssh.Models.SshProfile profile) =>
            RemoteMuxInstallDialog.ShowAsync(
                owner,
                profile,
                (source, report, progress) => CreateRemoteMuxInstaller(profile, source, report, progress),
                AppVersionInfo.Version,
                TopLevel.GetTopLevel(owner)?.Clipboard,
                ApplyThemeToDialogWindow);

        /// <summary>
        /// One install run's installer (spec §9: every step over the profile's own exec transport). The user is
        /// present, so the transport is interactive: OpenSSH may prompt through askpass, the native backend
        /// through this window's prompts. Called off the UI thread by the dialog.
        /// </summary>
        private Ntilde.Shell.Mux.Remote.RemoteMuxInstaller CreateRemoteMuxInstaller(
            Ntilde.Platform.Ssh.Models.SshProfile profile,
            Ntilde.Shell.Mux.Remote.IMuxDaemonAssetSource? source,
            Action<Ntilde.Shell.Mux.Remote.RemoteMuxInstallStep, string> report,
            IProgress<Ntilde.Shell.Mux.Remote.RemoteMuxInstallProgress>? progress) =>
            new(
                CreateRemoteMuxTransport(profile, new Ntilde.Shell.Mux.Remote.RemoteMuxTransportRequest(Interactive: true, _sshInteractionService)),
                source ?? new Ntilde.Shell.Mux.Remote.GitHubReleaseMuxAssetSource(
                    MuxReleaseHttp.Value,
                    AppVersionInfo.Version,
                    Ntilde.Shell.Mux.Remote.GitHubReleaseMuxAssetSource.DefaultCacheDirectory,
                    Ntilde.Shell.Mux.Remote.MuxAssetPins.Load()),
                report)
            {
                Progress = progress,
            };

        /// <summary>
        /// The exec transport for <paramref name="profile"/> (spec §8.2, §8.3), by its backend: what the remote hosts'
        /// attempts and the install flow both run over. May block (OpenSSH plans its config file, and an automatic attempt
        /// may run <c>ssh -V</c> once per ssh executable): off the UI thread.
        /// OpenSSH plans this very profile, which may be a host's blend of an edited or deleted one, self-contained once the
        /// host's destination is pinned (codex D2). A Native profile is refused while native SSH is off, as this window's
        /// settings say at that call (<see cref="NativeSshEnabledNow"/>; codex4 F).
        /// </summary>
        private Ntilde.Platform.Ssh.Exec.ISshExecTransport CreateRemoteMuxTransport(
            Ntilde.Platform.Ssh.Models.SshProfile profile,
            Ntilde.Shell.Mux.Remote.RemoteMuxTransportRequest request) =>
            Ntilde.Shell.Mux.Remote.RemoteMuxHostFactory.CreateTransport(
                profile,
                request,
                (p, selfContained) => _sshConnectionService.BuildLaunchDetailsFor(p, SshDiagnosticsLevel.None, selfContained),
                static () => new Ntilde.Platform.Ssh.Native.NativeSshInterop(),
                NativeSshEnabledNow,
                SshAskPassCommand.LocateHelper(),
                AppLogger.Log,
                Ntilde.Platform.Ssh.Exec.OpenSshClientVersionCache.Shared);

        /// <summary>
        /// The global native SSH switch (Settings &gt; SSH, <see cref="TerminalSettings.ExperimentalNativeSshEnabled"/>) as
        /// this window's settings say now - the source a plain SSH pane's spawn reads it from. A save replaces
        /// <see cref="_settings"/> whole, so this follows it. Read by each remote attempt's transport, off the UI thread.
        /// </summary>
        private bool NativeSshEnabledNow() => _settings.ExperimentalNativeSshEnabled;

        /// <summary>
        /// The action a persisted SSH tab's notice offers for <paramref name="failure"/> (spec §7.5): the install
        /// flow for <paramref name="profileId"/> when ntilde-mux is missing or outdated, and only while
        /// <see cref="OpenRemoteMuxInstall"/> is set; otherwise null. Its button names <paramref name="host"/>.
        /// </summary>
        internal PersistenceNoticeAction? RemoteMuxNoticeAction(Ntilde.Shell.Mux.Remote.RemoteMuxFailure? failure, Guid profileId, string host) =>
            PersistenceNoticeAction.ForRemoteFailure(failure, profileId, host, OpenRemoteMuxInstall);

        /// <summary>
        /// The production <see cref="RemoteMuxHostFactory"/> (Phase 4 spec §7.1): the profile from the SSH
        /// store, and per attempt a transport by the profile's backend - OpenSSH from its launch plan with
        /// the askpass helper (batch mode for automatic attempts, or its vault-only mode when the profile's
        /// password is saved), or native with this window's prompts. Automatic attempts read the saved password
        /// from the vault (<see cref="Vault"/>), off the UI thread, and sign in with it once.
        /// </summary>
        private Ntilde.Shell.Mux.MuxConnectionHost? CreateRemoteMuxHost(Ntilde.Shell.Mux.MuxEndpointId id) =>
            Ntilde.Shell.Mux.Remote.RemoteMuxHostFactory.Create(
                id,
                _sshConnectionService.GetStoredProfile,
                CreateRemoteMuxTransport,
                AppLogger.Log,
                _sshInteractionService,
                savedPassword: static profile => Ntilde.Shell.Mux.Remote.RemoteMuxHostFactory.ReadSavedPassword(Vault ?? new VaultService(), profile),
                askPassRecords: new SshAskPassSessionMarkers(static () => SshAskPassSessionMarkers.DefaultDirectory));

        /// <summary>
        /// Reuses the hosts kept from an earlier On period; only the first call builds them. Building
        /// the local host can throw (e.g. no Environment.ProcessPath to spawn the daemon from): that
        /// must not crash startup or a settings save, so it logs and falls back to normal sessions.
        /// </summary>
        private Ntilde.Pty.ITerminalSessionFactory CreatePersistentSessionFactory()
        {
            try
            {
                // Through a lambda, not the delegate itself: a test may set the seam after construction.
                _muxHosts ??= new Ntilde.Shell.Mux.MuxConnectionHosts(MuxHostFactory(), id => RemoteMuxHostFactory(id), AppLogger.Log);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[MainWindow] session persistence unavailable, using normal sessions: {ex.Message}");
                return Ntilde.Shell.DefaultTerminalSessionFactory.Instance;
            }

            // The SSH profile store, through a lambda: this runs before _sshConnectionService is assigned
            // (Task 19's note), and a profile's PersistRemoteSessions is read at each spawn. Panes make
            // their remote requests off the UI thread (TerminalPane.RunOffUiThread, Phase 4 spec §7.4).
            return new Ntilde.Shell.Mux.MuxTerminalSessionFactory(
                _muxHosts,
                Ntilde.Shell.DefaultTerminalSessionFactory.Instance,
                id => _sshConnectionService?.GetStoredProfile(id),
                AppLogger.Log);
        }

        /// <summary>Every pane in every tab, including a zoomed tab's stashed root.</summary>
        internal IReadOnlyList<TerminalPane> AllPanesForTest() => AllPanes();

        private List<TerminalPane> AllPanes() =>
            this.FindControl<TabControl>("Tabs")?.Items.OfType<TabItem>().SelectMany(t => EnumeratePanes(GetLayoutRootForTab(t))).ToList() ?? [];

        /// <summary>
        /// A mux session's HasActiveChildProcesses is a cached probe; the close confirmation asks
        /// the daemon first, waiting at most <paramref name="timeout"/>. A no-op for other sessions.
        /// </summary>
        internal static async Task RefreshPersistentSessionInfoAsync(ITerminalSession? session, TimeSpan timeout)
        {
            if (session is not Ntilde.Mux.MuxClientSession { IsConnected: true } mux) return;
            // The token also retires the request itself, so a daemon that never answers leaves no
            // pending request (or later unobserved fault) behind the abandoned wait.
            using var cts = new System.Threading.CancellationTokenSource(timeout);
            try
            {
                await mux.RefreshSessionInfoAsync(cts.Token).WaitAsync(timeout);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or Ntilde.Mux.Contracts.MuxProtocolException or ObjectDisposedException or IOException)
            {
                // Stale is acceptable: the confirmation then uses the last probed value.
            }
        }

        private void InitializeRestoredTabs(TabControl tabs)
        {
            foreach (var item in tabs.Items.Cast<TabItem>())
            {
                GetTabId(item);
                GetOrCreateTabState(item);
                ConfigureTabHeader(item, GetTabHeaderText(item));
                if (item.Content is Control c)
                {
                    WireControlTree(c);
                    RegisterPaneOwners(item, c);
                }
            }

            foreach (var item in tabs.Items.Cast<TabItem>())
            {
                if (item.Content is not Control c) continue;

                TerminalPane? active = null;
                if (item.Tag is TabSession saved &&
                    !string.IsNullOrWhiteSpace(saved.ActivePaneId) &&
                    Guid.TryParse(saved.ActivePaneId, out var activeId))
                {
                    active = FindPaneById(c, activeId);
                }

                active ??= FindFirstPane(c);
                if (active != null) _activePaneByTab[item] = active;

                if (item.Tag is TabSession savedSession && savedSession.BroadcastInputEnabled)
                {
                    _broadcastEnabledTabs.Add(item);
                }
            }

            foreach (var item in tabs.Items.Cast<TabItem>())
            {
                if (item.Tag is not TabSession saved ||
                    string.IsNullOrWhiteSpace(saved.ZoomedPaneId) ||
                    !Guid.TryParse(saved.ZoomedPaneId, out var zoomedPaneId) ||
                    item.Content is not Control c)
                {
                    continue;
                }

                var zoomPane = FindPaneById(c, zoomedPaneId);
                if (zoomPane != null)
                {
                    EnterPaneZoom(item, zoomPane, publishEvent: false);
                }
            }

            if (tabs.SelectedItem is TabItem selected)
            {
                var selectedPane = ResolvePaneForTab(selected);
                if (selectedPane != null)
                {
                    UpdateActivePane(selectedPane);
                    FocusPaneTerminal(selectedPane, defer: true);
                }
            }

            UpdateTabVisuals();
            UpdatePaneAutomationLabels();
            UpdateBroadcastIndicator();
            RefreshAllLayoutModels();
            CleanupTabMru(tabs);
            PopulateTabListMenu();
        }

        private void HandleSshSync()
        {
            try
            {
                var importedProfiles = Ntilde.Shell.ProfileImporter.ImportSshConfig();
                int changed = _sshConnectionService.MergeImportedProfiles(importedProfiles);
                if (changed > 0)
                {
                    RefreshProfileUIs();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Sync Error: {ex.Message}");
            }
        }

        public void ApplySettingsRecursive(Control? control, TerminalSettings settings)
        {
            if (control == null) return;
            if (control is TerminalPane pane)
            {
                // Refresh the profile object if possible to pick up overrides
                if (pane.Profile != null)
                {
                    TerminalProfile? updatedProfile = pane.Profile.Type == ConnectionType.SSH
                        ? _sshConnectionService.GetConnectionProfile(pane.Profile.Id)
                        : settings.Profiles.Find(p => p.Id == pane.Profile.Id);
                    if (updatedProfile != null) pane.UpdateProfile(updatedProfile);
                }
                pane.ApplySettings(settings);
            }
            else if (control is Panel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is Control c)
                    {
                        ApplySettingsRecursive(c, settings);
                    }
                }
            }
            else if (control is ContentControl cc)
            {
                ApplySettingsRecursive(cc.Content as Control, settings);
            }
            else if (control is Decorator decorator)
            {
                // Border/ScrollViewer-style single-child wrappers: without this, a pane nested
                // behind one was silently skipped and never received theme or font updates.
                ApplySettingsRecursive(decorator.Child as Control, settings);
            }
        }

        private void ApplySettingsToAllTabs()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs != null)
            {
                foreach (TabItem ti in tabs.Items.Cast<TabItem>())
                {
                    ApplySettingsRecursive(ti.Content as Control, _settings);
                }
            }
        }

        /// <summary>
        /// Takes every pane's assist surface down. Called after Settings clears command history.
        /// </summary>
        /// <remarks>
        /// <strong>PR #293 review, non-blocking 7.</strong> The rows on screen are a snapshot of a ranking
        /// pass, and nothing invalidates them when the store underneath is emptied - so a user who cleared
        /// their history while a bubble or popup was up went on looking at the commands they had just
        /// deleted, and could still accept one. Dismissing is the right verb rather than re-ranking: the
        /// honest answer for a just-emptied store is "nothing", and the next keystroke rebuilds the
        /// surface from the empty store anyway.
        /// </remarks>
        private void DismissCommandAssistSurfaces()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null)
            {
                return;
            }

            foreach (TabItem ti in tabs.Items.Cast<TabItem>())
            {
                foreach (var pane in EnumeratePanes(ti.Content as Control))
                {
                    pane.DismissCommandAssistSurface();
                }
            }
        }

        private void WireControlTree(Control control)
        {
            if (control is TerminalPane pane)
            {
                WirePane(pane);
                return;
            }

            if (control is Grid grid)
            {
                foreach (var child in grid.Children)
                {
                    if (child is GridSplitter splitter)
                    {
                        WireSplitter(splitter, grid);
                    }
                    else if (child is Control cc)
                    {
                        WireControlTree(cc);
                    }
                }

                return;
            }

            if (control is Panel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is Control cc) WireControlTree(cc);
                }

                return;
            }

            if (control is ContentControl contentControl && contentControl.Content is Control content)
            {
                WireControlTree(content);
            }
        }

        /// <summary>
        /// The single funnel every pane in this window passes through, and therefore the only
        /// correct place to inject per-pane window state.
        /// </summary>
        /// <remarks>
        /// The three creation sites in this file call it directly; panes rebuilt by
        /// <see cref="SessionManager"/> during session restore reach it via
        /// <c>InitializeRestoredTabs</c> -> <c>WireControlTree</c>. Assigning
        /// <c>CommandAssistServices</c> at the creation sites instead missed the restore path
        /// entirely, and a restored pane without the graph throws out of
        /// <c>ApplyShellIntegrationLaunchPlan</c> on the PTY spawn path - which the spawn's catch
        /// turns into "[ERROR] Failed to spawn process" and no session at all. Injecting here
        /// makes "the window wired this pane" and "the pane has its dependencies" the same fact.
        /// </remarks>
        private void WirePane(TerminalPane pane)
        {
            pane.CommandAssistServices = _commandAssistServices;
            pane.SessionFactory = _sessionFactory;
            pane.SshInteractionHandler = _sshInteractionService;
            // A method group: OpenRemoteMuxInstall is read when a notice is raised, not now.
            pane.RemoteNoticeAction = RemoteMuxNoticeAction;
            // Kept by UnwirePane: a stale remote result reaches a closed pane, and its shell's kill asks for the pass (codex C1).
            pane.RemoteMuxReleaseCheck = ScheduleRemoteMuxHostRelease;
            pane.RequestRemoteFilesSidebarTransfer -= OnPaneRequestRemoteFilesSidebarTransfer;
            pane.WorkingDirectoryChanged -= OnPaneWorkingDirectoryChanged;
            pane.TitleChanged -= OnPaneTitleChanged;
            pane.PaneActionRequested -= OnPaneActionRequested;
            pane.OutputReceived -= OnPaneOutputReceived;
            pane.BellReceived -= OnPaneBellReceived;
            pane.ProcessExited -= OnPaneProcessExited;
            pane.LongCommandCompleted -= OnPaneLongCommandCompleted;
            pane.PersistentSessionAttached -= OnPanePersistentSessionAttached;
            pane.PersistenceNotice -= OnPanePersistenceNotice;
            pane.MuxSharingChanged -= OnPaneMuxSharingChanged;
            pane.MuxShareEnded -= OnPaneMuxShareEnded;
            pane.MuxAdoptionLost -= OnPaneMuxAdoptionLost;
            pane.OscNotificationReceived -= OnPaneOscNotification;
            pane.ProgressReported -= OnPaneProgressReported;

            pane.RequestRemoteFilesSidebarTransfer += OnPaneRequestRemoteFilesSidebarTransfer;
            pane.WorkingDirectoryChanged += OnPaneWorkingDirectoryChanged;
            pane.TitleChanged += OnPaneTitleChanged;
            pane.PaneActionRequested += OnPaneActionRequested;
            pane.OutputReceived += OnPaneOutputReceived;
            pane.BellReceived += OnPaneBellReceived;
            pane.ProcessExited += OnPaneProcessExited;
            pane.LongCommandCompleted += OnPaneLongCommandCompleted;
            pane.PersistentSessionAttached += OnPanePersistentSessionAttached;
            pane.PersistenceNotice += OnPanePersistenceNotice;
            pane.MuxSharingChanged += OnPaneMuxSharingChanged;
            pane.MuxShareEnded += OnPaneMuxShareEnded;
            pane.MuxAdoptionLost += OnPaneMuxAdoptionLost;
            pane.OscNotificationReceived += OnPaneOscNotification;
            pane.ProgressReported += OnPaneProgressReported;
        }

        private void UnwirePane(TerminalPane pane)
        {
            // The pane stops reporting: re-derive its tab's marker once the layout has let it go.
            if (pane.MuxOtherClients > 0 && ResolveOwningTabForPane(pane) is { } sharedTab)
            {
                this.Dispatcher.Post(() => RefreshTabSharedMarker(sharedTab), DispatcherPriority.Background);
            }

            // Before the owner map goes: a closing pane withdraws its tab progress so the
            // bar/taskbar don't keep showing a dead report.
            WithdrawPaneProgress(pane);
            _paneOwnerTab.Remove(pane);
            pane.RequestRemoteFilesSidebarTransfer -= OnPaneRequestRemoteFilesSidebarTransfer;
            pane.WorkingDirectoryChanged -= OnPaneWorkingDirectoryChanged;
            pane.TitleChanged -= OnPaneTitleChanged;
            pane.PaneActionRequested -= OnPaneActionRequested;
            pane.OutputReceived -= OnPaneOutputReceived;
            pane.BellReceived -= OnPaneBellReceived;
            pane.ProcessExited -= OnPaneProcessExited;
            pane.LongCommandCompleted -= OnPaneLongCommandCompleted;
            pane.PersistentSessionAttached -= OnPanePersistentSessionAttached;
            pane.PersistenceNotice -= OnPanePersistenceNotice;
            pane.MuxSharingChanged -= OnPaneMuxSharingChanged;
            pane.MuxShareEnded -= OnPaneMuxShareEnded;
            pane.MuxAdoptionLost -= OnPaneMuxAdoptionLost;
            pane.OscNotificationReceived -= OnPaneOscNotification;
            pane.ProgressReported -= OnPaneProgressReported;
        }

        /// <summary>
        /// UI thread. "Attach to session…" chose a session that was gone by the time the pane attached:
        /// say so and close the pane (it has no session, so the close kills nothing).
        /// </summary>
        private void OnPaneMuxShareEnded(TerminalPane pane)
        {
            EnqueueNotice(TerminalPane.MuxShareEndedNoticeTitle, TerminalPane.MuxShareEndedBanner);
            _ = CloseSessionlessPaneAsync(pane, "whose shared session ended");
        }

        /// <summary>UI thread. An adopted orphan another instance claimed first: the tab goes quietly (it has no session).</summary>
        private void OnPaneMuxAdoptionLost(TerminalPane pane) => _ = CloseSessionlessPaneAsync(pane, "whose adopted session is open elsewhere");

        private async Task CloseSessionlessPaneAsync(TerminalPane pane, string which)
        {
            try
            {
                await ClosePaneAsync(pane, skipConfirm: true);
            }
            catch (Exception ex)
            {
                // Fire-and-forget: nothing else observes it. The pane's own banner stays as the fallback.
                AppLogger.Log($"[MainWindow] closing a pane {which} failed: {ex.Message}");
            }
        }

        /// <summary>UI thread. A tab is marked shared while any of its panes has other clients attached.</summary>
        private void OnPaneMuxSharingChanged(TerminalPane pane)
        {
            if (ResolveOwningTabForPane(pane) is { } tab) RefreshTabSharedMarker(tab);
        }

        private void RefreshTabSharedMarker(TabItem tab)
        {
            // A posted refresh can outlive its tab; recreating the closed tab's state would leak it.
            if (this.FindControl<TabControl>("Tabs")?.Items.Contains(tab) != true) return;
            TabRuntimeState state = GetOrCreateTabState(tab);
            bool shared = EnumeratePanes(GetLayoutRootForTab(tab)).Any(p => p.MuxOtherClients > 0);
            if (state.IsShared == shared) return;
            state.IsShared = shared;
            QueueTabVisualRefresh(tab);
        }

        private int _sessionSaveQueued; // 1 while a coalesced save is posted

        /// <summary>
        /// A crash right after launch must still know which daemon sessions are this window's
        /// (spec §9): save the session file after each attach, coalesced into one Background pass.
        /// </summary>
        private void OnPanePersistentSessionAttached(TerminalPane pane)
        {
            _ = pane;
            if (Interlocked.Exchange(ref _sessionSaveQueued, 1) == 1) return;
            Dispatcher.UIThread.Post(() =>
            {
                Volatile.Write(ref _sessionSaveQueued, 0);
                // After teardown the connection is gone; the teardown's own save is the last word.
                if (_teardownDone) return;
                if (this.FindControl<TabControl>("Tabs") is { } tabs) SessionManager.SaveSession(this, tabs);
            }, DispatcherPriority.Background);
        }

        // UI thread: notices raised since the coalesced toast was posted, keyed by title (a remote notice by title
        // and message, see EnqueueNotice), in arrival order.
        private readonly List<(string Key, string Title, string Message, int Count)> _pendingPersistenceNotices = [];

        // UI thread: the last action raised with those notices; the merged toast offers it (Phase 4 spec §7.5).
        private PersistenceNoticeAction? _pendingNoticeAction;

        /// <summary>
        /// A pane's session will not persist, or replaced a lost one. Shown as a toast rather than
        /// written into the pane (its shell paints over local text), with the notice's action, if any.
        /// </summary>
        private void OnPanePersistenceNotice(TerminalPane pane, string title, string message, PersistenceNoticeAction? action)
        {
            _ = pane;
            // Every pane that falls back raises it, and so does each retry after the connection
            // cooldown: the user needs to hear it once per launch, not once per pane.
            if (title == TerminalPane.MuxOrphanedNoticeTitle)
            {
                if (_muxOrphanedNoticeShown) return;
                _muxOrphanedNoticeShown = true;
            }

            EnqueueNotice(title, message, action);
        }

        private bool _muxOrphanedNoticeShown; // UI thread

        /// <summary>
        /// UI thread. The one way a session notice reaches the toast: everything raised together - panes
        /// restoring after a daemon crash, startup adoption, the detached-shells reminder - merges into
        /// one toast, one line per kind, instead of each ShowRecordingToast replacing the last. The toast
        /// has one action button, so the merged toast offers the last non-null <paramref name="action"/>.
        /// </summary>
        /// <remarks>
        /// A remote notice (<see cref="TerminalPane.RemoteMuxUnavailableNoticeTitle"/>) is one line per message - per
        /// host and reason - not per title: merged by title, the longer of two hosts' lines was shown with the other
        /// host's action (the Task 19 note), so the button could install on a host its line did not name. Each
        /// action's own line is now always in the toast; panes of one host and reason still merge ("(n panes)").
        /// </remarks>
        internal void EnqueueNotice(string title, string message, PersistenceNoticeAction? action = null)
        {
            if (action is not null) _pendingNoticeAction = action;
            string key = title == TerminalPane.RemoteMuxUnavailableNoticeTitle ? $"{title}\n{message}" : title;
            int index = _pendingPersistenceNotices.FindIndex(n => n.Key == key);
            bool first = _pendingPersistenceNotices.Count == 0;
            if (index < 0) _pendingPersistenceNotices.Add((key, title, message, 1));
            else
            {
                (string k, string t, string m, int c) = _pendingPersistenceNotices[index];
                // A version-mismatch hint on any of them is worth keeping.
                _pendingPersistenceNotices[index] = (k, t, message.Length > m.Length ? message : m, c + 1);
            }

            if (!first) return;
            this.Dispatcher.Post(FlushPersistenceNotices, DispatcherPriority.Background);
        }

        private void FlushPersistenceNotices()
        {
            if (_pendingPersistenceNotices.Count == 0) return;
            List<(string Key, string Title, string Message, int Count)> notices = [.. _pendingPersistenceNotices];
            _pendingPersistenceNotices.Clear();
            PersistenceNoticeAction? action = _pendingNoticeAction;
            _pendingNoticeAction = null;
            if (_teardownDone) return;

            // One toast surface: the most recent kind wins its title, every kind keeps a line. A toast
            // that offers an action stays until the user takes it or closes it: an offer that vanished
            // after a few seconds could not be taken at all.
            string title = notices[^1].Title;
            string message = string.Join('\n', notices.Select(n => BuildPersistenceNoticeMessage(n.Title, n.Message, n.Count)));
            ShowRecordingToast(title, message, filePath: null, folderPath: null, autoHide: action is null, action);
        }

        internal static string BuildPersistenceNoticeMessage(string title, string message, int count)
        {
            if (count <= 1) return message;
            if (title == TerminalPane.MuxPreviousLostNoticeTitle)
                return $"[{count} previous sessions were lost \u2014 started new shells]";
            if (title == TerminalPane.MuxAttachedElsewhereNoticeTitle)
                return $"[{count} previous shells are open in another window \u2014 started new shells]";
            if (title == TerminalPane.MuxUnavailableNoticeTitle)
            {
                string hint = message.Contains(TerminalPane.MuxVersionMismatchHint, StringComparison.Ordinal)
                    ? "\n" + TerminalPane.MuxVersionMismatchHint
                    : string.Empty;
                return $"[Multiplexer unavailable \u2014 {count} sessions will not persist]{hint}";
            }

            return $"{message} ({count} panes)";
        }

        /// <summary>
        /// Session persistence is on for this window: the mux commands exist only then. The host
        /// alone is not enough - it outlives an on→off flip for the mux panes still open, while the
        /// factory follows the setting.
        /// </summary>
        private bool IsMuxPersistenceActive => _muxHosts is not null && _sessionFactory is Ntilde.Shell.Mux.MuxTerminalSessionFactory;

        /// <summary>
        /// "Attach to session…" (spec §7.2). Lists the daemon's sessions off the UI thread, lets the
        /// user pick one, and opens it in a new tab attached shared. A session this window already
        /// shows is focused instead: one connection cannot hold two views of one session.
        /// </summary>
        internal async Task AttachToMuxSessionAsync()
        {
            if (!IsMuxPersistenceActive || _muxHosts?.Local is not { } host) return;
            IReadOnlyList<Ntilde.Mux.Contracts.SessionSummary>? sessions = await Task.Run(async () =>
            {
                Ntilde.Mux.MuxClient? client = host.GetClient(TimeSpan.FromSeconds(5));
                if (client is null) return null;
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    return await client.ListSessionsAsync(cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is Ntilde.Mux.Contracts.MuxProtocolException or IOException or TimeoutException or OperationCanceledException or ObjectDisposedException)
                {
                    AppLogger.Log($"[MainWindow] listing mux sessions failed: {ex.Message}");
                    return null;
                }
            });
            try
            {
                await OfferMuxSessionsAsync(sessions);
            }
            catch (Exception ex)
            {
                // Fire-and-forget from the palette and the shortcut: nothing else observes a throw.
                AppLogger.Log($"[MainWindow] Attach to session failed: {ex.Message}");
            }
        }

        private async Task OfferMuxSessionsAsync(IReadOnlyList<Ntilde.Mux.Contracts.SessionSummary>? sessions)
        {
            if (_teardownDone) return;
            if (sessions is null)
            {
                EnqueueNotice("Attach to session", "The multiplexer is not reachable.");
                return;
            }

            // Pending ids count too: an adopted background tab has not attached (spawned) yet. The local
            // daemon's panes only: one id on two daemons is two sessions (Phase 4 spec §5).
            var openHere = new HashSet<Guid>();
            foreach (TerminalPane p in AllPanes())
            {
                if (!ShowsLocalMuxEndpoint(p)) continue;
                if (p.Session is Ntilde.Mux.MuxClientSession m) openHere.Add(m.Id);
                if (p.MuxSessionIdToRestore is Guid pending) openHere.Add(pending);
            }

            IReadOnlyList<Ntilde.Shell.Mux.MuxSessionPickerRow> rows = Ntilde.Shell.Mux.MuxSessionPicker.BuildRows(sessions, openHere);
            if (rows.Count == 0)
            {
                EnqueueNotice("Attach to session", "No sessions are running in the multiplexer.");
                return;
            }

            Guid? chosen = await PickMuxSession(rows);
            if (chosen is not Guid id || _teardownDone) return;
            // Re-checked, not read from the row: the picker is modal, and a tab may have opened it meanwhile.
            if (FocusPaneShowingMuxSession(id)) return;
            if (sessions.FirstOrDefault(s => s.SessionId == id) is not { } summary) return;

            var pane = new TerminalPane(ShellHelper.ResolveExecutableOrDefault(summary.Command), summary.Arguments ?? string.Empty, _settings)
            {
                MuxSessionIdToRestore = id,
                MuxAttachSharedToRestore = true,
            };
            AddTabWithPane(pane, string.IsNullOrWhiteSpace(summary.Title) ? summary.Command : summary.Title, select: true);
        }

        /// <summary>
        /// The pane's daemon session (shown or pending) is on the local daemon, the one "Attach to session…", orphan
        /// adoption and the teardown count speak of (Phase 4 spec §5): a remote pane's id names another daemon's session.
        /// </summary>
        private static bool ShowsLocalMuxEndpoint(TerminalPane pane) => Ntilde.Shell.Mux.MuxEndpointId.Parse(pane.MuxEndpoint).IsLocal;

        /// <summary>UI thread. Selects and focuses the pane showing (or about to attach) local <paramref name="id"/>; false when none does.</summary>
        private bool FocusPaneShowingMuxSession(Guid id)
        {
            TerminalPane? pane = AllPanes().FirstOrDefault(p => ShowsLocalMuxEndpoint(p)
                && ((p.Session is Ntilde.Mux.MuxClientSession m && m.Id == id) || p.MuxSessionIdToRestore == id));
            if (pane is null) return false;
            if (ResolveOwningTabForPane(pane) is { } tab && this.FindControl<TabControl>("Tabs") is { } tabs) tabs.SelectedItem = tab;
            UpdateActivePane(pane);
            FocusPaneTerminal(pane, defer: true);
            return true;
        }

        private async Task<Guid?> ShowMuxSessionPickerAsync(IReadOnlyList<Ntilde.Shell.Mux.MuxSessionPickerRow> rows)
        {
            (Window dialog, Task<Guid?> result) = BuildMuxSessionPickerWindow(rows);
            await dialog.ShowDialog(this);
            return await result;
        }

        /// <summary>
        /// The "Attach to Session" dialog, not yet shown; the task completes with the chosen session
        /// (null: cancelled) when the window closes. Enter attaches, Escape cancels. Separate from
        /// <see cref="ShowMuxSessionPickerAsync"/> so a headless test can show it and press keys.
        /// </summary>
        internal (Window Window, Task<Guid?> Result) BuildMuxSessionPickerWindow(IReadOnlyList<Ntilde.Shell.Mux.MuxSessionPickerRow> rows)
        {
            Guid? chosen = null;
            var dialog = CreateThemedDialogWindow("Attach to Session", 640, 360, canResize: true);
            var list = new ListBox { ItemsSource = rows.Select(r => r.Display).ToList(), SelectedIndex = 0, MaxHeight = 240 };
            var attach = new Button { Content = "Attach", Width = 92, IsDefault = true };
            var cancel = new Button { Content = "Cancel", Width = 92, IsCancel = true };
            var closed = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
            dialog.Closed += (_, _) => closed.TrySetResult(chosen);
            void Accept()
            {
                if (list.SelectedIndex < 0) return;
                chosen = rows[list.SelectedIndex].SessionId;
                dialog.Close();
            }

            attach.Click += (_, _) => Accept();
            list.DoubleTapped += (_, _) => Accept();
            cancel.Click += (_, _) => dialog.Close();
            dialog.Content = new Border
            {
                Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Attach to a running session. It opens in a new tab and stays shared with its other windows.",
                            TextWrapping = TextWrapping.Wrap,
                        },
                        list,
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { cancel, attach },
                        },
                    },
                },
            };

            return (dialog, closed.Task);
        }

        /// <summary>
        /// Spec §9 orphans: running daemon sessions with no attached client that the restored session
        /// does not reference open as new tabs. The daemon is asked off the UI thread; nothing here
        /// blocks it.
        /// </summary>
        private async Task AdoptOrphanedMuxSessionsAsync(Ntilde.Shell.Mux.MuxConnectionHost host, HashSet<Guid> referenced)
        {
            try
            {
                (IReadOnlyList<Ntilde.Mux.Contracts.SessionSummary> orphans, int detached) = await Task.Run(async () =>
                {
                    Ntilde.Mux.MuxClient? client = host.GetClient(TimeSpan.FromSeconds(10));
                    if (client is null) return ((IReadOnlyList<Ntilde.Mux.Contracts.SessionSummary>)[], 0);
                    IReadOnlyList<Ntilde.Mux.Contracts.SessionSummary> all = await client.ListSessionsAsync().ConfigureAwait(false);
                    bool v2 = client.ProtocolVersion >= Ntilde.Mux.Contracts.MuxProtocol.SessionEventsVersion;
                    return (Ntilde.Shell.Mux.MuxOrphans.Select(all, referenced, v2), Ntilde.Shell.Mux.MuxOrphans.CountUserDetached(all, referenced, v2));
                }).ConfigureAwait(false);
                if (orphans.Count == 0 && detached == 0) return;

                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (orphans.Count > 0) AdoptOrphansOnUiThread(orphans);
                        if (detached > 0) AnnounceDetachedShellsOnce(detached);
                    }
                    catch (Exception ex)
                    {
                        // Posted: nothing awaits this, so a throw would reach the dispatcher unhandled.
                        AppLogger.Log($"[MainWindow] orphan adoption failed: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[MainWindow] orphan adoption failed: {ex.Message}");
            }
        }

        private bool _detachedShellsAnnounced; // UI thread: once per launch

        /// <summary>So deliberately detached shells are not forgotten (spec §7.7): one toast per launch.</summary>
        internal void AnnounceDetachedShellsOnce(int count)
        {
            if (_detachedShellsAnnounced || _teardownDone) return;
            _detachedShellsAnnounced = true;
            string message = count == 1
                ? "1 detached shell is running \u2014 Attach to session\u2026 to reopen it"
                : $"{count} detached shells are running \u2014 Attach to session\u2026 to reopen them";
            EnqueueNotice("Detached shells", message);
        }

        /// <summary>UI thread. Opens each orphan in a background tab (selection and focus stay put).</summary>
        private void AdoptOrphansOnUiThread(IReadOnlyList<Ntilde.Mux.Contracts.SessionSummary> orphans)
        {
            if (_teardownDone) return;
            // A pane this window spawned can be listed between its spawn and its attach
            // (AttachedClients still 0). Its Session is assigned on this thread as soon as the spawn
            // returns, so by now every such pane names its id here. Pending ids count too.
            // Local panes only: a remote pane's id is another daemon's session (Phase 4 spec §5).
            var live = new HashSet<Guid>();
            foreach (TerminalPane p in AllPanes())
            {
                if (!ShowsLocalMuxEndpoint(p)) continue;
                if (p.Session is Ntilde.Mux.MuxClientSession m) live.Add(m.Id);
                if (p.MuxSessionIdToRestore is Guid pending) live.Add(pending);
            }

            int adopted = 0;
            foreach (Ntilde.Mux.Contracts.SessionSummary s in orphans)
            {
                if (!live.Add(s.SessionId)) continue;
                var pane = new TerminalPane(ShellHelper.ResolveExecutableOrDefault(s.Command), s.Arguments ?? string.Empty, _settings)
                {
                    MuxSessionIdToRestore = s.SessionId,
                    MuxAdoptedOrphan = true,
                };
                // Background tab: it spawns (attaches) when first shown, and until then the session
                // file keeps its id through MuxSessionIdToRestore.
                AddTabWithPane(pane, string.IsNullOrWhiteSpace(s.Title) ? s.Command : s.Title, select: false);
                adopted++;
            }

            if (adopted == 0) return;
            AppLogger.Log($"[MainWindow] reattached {adopted} detached mux session(s)");
            EnqueueNotice("Sessions restored", $"Reattached {adopted} detached session{(adopted == 1 ? "" : "s")}");
        }

        private void OnPaneRequestRemoteFilesSidebarTransfer(TerminalPane srcPane, SidebarTransferRequest request)
        {
            if (srcPane.IsPersistentRemoteTab)
            {
                // Phase 4 spec §8.4: no native SSH session stands behind a persisted remote tab; say so, do nothing else.
                EnqueueNotice(TerminalPane.RemoteFilesUnavailableNoticeTitle, TerminalPane.RemoteFilesUnavailableMessage);
                return;
            }

            _ = InitiateSidebarSftpTransfer(srcPane, request.Direction, request.Kind, request.RemotePath);
        }

        private void OnPaneWorkingDirectoryChanged(TerminalPane srcPane, string cwd)
        {
            _ = srcPane;
            _ = cwd;
            Dispatcher.UIThread.Post(() => UpdateTabVisuals());
        }

        private void OnPaneTitleChanged(TerminalPane srcPane, string title)
        {
            _ = srcPane;
            _ = title;
            Dispatcher.UIThread.Post(() => UpdateTabVisuals());
        }

        private void OnPaneOutputReceived(TerminalPane pane)
        {
            var tab = ResolveOwningTabForPane(pane);
            if (tab == null) return;

            var tabState = GetOrCreateTabState(tab);
            tabState.Status.NoteOutput(DateTime.UtcNow);
            tabState.PreviewDirty = true;

            if (TryGetSelectedTab(out var selectedTabForStartup) && selectedTabForStartup == tab && ResolvePaneForTab(selectedTabForStartup) == pane)
            {
                _startup.Mark(StartupPhase.FirstTerminalReady);
            }

            if (!TryGetSelectedTab(out var selectedTab) || selectedTab != tab)
            {
                var state = GetOrCreateTabState(tab);
                state.HasActivity = true;
                QueueTabVisualRefresh(tab);
            }
        }

        private void OnPaneBellReceived(TerminalPane pane)
        {
            // ResolveOwningTabForPane rather than a tree walk (#314): it carries the
            // _paneOwnerTab cache and a rebuild fallback, so it also answers for a pane whose
            // tab is currently zoomed — EnterPaneZoom detaches the tab's other panes from the
            // logical tree, and a bell from one of those should still mark the tab. The previous
            // FindAncestorOfType<TabItem> call was a visual-tree lookup that never resolved for
            // any pane, so this handler returned here every time and the bell indicator had
            // never once appeared.
            var tab = ResolveOwningTabForPane(pane);
            if (tab == null) return;

            if (!TryGetSelectedTab(out var selectedTab) || selectedTab != tab)
            {
                var state = GetOrCreateTabState(tab);
                var now = DateTime.UtcNow;
                if ((now - state.LastBellUtc) < BellDebounceWindow)
                {
                    return;
                }

                state.LastBellUtc = now;
                state.HasBell = true;
                state.Status.NoteBell();
                QueueTabVisualRefresh(tab);
            }
        }

        private void OnPaneLongCommandCompleted(TerminalPane pane, string? commandText, int? exitCode, TimeSpan duration)
        {
            // Policy: opt-in, and only when the user isn't already looking at
            // this pane (a different pane is current, or the window is in the
            // background). The pane already applied the duration threshold.
            if (!LongCommandNotificationPolicy.ShouldNotify(
                    _settings.LongCommandNotificationsEnabled,
                    windowActive: IsActive,
                    isCurrentPane: ReferenceEquals(pane, _currentPane)))
            {
                return;
            }

            ShowRecordingToast(
                "Command finished",
                LongCommandNotificationPolicy.BuildMessage(commandText, exitCode, duration, pane.GetBaseTabTitle()),
                filePath: null,
                folderPath: null,
                autoHide: true);
        }

        private void OnPaneOscNotification(TerminalPane pane, string text)
        {
            // Same policy as the long-command toast — opt-in, suppressed while the user
            // is looking at the pane. The difference is provenance: the program asked
            // for this ping explicitly (OSC 9, #271 — e.g. Claude Code's completion
            // notification) rather than the terminal inferring "long command finished".
            if (!LongCommandNotificationPolicy.ShouldNotify(
                    _settings.Osc9NotificationsEnabled,
                    windowActive: IsActive,
                    isCurrentPane: ReferenceEquals(pane, _currentPane)))
            {
                return;
            }

            ShowRecordingToast(
                pane.GetBaseTabTitle(),
                text,
                filePath: null,
                folderPath: null,
                autoHide: true);
        }

        private void OnPaneProgressReported(TerminalPane pane, TerminalProgressReport? report)
        {
            var tab = ResolveOwningTabForPane(pane);
            if (tab == null) return;

            var state = GetOrCreateTabState(tab);
            if (report is null)
            {
                // Withdraw only what this pane owns; a split's other pane may still be
                // mid-report.
                if (!ReferenceEquals(state.ProgressPane, pane)) return;
                state.Progress = null;
                state.ProgressPane = null;
            }
            else
            {
                state.Progress = report;
                state.ProgressPane = pane;
            }

            QueueTabVisualRefresh(tab);
            RefreshTaskbarProgressFromSelection();
        }

        /// <summary>A pane went away (closed or exited): drop its progress report so the
        /// tab bar and taskbar don't keep advertising a dead operation.</summary>
        private void WithdrawPaneProgress(TerminalPane pane)
        {
            var tab = ResolveOwningTabForPane(pane);
            if (tab == null) return;

            // Lookup, not GetOrCreateTabState: CloseTab removes the tab's state BEFORE
            // the content dispose that unwires panes, so the create-on-miss path would
            // re-add an empty state entry for a tab that is already on its way out.
            if (!_tabStateByTab.TryGetValue(tab, out var state)) return;
            if (!ReferenceEquals(state.ProgressPane, pane)) return;

            state.Progress = null;
            state.ProgressPane = null;
            QueueTabVisualRefresh(tab);
            RefreshTaskbarProgressFromSelection();
        }

        /// <summary>
        /// The taskbar mirrors the SELECTED tab's OSC 9;4 progress (Windows shows one
        /// state per window). Re-run on every progress edge, tab switch, and pane
        /// removal; when the setting is off, this is what actively clears a state set
        /// before the toggle.
        /// </summary>
        private void RefreshTaskbarProgressFromSelection()
        {
            TerminalProgressReport? report = null;
            if (TryGetSelectedTab(out var tab))
            {
                report = GetOrCreateTabState(tab).Progress;
            }

            Taskbar.Apply(_settings.Osc9ProgressReportingEnabled ? report : null);
        }

        private void OnPaneProcessExited(TerminalPane pane, int exitCode)
        {
            // The pane's program is gone; its OSC 9;4 progress (if any) is stale.
            WithdrawPaneProgress(pane);

            // SSH panes write their own [SSH session disconnected] banner in HandleSessionExit and
            // never auto-close, so there is nothing left to do for them here. This has to run
            // before the tab==null branch below: an SSH pane that has already left the logical
            // tree by the time its shell exits must not also get a local banner written on top of
            // the one HandleSessionExit already wrote (#311 fix-wave finding B).
            if (pane.Profile?.Type == ConnectionType.SSH) return;

            var tab = pane.FindLogicalAncestorOfType<TabItem>();
            if (tab == null)
            {
                // No tab to close; the pane still has to say what happened.
                pane.WriteLocalExitBanner(exitCode);
                return;
            }

            // The tab strip says nothing about exit codes, deliberately (#311, then #314). The
            // ✓/✖N glyph it used to carry was written both per-command and on process exit and
            // rendered identically, so a dead shell looked exactly like a failed command — and
            // the only code that cleared it never ran, because it asked for its TabItem through
            // the visual tree. #314 resolved that by deleting the glyph rather than reviving an
            // ambiguous marker: a dead pane announces itself in the pane body instead. If a tab
            // marker is ever wanted, it needs its own state and its own reset, not this one.

            // A share of a shell that had already exited reports that exit with its attach: the user
            // opened it to look at, so it keeps its last screen whatever the policy says.
            if (pane.MuxShareOfExitedSession || !ShouldClosePaneOnExit(_settings.ShellExitPolicy, isSsh: false, exitCode))
            {
                pane.WriteLocalExitBanner(exitCode);
                return;
            }

            _ = HandlePaneExitCloseAsync(pane, exitCode);
        }

        /// <summary>
        /// #311: try to close a pane whose shell exited cleanly, and fall back to the banner when
        /// the close does not happen — a protected tab, an in-flight close, or a pane that has
        /// already left the tree. Every one of those paths has to end with a pane that says
        /// something rather than a pane that silently ignores you. This runs fire-and-forget from
        /// <see cref="OnPaneProcessExited"/>, so a throw here has no other observer: catch it,
        /// log it the same way other unexpected UI-side failures in this file do, and still fall
        /// back to the banner. There are three outcomes now: closed (nothing more to do), not
        /// closed (banner), and threw (log + banner).
        /// </summary>
        private async Task HandlePaneExitCloseAsync(TerminalPane pane, int exitCode)
        {
            bool closed;
            try
            {
                closed = await ClosePaneAsync(pane, skipConfirm: true);
            }
            catch (Exception ex)
            {
                TerminalLogger.Error($"[MainWindow] ClosePaneAsync failed while closing a pane whose shell exited (exit code {exitCode}): {ex.Message}");
                closed = false;
            }

            if (closed)
            {
                return;
            }

            try
            {
                pane.WriteLocalExitBanner(exitCode);
            }
            catch (Exception ex)
            {
                TerminalLogger.Error($"[MainWindow] WriteLocalExitBanner failed for a pane whose shell exited (exit code {exitCode}): {ex.Message}");
            }
        }

        private void OnPaneActionRequested(TerminalPane pane, PaneAction action)
        {
            UpdateActivePane(pane);
            FocusPaneTerminal(pane, defer: true);

            switch (action)
            {
                case PaneAction.SplitVertical:
                    SplitPane(Avalonia.Layout.Orientation.Horizontal);
                    break;
                case PaneAction.SplitHorizontal:
                    SplitPane(Avalonia.Layout.Orientation.Vertical);
                    break;
                case PaneAction.Equalize:
                    EqualizeCurrentSplit();
                    break;
                case PaneAction.ToggleZoom:
                    TogglePaneZoomForCurrentTab();
                    break;
                case PaneAction.ToggleBroadcast:
                    ToggleBroadcastForCurrentTab();
                    break;
                case PaneAction.Close:
                    CloseActivePane();
                    break;
            }
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
            ApplySplitterVisualState(splitter);
        }

        private void OnSplitterPointerEntered(object? sender, PointerEventArgs e)
        {
            if (sender is GridSplitter splitter)
            {
                SetSplitterStateClass(splitter, SplitterHoverClass, true);
                ApplySplitterVisualState(splitter);
            }
        }

        private void OnSplitterPointerExited(object? sender, PointerEventArgs e)
        {
            if (sender is GridSplitter splitter && !splitter.Classes.Contains(SplitterDraggingClass))
            {
                SetSplitterStateClass(splitter, SplitterHoverClass, false);
                ApplySplitterVisualState(splitter);
            }
        }

        private void OnSplitterPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is GridSplitter splitter && e.GetCurrentPoint(splitter).Properties.IsLeftButtonPressed)
            {
                SetSplitterStateClass(splitter, SplitterHoverClass, true);
                SetSplitterStateClass(splitter, SplitterDraggingClass, true);
                ApplySplitterVisualState(splitter);
            }
        }

        private void OnSplitterPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (sender is GridSplitter splitter)
            {
                SetSplitterStateClass(splitter, SplitterDraggingClass, false);
                SetSplitterStateClass(splitter, SplitterHoverClass, splitter.IsPointerOver);
                ApplySplitterVisualState(splitter);
            }
        }

        private void OnSplitterPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            if (sender is GridSplitter splitter)
            {
                SetSplitterStateClass(splitter, SplitterDraggingClass, false);
                SetSplitterStateClass(splitter, SplitterHoverClass, splitter.IsPointerOver);
                ApplySplitterVisualState(splitter);
            }
        }

        private void ApplySplitterVisualState(GridSplitter splitter)
        {
            var contrast = _settings.ActiveTheme.GetContrastForeground().ToAvaloniaColor();
            var alpha = splitter.Classes.Contains(SplitterDraggingClass)
                ? (byte)0x66
                : splitter.Classes.Contains(SplitterHoverClass)
                    ? (byte)0x44
                    : (byte)0x24;

            splitter.Background = new SolidColorBrush(Color.FromArgb(alpha, contrast.R, contrast.G, contrast.B));
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

        private IEnumerable<TerminalPane> EnumeratePanes(Control? control)
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

        public static VaultService? Vault { get; private set; }

        private void CloseActiveTab()
        {
            _ = CloseSelectedTabAsync();
        }

        private async Task CloseSelectedTabAsync()
        {
            if (!TryGetSelectedTab(out var selectedTab)) return;
            await CloseTabAsync(selectedTab);
        }

        private Task<bool> CloseTabAsync(TabItem tab, bool skipProcessChecks = false) =>
            CloseTabCoreAsync(tab, skipProcessChecks, Ntilde.Shell.Mux.PaneDisposition.EndSession);

        private async Task<bool> CloseTabCoreAsync(TabItem tab, bool skipProcessChecks, Ntilde.Shell.Mux.PaneDisposition requested)
        {
            if (_closeTabInProgress) return false;
            _closeTabInProgress = true;

            try
            {
                var tabState = GetOrCreateTabState(tab);
                if (tabState.IsProtected)
                {
                    return false;
                }

                if (_paneZoomStateByTab.ContainsKey(tab))
                {
                    ExitPaneZoom(tab, publishEvent: true);
                }

                var layoutRoot = GetLayoutRootForTab(tab);
                var detach = new HashSet<TerminalPane>();
                if (!skipProcessChecks && layoutRoot != null)
                {
                    foreach (var pane in EnumeratePanes(layoutRoot))
                    {
                        Ntilde.Shell.Mux.SharedCloseChoice choice = await DecidePaneCloseAsync(pane);
                        if (choice == Ntilde.Shell.Mux.SharedCloseChoice.Cancel)
                        {
                            UpdateActivePane(pane);
                            FocusPaneTerminal(pane, defer: true);
                            return false;
                        }

                        if (choice == Ntilde.Shell.Mux.SharedCloseChoice.Detach) detach.Add(pane);
                    }
                }

                PublishPaneEvent(tab, ResolvePaneForTab(tab), PaneAuditEventKind.Close, "tab");
                CloseTabCore(tab, requested, detach);
                return true;
            }
            finally
            {
                _closeTabInProgress = false;
            }
        }

        // A3 per-profile SSH allowlist probe, handed to the agent-host endpoint.
        // Reads the SSH profile store (off the UI thread on an IPC call), so it
        // must not touch Avalonia state — the store is thread-safe. Unknown or
        // non-SSH profile ids return false (fail closed).
        private bool IsSshProfileAgentAllowed(Guid profileId)
        {
            try
            {
                return _sshConnectionService?.GetStoredProfile(profileId)?.AllowAgentAccess == true;
            }
            catch
            {
                return false;
            }
        }

        // ── A3 agent act executor (spawn/close) ──────────────────────────────
        // Implemented on MainWindow because spawning/closing is inherently UI-thread
        // tab work. Published to the agent-host endpoint via SetActionExecutor; the
        // endpoint calls these from its IPC thread, so every body marshals to the UI
        // thread. Gating (act toggle) is enforced by the endpoint before we get here;
        // the SSH allowlist is enforced here since we own profile resolution.

        async Task<(AgentHost.AgentSpawnResult? Result, AgentHost.AgentSpawnError? Error)> AgentHost.IAgentActionExecutor.SpawnAsync(string? profileName)
        {
            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                TerminalProfile? profile;
                bool isSsh;

                if (string.IsNullOrWhiteSpace(profileName))
                {
                    profile = _settings.Profiles.Find(p => p.Id == _settings.DefaultProfileId)
                              ?? (_settings.Profiles.Count > 0 ? _settings.Profiles[0] : null);
                    isSsh = false;
                }
                else
                {
                    // Local settings profiles resolve before SSH store profiles on
                    // a name collision (documented in the A3 design).
                    profile = _settings.Profiles.Find(
                        p => p.Type == ConnectionType.Local &&
                             string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
                    isSsh = false;

                    if (profile == null)
                    {
                        TerminalProfile? sshProfile = _sshConnectionService.GetConnectionProfiles()
                            .FirstOrDefault(p => string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
                        if (sshProfile != null)
                        {
                            // SSH targets must be allowlisted per profile.
                            if (!IsSshProfileAgentAllowed(sshProfile.Id))
                            {
                                return ((AgentHost.AgentSpawnResult?)null, (AgentHost.AgentSpawnError?)AgentHost.AgentSpawnError.ProfileNotAllowed);
                            }
                            profile = sshProfile;
                            isSsh = true;
                        }
                    }
                }

                if (profile == null)
                {
                    return (null, AgentHost.AgentSpawnError.ProfileNotFound);
                }

                try
                {
                    AddTab(profile);
                    var pane = _currentPane;
                    if (pane == null)
                    {
                        return (null, AgentHost.AgentSpawnError.SpawnFailed);
                    }

                    Guid? tabId = _paneOwnerTab.TryGetValue(pane, out var tab)
                        ? GetPersistentTabId(tab)
                        : (Guid?)null;
                    string kind = isSsh || profile.Type == ConnectionType.SSH ? "ssh" : "local";
                    var result = new AgentHost.AgentSpawnResult(pane.PaneId, tabId, profile.Name, kind);
                    return ((AgentHost.AgentSpawnResult?)result, (AgentHost.AgentSpawnError?)null);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainWindow] Agent spawn failed: {ex.Message}");
                    return (null, AgentHost.AgentSpawnError.SpawnFailed);
                }
            });
        }

        async Task<bool> AgentHost.IAgentActionExecutor.ClosePaneAsync(Guid paneId)
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                TerminalPane? target = _paneOwnerTab.Keys.FirstOrDefault(p => p.PaneId == paneId);
                if (target == null)
                {
                    return false;
                }

                // Make the target the active pane of its (selected) tab, then reuse
                // the split-aware close path with the confirm dialog bypassed.
                if (_paneOwnerTab.TryGetValue(target, out var tab))
                {
                    var tabs = this.FindControl<TabControl>("Tabs");
                    if (tabs != null) tabs.SelectedItem = tab;
                    _activePaneByTab[tab] = target;
                }
                _currentPane = target;

                // CloseActivePaneAsync can no-op if a close is already in flight
                // (_closePaneInProgress); report the real outcome by checking the
                // pane is actually gone, so closeSession never claims a false success.
                await CloseActivePaneAsync(skipConfirm: true);
                return !_paneOwnerTab.ContainsKey(target);
            });
        }

        async Task<AgentHost.AgentLiveCapture?> AgentHost.IAgentActionExecutor.CaptureLiveAsync(Guid paneId, int maxWidth, double scale)
        {
            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                TerminalPane? target = _paneOwnerTab.Keys.FirstOrDefault(p => p.PaneId == paneId);
                return target?.CaptureLiveForAgent(maxWidth, scale);
            });
        }

        /// <summary>
        /// Every (endpoint, mux session id) a pane of this window shows, or is about to (a pending
        /// <see cref="TerminalPane.MuxSessionIdToRestore"/>: a restored tab not shown yet, an adopted orphan), on every
        /// endpoint: <see cref="OfferMuxSessionsAsync"/>'s "open here", not only the local daemon's. The windowless source
        /// asks it from the agent host's thread; this is its one touch of the window, posted to the UI thread and
        /// awaited there, never waited for.
        /// </summary>
        private async Task<IReadOnlySet<(string Endpoint, Guid Id)>> MuxSessionsShownHereAsync()
        {
            return await Dispatcher.UIThread.InvokeAsync<IReadOnlySet<(string Endpoint, Guid Id)>>(() =>
            {
                var shown = new HashSet<(string Endpoint, Guid Id)>();
                foreach (TerminalPane p in AllPanes())
                {
                    string endpoint = Ntilde.Shell.Mux.MuxEndpointId.Parse(p.MuxEndpoint).ToString();
                    if (p.Session is Ntilde.Mux.MuxClientSession m) shown.Add((endpoint, m.Id));
                    if (p.MuxSessionIdToRestore is Guid pending) shown.Add((endpoint, pending));
                }

                return shown;
            });
        }

        private void CloseTab(TabItem ti) => CloseTabCore(ti, Ntilde.Shell.Mux.PaneDisposition.EndSession, null);

        /// <param name="detach">Panes the user chose to detach rather than close; they override <paramref name="disposition"/>.</param>
        private void CloseTabCore(TabItem ti, Ntilde.Shell.Mux.PaneDisposition disposition, IReadOnlySet<TerminalPane>? detach)
        {
            if (_paneZoomStateByTab.ContainsKey(ti))
            {
                ExitPaneZoom(ti, publishEvent: false);
            }

            if (_activePaneByTab.TryGetValue(ti, out var mapped) && _currentPane == mapped)
            {
                _currentPane.RecordingStateChanged -= OnRecordingStateChanged;
                _currentPane.RecordingNotification -= OnRecordingNotification;
                _currentPane = null;
                OnRecordingStateChanged(false);
            }
            _activePaneByTab.Remove(ti);
            _paneZoomStateByTab.Remove(ti);
            _zoomedPaneIdByTab.Remove(ti);
            _broadcastEnabledTabs.Remove(ti);
            _tabIds.Remove(ti);
            _layoutModelByTab.Remove(ti);
            _tabMru.Remove(ti);
            _tabStateByTab.Remove(ti);
            _pendingVisualRefreshTabs.Remove(ti);

            if (ti.Content is Control content) DisposeControlTree(content, disposition, detach);
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs != null)
            {
                tabs.Items.Remove(ti);
                if (tabs.Items.Count == 0) Close();
            }

            // A closed tab is still referenced from places that outlive it - menu closures, cached
            // lookups - so cut it loose from its pane tree, which is where the memory is.
            ti.Content = null;

            UpdateTabVisuals();
            UpdatePaneAutomationLabels();
            UpdateBroadcastIndicator();
            RefreshAllLayoutModels();
        }

        private void CloseActivePane()
        {
            _ = CloseActivePaneAsync();
        }

        private Task CloseActivePaneAsync(bool skipConfirm = false)
        {
            if (_currentPane == null) return Task.CompletedTask;
            return ClosePaneAsync(_currentPane, skipConfirm);
        }

        /// <summary>
        /// Closes a specific pane, resolving everything from that pane rather than from the
        /// selection. The distinction matters for #311: a shell can die in a background tab, and
        /// the old selection-based fallback would have closed the tab the user was looking at.
        /// Returns true when the pane (or its tab) actually went away — callers use that to fall
        /// back to a banner when a protected tab or an in-flight close refuses.
        /// </summary>
        private Task<bool> ClosePaneAsync(TerminalPane pane, bool skipConfirm = false) =>
            ClosePaneCoreAsync(pane, skipConfirm, Ntilde.Shell.Mux.PaneDisposition.EndSession);

        private async Task<bool> ClosePaneCoreAsync(TerminalPane pane, bool skipConfirm, Ntilde.Shell.Mux.PaneDisposition requested)
        {
            if (_closePaneInProgress || pane == null) return false;
            _closePaneInProgress = true;

            try
            {
                var paneToClose = pane;
                // FindAncestorOfType<TabItem> (visual tree) never resolves here: Avalonia's
                // TabControl renders only the selected item's Content through its own
                // PART_SelectedContentHost presenter, so a TabItem is never a visual ancestor of
                // its own Content — selected or not. The logical tree is what's actually reliable
                // (ContentControl sets the logical parent immediately on assignment, same
                // mechanism the split-detection Parent check below already leans on).
                var paneTab = paneToClose.FindLogicalAncestorOfType<TabItem>();
                if (paneTab == null)
                {
                    // A zoomed tab's EnterPaneZoom replaces TabItem.Content with only the zoomed
                    // pane, so the tab's original split Grid — and every non-zoomed sibling still
                    // inside it — is detached from the logical tree and has no TabItem ancestor.
                    // Falling through to the split branch below for such a pane would clear that
                    // detached Grid's Children while its Parent is null: none of the promotion
                    // branches match, and the sibling pane is silently orphaned with its shell
                    // still running. Returning false here instead lets the caller fall back to a
                    // banner (#311 fix-wave finding C).
                    return false;
                }

                if (_paneZoomStateByTab.ContainsKey(paneTab))
                {
                    ExitPaneZoom(paneTab, publishEvent: true);
                }

                // Agent-initiated and exit-driven closes bypass the confirmation dialog: an agent
                // can't answer a modal, a dead shell has nothing left to lose, and an unattended
                // prompt is the stuck state #311 is about.
                Ntilde.Shell.Mux.PaneDisposition disposition = requested;
                if (!skipConfirm)
                {
                    Ntilde.Shell.Mux.SharedCloseChoice choice = await DecidePaneCloseAsync(paneToClose);
                    if (choice == Ntilde.Shell.Mux.SharedCloseChoice.Cancel)
                    {
                        FocusPaneTerminal(paneToClose, defer: true);
                        return false;
                    }

                    if (choice == Ntilde.Shell.Mux.SharedCloseChoice.Detach) disposition = Ntilde.Shell.Mux.PaneDisposition.Detach;
                }
                else if (requested == Ntilde.Shell.Mux.PaneDisposition.EndSession
                    && paneToClose.Session is Ntilde.Mux.MuxClientSession { IsConnected: true, IsProcessRunning: true, InteractiveOthers: > 0 })
                {
                    // An agent cannot answer the shared-close question, and skipping it must not end a shell
                    // other windows are still using: this pane lets go and the shell keeps running for them.
                    // Not a deliberate detach, so nothing is marked. InteractiveOthers is the cached v2 count,
                    // read-only observers excluded (always null on v1, which keeps the old close). An exit-driven close never gets here:
                    // its shell is no longer running.
                    disposition = Ntilde.Shell.Mux.PaneDisposition.Leave;
                }

                // Check if we are in a split (Parent is Grid with multiple children/splitter)
                if (paneToClose.Parent is Grid parentGrid && parentGrid.Children.Count >= 2)
                {
                    // We are in a split!
                    // 1. Identify Sibling (The non-splitter control that isn't us)
                    var sibling = parentGrid.Children.OfType<Control>()
                                        .FirstOrDefault(c => c != paneToClose && !(c is GridSplitter));

                    if (sibling != null)
                    {
                        // 2. Identify Grandparent
                        var grandParent = parentGrid.Parent;

                        // 3. Detach visuals
                        parentGrid.Children.Clear();

                        // 4. Promote Sibling to Grandparent
                        if (grandParent is ContentPresenter cp) cp.Content = sibling;
                        else if (grandParent is TabItem tab) tab.Content = sibling;
                        else if (grandParent is Grid gpGrid)
                        {
                            Grid.SetRow(sibling, Grid.GetRow(parentGrid));
                            Grid.SetColumn(sibling, Grid.GetColumn(parentGrid));
                            Grid.SetRowSpan(sibling, Grid.GetRowSpan(parentGrid));
                            Grid.SetColumnSpan(sibling, Grid.GetColumnSpan(parentGrid));

                            int index = gpGrid.Children.IndexOf(parentGrid);
                            if (index >= 0)
                            {
                                gpGrid.Children.RemoveAt(index);
                                gpGrid.Children.Insert(index, sibling);
                            }
                            else gpGrid.Children.Add(sibling);
                        }
                        else if (grandParent is Panel p)
                        {
                            int index = p.Children.IndexOf(parentGrid);
                            p.Children.Remove(parentGrid);
                            if (index >= 0) p.Children.Insert(index, sibling);
                            else p.Children.Add(sibling);
                        }

                        // 5. Dispose the closed pane
                        DisposeControlTree(paneToClose, disposition);

                        // 6. Focus Sibling
                        FocusFirstPane(sibling);
                        UpdatePaneAutomationLabels();
                        RefreshLayoutModelForTab(paneTab);
                        PublishPaneEvent(paneTab, paneToClose, PaneAuditEventKind.Close);
                        return true;
                    }
                }

                // Fallback: If not in a split, close the pane's own tab. paneTab is non-null from
                // the early return above, so there is no null branch left to guard here.
                return await CloseTabCoreAsync(paneTab, skipProcessChecks: true, disposition);
            }
            finally
            {
                _closePaneInProgress = false;
            }
        }

        /// <summary>
        /// Close, Detach or Cancel for one pane. A mux pane whose shell other clients also show asks the
        /// three-way question first; the count comes from listSessions (bounded, works on v1 too).
        /// Otherwise the Phase 2 decision (<see cref="ShouldClosePaneAsync"/>) stands.
        /// </summary>
        private async Task<Ntilde.Shell.Mux.SharedCloseChoice> DecidePaneCloseAsync(TerminalPane pane)
        {
            // One budget for both daemon reads (the sharing count here, the child-process probe in
            // ShouldClosePaneAsync): a stalled daemon costs a close about a second, not two.
            var budget = System.Diagnostics.Stopwatch.StartNew();
            if (pane.Session is Ntilde.Mux.MuxClientSession { IsConnected: true, IsAttached: true, IsProcessRunning: true } mux)
            {
                int? interactive; // clients that can type, this one included
                using var cts = new CancellationTokenSource(PaneCloseRefreshBudget);
                try
                {
                    interactive = await mux.RefreshSharingAsync(cts.Token).WaitAsync(PaneCloseRefreshBudget);
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or Ntilde.Mux.Contracts.MuxProtocolException or ObjectDisposedException or IOException)
                {
                    interactive = mux.InteractiveOthers + 1; // stale is acceptable
                }

                if (interactive is int n && n > 1)
                {
                    try
                    {
                        return await ConfirmSharedClose(n - 1);
                    }
                    catch (Exception ex)
                    {
                        // A question that could not be asked is never read as "close it for everyone".
                        AppLogger.Log($"[MainWindow] the shared-close prompt failed: {ex.Message}");
                        return Ntilde.Shell.Mux.SharedCloseChoice.Cancel;
                    }
                }
            }

            TimeSpan left = PaneCloseRefreshBudget - budget.Elapsed;
            bool close = await ShouldClosePaneAsync(pane, left > TimeSpan.Zero ? left : TimeSpan.Zero);
            return close ? Ntilde.Shell.Mux.SharedCloseChoice.Close : Ntilde.Shell.Mux.SharedCloseChoice.Cancel;
        }

        /// <summary>How long a pane close waits on the daemon, in total, before deciding on cached values.</summary>
        private static readonly TimeSpan PaneCloseRefreshBudget = TimeSpan.FromSeconds(1);

        private async Task<Ntilde.Shell.Mux.SharedCloseChoice> ShowSharedCloseDialogAsync(int others)
        {
            (Window dialog, Task<Ntilde.Shell.Mux.SharedCloseChoice> result) = BuildSharedCloseWindow(others);
            await dialog.ShowDialog(this);
            return await result;
        }

        /// <summary>
        /// The shared-close dialog, not yet shown; the task completes with the choice (Cancel when the
        /// window is dismissed) when it closes. Enter detaches, Escape cancels, and Close (which ends
        /// the shell for everyone) is never the default. Separate from <see cref="ShowSharedCloseDialogAsync"/>
        /// so a headless test can show it and press keys.
        /// </summary>
        internal (Window Window, Task<Ntilde.Shell.Mux.SharedCloseChoice> Result) BuildSharedCloseWindow(int others)
        {
            var choice = Ntilde.Shell.Mux.SharedCloseChoice.Cancel;
            var dialog = CreateThemedDialogWindow("Close Shared Shell", 480, 200, canResize: false);
            string who = others == 1 ? "1 other window is" : $"{others} other windows are";
            var cancel = new Button { Content = "Cancel", Width = 92, IsCancel = true };
            var detach = new Button { Content = "Detach", Width = 92, IsDefault = true };
            var close = new Button { Content = "Close (ends it)", Width = 130 };
            var closed = new TaskCompletionSource<Ntilde.Shell.Mux.SharedCloseChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
            dialog.Closed += (_, _) => closed.TrySetResult(choice);
            // Detach holds focus, not Cancel: a focused button takes Enter for itself before IsDefault
            // is consulted, so a focused Cancel would make Enter cancel. Close is never focused.
            dialog.Opened += (_, _) => detach.Focus();
            cancel.Click += (_, _) => { choice = Ntilde.Shell.Mux.SharedCloseChoice.Cancel; dialog.Close(); };
            detach.Click += (_, _) => { choice = Ntilde.Shell.Mux.SharedCloseChoice.Detach; dialog.Close(); };
            close.Click += (_, _) => { choice = Ntilde.Shell.Mux.SharedCloseChoice.Close; dialog.Close(); };
            dialog.Content = new Border
            {
                Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = $"{who} attached to this shell.", FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = "Close ends the shell for every window. Detach closes only this pane and keeps the shell running.", TextWrapping = TextWrapping.Wrap },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { cancel, detach, close },
                        },
                    },
                },
            };

            return (dialog, closed.Task);
        }

        private async Task<FirstCloseAnswer> ShowFirstCloseDialogAsync(int count)
        {
            // A close from the taskbar can reach a minimized window, and a dialog it owns would open out of sight:
            // the close would seem to do nothing. (A hidden owner makes ShowDialog throw, which closes as Keep.)
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            (Window dialog, Task<FirstCloseAnswer> result) = BuildFirstCloseDialog(count);
            await dialog.ShowDialog(this);
            return await result;
        }

        /// <summary>
        /// The first-close question (spec R1), not yet shown; the task completes with the answer when it closes.
        /// Enter keeps the shells (the safe default) wherever focus is, with "Don't ask again" as it stands; Escape or
        /// closing the dialog cancels the window's close; Close them (which ends the shells) is never the default and
        /// never answered by Enter. Separate from <see cref="ShowFirstCloseDialogAsync"/> so a headless test can show
        /// it and press keys.
        /// </summary>
        internal (Window Window, Task<FirstCloseAnswer> Result) BuildFirstCloseDialog(int count)
        {
            var answer = new FirstCloseAnswer(FirstCloseAction.Cancel, Remember: false);
            var dialog = CreateThemedDialogWindow("Close Ntilde", 480, 250, canResize: false);
            var dontAskAgain = new CheckBox { Content = "Don't ask again" };
            var keep = new Button { Content = "Keep running", MinWidth = 110, IsDefault = true };
            var close = new Button { Content = "Close them", MinWidth = 110 };
            var closed = new TaskCompletionSource<FirstCloseAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
            dialog.Closed += (_, _) => closed.TrySetResult(answer);
            dialog.Opened += (_, _) => keep.Focus();
            // Tunnel, so the dialog sees the key before whatever holds focus: after a click on "Don't ask again" that is
            // the CheckBox, which would take Enter for itself (toggling the box) before IsDefault is consulted. Enter
            // always keeps, with the box as it stands - nothing ends the shells by key; Space still presses a button.
            dialog.AddHandler(InputElement.KeyDownEvent, (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    answer = new FirstCloseAnswer(FirstCloseAction.Keep, dontAskAgain.IsChecked == true);
                }
                else if (e.Key == Key.Escape)
                {
                    answer = new FirstCloseAnswer(FirstCloseAction.Cancel, Remember: false);
                }
                else
                {
                    return;
                }

                e.Handled = true;
                dialog.Close();
            }, RoutingStrategies.Tunnel);
            keep.Click += (_, _) => { answer = new FirstCloseAnswer(FirstCloseAction.Keep, dontAskAgain.IsChecked == true); dialog.Close(); };
            close.Click += (_, _) => { answer = new FirstCloseAnswer(FirstCloseAction.Close, dontAskAgain.IsChecked == true); dialog.Close(); };
            string body = count > 1 ? $"Reopen Ntilde to get them back. ({count} shells)" : "Reopen Ntilde to get them back.";
            dialog.Content = new Border
            {
                Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = "Your shells keep running in the background.", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
                        dontAskAgain,
                        new TextBlock { Text = "Turn this off in Settings → Keep shells running when the window closes.", FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { keep, close },
                        },
                    },
                },
            };

            return (dialog, closed.Task);
        }

        private void DetachActivePane()
        {
            if (_currentPane is { } pane) _ = DetachPaneAsync(pane);
        }

        /// <summary>"Pane: Detach" (spec §7.4): the pane closes, the shell keeps running in the daemon.</summary>
        internal async Task<bool> DetachPaneAsync(TerminalPane pane)
        {
            if (pane.Session is not Ntilde.Mux.MuxClientSession { IsConnected: true } mux)
            {
                EnqueueNotice("Pane: Detach", pane.Session is Ntilde.Mux.MuxClientSession
                    ? "The multiplexer connection was lost."
                    : "Only a persistent shell can be detached.");
                return false;
            }

            // Its shell already exited: nothing would keep running, so this is a plain close (which
            // sends no kill for an exited shell) and no "kept running" toast.
            if (!mux.IsProcessRunning) return await ClosePaneAsync(pane, skipConfirm: true);

            // Read before the close: the pane is gone after it.
            Ntilde.Shell.Mux.MuxEndpointId endpoint = Ntilde.Shell.Mux.MuxEndpointId.Parse(pane.MuxEndpoint);
            bool remote = !endpoint.IsLocal;
            string remoteHost = pane.RemoteHostName is { Length: > 0 } named
                ? named
                : _muxHosts?.TryGet(endpoint)?.Policy.DisplayName ?? "its host";
            bool closed = await ClosePaneCoreAsync(pane, skipConfirm: true, Ntilde.Shell.Mux.PaneDisposition.Detach);
            if (closed && !_teardownDone)
            {
                EnqueueNotice("Shell detached", remote
                    ? RemoteDetachedMessage(remoteHost, mux.Id)
                    : "Shell kept running \u2014 Attach to session\u2026 to get it back");
            }

            return closed;
        }

        /// <summary>
        /// Final review F2: "Attach to session…" lists local shells only, and a remote shell is never adopted, so a
        /// detached remote shell comes back through <c>ntilde-mux attach</c> on its own host (USER_MANUAL §3.3), by the
        /// full id <c>ntilde-mux ls</c> shows. <paramref name="host"/> is <c>user@host</c>, as the remote banners say.
        /// </summary>
        internal static string RemoteDetachedMessage(string host, Guid sessionId) =>
            $"Shell kept running on {host} \u2014 run 'ntilde-mux attach {sessionId}' on that host to get it back";

        /// <summary>Test seam: the budget the last <see cref="ShouldClosePaneAsync"/> was given.</summary>
        internal TimeSpan? LastPaneCloseRefreshBudgetForTest { get; private set; }

        /// <param name="refreshBudget">What is left of the close's daemon budget; zero decides on the cached probe.</param>
        private async Task<bool> ShouldClosePaneAsync(TerminalPane pane, TimeSpan refreshBudget)
        {
            // A mux session's child-process flag is a cached daemon probe: refresh it (bounded)
            // so the decision below is not made on a stale answer.
            LastPaneCloseRefreshBudgetForTest = refreshBudget;
            if (refreshBudget > TimeSpan.Zero) await RefreshPersistentSessionInfoAsync(pane.Session, refreshBudget);

            if (ShouldAutoAcceptRunningPaneClose(
                pane.IsProcessRunning,
                pane.HasActiveChildProcesses,
                pane.HasUserInteraction,
                pane.Profile?.Type,
                pane.Profile?.Command,
                pane.ShellArgs,
                _settings.PaneClosePolicy))
            {
                return true;
            }

            string policy = (_settings.PaneClosePolicy ?? "Confirm").Trim().ToLowerInvariant();
            switch (policy)
            {
                case "force":
                    return true;
                case "graceful":
                    try
                    {
                        pane.Session?.SendInput("\x03");
                        pane.Session?.SendInput("exit\r");
                    }
                    catch { }

                    await Task.Delay(150);
                    if (!pane.IsProcessRunning) return true;
                    return await ShowRunningProcessCloseConfirmationAsync("Process is still running in this pane.");
                default:
                    return await ShowRunningProcessCloseConfirmationAsync("Closing this pane will terminate the running process.");
            }
        }

        /// <summary>
        /// #311: whether a pane whose shell just exited should close itself. Protection and
        /// close-in-progress guards deliberately live outside this decision — the caller attempts
        /// the close and falls back to the banner if it does not happen, so that a pane which
        /// cannot close still says something.
        /// </summary>
        internal static bool ShouldClosePaneOnExit(string? shellExitPolicy, bool isSsh, int exitCode)
        {
            // A dropped SSH session keeps its [Press Enter to reconnect] banner regardless: the
            // remote end may have cost an MFA prompt or a jump host to reach.
            if (isSsh) return false;

            string policy = shellExitPolicy?.Trim() ?? string.Empty;

            if (policy.Equals("Never", StringComparison.OrdinalIgnoreCase)) return false;
            if (policy.Equals("Always", StringComparison.OrdinalIgnoreCase)) return true;
            if (policy.Equals("Graceful", StringComparison.OrdinalIgnoreCase)) return exitCode == 0;

            // Fall-through: an unrecognised or empty value behaves as "Never", which is now more
            // conservative than the default ("Graceful") rather than equal to it — deliberately. A
            // typo in a hand-edited settings file must not be more destructive than what the user
            // asked for, and keeping the pane is the recoverable outcome: "Never" still tells them
            // what happened through the exit banner, whereas a closed pane cannot be un-closed.
            return false;
        }

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
        {
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
                paneHiddenByZoom: selectedTabIsZoomed && registration.PaneId != zoomedPaneId);
        }

        /// <summary>Applies <see cref="ComputeObserveIndicatorState"/> to the chrome. UI thread.</summary>
        internal void RefreshAgentObserveIndicator()
        {
            var indicator = this.FindControl<Button>("AgentObserveIndicator");
            var dot = this.FindControl<Avalonia.Controls.Shapes.Ellipse>("AgentObserveIndicatorDot");
            if (indicator == null || dot == null) return;

            var service = AgentHost.AgentHostService.Instance;

            // "Unmarked" = the pane shows no agent segment the user can see
            // right now, so this light is the only place its read can appear.
            bool anyUnmarkedPaneWatched = AgentHost.AgentSessionRegistry.Instance.GetRegistrations()
                .Any(r => IsPaneReadInvisibleWithoutWindowLight(r)
                    && r.AttentionMachine.Snapshot().Tier == AgentHost.AgentAttentionTier.Watched);

            var (visible, active) = ComputeObserveIndicatorState(
                service.IsRunning, service.InFlightPollCount > 0, anyUnmarkedPaneWatched, service.WindowlessWatched);

            indicator.IsVisible = visible;
            if (!visible) return;
            dot.Fill = new SolidColorBrush(Color.Parse(active ? "#4FB0D4" : "#6B737F"));
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

        internal static bool ShouldAutoAcceptRunningPaneClose(
            bool isProcessRunning,
            bool hasActiveChildProcesses,
            bool hasUserInteraction,
            ConnectionType? profileType,
            string? shellCommand,
            string? shellArgs,
            string? paneClosePolicy)
        {
            if (!isProcessRunning) return true;

            if (string.Equals(paneClosePolicy?.Trim(), "force", StringComparison.OrdinalIgnoreCase))
                return true;

            bool isWsl = shellCommand?.Contains("wsl", StringComparison.OrdinalIgnoreCase) == true;
            bool isSsh = profileType == ConnectionType.SSH;

            bool isSafeArgs = string.IsNullOrWhiteSpace(shellArgs);
            if (!isSafeArgs && isWsl)
            {
                // wsl profiles often have "-d Ubuntu" or similar as default args.
                // We treat these as safe to close without prompting.
                isSafeArgs = true;
            }

            if (isSsh)
            {
                // SSH connections are always considered precious and should warn on close
                // unless forced or process is dead.
                return false;
            }
            else if (isWsl)
            {
                // WSL is an opaque shell. We cannot see its Linux child processes.
                if (!hasUserInteraction && isSafeArgs)
                {
                    return true;
                }
            }
            else
            {
                // For native/transparent shells, we can trust explicit tracking.
                // If there are no active child processes, it's safe to close silently,
                // even if the user has been interacting with it.
                if (!hasActiveChildProcesses && isSafeArgs)
                {
                    return true;
                }
            }

            return false;
        }

        private Task<bool> ShowRunningProcessCloseConfirmationAsync(string message) =>
            ShowConfirmationDialogAsync("Close Running Pane", "A process is still running.", message, "Close Pane", 110);

        /// <summary>The update path's question (spec §9): the pane-close wording would misname what the button does.</summary>
        internal Task<bool> ShowUpdateSessionLossConfirmationAsync(string message) =>
            ShowConfirmationDialogAsync("Apply Update", "Multiplexed sessions are still running.", message, "Close sessions and update", 190);

        /// <summary>A modal Cancel / confirm question; true only when the confirm button was pressed.</summary>
        private async Task<bool> ShowConfirmationDialogAsync(string title, string heading, string message, string confirmText, double confirmWidth)
        {
            bool confirmed = false;

            var dialog = CreateThemedDialogWindow(title, 460, 190, canResize: false);

            var messageBlock = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14)
            };

            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 92
            };
            cancelButton.Click += (_, __) =>
            {
                confirmed = false;
                dialog.Close();
            };

            var closeButton = new Button
            {
                Content = confirmText,
                Width = confirmWidth
            };
            closeButton.Click += (_, __) =>
            {
                confirmed = true;
                dialog.Close();
            };

            dialog.Content = new Border
            {
                Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = heading,
                            FontWeight = FontWeight.SemiBold
                        },
                        messageBlock,
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { cancelButton, closeButton }
                        }
                    }
                }
            };

            await dialog.ShowDialog(this);
            return confirmed;
        }

        // Collects the ad-hoc shell commands a bundle would actually spawn on restore.
        // Mirrors SessionManager.RestorePaneTree: a leaf runs its raw Command/Arguments
        // ONLY when its profile doesn't resolve, so panes whose profile resolves are
        // skipped — that both matches what runs and avoids prompting for locally-saved
        // workspaces that store a ShellCommand alongside a resolvable ProfileId (#171).
        internal static List<string> CollectBundleCommands(NtildeSession? session, TerminalSettings settings)
        {
            var commands = new List<string>();
            if (session?.Tabs == null) return commands;

            void Walk(PaneNode? node)
            {
                if (node == null) return;
                if (node.Type == NodeType.Leaf)
                {
                    // Skip panes that resolve a known local/SSH profile — RestorePaneTree
                    // uses the profile and ignores Command/Arguments for those.
                    if (SessionManager.TryResolvePaneProfile(node, settings) != null)
                    {
                        return;
                    }

                    // Otherwise the fallback runs `cmd.exe`/Command with Arguments, so an
                    // argument-only leaf still runs `cmd.exe <args>` and can smuggle cmd
                    // metacharacters (#171 review).
                    bool hasCommand = !string.IsNullOrWhiteSpace(node.Command);
                    bool hasArgs = !string.IsNullOrWhiteSpace(node.Arguments);
                    if (hasCommand || hasArgs)
                    {
                        string effectiveCommand = hasCommand ? node.Command! : "cmd.exe";
                        string args = hasArgs ? " " + node.Arguments : "";
                        commands.Add((effectiveCommand + args).Trim());
                    }
                }
                else if (node.Children != null)
                {
                    foreach (var child in node.Children) Walk(child);
                }
            }

            foreach (var tab in session.Tabs)
            {
                if (tab != null) Walk(tab.Root);
            }
            return commands;
        }

        // Confirms the commands a foreign bundle will run before it spawns anything.
        // Returns true when there is nothing to run or the user approves.
        private async Task<bool> ConfirmBundleCommandsAsync(NtildeSession? session, string bundleName)
        {
            var commands = CollectBundleCommands(session, _settings);
            if (commands.Count == 0) return true; // profile-only bundles run known targets

            bool confirmed = false;
            var dialog = CreateThemedDialogWindow("Run Workspace Commands?", 520, 320, canResize: false);

            var listText = string.Join("\n", commands.ConvertAll(c => "\u2022  " + c));

            var cancelButton = new Button { Content = "Cancel", Width = 92 };
            cancelButton.Click += (_, __) => { confirmed = false; dialog.Close(); };

            var runButton = new Button { Content = "Run Commands", Width = 130 };
            runButton.Click += (_, __) => { confirmed = true; dialog.Close(); };

            dialog.Content = new Border
            {
                Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"The workspace \"{bundleName}\" will run these commands:",
                            FontWeight = FontWeight.SemiBold,
                            TextWrapping = TextWrapping.Wrap
                        },
                        new ScrollViewer
                        {
                            MaxHeight = 180,
                            Content = new TextBlock
                            {
                                Text = listText,
                                FontFamily = new Avalonia.Media.FontFamily("Consolas, monospace"),
                                TextWrapping = TextWrapping.Wrap
                            }
                        },
                        new TextBlock
                        {
                            Text = "Only run commands from a workspace you trust.",
                            Opacity = 0.8,
                            TextWrapping = TextWrapping.Wrap
                        },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { cancelButton, runButton }
                        }
                    }
                }
            };

            await dialog.ShowDialog(this);
            return confirmed;
        }

        /// <param name="disposition">
        /// EndSession for a close: a mux shell is killed rather than left running detached in the daemon
        /// (spec §9). Detach for Pane: Detach and the shared prompt's Detach (Phase 3 spec §7.4).
        /// </param>
        /// <param name="detach">Panes detached whatever <paramref name="disposition"/> says (a tab close's per-pane Detach answers).</param>
        private void DisposeControlTree(Control control, Ntilde.Shell.Mux.PaneDisposition disposition = Ntilde.Shell.Mux.PaneDisposition.EndSession, IReadOnlySet<TerminalPane>? detach = null)
        {
            // All call sites are UI event paths, but marshal defensively: the UI-affine
            // detach below throws VerifyAccess off the UI thread.
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => DisposeControlTree(control, disposition, detach));
                return;
            }

            if (control is TerminalPane pane)
            {
                UnwirePane(pane);

                // The pane's scrollback and glyph atlases are only reclaimable after a full GC, and
                // nothing else would trigger one in an idle window.
                IdleMemoryReclaimer.RequestIdleCollection();

                // Two-phase teardown (#154): UI-affine detach runs here on the UI thread;
                // only the potentially blocking session teardown moves to the pool.
                // Previously the whole pane.Dispose() ran in Task.Run with a swallowed
                // catch, so a VerifyAccess throw aborted teardown before the session was
                // disposed, leaking the PTY and its child shell.
                var session = pane.DetachFromUiThread();
                Ntilde.Shell.Mux.PaneDisposition effective = detach?.Contains(pane) == true ? Ntilde.Shell.Mux.PaneDisposition.Detach : disposition;
                // Here on the UI thread, not in the Task.Run below (see KillMuxSessionOnClose). Detach: no kill.
                KillMuxSessionOnClose(session, effective, pane.MuxEndpoint);
                if (session is not Ntilde.Mux.MuxClientSession)
                {
                    KillPendingRemoteMuxSessionOnClose(pane.MuxSessionIdToRestore, effective, pane.MuxEndpoint);
                }
                if (effective != Ntilde.Shell.Mux.PaneDisposition.EndSession && session is Ntilde.Mux.MuxClientSession detaching)
                {
                    // A deliberate detach (the command, or the shared prompt's Detach): tell a v2 daemon, so the
                    // next launch does not adopt it back (spec §7.7). Leave is a plain detach. On the UI thread;
                    // the pool Dispose below is then a no-op.
                    detaching.Detach(userDetached: effective == Ntilde.Shell.Mux.PaneDisposition.Detach);
                }

                // After its kill is queued (or its detach sent): if no pane needs that remote connection any more, it goes.
                if (!Ntilde.Shell.Mux.MuxEndpointId.Parse(pane.MuxEndpoint).IsLocal) ScheduleRemoteMuxHostRelease();

                if (session != null)
                {
                    Task.Run(() =>
                    {
                        try { session.Dispose(); }
                        catch (Exception ex)
                        {
                            // Debug.WriteLine is compiled out of Release builds; use the
                            // logger so production dispose failures leave a trace.
                            TerminalLogger.Log($"[MainWindow] Session dispose failed: {ex.Message}");
                        }
                    });
                }
            }
            else if (control is Panel panel) { foreach (var child in panel.Children) if (child is Control c) DisposeControlTree(c, disposition, detach); }
            else if (control is ContentPresenter cp && cp.Content is Control childContent) DisposeControlTree(childContent, disposition, detach);
        }

        /// <summary>
        /// A user closed this pane: a mux shell must end. KillAsync (not the fire-and-forget Kill): its
        /// reply means the kill landed, and the host waits for it on dispose, so closing the last tab
        /// cannot drop it. Still enqueued synchronously on the UI thread (RequestAsync enqueues before
        /// its first await). A session that already exited is left alone, and so is a local one whose
        /// connection is gone; a remote one goes through its host whatever its connection says.
        /// </summary>
        /// <param name="muxEndpoint">The pane's <see cref="TerminalPane.MuxEndpoint"/>: its own endpoint's host tracks the kill (Phase 4 spec §5).</param>
        private void KillMuxSessionOnClose(ITerminalSession? session, Ntilde.Shell.Mux.PaneDisposition disposition, string? muxEndpoint)
        {
            if (disposition != Ntilde.Shell.Mux.PaneDisposition.EndSession || session is not Ntilde.Mux.MuxClientSession mux)
            {
                return;
            }

            if (!mux.IsProcessRunning) return;
            Ntilde.Shell.Mux.MuxEndpointId endpoint = Ntilde.Shell.Mux.MuxEndpointId.Parse(muxEndpoint);
            if (!endpoint.IsLocal)
            {
                // Review Focus 1: the user meant to end that shell, and nobody can adopt a remote one. Always through
                // its host: sent at once (and tracked) while connected, else queued for the next connect - and queued
                // again if the connection closes before the daemon answers. A dead link can still look connected
                // until the liveness ping notices, and a kill sent into it straight from here would only be logged.
                KillThroughRemoteHost(endpoint, mux.Id);
                return;
            }

            // A local session whose connection is gone is left to orphan adoption, as before.
            if (!mux.IsConnected) return;

            try
            {
                Task kill = mux.KillAsync();
                // The session's own connection is the one whose teardown must wait for this reply. Its
                // host exists: the factory built it to open the session. TryGet, never build one here.
                _muxHosts?.TryGet(endpoint)?.TrackPendingKill(kill);
                _ = kill.ContinueWith(
                    t => TerminalLogger.Log($"[MainWindow] mux kill of {mux.Id} failed: {t.Exception?.GetBaseException().Message}"),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                TerminalLogger.Log($"[MainWindow] mux kill failed: {ex.Message}");
            }
        }

        private bool _remoteMuxReleasePosted; // UI thread: a ReleaseUnusedRemoteMuxHosts pass is queued

        /// <summary>
        /// UI thread. Final review F1: a remote pane closed. The pass that releases the remote hosts no pane needs runs
        /// once the close is done - a tab's whole tree disposed, the tab itself removed - not in the middle of it, where
        /// the closing tab's other panes would still count. One pass for every pane closed together. A pane also asks for
        /// one once it has queued the kill of a shell its stale remote result started (codex C1), or discarded any other
        /// stale remote result, for which the factory may have queued a kill (codex E1;
        /// <see cref="TerminalPane.RemoteMuxReleaseCheck"/>): that result may come back after its close's pass ran.
        /// </summary>
        private void ScheduleRemoteMuxHostRelease()
        {
            if (_remoteMuxReleasePosted || _muxHosts is null) return;
            _remoteMuxReleasePosted = true;
            Dispatcher.UIThread.Post(ReleaseUnusedRemoteMuxHosts, DispatcherPriority.Background);
        }

        /// <summary>
        /// UI thread. Releases every remote host whose endpoint no pane of this window needs
        /// (<see cref="TerminalPane.RemoteMuxEndpointInUse"/>): closed once its kills are delivered
        /// (<see cref="Ntilde.Shell.Mux.MuxConnectionHosts.Release"/>), so it stops pinging and reconnecting, the remote
        /// proxy and daemon are let go, and its remembered secret is forgotten. Not during teardown, which closes every host.
        /// </summary>
        private void ReleaseUnusedRemoteMuxHosts()
        {
            _remoteMuxReleasePosted = false;
            if (_teardownDone || _muxHosts is not { } hosts) return;
            HashSet<Ntilde.Shell.Mux.MuxEndpointId> needed = [.. AllPanes().Select(p => p.RemoteMuxEndpointInUse).OfType<Ntilde.Shell.Mux.MuxEndpointId>()];
            foreach (Ntilde.Shell.Mux.MuxEndpointId endpoint in hosts.RemoteEndpoints)
            {
                if (!needed.Contains(endpoint)) hosts.Release(endpoint);
            }
        }

        /// <summary>
        /// Review Focus 1, for a remote pane closed with no session of its own: its id is still pending - a restored
        /// tab never shown, a connect or reattach in flight, a reattach that failed, a restore that could not reach
        /// the host. The user meant to end that shell too, and nobody can adopt a remote one, so its host kills it -
        /// connecting for it if nothing else is (<see cref="Ntilde.Shell.Mux.MuxConnectionHost.KillWhenConnected"/>).
        /// A local pending id is left to orphan adoption, as before.
        /// </summary>
        private void KillPendingRemoteMuxSessionOnClose(Guid? pendingId, Ntilde.Shell.Mux.PaneDisposition disposition, string? muxEndpoint)
        {
            if (disposition != Ntilde.Shell.Mux.PaneDisposition.EndSession || pendingId is not Guid id) return;
            Ntilde.Shell.Mux.MuxEndpointId endpoint = Ntilde.Shell.Mux.MuxEndpointId.Parse(muxEndpoint);
            if (endpoint.IsLocal) return;
            KillThroughRemoteHost(endpoint, id);
        }

        /// <summary>
        /// A closed pane's remote shell is killed through its endpoint's host (<see cref="RemoteMuxHostFor"/>). Without one
        /// the kill cannot be sent at all, and that is logged, naming the shell left running: nothing adopts a remote orphan.
        /// </summary>
        private void KillThroughRemoteHost(Ntilde.Shell.Mux.MuxEndpointId endpoint, Guid sessionId)
        {
            if (RemoteMuxHostFor(endpoint, out string? whyNone) is { } host)
            {
                host.KillWhenConnected(sessionId);
                return;
            }

            TerminalLogger.Log($"[MainWindow] cannot end session {sessionId} on {endpoint}: no connection to send its kill on ({whyNone}); it keeps running there");
        }

        /// <summary>
        /// The remote endpoint's host, built if no pane has used it yet - a restored tab closed before it was shown,
        /// or before its own factory call got that far (the creator only constructs; it never connects). Built whatever
        /// the profile's <c>PersistRemoteSessions</c> says (codex C2): the flag decides where new tabs go, not whether a
        /// shell the user closed ends, and a pane whose profile lost the flag still keeps its pending id. A host built
        /// only for a kill connects for it in one automatic attempt, which never prompts, and the window releases it once
        /// the kill is delivered.
        /// </summary>
        /// <param name="whyNone">
        /// When there is none, the reason that holds (codex residual round): session persistence was never on in this
        /// window, so it has no connection registry; the window is closing (the registry is disposed); the SSH profile
        /// is gone (the only thing the creator declines); or setting the connection up failed.
        /// </param>
        private Ntilde.Shell.Mux.MuxConnectionHost? RemoteMuxHostFor(Ntilde.Shell.Mux.MuxEndpointId endpoint, out string? whyNone)
        {
            whyNone = null;
            if (_muxHosts is not { } hosts)
            {
                whyNone = "session persistence has not been on in this window, so it keeps no remote connections";
                return null;
            }

            try
            {
                if (hosts.GetOrCreate(endpoint) is { } host) return host;
                whyNone = hosts.IsDisposed ? "the window is closing" : "its SSH profile is gone";
            }
            catch (Exception ex)
            {
                whyNone = $"its connection could not be set up: {ex.Message}";
            }

            return null;
        }

        private void HandleSshQuickOpen(TerminalProfile profile, SshQuickOpenTarget target, SshDiagnosticsLevel diagnosticsLevel)
        {
            switch (target)
            {
                case SshQuickOpenTarget.CurrentPane:
                    OpenProfileInCurrentPane(profile, diagnosticsLevel);
                    return;
                case SshQuickOpenTarget.NewTab:
                    AddTab(profile, diagnosticsLevel);
                    return;
                case SshQuickOpenTarget.SplitHorizontal:
                    OpenProfileInSplitPane(profile, Avalonia.Layout.Orientation.Vertical, diagnosticsLevel);
                    return;
                case SshQuickOpenTarget.SplitVertical:
                    OpenProfileInSplitPane(profile, Avalonia.Layout.Orientation.Horizontal, diagnosticsLevel);
                    return;
                default:
                    AddTab(profile, diagnosticsLevel);
                    return;
            }
        }

        private void OpenProfileInSplitPane(TerminalProfile profile, Avalonia.Layout.Orientation splitOrientation, SshDiagnosticsLevel diagnosticsLevel)
        {
            if (_currentPane == null)
            {
                AddTab(profile, diagnosticsLevel);
                return;
            }

            SplitPane(splitOrientation);
            OpenProfileInCurrentPane(profile, diagnosticsLevel);
        }

        private void OpenProfileInCurrentPane(TerminalProfile profile, SshDiagnosticsLevel diagnosticsLevel)
        {
            if (_currentPane == null)
            {
                AddTab(profile, diagnosticsLevel);
                return;
            }

            TerminalProfile resolvedProfile = profile.Type == ConnectionType.SSH
                ? _sshConnectionService.GetConnectionProfile(profile.Id) ?? profile
                : _settings.Profiles.Find(p => p.Id == profile.Id) ?? profile;
            var paneToReplace = _currentPane;
            var replacementPane = new TerminalPane(resolvedProfile, diagnosticsLevel);
            WirePane(replacementPane);
            replacementPane.ApplySettings(_settings);

            if (!ReplacePaneInVisualTree(paneToReplace, replacementPane))
            {
                DisposeControlTree(replacementPane);
                AddTab(resolvedProfile, diagnosticsLevel);
                return;
            }

            _currentPane = replacementPane;
            if (TryGetSelectedTab(out var selectedTab))
            {
                _activePaneByTab[selectedTab] = replacementPane;
                AgentHost.AgentSessionRegistry.Instance.SetTabAssociation(replacementPane.PaneId, GetPersistentTabId(selectedTab));
                if (FindTabHeaderTextBlock(selectedTab.Header) is TextBlock tabHeader)
                {
                    tabHeader.Text = resolvedProfile.Name;
                }
            }

            DisposeControlTree(paneToReplace);
            Dispatcher.UIThread.Post(() => replacementPane.ActiveControl.Focus(), DispatcherPriority.Loaded);
            UpdateTabVisuals();
            UpdatePaneAutomationLabels();
            UpdateBroadcastIndicator();
            if (TryGetSelectedTab(out selectedTab))
            {
                RefreshLayoutModelForTab(selectedTab);
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

        void AddTab(TerminalProfile? profile = null, SshDiagnosticsLevel sshDiagnostics = SshDiagnosticsLevel.None)
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

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

            if (TryApplyTemplateRuleForProfile(profile))
            {
                return;
            }

            AddTabWithPane(new TerminalPane(profile, sshDiagnostics), profile.Name);
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

            var theme = _settings.ActiveTheme;
            var borderBrush = new SolidColorBrush(theme.Blue.ToAvaloniaColor());

            // Calculate contrasting foreground for tabs
            double luminance = (0.299 * theme.Background.R + 0.587 * theme.Background.G + 0.114 * theme.Background.B) / 255.0;
            var contrastForeground = luminance > 0.5 ? Brushes.Black : Brushes.White;

            var tabItems = tabs.Items.Cast<TabItem>().ToList();
            // Vertical headers show attention as trailing chips (UpdateVerticalTabExtras),
            // so their title text omits the marker suffix; horizontal headers keep the
            // suffix inside the title as before.
            var labels = BuildTabDisplayLabels(tabItems, 44, includeMarkers: !_isVerticalTabStrip);

            foreach (TabItem ti in tabItems)
            {
                ti.BorderBrush = ti.IsSelected ? borderBrush : Brushes.Transparent;

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
                    tb.Foreground = contrastForeground;
                    tb.Text = labels[ti];
                }

                if (ti.Header is Control headerControl)
                {
                    ToolTip.SetTip(headerControl, BuildFullTabLabel(ti));
                }

                if (_isVerticalTabStrip)
                {
                    UpdateVerticalTabExtras(ti, GetOrCreateTabState(ti), borderBrush);
                }
            }

            UpdateTabAutomationLabels();
            PopulateTabListMenu();
            UpdateTabHeaderViewport();
            sw.Stop();
            RendererStatistics.RecordTabVisualUpdateTime(sw.ElapsedMilliseconds);
        }

        private void UpdateVerticalTabExtras(TabItem tab, TabRuntimeState state, IBrush workingBrush)
        {
            // One pure resolve per tab per pass drives both the dot and the marker chips —
            // the vertical replacement for the title-suffix attention markers (which the
            // display-label builder no longer appends in vertical mode).
            var markers = TabStatusPresentation.ResolveTabMarkers(
                state.HasBell, state.HasActivity, state.AgentTier, _settings.AgentIndicatorTabRollup, state.IsShared);
            var dotVisual = TabStatusPresentation.ResolveTabDot(state.RenderedStatus, markers, state.HasRunningCommand);

            if (FindTabHeaderDescendant<Avalonia.Controls.Shapes.Ellipse>(tab.Header, "TabStatusDot") is { } dot)
            {
                dot.Fill = dotVisual switch
                {
                    TabDotVisual.Working => workingBrush,
                    TabDotVisual.Attention => TabAttentionBrush,
                    TabDotVisual.AgentWrote => TabAgentWroteDotBrush,
                    TabDotVisual.AgentWatched => TabAgentWatchedDotBrush,
                    _ => Brushes.Transparent,
                };
            }

            SetChipVisibility(tab, "TabBellChip", markers.Bell);
            SetChipVisibility(tab, "TabActivityChip", markers.Activity);
            SetChipVisibility(tab, "TabAgentWroteChip", markers.AgentWrote);
            SetChipVisibility(tab, "TabAgentWatchedChip", markers.AgentWatched);
            SetChipVisibility(tab, "TabSharedChip", markers.Shared);

            // OSC 9;4 progress (#271): geometry from the pure resolver; brush by kind —
            // theme blue for normal, the attention amber for error, dim white for
            // paused (matching the dot/chip palette conventions above). Gated at render
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
                    progressBar.Foreground = report is { Kind: TerminalProgressKind.Error }
                        ? TabAttentionBrush
                        : report is { Kind: TerminalProgressKind.Paused }
                            ? TabActivityChipBrush
                            : workingBrush;
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

            var dividerBrush = new SolidColorBrush(Color.FromRgb(35, 35, 35)); // Even more subtle

            if (orientation == Avalonia.Layout.Orientation.Horizontal)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = minPaneWidth });
                grid.ColumnDefinitions.Add(new ColumnDefinition(3, GridUnitType.Pixel)); // 3px hit area
                grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = minPaneWidth });

                var splitter = new GridSplitter
                {
                    Width = 3,
                    Background = dividerBrush,
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
                    Background = dividerBrush,
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

        private void ApplyThemeToUI()
        {
            var theme = _settings.ActiveTheme;

            // Background brush for the main content area
            var bgBrush = new SolidColorBrush(theme.Background.ToAvaloniaColor(), _settings.WindowOpacity);

            // Header/TitleBar brush (slightly darker/different to provide contrast)
            var themeBg = theme.Background.ToAvaloniaColor();
            var headerBg = Color.FromRgb(
                (byte)Math.Max(0, themeBg.R - 10),
                (byte)Math.Max(0, themeBg.G - 10),
                (byte)Math.Max(0, themeBg.B - 10));

            // Use slightly higher opacity for the header to keep buttons visible
            var titleBarOpacity = Math.Min(1.0, _settings.WindowOpacity + 0.1);
            var headerBrush = new SolidColorBrush(headerBg, titleBarOpacity);

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

            var contrastColor = theme.GetContrastForeground();
            var contrastForeground = new SolidColorBrush(contrastColor.ToAvaloniaColor());

            // Set the window theme variant to ensure OS caption buttons (Min/Max/Close)
            // adapt to the background brightness (Dark background -> Light buttons, Light background -> Dark buttons).
            this.RequestedThemeVariant = contrastColor == TermColor.Black ? ThemeVariant.Light : ThemeVariant.Dark;

            // Apply to Window Foreground (inherited by many controls)
            this.Foreground = contrastForeground;

            ApplyTitleBarForegrounds(contrastForeground);
            SyncRecordingButtonState();
            foreach (var splitter in this.GetVisualDescendants().OfType<GridSplitter>())
            {
                ApplySplitterVisualState(splitter);
            }

            // Force update of tab borders (blue line) since theme color changed
            UpdateTabVisuals();

            var titleBar = this.FindControl<Grid>("TitleBar");
            if (titleBar != null) titleBar.Background = Brushes.Transparent;

            var dragBorder = this.FindControl<Border>("DragBorder");
            if (dragBorder != null)
            {
                dragBorder.Background = headerBrush;
            }

            _connectionManagerWindow?.ApplyTheme(theme);
        }

        /// <summary>
        /// The theme's contrast foreground as a fresh brush - the color every title-bar button
        /// and icon should read in the current theme (dark ink on light themes, light on dark).
        /// </summary>
        private SolidColorBrush TitleBarContrastForeground()
            => new SolidColorBrush(_settings.ActiveTheme.GetContrastForeground().ToAvaloniaColor());

        /// <summary>
        /// Re-applies the active theme's contrast foreground to the title-bar buttons and icons.
        /// Extracted from <see cref="ApplyThemeToUI"/> because ordering defeats it there in two
        /// real paths: at startup ApplyThemeToUI runs before the first RebuildTitleBar (so every
        /// FindTitleBarButton lookup missed), and the settings-save path runs ApplyThemeToUI and
        /// then rebuilds the bar anyway. Populate() replaces the generated buttons with fresh
        /// instances whose foregrounds fall back to the Fluent variant brushes - white under the
        /// app's default Dark variant - so a rebuild must re-apply these itself, or light themes
        /// show white-on-light icons until the next theme action.
        /// </summary>
        private void ApplyTitleBarForegrounds(SolidColorBrush? contrastForeground = null)
        {
            // Default to a fresh brush, but let ApplyThemeToUI share ITS instance so the buttons
            // keep pointing at the same brush as the window Foreground (tests pin that invariant,
            // and it keeps one invalidation covering the whole bar).
            contrastForeground ??= TitleBarContrastForeground();

            var btnNew = this.FindControl<Button>(TitleBarViewFactory.NewTabButtonName);
            var btnTabList = FindTitleBarButton(TitleBarCatalog.OpenTabListId);
            var iconTabList = btnTabList?.Content as PathIcon;
            var btnRecord = FindTitleBarButton("toggle_recording");
            var btnConns = FindTitleBarButton("connections");
            var commandSearchBox = this.FindControl<TextBox>("CommandSearchBox");

            if (btnNew != null) btnNew.Foreground = contrastForeground;
            if (btnTabList != null) btnTabList.Foreground = contrastForeground;
            if (iconTabList != null) iconTabList.Foreground = contrastForeground;
            if (btnConns != null) btnConns.Foreground = contrastForeground;
            if (btnRecord != null) btnRecord.Foreground = contrastForeground;
            if (commandSearchBox != null) commandSearchBox.Foreground = contrastForeground;
        }

        private void SetupCommandPalette()
        {
            CommandRegistry.Clear();

            // 1. Register Default Commands
            CommandRegistry.Register("New Tab", "General", () => AddTab(), GetEffectiveShortcutBinding("new_tab", "Ctrl+Shift+T"), "new_tab");

            // "Check for updates" is always registered, even on a portable-zip or dev run: its
            // handler calls EnsureUpdateCoordinator() before checking, and answering "this build
            // cannot update itself" is what makes the entry meaningful there - a dev run should
            // never simply have it missing. That also means it works within the first 10 seconds
            // of the process's life, before the deferred startup timer would otherwise build the
            // coordinator itself. The restart entry is different: it only makes sense once
            // something is actually staged, and SetupCommandPalette()'s laziness (it runs on
            // palette-open and settings-save) is what lets this reflect the live staged state
            // rather than a value latched at startup.
            if (_updateCoordinator is { IsUpdateStaged: true })
            {
                CommandRegistry.Register(
                    $"Update: Restart to apply {_updateCoordinator.StagedVersion}",
                    "Application",
                    () => ApplyStagedUpdate(),
                    "");
            }

            CommandRegistry.Register("Update: Check for updates", "Application", () =>
            {
                _ = RunManualCheckSafeAsync();
            }, "");

            // Dynamic Profile Tabs
            if (_settings.Profiles != null)
            {
                foreach (var profile in _settings.Profiles.Where(p => p.Type == ConnectionType.Local))
                {
                    // Hidden only if the command belongs to another OS. Keyed off existence, this
                    // dropped profiles the terminal can open perfectly well - a quoted path, or a
                    // command with inline arguments - so they were missing from the palette while
                    // still working from a restored session.
                    if (ShellHelper.IsCommandForAnotherPlatform(profile.Command)) continue;
                    CommandRegistry.Register($"New Tab: {profile.Name}", "Shell", () => AddTab(profile), "");
                }
            }

            if (_sshConnectionService != null)
            {
                foreach (var profile in _sshConnectionService.GetConnectionProfiles())
                {
                    var capturedProfile = profile;
                    CommandRegistry.Register($"New Connection (SSH): {profile.Name}", "SSH", () => AddTab(capturedProfile), "");
                }
            }

            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs != null)
            {
                foreach (var tab in tabs.Items.Cast<TabItem>())
                {
                    var capturedTab = tab;
                    string label = GetTabSwitchCommandLabel(capturedTab);
                    CommandRegistry.Register(label, "Tabs", () =>
                    {
                        tabs.SelectedItem = capturedTab;
                    }, "");
                }
            }

            CommandRegistry.Register("Workspace: Save Current", "Workspace", () => _ = SaveWorkspaceInteractiveAsync(), "");
            CommandRegistry.Register("Workspace: Load...", "Workspace", () => _ = LoadWorkspaceInteractiveAsync(), "");
            CommandRegistry.Register("Workspace Template: Save Current", "Workspace", () => _ = SaveWorkspaceTemplateInteractiveAsync(), "");
            CommandRegistry.Register("Workspace Template: Apply...", "Workspace", () => _ = LoadWorkspaceTemplateInteractiveAsync(), "");
            CommandRegistry.Register("Tab Rule: Set Template for Current Profile...", "Workspace", () => _ = SetTemplateRuleForCurrentPaneProfileAsync(), "");
            CommandRegistry.Register("Tab Rule: Clear Template for Current Profile", "Workspace", () => ClearTemplateRuleForCurrentPaneProfile(), "");
            var workspacePolicy = WorkspacePolicyManager.Current;
            if (workspacePolicy.AllowWorkspaceBundleExport)
            {
                CommandRegistry.Register("Workspace: Export Bundle...", "Workspace", () => _ = ExportWorkspaceBundleInteractiveAsync(), "");
                CommandRegistry.Register("Workspace: Export Current Session Bundle...", "Workspace", () => _ = ExportCurrentSessionBundleInteractiveAsync(), "");
            }
            if (workspacePolicy.AllowWorkspaceBundleImport)
            {
                CommandRegistry.Register("Workspace: Import Bundle...", "Workspace", () => _ = ImportWorkspaceBundleInteractiveAsync(), "");
                CommandRegistry.Register("Workspace: Open Bundle...", "Workspace", () => _ = OpenWorkspaceBundleInteractiveAsync(), "");
            }
            foreach (var workspaceName in WorkspaceManager.ListWorkspaceNames())
            {
                string capturedName = workspaceName;
                CommandRegistry.Register($"Workspace: Load {capturedName}", "Workspace", () => LoadWorkspaceByName(capturedName), "");
            }
            foreach (var templateName in WorkspaceManager.ListWorkspaceTemplateNames())
            {
                string capturedTemplate = templateName;
                CommandRegistry.Register($"Workspace Template: Apply {capturedTemplate}", "Workspace", () => ApplyWorkspaceTemplateByName(capturedTemplate), "");
            }

            CommandRegistry.Register("Close Tab", "General", () => CloseActiveTab(), GetEffectiveShortcutBinding("close_tab", "Ctrl+W"), "close_tab");
            CommandRegistry.Register("Close Pane", "General", () => CloseActivePane(), GetEffectiveShortcutBinding("close_pane", "Ctrl+Shift+W"), "close_pane");
            CommandRegistry.Register("Tab: Next (MRU)", "General", () => SwitchTabByMru(reverse: false), GetEffectiveShortcutBinding("next_tab", "Ctrl+Tab"), "next_tab");
            CommandRegistry.Register("Tab: Previous (MRU)", "General", () => SwitchTabByMru(reverse: true), GetEffectiveShortcutBinding("prev_tab", "Ctrl+Shift+Tab"), "prev_tab");
            CommandRegistry.Register("Tab: Move Previous", "General", () => MoveSelectedTab(-1), GetEffectiveShortcutBinding(MoveTabPrevCommandId, "Ctrl+Shift+PageUp"), MoveTabPrevCommandId);
            CommandRegistry.Register("Tab: Move Next", "General", () => MoveSelectedTab(1), GetEffectiveShortcutBinding(MoveTabNextCommandId, "Ctrl+Shift+PageDown"), MoveTabNextCommandId);
            CommandRegistry.Register("Tab: Open Tab List", "General", () => PopulateTabListMenu(showFlyout: true), GetEffectiveShortcutBinding(TitleBarCatalog.OpenTabListId, "Ctrl+Shift+O"), TitleBarCatalog.OpenTabListId);
            CommandRegistry.Register("Tabs: Toggle Vertical Tab Sidebar", "General", () => ToggleTabOrientation(), GetEffectiveShortcutBinding("toggle_tab_orientation", "Ctrl+Shift+L"), "toggle_tab_orientation");
            CommandRegistry.Register("Tab: Rename Current", "General", () => _ = RenameSelectedTabAsync(), "");
            CommandRegistry.Register("Tab: Copy Current Title", "General", () => _ = CopySelectedTabTitleAsync(), "");
            CommandRegistry.Register("Tab: Close Others", "General", () => _ = CloseOtherTabsAsync(), "");
            CommandRegistry.Register("Tab: Toggle Pin", "General", () => TogglePinSelectedTab(), "");
            CommandRegistry.Register("Tab: Toggle Protect", "General", () => ToggleProtectSelectedTab(), "");
            // Keep command naming aligned with common terminal UX:
            // Vertical split => vertical divider => side-by-side panes.
            CommandRegistry.Register("Split Vertical", "View", () => SplitPane(Avalonia.Layout.Orientation.Horizontal), GetEffectiveShortcutBinding("split_vertical", "Ctrl+Shift+D"), "split_vertical");
            // Horizontal split => horizontal divider => stacked panes.
            CommandRegistry.Register("Split Horizontal", "View", () => SplitPane(Avalonia.Layout.Orientation.Vertical), GetEffectiveShortcutBinding("split_horizontal", "Ctrl+Shift+E"), "split_horizontal");
            CommandRegistry.Register("Equalize Panes", "View", () => EqualizeCurrentSplit(), GetEffectiveShortcutBinding("equalize_panes", "Ctrl+Shift+G"), "equalize_panes");
            CommandRegistry.Register("Pane: Toggle Zoom", "View", () => TogglePaneZoomForCurrentTab(), GetEffectiveShortcutBinding("toggle_pane_zoom", "Ctrl+Shift+Z"), "toggle_pane_zoom");
            CommandRegistry.Register("Pane: Toggle Broadcast Input (Tab)", "View", () => ToggleBroadcastForCurrentTab(), GetEffectiveShortcutBinding("toggle_broadcast_input", "Ctrl+Shift+B"), "toggle_broadcast_input");
            CommandRegistry.Register("Pane: Reconnect", "View", () => _currentPane?.Reconnect(), "");
            if (IsMuxPersistenceActive)
            {
                CommandRegistry.Register("Session: Attach to Session\u2026", "General", () => _ = AttachToMuxSessionAsync(), GetEffectiveShortcutBinding(ShortcutCatalog.AttachSessionId, ""), ShortcutCatalog.AttachSessionId);
                CommandRegistry.Register("Pane: Detach", "View", () => DetachActivePane(), GetEffectiveShortcutBinding(ShortcutCatalog.DetachPaneId, ""), ShortcutCatalog.DetachPaneId);
                CommandRegistry.Register("Session: Quit and Close All Shells", "General", () => _ = QuitAndCloseAllShellsAsync(), GetEffectiveShortcutBinding(ShortcutCatalog.QuitAndCloseAllShellsId, ""), ShortcutCatalog.QuitAndCloseAllShellsId);
            }
            CommandRegistry.Register("Focus Pane Left", "View", () => NavigatePane(MoveDirection.Left), "Alt+Left");
            CommandRegistry.Register("Focus Pane Right", "View", () => NavigatePane(MoveDirection.Right), "Alt+Right");
            CommandRegistry.Register("Focus Pane Up", "View", () => NavigatePane(MoveDirection.Up), "Alt+Up");
            CommandRegistry.Register("Focus Pane Down", "View", () => NavigatePane(MoveDirection.Down), "Alt+Down");
            CommandRegistry.Register("Find in Terminal", "Edit", () => _currentPane?.ToggleSearch(), GetEffectiveShortcutBinding("find", "Ctrl+F"), "find");
            CommandRegistry.Register("Pane: Export Snapshot (Plain Text)", "View", () => _currentPane?.ExportSnapshotAsync("txt"), "");
            CommandRegistry.Register("Pane: Export Snapshot (ANSI)", "View", () => _currentPane?.ExportSnapshotAsync("ansi"), "");
            CommandRegistry.Register("Pane: Export Snapshot (PNG)", "View", () => _currentPane?.ExportSnapshotAsync("png"), "");
            CommandRegistry.Register("Pane: Toggle Render HUD", "View", () => _currentPane?.ToggleRenderHud(), "");
            CommandRegistry.Register("Paste", "Edit", () => _ = PasteFromClipboardAsync(), GetEffectiveShortcutBinding("paste", "Ctrl+V"), "paste");
            CommandRegistry.Register("Font: Increase", "View", () => { _settings.FontSize++; ApplySettingsToAllTabs(); _settings.Save(); }, GetEffectiveShortcutBinding("font_increase", "Ctrl++"), "font_increase");
            CommandRegistry.Register("Font: Decrease", "View", () => { _settings.FontSize = Math.Max(6, _settings.FontSize - 1); ApplySettingsToAllTabs(); _settings.Save(); }, GetEffectiveShortcutBinding("font_decrease", "Ctrl+-"), "font_decrease");
            CommandRegistry.Register("Settings", "General", async () =>
            {
                await OpenSettings(0);
            }, GetEffectiveShortcutBinding("settings", "Ctrl+,"), "settings");
            CommandRegistry.Register("Connections", "General", () => ToggleConnections(), GetEffectiveShortcutBinding("connections", "Ctrl+Shift+K"), "connections");
            CommandRegistry.Register("Toggle Recording", "General", () => _currentPane?.ToggleRecording(), GetEffectiveShortcutBinding("toggle_recording", "Ctrl+Shift+R"), "toggle_recording");
            CommandRegistry.Register(
                "Command Assist: Pin/Unpin Selection",
                "General",
                () => _currentPane?.TryToggleCommandAssistPinShortcut(),
                GetEffectiveShortcutBinding("command_assist_pin", "Ctrl+Shift+S"),
                "command_assist_pin");
            CommandRegistry.Register("Open Recording...", "General", () => _ = ExecuteUiCommandAsync(ExecuteOpenRecordingCommandAsync, "Open Recording..."), "");
            CommandRegistry.Register("Open Recordings Folder", "General", () => OpenRecordingsFolder(), "");

            // All three route to the same Backup & Restore settings page rather than acting
            // directly from the palette. Export and import both need a file picker plus a
            // merge/replace mode prompt, and Restore needs the "this replaces your current
            // config" confirmation - all of which already live on that page. Duplicating any of
            // that into the palette would create a second copy of the destructive-action
            // confirmation, which is exactly the part that must not drift between two call sites.
            // Method-group, not a lambda wrapper: keeps the delegate's Method identity as
            // OpenSettingsToBackupPage itself, so a test can assert what these route to via
            // reflection without ever invoking through to the real (headlessly-hanging) ShowDialog.
            CommandRegistry.Register(
                "Export configuration\u2026",
                "Backup",
                OpenSettingsToBackupPage,
                id: "backup.export");

            CommandRegistry.Register(
                "Import configuration\u2026",
                "Backup",
                OpenSettingsToBackupPage,
                id: "backup.import");

            CommandRegistry.Register(
                "Restore from snapshot\u2026",
                "Backup",
                OpenSettingsToBackupPage,
                id: "backup.restore");

            // SFTP Actions
            CommandRegistry.Register("SFTP: Toggle Remote Files", "Remote", () => _currentPane?.ToggleRemoteFilesSidebar(), "");
            CommandRegistry.Register("SFTP: Upload File...", "Remote", () => _ = InitiateSftpTransfer(null, TransferDirection.Upload, TransferKind.File), "");
            CommandRegistry.Register("SFTP: Upload Folder...", "Remote", () => _ = InitiateSftpTransfer(null, TransferDirection.Upload, TransferKind.Folder), "");
            CommandRegistry.Register("SFTP: Download File...", "Remote", () => _ = InitiateSftpTransfer(null, TransferDirection.Download, TransferKind.File), "");
            CommandRegistry.Register("SFTP: Download Folder...", "Remote", () => _ = InitiateSftpTransfer(null, TransferDirection.Download, TransferKind.Folder), "");
            CommandRegistry.Register("SFTP: Show Transfers", "Remote", () => ToggleTransferCenter(), "");

            // Themes: one entry per installed theme (built-in + imported), so the
            // palette never drifts from what the Settings theme list offers.
            foreach (var themeName in _settings.ThemeManager.GetAvailableThemes()
                         .OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
            {
                CommandRegistry.Register(
                    $"Theme: {themeName}",
                    "Theme",
                    () =>
                    {
                        _settings.ThemeName = themeName;
                        _settings.RefreshActiveTheme();
                        ApplyThemeToUI();
                        ApplySettingsToAllTabs();
                        _settings.Save();
                    },
                    "",
                    $"theme:{themeName}");
            }

            // Cursor UX
            CommandRegistry.Register("Cursor: Block", "View", () => { _settings.CursorStyle = "Block"; ApplySettingsToAllTabs(); _settings.Save(); }, "");
            CommandRegistry.Register("Cursor: Beam", "View", () => { _settings.CursorStyle = "Beam"; ApplySettingsToAllTabs(); _settings.Save(); }, "");
            CommandRegistry.Register("Cursor: Underline", "View", () => { _settings.CursorStyle = "Underline"; ApplySettingsToAllTabs(); _settings.Save(); }, "");
            CommandRegistry.Register("Cursor: Toggle Blink", "View", () => { _settings.CursorBlink = !_settings.CursorBlink; ApplySettingsToAllTabs(); _settings.Save(); }, "");

            // Bell UX
            CommandRegistry.Register("Bell: Toggle Audio", "View", () => { _settings.BellAudioEnabled = !_settings.BellAudioEnabled; ApplySettingsToAllTabs(); _settings.Save(); }, "");
            CommandRegistry.Register("Bell: Toggle Visual Flash", "View", () => { _settings.BellVisualEnabled = !_settings.BellVisualEnabled; ApplySettingsToAllTabs(); _settings.Save(); }, "");

            // Scrolling UX
            CommandRegistry.Register("Scroll: Toggle Smooth", "View", () => { _settings.SmoothScrolling = !_settings.SmoothScrolling; ApplySettingsToAllTabs(); _settings.Save(); }, "");
            CommandRegistry.Register("Debug: Box Drawing Test Screen", "Debug", () => ShowBoxDrawingTestScreen(), "");

            // Keep toolbar tooltips in sync with the (possibly rebound) shortcuts.
            UpdateShortcutTooltips();
        }

        private void UpdateShortcutTooltips()
        {
            var btnConnections = FindTitleBarButton("connections");
            if (btnConnections != null)
            {
                ToolTip.SetTip(btnConnections, $"Connections ({GetEffectiveShortcutBinding("connections", "Ctrl+Shift+K")})");
            }

            // The record button's tooltip embeds the recording shortcut and is
            // produced by UpdateRecordButtonUi; re-sync it to pick up rebindings.
            SyncRecordingButtonState();
        }

        private void ShowBoxDrawingTestScreen()
        {
            var pane = _currentPane;
            var buffer = pane?.Buffer;
            if (buffer == null) return;

            int cols = Math.Max(20, buffer.Cols);
            int inner = Math.Max(2, cols - 2);
            string horizontal = new string('\u2500', inner);
            string middle = "\u2502" + new string('.', inner) + "\u2502";

            var ruler = new System.Text.StringBuilder(cols);
            for (int i = 1; i <= cols; i++)
            {
                ruler.Append((char)('0' + (i % 10)));
            }

            var screen = new System.Text.StringBuilder();
            screen.AppendLine("[Ntilde] Box Drawing Repro");
            screen.AppendLine(ruler.ToString());
            screen.AppendLine("\u250C" + horizontal + "\u2510");
            screen.AppendLine(middle);
            screen.AppendLine("\u2514" + horizontal + "\u2518");
            screen.AppendLine("\u253C\u253C\u253C\u253C\u253C  \u2502\u2502\u2502\u2502\u2502  \u2500\u2500\u2500\u2500\u2500");

            buffer.Clear(resetCursor: true);
            buffer.SetCursorPosition(0, 0);
            buffer.WriteContent(screen.ToString(), false);
        }

        private async Task InitiateSftpTransfer(TerminalPane? explicitPane, TransferDirection direction, TransferKind kind)
        {
            var pane = explicitPane ?? _currentPane;
            if (pane == null || pane.Profile == null || pane.Profile.Type != ConnectionType.SSH)
            {
                // Only for SSH sessions
                return;
            }

            if (pane.IsPersistentRemoteTab)
            {
                // Phase 4 spec §8.4, final review I3: as for the sidebar and its transfers - no SSH session of this app
                // stands behind a persisted remote tab (its session id names a daemon session), so say so and do nothing else.
                EnqueueNotice(TerminalPane.RemoteFilesUnavailableNoticeTitle, TerminalPane.RemoteFilesUnavailableMessage);
                return;
            }

            var profile = pane.Profile;
            var sessionId = pane.Session?.Id ?? Guid.Empty;
            await InitiateSftpTransferAsync(profile, sessionId, direction, kind);
        }

        internal Task InitiateSftpTransferForTest(
            TerminalProfile profile,
            Guid sessionId,
            TransferDirection direction,
            TransferKind kind)
        {
            ArgumentNullException.ThrowIfNull(profile);
            return InitiateSftpTransferAsync(profile, sessionId, direction, kind);
        }

        internal Task StartSidebarDownloadForTest(
            TerminalProfile profile,
            Guid sessionId,
            string selectedRemotePath,
            TransferKind kind)
        {
            ArgumentNullException.ThrowIfNull(profile);
            return InitiateSidebarSftpTransferAsync(
                profile,
                sessionId,
                TransferDirection.Download,
                kind,
                selectedRemotePath);
        }

        internal Task StartSidebarUploadForTest(
            TerminalProfile profile,
            Guid sessionId,
            string remoteDirectory,
            TransferKind kind)
        {
            ArgumentNullException.ThrowIfNull(profile);
            return InitiateSidebarSftpTransferAsync(
                profile,
                sessionId,
                TransferDirection.Upload,
                kind,
                remoteDirectory);
        }

        private async Task InitiateSftpTransferAsync(
            TerminalProfile profile,
            Guid sessionId,
            TransferDirection direction,
            TransferKind kind)
        {
            var request = TransferDialogRequest.ForAction(
                direction,
                kind,
                profile.DefaultRemoteDir ?? "~",
                profile.Id,
                sessionId);

            TransferDialogResult? result = await ShowTransferDialogAsync(request);
            if (result is not { IsConfirmed: true })
            {
                return;
            }

            var job = new TransferJob
            {
                SessionId = sessionId,
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                Direction = direction,
                Kind = kind,
                LocalPath = result.LocalPath,
                RemotePath = result.RemotePath
            };

            EnqueueTransferJob(job);
        }

        private Task InitiateSidebarSftpTransfer(
            TerminalPane srcPane,
            TransferDirection direction,
            TransferKind kind,
            string remotePath)
        {
            if (srcPane.Profile == null || srcPane.Session == null)
            {
                return Task.CompletedTask;
            }

            return InitiateSidebarSftpTransferAsync(
                srcPane.Profile,
                srcPane.Session.Id,
                direction,
                kind,
                remotePath);
        }

        private async Task InitiateSidebarSftpTransferAsync(
            TerminalProfile profile,
            Guid sessionId,
            TransferDirection direction,
            TransferKind kind,
            string remotePath)
        {
            if (direction == TransferDirection.Upload)
            {
                string? localPath = kind == TransferKind.File
                    ? await PickLocalUploadFilePathAsync()
                    : await PickLocalUploadFolderPathAsync();
                if (string.IsNullOrWhiteSpace(localPath))
                {
                    return;
                }

                var uploadJob = new TransferJob
                {
                    SessionId = sessionId,
                    ProfileId = profile.Id,
                    ProfileName = profile.Name,
                    Direction = direction,
                    Kind = kind,
                    LocalPath = localPath,
                    RemotePath = remotePath
                };

                EnqueueTransferJob(uploadJob);
                return;
            }

            string? localDownloadPath = kind == TransferKind.File
                ? await PickLocalDownloadFilePathAsync(ResolveSuggestedDownloadFileName(remotePath))
                : await PickLocalDownloadFolderPathAsync();
            if (string.IsNullOrWhiteSpace(localDownloadPath))
            {
                return;
            }

            var job = new TransferJob
            {
                SessionId = sessionId,
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                Direction = direction,
                Kind = kind,
                LocalPath = localDownloadPath,
                RemotePath = remotePath
            };

            EnqueueTransferJob(job);
        }

        internal virtual async Task<TransferDialogResult?> ShowTransferDialogAsync(TransferDialogRequest request)
        {
            var dialog = new TransferDialog(request);
            return await dialog.ShowDialog<TransferDialogResult?>(this);
        }

        internal virtual async Task<string?> PickLocalUploadFilePathAsync()
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null)
            {
                return null;
            }

            IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select File to Upload",
                AllowMultiple = false
            });

            return files.Count > 0 ? files[0].Path.LocalPath : null;
        }

        internal virtual async Task<string?> PickLocalUploadFolderPathAsync()
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null)
            {
                return null;
            }

            IReadOnlyList<IStorageFolder> folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Folder to Upload",
                AllowMultiple = false
            });

            return folders.Count > 0 ? folders[0].Path.LocalPath : null;
        }

        internal virtual async Task<string?> PickLocalDownloadFilePathAsync(string suggestedFileName)
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null)
            {
                return null;
            }

            IStorageFile? file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Select Local Destination",
                SuggestedFileName = suggestedFileName
            });

            return file?.Path.LocalPath;
        }

        internal virtual async Task<string?> PickLocalDownloadFolderPathAsync()
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null)
            {
                return null;
            }

            IReadOnlyList<IStorageFolder> folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Local Destination Folder",
                AllowMultiple = false
            });

            return folders.Count > 0 ? folders[0].Path.LocalPath : null;
        }

        internal virtual void EnqueueTransferJob(TransferJob job)
        {
            SftpService.Instance.AddJob(job, Dispatcher);
            ShowTransferCenter();
        }

        private static string ResolveSuggestedDownloadFileName(string remotePath)
        {
            string trimmed = remotePath.Trim().TrimEnd('/', '\\');
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return "download";
            }

            string fileName = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(fileName)
                ? "download"
                : fileName;
        }

        private void InitializeCommandPaletteUI()
        {
            // 2. Wire UI (Wires only ONCE)
            var overlay = this.FindControl<Grid>("CommandPaletteOverlay");
            var box = this.FindControl<TextBox>("CommandSearchBox");
            var list = this.FindControl<ListBox>("CommandList");

            if (overlay != null)
            {
                overlay.PointerPressed += (s, e) =>
                {
                    // If we clicked the background overlay itself (not the border dialog), close it
                    if (e.Source == overlay) ToggleCommandPalette();
                };
            }

            if (box != null && list != null)
            {
                box.KeyUp += (s, e) =>
                {
                    if (e.Key == Key.Down)
                    {
                        list.SelectedIndex = Math.Min(list.ItemCount - 1, list.SelectedIndex + 1);
                        list.ScrollIntoView(list.SelectedIndex);
                    }
                    else if (e.Key == Key.Up)
                    {
                        list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1);
                        list.ScrollIntoView(list.SelectedIndex);
                    }
                    else if (e.Key == Key.Enter)
                    {
                        if (list.SelectedItem is TerminalCommand cmd)
                        {
                            ExecuteCommand(cmd);
                        }
                    }
                    else if (e.Key == Key.Escape)
                    {
                        ToggleCommandPalette();
                    }
                    else
                    {
                        var results = GetPaletteCommands(box.Text ?? "");
                        list.ItemsSource = results;
                        if (results.Count > 0) list.SelectedIndex = 0;
                    }
                };

                // Filter on text changed too for smoother feel
                box.PropertyChanged += (s, e) =>
                {
                    if (e.Property.Name == "Text")
                    {
                        var results = GetPaletteCommands(box.Text ?? "");
                        list.ItemsSource = results;
                        if (results.Count > 0) list.SelectedIndex = 0;
                    }
                };
            }

            if (list != null)
            {
                list.DoubleTapped += (s, e) =>
                {
                    if (list.SelectedItem is TerminalCommand cmd) ExecuteCommand(cmd);
                };
            }
        }

        private void ToggleCommandPalette()
        {
            var overlay = this.FindControl<Grid>("CommandPaletteOverlay");
            var box = this.FindControl<TextBox>("CommandSearchBox");
            var list = this.FindControl<ListBox>("CommandList");

            if (overlay == null) return;

            bool isVisible = overlay.IsVisible;
            overlay.IsVisible = !isVisible;

            if (!isVisible)
            {
                // Opening
                if (box != null && list != null)
                {
                    SetupCommandPalette();
                    box.Text = "";
                    list.ItemsSource = GetPaletteCommands("");
                    list.SelectedIndex = 0;
                    box.Focus();
                }
            }
            else
            {
                // Closing - return focus to terminal
                _currentPane?.ActiveControl.Focus();
            }
        }

        private TransferCenter? EnsureTransferCenterControl()
        {
            if (_transferCenterControl != null)
            {
                return _transferCenterControl;
            }

            var host = this.FindControl<ContentControl>("TransferCenterHost");
            if (host == null)
            {
                return null;
            }

            _transferCenterControl = new TransferCenter();
            host.Content = _transferCenterControl;
            return _transferCenterControl;
        }

        private void ToggleTransferCenter()
        {
            var overlay = this.FindControl<Border>("TransferOverlay");
            if (overlay != null)
            {
                if (!overlay.IsVisible)
                {
                    _ = EnsureTransferCenterControl();
                }

                overlay.IsVisible = !overlay.IsVisible;
            }
        }

        private void ShowTransferCenter()
        {
            void show()
            {
                var overlay = this.FindControl<Border>("TransferOverlay");
                if (overlay != null)
                {
                    _ = EnsureTransferCenterControl();
                    overlay.IsVisible = true;
                }
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                show();
            }
            else
            {
                Dispatcher.UIThread
                    .InvokeAsync(show, DispatcherPriority.Send)
                    .GetAwaiter()
                    .GetResult();
            }
        }

        private void InitializeTransferCenterUI()
        {
            var btnClose = this.FindControl<Button>("BtnCloseTransfers");
            var overlay = this.FindControl<Border>("TransferOverlay");
            var titleBar = this.FindControl<Grid>("TransferTitleBar");

            if (btnClose != null) btnClose.Click += (s, e) => ToggleTransferCenter();

            if (overlay != null)
            {
                _transferOverlayTransform = overlay.RenderTransform as TranslateTransform;
                if (_transferOverlayTransform == null)
                {
                    _transferOverlayTransform = new TranslateTransform();
                    overlay.RenderTransform = _transferOverlayTransform;
                }
            }

            if (titleBar != null)
            {
                titleBar.PointerPressed += OnTransferTitleBarPointerPressed;
                titleBar.PointerMoved += OnTransferTitleBarPointerMoved;
                titleBar.PointerReleased += OnTransferTitleBarPointerReleased;
                titleBar.PointerCaptureLost += OnTransferTitleBarPointerCaptureLost;
            }
        }

        private void OnTransferTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Control titleBar || e.Source is Button)
            {
                return;
            }

            if (!e.GetCurrentPoint(titleBar).Properties.IsLeftButtonPressed)
            {
                return;
            }

            _isDraggingTransferOverlay = true;
            _transferOverlayDragStart = e.GetPosition(this);
            _transferOverlayOffsetStart = new Point(
                _transferOverlayTransform?.X ?? 0,
                _transferOverlayTransform?.Y ?? 0);
            e.Pointer.Capture(titleBar);
            e.Handled = true;
        }

        private void OnTransferTitleBarPointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_isDraggingTransferOverlay || _transferOverlayTransform == null)
            {
                return;
            }

            Point current = e.GetPosition(this);
            Vector delta = current - _transferOverlayDragStart;
            _transferOverlayTransform.X = _transferOverlayOffsetStart.X + delta.X;
            _transferOverlayTransform.Y = _transferOverlayOffsetStart.Y + delta.Y;
        }

        private void OnTransferTitleBarPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            StopTransferTitleBarDrag(sender, e);
        }

        private void OnTransferTitleBarPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            _isDraggingTransferOverlay = false;
        }

        private void StopTransferTitleBarDrag(object? sender, PointerReleasedEventArgs e)
        {
            if (sender is Control titleBar)
            {
                e.Pointer.Capture(null);
            }

            _isDraggingTransferOverlay = false;
        }

        /// <summary>
        /// The (tab index, section) the title bar's right-click "Customize Title Bar..." entry
        /// point asks <see cref="OpenSettings"/> for. Pulled out of the click handler as its own
        /// synchronous method purely so a test can assert on the target without going through
        /// <c>OpenSettings</c> itself, which reaches a real <c>Window.ShowDialog</c> - headlessly a
        /// hang with no owner shown and nothing to close it (see MainWindowShellExitTests' remarks
        /// on the same hazard for a different dialog). The click handler calls this rather than
        /// inlining the tuple, so this genuinely is what production runs, not a parallel duplicate.
        /// </summary>
        private static (int TabIndex, SettingsSection Section) CustomizeTitleBarSettingsTarget()
            => (0, SettingsSection.TitleBar);

        /// <summary>
        /// Opens Settings on the Backup &amp; Restore page. Routes through <see cref="OpenSettings"/> -
        /// this window's one construction site - rather than constructing a <see cref="SettingsWindow"/>
        /// directly, so Backup gets the same save/legacy-migration handling as every other entry
        /// point. The tab index itself is never hardcoded here: <c>selectBackupPage</c> makes
        /// <see cref="OpenSettings"/> call <see cref="SettingsWindow.SelectBackupPage"/> after
        /// construction, which derives the index from the tab count, so inserting a tab before
        /// Backup can't silently point this at the wrong page.
        /// </summary>
        private void OpenSettingsToBackupPage()
        {
            _ = OpenSettings(0, selectBackupPage: true);
        }

        private async Task OpenSettings(int tabIndex, Guid? profileId = null, SettingsSection section = SettingsSection.None, bool selectBackupPage = false)
        {
            var sw = new SettingsWindow(tabIndex, profileId, section);
            if (selectBackupPage)
            {
                sw.SelectBackupPage();
            }

            // The one live history store, so Settings' "Clear history" acts on the same instance the
            // panes append to (V2 Phase 3b task 5). Reading the property constructs it lazily, which is
            // acceptable here: the user is opening a settings dialog, not on the startup path.
            //
            // Assigned after construction rather than passed to the constructor, and that is safe for a
            // specific reason worth writing down (PR #293 review, non-blocking 8): the window's clear
            // handler reads this property when the button is *clicked*, not when the row is wired. Wiring
            // happens during construction, above this line; the read cannot happen before the dialog is
            // shown, which is after it. A null store is still handled - the row reports "not available in
            // this window" - so a future caller that constructs a SettingsWindow without injecting one
            // degrades visibly instead of throwing.
            sw.CommandAssistHistoryStore = _commandAssistServices.HistoryStore;

            // N7: clearing history deletes the rows any open assist surface is currently showing, and
            // those surfaces do not watch the store. Refreshed from here because MainWindow is what can
            // see the panes.
            sw.OnCommandAssistHistoryCleared += DismissCommandAssistSurfaces;

            // The same injection, for the same reason, for the snippet manager (V2 Phase 4b): one
            // live JsonSnippetStore, so Settings and the panes' pin action write through the same
            // instance rather than two views of one file. Editing or deleting a snippet also invalidates
            // whatever an open assist surface is showing, so it takes the same refresh.
            sw.CommandAssistSnippetStore = _commandAssistServices.SnippetStore;
            sw.OnCommandAssistSnippetsChanged += DismissCommandAssistSurfaces;

            // Snapshot the live-previewed values so Cancel can restore them (#167).
            // The preview handlers below mutate _settings directly; without this,
            // closing the dialog without saving left the preview values live, and any
            // later unrelated Save() persisted them to disk.
            var previewSnapshot = new PreviewSnapshot(
                _settings.WindowOpacity,
                _settings.BlurEffect,
                _settings.BackgroundImagePath,
                _settings.BackgroundImageOpacity,
                _settings.BackgroundImageStretch,
                _settings.FontFamily,
                _settings.FontSize,
                _settings.ThemeName,
                _settings.UiScale);

            // Wire up live preview events
            sw.OnOpacityChanged += (val) => { _settings.WindowOpacity = val; ApplyThemeToUI(); ApplySettingsToAllTabs(); };
            sw.OnBlurChanged += (val) => { _settings.BlurEffect = val; UpdateTransparencyHints(); };
            sw.OnBgImageChanged += (path, opacity, stretch) =>
            {
                _settings.BackgroundImagePath = path;
                _settings.BackgroundImageOpacity = opacity;
                _settings.BackgroundImageStretch = stretch;
                ApplyThemeToUI();
                ApplySettingsToAllTabs();
            };
            sw.OnFontChanged += (font) => { _settings.FontFamily = font; ApplySettingsToAllTabs(); };
            sw.OnFontSizeChanged += (size) => { _settings.FontSize = size; ApplySettingsToAllTabs(); };
            sw.OnUiScaleChanged += (scale) => { _settings.UiScale = scale; UiScale.Apply(scale); };
            sw.OnThemeChanged += (theme) =>
        {
            _settings.ThemeName = theme;
            // Force reload themes to pick up any changes from settings window.
            _settings.ThemeManager.ReloadThemes();
            _settings.RefreshActiveTheme();
            ApplyThemeToUI();
            ApplySettingsToAllTabs();
            UpdateTabVisuals();
        };

            // "Quit and close all shells…" (Task 17): Settings closes without saving, then the quit runs.
            sw.SessionPersistenceActive = IsMuxPersistenceActive;
            bool quitRequested = false;
            sw.OnQuitAndCloseAllShellsRequested += () => quitRequested = true;

            bool saved = await sw.ShowDialog<bool>(this);

            ApplySettingsWindowResult(sw, saved, previewSnapshot);

            if (quitRequested) await QuitAndCloseAllShellsAsync();
        }

        /// <summary>
        /// F1: a successful Import or Restore run from the Backup page changes settings.json (and
        /// possibly more) on disk while this dialog is still open (see
        /// <c>SettingsWindow.ReloadSettingsAfterExternalChangeAsync</c>). Before this fix, only the
        /// <paramref name="saved"/> == true branch below adopted that change; closing the dialog any
        /// other way - Cancel, or the window's X, both of which surface here as
        /// <paramref name="saved"/> == false - left <c>_settings</c> pointing at the stale
        /// PRE-import object. <c>_settings.Save()</c> runs from roughly ten ordinary places in this
        /// class (e.g. the "Font: Increase" / "Font: Decrease" palette commands), so the very next
        /// one of those silently overwrote the just-imported configuration on disk.
        ///
        /// <see cref="SettingsWindow.ConfigurationReplacedExternally"/> is the signal: when it is
        /// set, this method takes the <paramref name="saved"/> == true path's handling regardless of
        /// what <paramref name="saved"/> actually is, so both branches leave <c>MainWindow</c> in
        /// equivalent state. The plain-Cancel path (no import/restore happened) is unchanged: it
        /// still reverts the live-previewed fields captured in <paramref name="previewSnapshot"/>.
        ///
        /// Pulled out of <see cref="OpenSettings"/> as its own method purely for testability -
        /// <c>OpenSettings</c> itself reaches a real <c>Window.ShowDialog</c>, which this repo's
        /// headless test host cannot return from. A test can build a <see cref="SettingsWindow"/>
        /// through the same reflection seam <c>SettingsWindowBackupSectionTests</c> uses to invoke
        /// <c>ReloadSettingsAfterExternalChangeAsync</c>, then call this method directly with
        /// <paramref name="saved"/> = false to simulate Cancel/X after a successful import.
        /// </summary>
        internal void ApplySettingsWindowResult(SettingsWindow sw, bool saved, PreviewSnapshot previewSnapshot)
        {
            if (saved || sw.ConfigurationReplacedExternally)
            {
                // Use the settings object directly from the dialog to avoid disk I/O race conditions
                if (sw.Settings != null)
                {
                    _settings = sw.Settings;
                }
                else
                {
                    _settings = TerminalSettings.Load();
                }

                if (_sshLegacyMigrationService.MigrateLegacyProfiles(_settings))
                {
                    _settings.Save();
                }

                ApplySessionPersistenceSetting();
                RefreshProfileUIs();
                ApplyThemeToUI();
                ApplySettingsToAllTabs();
                UiScale.Apply(_settings.UiScale);
                RebuildTitleBar();
                ApplyTabLayout();
                UpdateTransparencyHints();
                ApplyAgentHostSettingsLive();
                // A toggled-off Osc9ProgressReportingEnabled must clear a taskbar state
                // set before the change (and vice versa).
                RefreshTaskbarProgressFromSelection();

                // Refresh Connection Manager if open
                _connectionManagerWindow?.LoadProfiles(_sshConnectionService.GetConnectionProfiles());
            }
            else
            {
                // Cancel: revert the live preview (#167).
                _settings.WindowOpacity = previewSnapshot.WindowOpacity;
                _settings.BlurEffect = previewSnapshot.BlurEffect;
                _settings.BackgroundImagePath = previewSnapshot.BackgroundImagePath;
                _settings.BackgroundImageOpacity = previewSnapshot.BackgroundImageOpacity;
                _settings.BackgroundImageStretch = previewSnapshot.BackgroundImageStretch;
                _settings.FontFamily = previewSnapshot.FontFamily;
                _settings.FontSize = previewSnapshot.FontSize;
                _settings.ThemeName = previewSnapshot.ThemeName;
                _settings.UiScale = previewSnapshot.UiScale;
                _settings.RefreshActiveTheme();
                UiScale.Apply(_settings.UiScale);

                ApplyThemeToUI();
                ApplySettingsToAllTabs();
                UpdateTransparencyHints();
                UpdateTabVisuals();
                ApplyTabLayout();
            }
        }

        /// <summary>
        /// The factory follows a saved SessionPersistence flip (spec §9). Compared against the
        /// factory in use, not the previous settings object, which the dialog may have edited in
        /// place. Off→on builds (or reuses) the host and warms it; on→off only swaps the factory:
        /// the host stays alive for the mux panes already open. Either way every pane is re-wired,
        /// which only changes what its Reconnect creates.
        /// </summary>
        private void ApplySessionPersistenceSetting()
        {
            bool wantPersistent = Ntilde.Shell.Mux.SessionPersistenceMode.IsKeepOnClose(_settings.SessionPersistence);
            bool isPersistent = _sessionFactory is Ntilde.Shell.Mux.MuxTerminalSessionFactory;
            if (wantPersistent == isPersistent) return;

            if (wantPersistent)
            {
                _sessionFactory = CreatePersistentSessionFactory();
                if (_sessionFactory is not Ntilde.Shell.Mux.MuxTerminalSessionFactory) return; // creation failed and was logged: nothing changed
                _muxHosts!.Local.WarmUp();
            }
            else
            {
                _sessionFactory = Ntilde.Shell.DefaultTerminalSessionFactory.Instance;
            }

            foreach (var pane in _paneOwnerTab.Keys.ToList())
            {
                WirePane(pane);
            }
        }

        /// <summary>
        /// The live-previewed fields <see cref="OpenSettings"/> snapshots before showing the dialog
        /// (#167), so a plain Cancel can revert them. A named type rather than the anonymous type
        /// this used to be, so <see cref="ApplySettingsWindowResult"/> can take it as a parameter and
        /// be called from a test without going through <c>OpenSettings</c> itself.
        /// </summary>
        internal readonly record struct PreviewSnapshot(
            double WindowOpacity,
            string BlurEffect,
            string BackgroundImagePath,
            double BackgroundImageOpacity,
            string BackgroundImageStretch,
            string FontFamily,
            double FontSize,
            string ThemeName,
            double UiScale);

        // Dialogs raised from the Connection Manager window take that window as their owner
        // (optional `owner`, falling back to MainWindow everywhere else) so they stack on the
        // surface the user actually acted on rather than on the disabled root window.
        private async Task ShowNewSshConnectionDialogAsync(TerminalProfile? existingProfile, Window? owner = null)
        {
            var vm = _sshConnectionService.CreateEditorViewModel(existingProfile);
            // The default backend for a NEW profile follows the global toggle: native where it is
            // enabled, OpenSSH where it is not — so the default can never point at a backend that
            // would refuse to connect. Existing profiles arrive with their stored kind and are
            // never re-defaulted. Model-level defaults stay OpenSsh on purpose (old stores whose
            // JSON predates the field must not silently migrate); this is the creation surface.
            vm.BackendKind ??= _settings.ExperimentalNativeSshEnabled
                ? Ntilde.Platform.Ssh.Models.SshBackendKind.Native
                : Ntilde.Platform.Ssh.Models.SshBackendKind.OpenSsh;
            vm.ExperimentalNativeSshEnabled = _settings.ExperimentalNativeSshEnabled;
            var dialog = new NewSshConnectionView(vm);
            ApplyThemeToDialogWindow(dialog);
            WireRemoteMuxInstall(vm, dialog);
            bool saved = await dialog.ShowDialog<bool>(owner ?? this);

            if (!saved)
            {
                return;
            }

            try
            {
                var savedProfile = _sshConnectionService.SaveProfile(vm);
                TerminalProfile profile = _sshConnectionService.GetConnectionProfile(savedProfile.Id)
                    ?? SshConnectionService.ToRuntimeProfile(savedProfile);
                RefreshProfileUIs();

                if (vm.ConnectAfterSave)
                {
                    if (profile.SshBackendKind == Ntilde.Platform.Ssh.Models.SshBackendKind.Native &&
                        !_settings.ExperimentalNativeSshEnabled)
                    {
                        await ShowSimpleMessageDialogAsync(
                            "Native SSH disabled",
                            "This profile was saved with the Native backend, but native SSH is disabled globally. Turn it on under Settings > SSH or switch the profile back to OpenSSH.",
                            owner);
                        return;
                    }

                    AddTab(profile, SshDiagnosticsLevel.None);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] Failed to save SSH connection: {ex.Message}");
            }
        }

        private async Task DeleteSshProfileAsync(TerminalProfile profile, Window? owner = null)
        {
            if (profile == null)
            {
                return;
            }

            string trimmedName = string.IsNullOrWhiteSpace(profile.Name) ? string.Empty : profile.Name.Trim();
            const int maxLabelLength = 60;
            string displayName = trimmedName.Length > maxLabelLength
                ? trimmedName[..maxLabelLength] + "\u2026"
                : trimmedName;
            string label = string.IsNullOrWhiteSpace(displayName)
                ? "this connection"
                : $"\"{displayName}\"";

            if (!await ShowDeleteConnectionConfirmationAsync(label, owner))
            {
                return;
            }

            try
            {
                _sshConnectionService.DeleteProfile(profile.Id);

                // Two orderings matter here and they pull in opposite directions, so the
                // purge is sequenced and the refresh is guaranteed:
                //   * the purge runs AFTER the store delete, so if the delete throws the
                //     secret stays put rather than being orphaned from a profile that
                //     still exists;
                //   * the refresh runs in a finally, so a throwing purge cannot leave the
                //     deleted connection sitting in the list.
                // Ordering them without the finally forces a choice between an orphaned
                // secret and a stale row; this way neither failure mode exists.
                try
                {
                    Vault?.ForgetSavedPassword(profile);
                }
                finally
                {
                    RefreshProfileUIs();
                }
            }
            catch (Exception ex)
            {
                await ShowSimpleMessageDialogAsync("Delete connection", ex.Message, owner);
            }
        }

        private async Task<bool> ShowDeleteConnectionConfirmationAsync(string label, Window? owner = null)
        {
            bool confirmed = false;

            var dialog = CreateThemedDialogWindow("Delete connection?", 480, 210, canResize: false);

            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 92,
                IsCancel = true
            };
            cancelButton.Click += (_, __) =>
            {
                confirmed = false;
                dialog.Close();
            };

            var deleteButton = new Button
            {
                Content = "Delete",
                Width = 92
            };
            deleteButton.Click += (_, __) =>
            {
                confirmed = true;
                dialog.Close();
            };

            // Give Cancel initial focus: combined with IsCancel above, Escape reliably backs
            // out of a dialog the user may have opened by accident. Delete intentionally has
            // no IsDefault, so Enter never triggers the destructive action.
            dialog.Opened += (_, __) => cancelButton.Focus();

            dialog.Content = new Border
            {
                Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"Delete {label}?",
                            FontWeight = FontWeight.SemiBold,
                            TextWrapping = TextWrapping.Wrap
                        },
                        new TextBlock
                        {
                            Text = "The saved connection and any password stored for it are removed. "
                                 + "Panes already connected keep running until you close them.",
                            Opacity = 0.8,
                            TextWrapping = TextWrapping.Wrap
                        },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { cancelButton, deleteButton }
                        }
                    }
                }
            };

            await dialog.ShowDialog(owner ?? this);
            return confirmed;
        }

        private async Task CopySshLaunchCommandAsync(TerminalProfile profile, SshDiagnosticsLevel diagnosticsLevel, Window? owner = null)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard == null)
            {
                await ShowSimpleMessageDialogAsync("Copy launch command", "Clipboard is not available.", owner);
                return;
            }

            try
            {
                string command = _sshConnectionService.BuildLaunchCommand(profile, diagnosticsLevel);
                await topLevel.Clipboard.SetTextAsync(command);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] Failed to copy SSH command: {ex.Message}");
                await ShowSimpleMessageDialogAsync("Copy launch command", ex.Message, owner);
            }
        }

        private async Task ShowSshConnectionDetailsAsync(TerminalProfile profile, SshDiagnosticsLevel diagnosticsLevel, Window? owner = null)
        {
            try
            {
                var details = _sshConnectionService.BuildLaunchDetails(profile, diagnosticsLevel);
                await ShowConnectionDetailsDialogAsync(details, diagnosticsLevel, owner);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] Failed to show SSH connection details: {ex.Message}");
                await ShowSimpleMessageDialogAsync("Connection details", ex.Message, owner);
            }
        }

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

        private async Task ShowSimpleMessageDialogAsync(string title, string message, Window? owner = null)
        {
            var dialog = CreateThemedDialogWindow(title, 520, 220, canResize: false);

            var closeButton = new Button { Content = "Close", Width = 92 };
            closeButton.Click += (_, __) => dialog.Close();

            dialog.Content = new Border
            {
                Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = message,
                            TextWrapping = TextWrapping.Wrap
                        },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Children = { closeButton }
                        }
                    }
                }
            };

            await dialog.ShowDialog(owner ?? this);
        }

        protected virtual Task ExecuteOpenRecordingCommandAsync()
        {
            return OpenRecordingAsync();
        }

        private async Task OpenRecordingAsync()
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null)
            {
                await ShowSimpleMessageDialogAsync("Open Recording", "File picker is not available in the current window.");
                return;
            }

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Replay File",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Ntilde Recordings") { Patterns = new[] { "*.rec", "*.cast" } } }
            });

            if (files.Count < 1)
            {
                return;
            }

            var path = files[0].Path.LocalPath;
            var replayWin = new Ntilde.UI.Replay.ReplayWindow(path);
            replayWin.Show();
        }

        private async Task ExecuteUiCommandAsync(Func<Task> action, string commandTitle)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                TerminalLogger.Error($"Error executing command {commandTitle}: {ex.Message}");
                await ShowSimpleMessageDialogAsync(commandTitle, ex.Message);
            }
        }

        private void OpenRecordingsFolder()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.RecordingsDirectory);
                OpenPathInShell(new ShellOpenRequest(AppPaths.RecordingsDirectory, null));
            }
            catch
            {
                ShowRecordingToast(
                    "Unable to open recordings folder",
                    AppPaths.RecordingsDirectory,
                    null,
                    AppPaths.RecordingsDirectory,
                    autoHide: true);
            }
        }

        private void OpenRecordingToastFolder()
        {
            if (string.IsNullOrWhiteSpace(_recordingToastFolderPath))
            {
                OpenRecordingsFolder();
                return;
            }

            try
            {
                Directory.CreateDirectory(_recordingToastFolderPath);
                var request = ResolveRecordingRevealRequest(_recordingToastFilePath, _recordingToastFolderPath, OperatingSystem.IsWindows());
                OpenPathInShell(request);
            }
            catch
            {
                ShowRecordingToast(
                    "Unable to open recordings folder",
                    _recordingToastFolderPath,
                    _recordingToastFilePath,
                    _recordingToastFolderPath,
                    autoHide: true);
            }
        }

        private static void OpenPathInShell(ShellOpenRequest request)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = request.FileName,
                Arguments = request.Arguments,
                UseShellExecute = true
            });
        }

        internal static ShellOpenRequest ResolveRecordingRevealRequest(string? filePath, string recordingsDirectory, bool isWindows)
        {
            if (isWindows && !string.IsNullOrWhiteSpace(filePath))
            {
                return new ShellOpenRequest("explorer.exe", $"/select,\"{filePath}\"");
            }

            return new ShellOpenRequest(recordingsDirectory, null);
        }

        private async Task ShowConnectionDetailsDialogAsync(SshLaunchDetails details, SshDiagnosticsLevel diagnosticsLevel, Window? owner = null)
        {
            var dialog = CreateThemedDialogWindow("Connection details", 760, 340, canResize: false);

            var sshPathBox = new TextBox { Text = details.SshPath, IsReadOnly = true };
            var configPathBox = new TextBox { Text = details.ConfigPath, IsReadOnly = true };
            var commandBox = new TextBox
            {
                Text = details.CommandLine,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                Height = 72
            };

            var closeButton = new Button { Content = "Close", Width = 92 };
            closeButton.Click += (_, __) => dialog.Close();

            var copyButton = new Button { Content = "Copy command", Width = 120 };
            copyButton.Click += async (_, __) =>
            {
                var topLevel = TopLevel.GetTopLevel(dialog);
                if (topLevel?.Clipboard != null)
                {
                    await topLevel.Clipboard.SetTextAsync(details.CommandLine);
                }
            };

            dialog.Content = new Border
            {
                Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = $"Diagnostics: {diagnosticsLevel}" },
                        new TextBlock { Text = $"Alias: {details.Alias}" },
                        new TextBlock { Text = "Resolved SSH path" },
                        sshPathBox,
                        new TextBlock { Text = "Generated config path" },
                        configPathBox,
                        new TextBlock { Text = "Repro command" },
                        commandBox,
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { copyButton, closeButton }
                        }
                    }
                }
            };

            await dialog.ShowDialog(owner ?? this);
        }

        private void RefreshProfileUIs()
        {
            PopulateNewTabMenu();
            SetupCommandPalette();

            // Refresh Connection Manager if open
            _connectionManagerWindow?.LoadProfiles(_sshConnectionService.GetConnectionProfiles());
        }

        private void ExecuteCommand(TerminalCommand cmd)
        {
            ToggleCommandPalette(); // Close first
            RecordCommandUsage(cmd.Id);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    cmd.Action?.Invoke();
                }
                catch (Exception ex)
                {
                    TerminalLogger.Error($"Error executing command {cmd.Title}: {ex.Message}");
                }
            }, DispatcherPriority.Background);
        }

        private List<TerminalCommand> GetPaletteCommands(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return CommandPaletteOrdering.OrderForEmptyQuery(CommandRegistry.GetCommands(), _commandPaletteUsage).ToList();
            }

            return CommandPaletteOrdering.OrderSearchResults(CommandRegistry.Search(query), _commandPaletteUsage).ToList();
        }

        private void RecordCommandUsage(string commandId)
        {
            if (string.IsNullOrWhiteSpace(commandId))
            {
                return;
            }

            try
            {
                _commandPaletteUsageStore.RecordUse(commandId, DateTimeOffset.UtcNow);
                _commandPaletteUsageStore.Save();
                _commandPaletteUsage = new Dictionary<string, CommandPaletteUsageEntry>(_commandPaletteUsageStore.Load(), StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                // Keep command execution resilient if usage persistence is unavailable.
            }
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
        private bool _teardownDone;

        /// <summary>
        /// This close is settled - the first-close question was answered (R1), or Task 17's quit was confirmed - so
        /// the close that follows goes straight to the teardown without asking.
        /// </summary>
        private bool _closeConfirmed;

        /// <summary>The first-close question is posted or on screen: another close request meanwhile is held, not asked again.</summary>
        private bool _firstCloseQuestionOpen;

        /// <summary>Local shells <see cref="EndLocalSessionsOnTeardown"/> has ended: neither counted as kept nor saved for reattach.</summary>
        private readonly HashSet<Guid> _localSessionsEndedOnClose = [];

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            if (!ProceedWithClose(e.CloseReason)) e.Cancel = true;
        }

        /// <summary>
        /// Test seam: what <see cref="OnClosing"/> does for a close with <paramref name="reason"/> (only Avalonia can
        /// build the event args, and only its lifetime raises the shutdown reasons). True when the close was held.
        /// </summary>
        internal bool HandleClosingForTest(WindowCloseReason reason) => !ProceedWithClose(reason);

        /// <summary>OnClosing's body: false holds the close while the first-close question is asked; otherwise the teardown runs.</summary>
        private bool ProceedWithClose(WindowCloseReason reason)
        {
            if (HoldCloseForFirstCloseQuestion(reason)) return false;
            PerformAppTeardown();
            return true;
        }

        /// <summary>
        /// Spec R1: a window closing with live local shells asks whether they keep running, unless the answer was
        /// remembered ("Don't ask again"), which is then applied without asking. Not asked when this close is already
        /// settled, when persistence is off (the setting, and the factory with it: with "Off" nothing new appears), or
        /// when no local shell would be left running (remote shells never ask).
        /// <para>
        /// Only a window close asks; a shutdown is never held, since holding it would cancel the shutdown itself. The
        /// application lifetime shutting down (<see cref="WindowCloseReason.ApplicationShutdown"/>: macOS Cmd+Q, the
        /// usual way to quit there, and <c>TryShutdown</c>) applies a remembered answer - a remembered "close" ends the
        /// shells - and with none keeps them. The OS ending the session (<see cref="WindowCloseReason.OSShutdown"/>)
        /// never kills, whatever was remembered: it behaves like Keep, as every close did before R1.
        /// </para>
        /// <para>
        /// While the question is open another window close is held, not asked again - unless nothing is left to keep
        /// (the last tab closed because its shell exited): that close goes through, and the late answer is dropped,
        /// rather than leave a Cancel with a window that has no tabs.
        /// </para>
        /// True holds this close: the question is posted, never asked inside OnClosing, and its answer closes again.
        /// </summary>
        private bool HoldCloseForFirstCloseQuestion(WindowCloseReason reason)
        {
            if (_closeConfirmed || _teardownDone || reason == WindowCloseReason.OSShutdown) return false;
            if (!IsMuxPersistenceActive || !Ntilde.Shell.Mux.SessionPersistenceMode.IsKeepOnClose(_settings.SessionPersistence)) return false;
            bool applicationShutdown = reason == WindowCloseReason.ApplicationShutdown;
            int count = CountKeptLocalSessions();
            if (_firstCloseQuestionOpen && !applicationShutdown) return count > 0; // one question at a time
            if (count == 0) return false;

            switch (MuxCloseChoiceStore.Read())
            {
                case Ntilde.Shell.Mux.MuxCloseChoice.Keep:
                    return false;
                case Ntilde.Shell.Mux.MuxCloseChoice.Close:
                    EndLocalSessionsOnTeardown();
                    return false;
                default:
                    if (applicationShutdown) return false; // never asked: kept
                    _firstCloseQuestionOpen = true;
                    // Posted: the answer closes the window again, and Close() must never re-enter this OnClosing.
                    Dispatcher.UIThread.Post(() => _ = AskFirstCloseAsync(count));
                    return true;
            }
        }

        /// <summary>
        /// Asks the first-close question (R1) and settles the held close with the answer: Cancel leaves the window
        /// open; Keep closes it again, and Close ends the local shells first; either is remembered first when "Don't
        /// ask again" was ticked. A question that could not be asked closes as Keep - nothing is lost, as before R1 -
        /// rather than leave a window that cannot be closed. A close that happened meanwhile by another way (an OS
        /// shutdown, an update restart) already applied its own rules, and the answer is dropped.
        /// </summary>
        private async Task AskFirstCloseAsync(int count)
        {
            // Overtaken before the post ran (an OS shutdown, an update restart): never show the question at all.
            if (_teardownDone)
            {
                _firstCloseQuestionOpen = false;
                return;
            }

            FirstCloseAnswer answer;
            try
            {
                answer = await ConfirmFirstClose(count);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[MainWindow] the first-close question failed; keeping the shells running: {ex.Message}");
                answer = new FirstCloseAnswer(FirstCloseAction.Keep, Remember: false);
            }
            finally
            {
                _firstCloseQuestionOpen = false;
            }

            if (_teardownDone || answer.Action is not (FirstCloseAction.Keep or FirstCloseAction.Close)) return;

            try
            {
                bool end = answer.Action == FirstCloseAction.Close;
                if (answer.Remember) MuxCloseChoiceStore.Remember(end ? Ntilde.Shell.Mux.MuxCloseChoice.Close : Ntilde.Shell.Mux.MuxCloseChoice.Keep);
                if (end) EndLocalSessionsOnTeardown();
                _closeConfirmed = true;
                Close();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[MainWindow] closing after the first-close question failed: {ex}");
            }
        }

        /// <summary>
        /// The local panes whose shells a close would leave running in the daemon: what the first-close question (R1)
        /// counts and the teardown logs. Shells <see cref="EndLocalSessionsOnTeardown"/> has ended are not kept.
        /// </summary>
        private int CountKeptLocalSessions() => _paneOwnerTab.Keys.Count(p => ShowsLocalMuxEndpoint(p)
            && p.Session is Ntilde.Mux.MuxClientSession { IsConnected: true, IsProcessRunning: true } mux
            && !_localSessionsEndedOnClose.Contains(mux.Id));

        /// <summary>
        /// Ends every local pane's live shell as the window closes: R1's "Close them" (asked or remembered), and Task
        /// 17's quit. Each kill goes to the local host (<see cref="Ntilde.Shell.Mux.MuxConnectionHost.KillWhenConnected"/>),
        /// sent at once and tracked, and the teardown's <see cref="Ntilde.Shell.Mux.MuxConnectionHosts.Dispose"/> waits
        /// for the replies before it disconnects, so none is dropped behind the close. The ended shells are not saved for
        /// reattach either: the next launch starts fresh shells quietly, as with persistence off, rather than report
        /// them lost. Remote shells are left alone, and so is a shell another client is also typing into: a pane close
        /// never ends one without asking (the shared-close question), so it detaches, as Keep does, and stays counted
        /// as kept. Idempotent.
        /// </summary>
        private void EndLocalSessionsOnTeardown()
        {
            if (_muxHosts is not { } hosts) return;
            int ended = 0;
            int shared = 0;
            foreach (TerminalPane pane in _paneOwnerTab.Keys)
            {
                if (!ShowsLocalMuxEndpoint(pane)
                    || pane.Session is not Ntilde.Mux.MuxClientSession { IsConnected: true, IsProcessRunning: true } mux
                    || _localSessionsEndedOnClose.Contains(mux.Id))
                {
                    continue;
                }

                // The session's cached count (read-only observers excluded) or the pane's, whichever knows of another client.
                if (mux.InteractiveOthers > 0 || pane.MuxOtherClients > 0)
                {
                    shared++;
                    continue;
                }

                _localSessionsEndedOnClose.Add(mux.Id);
                hosts.Local.KillWhenConnected(mux.Id);
                ended++;
            }

            if (ended > 0) AppLogger.Log($"[MainWindow] ending {ended} local session(s) as the window closes");
            if (shared > 0) AppLogger.Log($"[MainWindow] {shared} shared local session(s) left running for the other clients that show them");
        }

        private bool _quitAndCloseAllInProgress;

        /// <summary>
        /// "Quit and close all shells" (Task 17): ends EVERY shell in the local daemon - this window's panes, shells
        /// shared with other clients, and detached ones - shuts the daemon down, and closes the window. Remote shells
        /// are untouched. Asks first, naming the daemon's running count. The ended shells are not saved for reattach,
        /// so the next launch starts fresh ones quietly. A daemon that cannot be reached or does not stop is logged and
        /// the window closes anyway.
        /// </summary>
        internal async Task QuitAndCloseAllShellsAsync()
        {
            if (_quitAndCloseAllInProgress || _teardownDone || !IsMuxPersistenceActive || _muxHosts?.Local is not { } host) return;
            _quitAndCloseAllInProgress = true;
            try
            {
                // Off the UI thread: GetClient blocks while a connect is in flight.
                IReadOnlyList<Guid>? running = await Task.Run(async () =>
                {
                    Ntilde.Mux.MuxClient? client = host.GetClient(TimeSpan.FromSeconds(3));
                    if (client is null) return null;
                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try
                    {
                        IReadOnlyList<Ntilde.Mux.Contracts.SessionSummary> all = await client.ListSessionsAsync(cts.Token).ConfigureAwait(false);
                        return (IReadOnlyList<Guid>)all.Where(s => s.Running).Select(s => s.SessionId).ToList();
                    }
                    catch (Exception ex) when (ex is Ntilde.Mux.Contracts.MuxProtocolException or IOException or TimeoutException or OperationCanceledException or ObjectDisposedException)
                    {
                        AppLogger.Log($"[MainWindow] listing mux sessions before quit failed: {ex.Message}");
                        return null;
                    }
                });

                if (running is null || running.Count > 0)
                {
                    bool confirmed;
                    try
                    {
                        confirmed = await ConfirmQuitAndCloseAll(running?.Count ?? -1);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Log($"[MainWindow] the quit confirmation failed; not quitting: {ex.Message}");
                        return;
                    }

                    if (!confirmed) return;
                }

                // The panes' shells go through the host (tracked, flushed at teardown); then every other shell the daemon
                // runs - shared and detached ones - is killed on this connection, and the daemon is shut down.
                EndLocalSessionsOnTeardown();
                var others = running?.Where(id => !_localSessionsEndedOnClose.Contains(id)).ToList() ?? [];
                foreach (TerminalPane pane in _paneOwnerTab.Keys)
                {
                    if (ShowsLocalMuxEndpoint(pane) && pane.Session is Ntilde.Mux.MuxClientSession mux) _localSessionsEndedOnClose.Add(mux.Id);
                }

                await Task.Run(async () =>
                {
                    if (others.Count > 0 && host.GetClient(TimeSpan.FromSeconds(3)) is { } client)
                    {
                        foreach (Guid id in others)
                        {
                            try
                            {
                                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
                                await client.KillAsync(id, cts.Token).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                AppLogger.Log($"[MainWindow] killing session {id} before quit failed: {ex.Message}");
                            }
                        }
                    }
                });

                await ShutdownLocalDaemonAsync(TimeSpan.FromSeconds(5));

                _closeConfirmed = true;
                Close();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[MainWindow] quit and close all shells failed: {ex}");
            }
            finally
            {
                _quitAndCloseAllInProgress = false;
            }
        }

        /// <summary>The production "Quit and close all shells" question; <paramref name="count"/> is -1 when unknown.</summary>
        private Task<bool> ShowQuitAndCloseAllDialogAsync(int count)
        {
            string what = count switch
            {
                < 0 => "All shells running in the background will be closed, including detached and shared ones.",
                1 => "1 shell running in the background will be closed, including detached and shared ones.",
                _ => $"{count} shells running in the background will be closed, including detached and shared ones.",
            };
            bool remote = _paneOwnerTab.Keys.Any(p => !ShowsLocalMuxEndpoint(p) && p.Session is Ntilde.Mux.MuxClientSession);
            if (remote) what += " Remote shells keep running.";
            return ShowConfirmationDialogAsync("Quit Ntilde", "Close every shell?", what, "Quit and close", 140);
        }

        /// <summary>
        /// The teardown a normal close performs: save the session, stop the toast timers, and
        /// dispose/stop what OnOpened set up. Extracted so <see cref="ApplyStagedUpdate"/> can run
        /// it too - an update restart terminates the process directly and never reaches
        /// <see cref="OnClosing"/>, so without calling this first, taking the update would
        /// silently skip all of it.
        /// </summary>
        private void PerformAppTeardown()
        {
            // Idempotent: an update restart runs this and the window's own close can follow it.
            // A second pass would re-save a session whose connection is already gone.
            if (_teardownDone) return;
            _teardownDone = true;
            TeardownFaultForTest?.Invoke();

            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs != null)
            {
                SessionManager.SaveSession(this, tabs, _localSessionsEndedOnClose);
            }

            if (_muxHosts is { } muxHosts)
            {
                // The agent host stops asking these hosts before they close (the Stop below clears it again).
                AgentHost.AgentHostService.Instance.SetWindowlessSource(null);
                // Local panes only: the log line points at `ntilde mux ls`, which lists the local daemon.
                int kept = CountKeptLocalSessions();
                // Explicit detach: closing each connection makes its daemon drop this client's
                // subscriptions and keep every shell running. Panes are deliberately not disposed
                // (that would kill them). Each host flushes what is already queued first, so a kill
                // from a pane closed just before (the last tab) still reaches its daemon. The remote
                // hosts go first, together; the local one last (Phase 4 spec §5).
                muxHosts.Dispose();
                if (kept > 0) AppLogger.Log($"[MainWindow] {kept} session(s) kept running; `ntilde mux ls` lists them");
            }
            _recordingToastTimer.Stop();
            _updateCheckTimer.Stop();
            _tabStatusTimer?.Stop();
            // A mid-drag close can beat the release/capture-lost cleanup, and a live
            // DispatcherTimer would keep ticking (and rooting) the closed window.
            _tabDragAutoScrollTimer?.Stop();
            _globalHotkey?.Dispose();
            // Dispose the snapshot scheduler before the agent-host teardown below: its Dispose
            // performs a best-effort final flush, which writes a snapshot, which logs — and the
            // agent-host stop path below is not something a snapshot write should race.
            _snapshotScheduler?.Dispose();
            _snapshotScheduler = null;
            AgentHost.AgentHostService.Instance.ObserveActivityChanged -= OnAgentObserveActivityChanged;
            AgentHost.AgentSessionRegistry.Instance.SessionRegistered -= OnAgentSessionRegisteredForAttention;
            AgentHost.AgentSessionRegistry.Instance.SessionUnregistered -= OnAgentSessionUnregisteredForAttention;
            foreach (var registration in AgentHost.AgentSessionRegistry.Instance.GetRegistrations())
            {
                registration.AttentionMachine.Changed -= OnAgentAttentionChangedForTabs;
            }
            AgentHost.AgentHostService.Instance.Stop();
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

        private TerminalPane? FindFirstPane(Control? control)
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

        private void OnRecordingStateChanged(bool isRecording)
        {
            Dispatcher.UIThread.Post(() =>
            {
                bool changed = isRecording
                    ? _activeTitleBarToggles.Add("toggle_recording")
                    : _activeTitleBarToggles.Remove("toggle_recording");

                if (changed)
                {
                    // Surfaces an overflowed Record button into the bar while recording and drops
                    // it back into the … flyout when it stops. RebuildTitleBar re-syncs the button
                    // colouring itself, so no separate UpdateRecordButtonUi call belongs here.
                    RebuildTitleBar();
                }
                else
                {
                    UpdateRecordButtonUi(isRecording);
                }
            });
        }

        private void OnRecordingNotification(RecordingNotificationEventArgs notification)
        {
            Dispatcher.UIThread.Post(() =>
            {
                switch (notification.Kind)
                {
                    case RecordingNotificationKind.Started:
                        ShowRecordingToast(
                            "Recording started",
                            BuildRecordingToastMessage(notification),
                            notification.FilePath,
                            notification.RecordingsDirectory,
                            autoHide: true);
                        break;
                    case RecordingNotificationKind.Stopped:
                        ShowRecordingToast(
                            "Recording saved",
                            BuildRecordingToastMessage(notification),
                            notification.FilePath,
                            notification.RecordingsDirectory,
                            autoHide: true);
                        break;
                    case RecordingNotificationKind.Failed:
                        ShowRecordingToast(
                            "Recording failed",
                            notification.ErrorMessage ?? "Unable to start recording.",
                            notification.FilePath,
                            notification.RecordingsDirectory,
                            autoHide: true);
                        break;
                }
            });
        }

        private static string BuildRecordingToastMessage(RecordingNotificationEventArgs notification)
        {
            if (!string.IsNullOrWhiteSpace(notification.FilePath))
            {
                return notification.FilePath!;
            }

            return notification.RecordingsDirectory;
        }

        /// <param name="action">
        /// The toast's one generic action button (Phase 4 spec §7.5), shown only when there is one: a
        /// click hides the toast and runs it. Every toast without one hides the button.
        /// </param>
        private void ShowRecordingToast(string title, string message, string? filePath, string? folderPath, bool autoHide, PersistenceNoticeAction? action = null)
        {
            var toast = this.FindControl<Border>("RecordingToast");
            var titleBlock = this.FindControl<TextBlock>("RecordingToastTitle");
            var messageBlock = this.FindControl<TextBlock>("RecordingToastMessage");
            if (toast == null || titleBlock == null || messageBlock == null)
            {
                return;
            }

            _recordingToastFilePath = filePath;
            _recordingToastFolderPath = folderPath;
            _recordingToastAction = action;
            titleBlock.Text = title;
            messageBlock.Text = message;

            // The folder button only makes sense for toasts that have one
            // (recordings). Non-file toasts (long-command completion) would
            // otherwise offer a button that opens an unrelated folder.
            var openFolderButton = this.FindControl<Button>("RecordingToastOpenFolder");
            if (openFolderButton != null)
            {
                openFolderButton.IsVisible = folderPath != null || filePath != null;
            }

            var actionButton = this.FindControl<Button>("RecordingToastAction");
            if (actionButton != null)
            {
                actionButton.Content = action?.Label;
                actionButton.IsVisible = action != null;
            }

            toast.IsVisible = true;

            _recordingToastTimer.Stop();
            if (autoHide)
            {
                _recordingToastTimer.Start();
            }
        }

        private void HideRecordingToast()
        {
            _recordingToastTimer.Stop();
            // A hidden toast offers nothing: a stray click must not run an action it no longer shows.
            _recordingToastAction = null;
            var toast = this.FindControl<Border>("RecordingToast");
            if (toast != null)
            {
                toast.IsVisible = false;
            }
        }

        /// <summary>UI thread. The toast's action button: the toast goes first, then its action runs.</summary>
        private void RunRecordingToastAction()
        {
            PersistenceNoticeAction? action = _recordingToastAction;
            HideRecordingToast();
            if (action is null) return;
            try
            {
                action.Run();
            }
            catch (Exception ex)
            {
                // A click handler's throw would take the app down; the action's own UI is the place to say more.
                AppLogger.Log($"[MainWindow] the toast action '{action.Label}' failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Raises the update-ready notice. No auto-hide: unlike a recording toast this is an
        /// offer the user may take minutes later, and closing it only dismisses the notice - the
        /// update stays staged.
        /// </summary>
        private void ShowUpdateToast(string version)
        {
            var toast = this.FindControl<Border>("UpdateToast");
            var messageBlock = this.FindControl<TextBlock>("UpdateToastMessage");
            if (toast == null || messageBlock == null)
            {
                return;
            }

            messageBlock.Text = $"Ntilde {version} is downloaded and will be applied when you restart.";
            toast.IsVisible = true;
        }

        private void HideUpdateToast()
        {
            var toast = this.FindControl<Border>("UpdateToast");
            if (toast != null)
            {
                toast.IsVisible = false;
            }
        }

        /// <summary>
        /// Builds the update coordinator on first use, at most once per process. Deliberately not
        /// called from <see cref="OnOpened"/> directly: <see cref="StartupPerformanceTracker"/>
        /// exists because that interval is measured, and constructing
        /// <see cref="Ntilde.Update.VelopackUpdateService"/> means an assembly load plus a
        /// filesystem probe (its own doc comment explains why that constructor can throw on a
        /// non-Velopack host) - work with no business inside a measured interval, and, unlike the
        /// <c>_globalHotkey</c> block above in <see cref="OnOpened"/>, not previously guarded
        /// against throwing at all. Callers are the one-shot timer's tick (after the measured
        /// interval has closed) and the palette's manual check (a user action, never on the
        /// startup path even if it happens to land in the first 10 seconds).
        /// </summary>
        private void EnsureUpdateCoordinator()
        {
            if (_updateCoordinator != null)
            {
                return;
            }

            try
            {
                _updateCoordinator = new Ntilde.Update.UpdateCoordinator(
                    new Ntilde.Update.VelopackUpdateService(
                        Ntilde.Update.VelopackUpdateService.DefaultRepoUrl,
                        message => TerminalLogger.Log(message)),
                    () => _settings.AutomaticUpdateChecks,
                    version => Dispatcher.UIThread.Post(() =>
                    {
                        ShowUpdateToast(version);
                        SetupCommandPalette();
                    }),
                    message => TerminalLogger.Log(message));
            }
            catch (Exception ex)
            {
                TerminalLogger.Log("Update coordinator construction failed: " + ex);
            }
        }

        /// <summary>
        /// The background check's fire-and-forget entry point. Wrapping it here - rather than
        /// discarding <c>_updateCoordinator.RunAutomaticCheckAsync()</c> directly - matters
        /// because <see cref="Ntilde.Update.UpdateCoordinator.RunCheckAsync"/> only wraps
        /// the network call in its own try/catch; the <c>IsSupported</c> read and the
        /// <c>onUpdateReady</c> callback both sit outside it, so a throw from either would
        /// otherwise fault this task unobserved - invisible even in the log, which is the one
        /// thing the silent-failure design for the automatic check leans on. Also guards against
        /// racing a concurrent manual check into the same staging directory (see
        /// <see cref="_updateCheckInFlight"/>).
        /// </summary>
        private async System.Threading.Tasks.Task RunAutomaticCheckSafeAsync()
        {
            if (_updateCheckInFlight || _updateCoordinator == null)
            {
                return;
            }

            _updateCheckInFlight = true;
            try
            {
                // Task.Run, deliberately: this method is reached from a DispatcherTimer tick,
                // so everything up to the first real await would otherwise run on the UI
                // thread - and Velopack's prologue is not cheap. UpdateManager
                // .CheckForUpdatesAsync synchronously calls EnsureInstalled(),
                // Locator.GetOrCreateStagedUserId() (a file read plus write) and
                // GetLatestLocalFullPackage() -> GetLocalPackages(), which enumerates the
                // packages directory and parses the nuspec inside every .nupkg - i.e. inside a
                // ~150 MB self-contained bundle. On a cold disk, or with AV in the path, that
                // is a visible hitch ~10 s after launch. Do not "simplify" this back to a bare
                // await; the continuation still resumes on the UI thread, which is what the
                // outcome handling (ShowRecordingToast / ShowUpdateToast) needs.
                await Task.Run(() => _updateCoordinator.RunAutomaticCheckAsync());
            }
            catch (Exception ex)
            {
                TerminalLogger.Log("Automatic update check failed unexpectedly: " + ex);
            }
            finally
            {
                _updateCheckInFlight = false;
            }
        }

        /// <summary>
        /// The manual check's fire-and-forget entry point, for the same reason
        /// <see cref="RunAutomaticCheckSafeAsync"/> exists: <c>ExecuteCommand</c> invokes the
        /// palette action synchronously, so a bare <c>_ = CheckForUpdatesInteractiveAsync()</c>
        /// would leave any throw past the first await faulting an unobserved task - invisible
        /// even in the log. <see cref="CheckForUpdatesInteractiveAsync"/> handles every outcome
        /// the coordinator reports; this only catches what the coordinator cannot (a throwing
        /// <c>IsSupported</c>, a throwing toast) and makes sure the user still hears something.
        /// </summary>
        private async System.Threading.Tasks.Task RunManualCheckSafeAsync()
        {
            try
            {
                await CheckForUpdatesInteractiveAsync();
            }
            catch (Exception ex)
            {
                TerminalLogger.Log("Manual update check failed unexpectedly: " + ex);
                ShowRecordingToast("Update check failed", "Could not check for updates. See the debug log for details.", null, null, autoHide: true);
            }
        }

        /// <summary>
        /// Runs the shared app-teardown (session save, timers, global hotkey, agent host) before
        /// handing off to the coordinator. The underlying restart terminates this process itself,
        /// so <see cref="OnClosing"/> never runs for it - without doing the same teardown here
        /// first, "taking the update" would silently drop the session, which is worse than just
        /// quitting and relaunching by hand.
        /// </summary>
        /// <remarks>
        /// The try/catch lives here rather than at the call sites so that BOTH entry points get
        /// it: the toast's Restart button (a raw Click handler - an escaping exception there is
        /// an unhandled exception on the UI thread, which kills the app with no explanation) and
        /// the palette entry (whose <c>ExecuteCommand</c> try/catch would otherwise swallow the
        /// failure without telling the user anything). <c>ApplyUpdatesAndRestart</c> can throw
        /// for real reasons: a missing or locked <c>Update.exe</c>, or the update lock already
        /// held by another instance.
        /// </remarks>
        /// <summary>
        /// The toast's Restart button and the palette/About entries all invoke this as a plain
        /// <c>Action</c>, so it stays synchronous and fire-and-forget; <see cref="ApplyStagedUpdateAsync"/>
        /// is the real body and guarantees it never lets an exception escape as an unobserved task fault.
        /// </summary>
        private void ApplyStagedUpdate() => _ = ApplyStagedUpdateAsync();

        /// <summary>
        /// Runs the shared app-teardown (session save, timers, global hotkey, agent host) before
        /// handing off to the coordinator. The underlying restart terminates this process itself,
        /// so <see cref="OnClosing"/> never runs for it - without doing the same teardown here
        /// first, "taking the update" would silently drop the session, which is worse than just
        /// quitting and relaunching by hand.
        /// </summary>
        /// <remarks>
        /// The try/catch lives here rather than at the call sites so that BOTH entry points get
        /// it: the toast's Restart button (a raw Click handler - an escaping exception there is
        /// an unhandled exception on the UI thread, which kills the app with no explanation) and
        /// the palette entry (whose <c>ExecuteCommand</c> try/catch would otherwise swallow the
        /// failure without telling the user anything). <c>ApplyUpdatesAndRestart</c> can throw
        /// for real reasons: a missing or locked <c>Update.exe</c>, or the update lock already
        /// held by another instance.
        ///
        /// Before any of that, a live daemon is probed (spec §9): the new build must not start
        /// beside a daemon of the old one - the protocol version range is the backstop, not the
        /// mechanism. If it has running sessions, the user is asked to confirm the loss; declining
        /// leaves everything untouched (no teardown, no apply). Confirming - or no daemon at all -
        /// sends <c>shutdown</c> so no old-build daemon survives beside the new one, then proceeds
        /// exactly as before. If listing sessions fails, the count is unknown so there is nothing
        /// to confirm, but <c>shutdown</c> is still attempted best-effort - the alternative is the
        /// exact bug this method exists to prevent, an old-build daemon surviving beside the new
        /// one, just because a request on the way in happened to fail. A confirmation dialog that
        /// itself throws is treated as a decline (logged, toasted, no shutdown, no apply) rather
        /// than letting the exception escape this fire-and-forget method as an unobserved fault -
        /// "yes" must never be inferred from a question that could not be asked.
        ///
        /// Re-entrant: the toast button, the palette command and About's button can all reach this
        /// (see <see cref="ApplyStagedUpdate"/>), and nothing stops two of them firing before the
        /// first has finished probing. <see cref="_applyStagedUpdateInProgress"/> makes a second
        /// call while one is already running a no-op rather than double-probing/double-confirming.
        /// </remarks>
        internal async System.Threading.Tasks.Task ApplyStagedUpdateAsync()
        {
            if (_updateCoordinator is not { IsUpdateStaged: true })
            {
                return;
            }

            if (_applyStagedUpdateInProgress)
            {
                return;
            }

            _applyStagedUpdateInProgress = true;
            try
            {
                if (!await PrepareMuxDaemonForUpdateAsync())
                {
                    return;
                }

                try
                {
                    PerformAppTeardown();
                }
                catch (Exception ex)
                {
                    // Outside the apply try before (PR #489 follow-up): a throw here escaped this
                    // fire-and-forget method as an unobserved task fault. Nothing is applied; the
                    // window stays up and its own close must run the teardown again.
                    TerminalLogger.Log("Tearing down before the update failed: " + ex);
                    _teardownDone = false;
                    ShowRecordingToast(
                        "Update could not be applied",
                        "The update was downloaded but could not be applied. Close Ntilde and start it again to finish updating.",
                        null,
                        null,
                        autoHide: false);
                    return;
                }

                try
                {
                    _updateCoordinator.ApplyStagedUpdate();
                }
                catch (Exception ex)
                {
                    // Teardown already ran by this point, so the window is still up but the session
                    // has been saved and the agent host and global hotkey are stopped - the app is
                    // degraded, not healthy. The message has to say "restart manually" rather than
                    // "try again", because carrying on in this state is not a supported outcome.
                    TerminalLogger.Log("Applying the staged update failed: " + ex);
                    // The window stays up and the user is told to close it: that close must run the
                    // teardown again (above all SaveSession), not hit PerformAppTeardown's one-shot guard.
                    _teardownDone = false;
                    ShowRecordingToast(
                        "Update could not be applied",
                        "The update was downloaded but could not be applied. Close Ntilde and start it again to finish updating.",
                        null,
                        null,
                        autoHide: false);
                }
            }
            finally
            {
                _applyStagedUpdateInProgress = false;
            }
        }

        /// <summary>
        /// The mux half of <see cref="ApplyStagedUpdateAsync"/> (spec §9): probes for a live daemon
        /// and, when there is one, confirms the loss of its running sessions and shuts it down.
        /// False means the user declined (or could not be asked): leave everything untouched.
        /// </summary>
        private async System.Threading.Tasks.Task<bool> PrepareMuxDaemonForUpdateAsync()
        {
            Ntilde.Mux.MuxClient? daemon = await ProbeMuxDaemonForUpdateAsync();
            if (daemon is null)
            {
                return true;
            }

            using (daemon)
            {
                if (!await ConfirmMuxSessionLossForUpdateAsync(daemon))
                {
                    return false;
                }

                await ShutdownMuxDaemonForUpdateAsync(daemon);
                return true;
            }
        }

        private async System.Threading.Tasks.Task<Ntilde.Mux.MuxClient?> ProbeMuxDaemonForUpdateAsync()
        {
            try
            {
                return await MuxProbeForUpdate(CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Best-effort: a daemon that cannot even be reached is not one the update
                // needs to wait on (there is no client to send `shutdown` to either).
                AppLogger.Log($"[MainWindow] mux probe before update failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>True to go ahead: no running sessions, an unknown count, or the user confirmed.</summary>
        private async System.Threading.Tasks.Task<bool> ConfirmMuxSessionLossForUpdateAsync(Ntilde.Mux.MuxClient daemon)
        {
            IReadOnlyList<Ntilde.Mux.Contracts.SessionSummary> sessions;
            try
            {
                sessions = await daemon.ListSessionsAsync();
            }
            catch (Exception ex)
            {
                // The session count is now unknown, so there is nothing to confirm -
                // but the daemon answered the probe, so it is still there to shut down.
                AppLogger.Log($"[MainWindow] mux list-sessions before update failed: {ex.Message}");
                return true;
            }

            int running = sessions.Count(s => s.Running);
            if (running == 0)
            {
                return true;
            }

            try
            {
                return await ConfirmSessionLossForUpdate(
                    $"{running} multiplexed session{(running == 1 ? "" : "s")} will be closed by the update.");
            }
            catch (Exception ex)
            {
                // Treated as a decline: a confirmation dialog that cannot even
                // ask the question must not be read as "yes, close the sessions"
                // - the safer failure is leaving the daemon and the update alone.
                AppLogger.Log($"[MainWindow] mux update confirmation failed: {ex.Message}");
                ShowRecordingToast(
                    "Update not applied",
                    "Could not confirm closing the multiplexed sessions; try again.",
                    null,
                    null,
                    autoHide: false);
                return false;
            }
        }

        /// <summary>
        /// Probes for the local daemon and, when one is up, sends it <c>shutdown</c> and waits up to
        /// <paramref name="wait"/> for it to exit (the update path's probe-and-shutdown, shared with Task 17's quit).
        /// Never throws: a daemon that cannot be reached or does not stop is logged, and the caller carries on.
        /// </summary>
        private async System.Threading.Tasks.Task ShutdownLocalDaemonAsync(TimeSpan wait)
        {
            using Ntilde.Mux.MuxClient? daemon = await ProbeMuxDaemonForUpdateAsync();
            if (daemon is null) return;
            await ShutdownMuxDaemonForUpdateAsync(daemon, wait);
        }

        private System.Threading.Tasks.Task ShutdownMuxDaemonForUpdateAsync(Ntilde.Mux.MuxClient daemon) =>
            ShutdownMuxDaemonForUpdateAsync(daemon, System.Threading.Timeout.InfiniteTimeSpan);

        /// <param name="daemon">A connection to the daemon.</param>
        /// <param name="wait">How long to wait for the exit on top of the exit seam's own limit; infinite adds none.</param>
        private async System.Threading.Tasks.Task ShutdownMuxDaemonForUpdateAsync(Ntilde.Mux.MuxClient daemon, TimeSpan wait)
        {
            // Read before the shutdown: the daemon deletes its descriptor on the way out,
            // and the pid in it is what says when the process is really gone.
            Ntilde.Mux.Contracts.MuxEndpointDescriptor? before = null;
            try { before = MuxReadDescriptorForUpdate(); }
            catch (Exception ex) { AppLogger.Log($"[MainWindow] reading the mux descriptor before update failed: {ex.Message}"); }

            try
            {
                await daemon.ShutdownServerAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[MainWindow] mux shutdown before update failed: {ex.Message}");
                return;
            }

            // As kill-server does: the apply replaces the executable the daemon runs from,
            // so let it finish exiting first. Awaited, never blocking the UI thread.
            if (before is null)
            {
                return;
            }

            bool gone = false;
            try
            {
                Task<bool> exit = MuxWaitForDaemonExitForUpdate(before);
                if (wait != System.Threading.Timeout.InfiniteTimeSpan && await Task.WhenAny(exit, Task.Delay(wait)) != exit)
                {
                    gone = false;
                }
                else
                {
                    gone = await exit;
                }
            }
            catch (Exception ex) { AppLogger.Log($"[MainWindow] waiting for the mux daemon to exit failed: {ex.Message}"); }
            if (!gone) AppLogger.Log($"[MainWindow] the mux daemon (pid {before.Pid}) did not exit within 5 s; applying the update anyway");
        }

        /// <summary>
        /// The palette's "Check for updates". Unlike the background check this one always says
        /// what happened: the user asked a direct question and silence would read as a hang.
        /// Constructs the coordinator on demand (see <see cref="EnsureUpdateCoordinator"/>) so
        /// this works even in the first 10 seconds of the process's life, before the deferred
        /// startup timer would otherwise have built it. Reporting goes through
        /// <see cref="Ntilde.Update.IUpdateCheckFeedback"/> so the About window can run
        /// this same pipeline and render the answer inline; without one, the answers surface
        /// as toasts (see <see cref="ToastUpdateCheckFeedback"/>).
        /// </summary>
        private async System.Threading.Tasks.Task CheckForUpdatesInteractiveAsync(Ntilde.Update.IUpdateCheckFeedback? feedback = null)
        {
            feedback ??= new ToastUpdateCheckFeedback(this);

            EnsureUpdateCoordinator();
            if (_updateCoordinator == null)
            {
                // Construction itself failed unexpectedly (see EnsureUpdateCoordinator) - distinct
                // from Unsupported below, which is a healthy check reporting a non-Velopack host.
                feedback.CoordinatorUnavailable();
                return;
            }

            if (_updateCheckInFlight)
            {
                // A check (automatic, or an earlier manual one) is already running; let it finish
                // rather than racing a second download into the same staging directory. Say so,
                // though: returning silently here contradicts this method's whole contract, and
                // the startup check runs 10 s in, so a user who opens the palette promptly is
                // exactly who hits this.
                //
                // The wording is deliberately narrower than "the result will appear". If the
                // in-flight check is the AUTOMATIC one, it reports nothing unless it finds an
                // update - UpToDate and Failed are swallowed by design, because a background
                // check must not interrupt anyone. Promising a result we would not deliver is
                // worse than promising less. Sharing the in-flight task so a manual request
                // could adopt its outcome is the fuller answer, but it is a lot of machinery
                // for a ~10 s window, and it would make a background check's failure suddenly
                // user-visible depending on timing. (Codex P2 on #340.)
                feedback.AlreadyRunning();
                return;
            }

            _updateCheckInFlight = true;
            Ntilde.Update.UpdateCheckOutcome outcome;
            try
            {
                // Task.Run for the same reason as RunAutomaticCheckSafeAsync above: Velopack's
                // check does synchronous file I/O (staged-user-id read/write, nuspec parse of
                // every local .nupkg) before its first await, and this runs from a palette
                // click on the UI thread. The continuation resumes on the UI thread, which the
                // feedback surfaces (toasts, or the About window's status area) require.
                outcome = await Task.Run(() => _updateCoordinator.RunManualCheckAsync());
            }
            finally
            {
                _updateCheckInFlight = false;
            }

            feedback.Outcome(outcome, _updateCoordinator.StagedVersion);
        }

        /// <summary>
        /// The palette's surface: the same answers every check gives, delivered as toasts. The
        /// strings come from <see cref="Ntilde.Update.UpdateCheckMessages"/> so this and
        /// the About window's inline rendering cannot drift apart.
        /// </summary>
        private sealed class ToastUpdateCheckFeedback : Ntilde.Update.IUpdateCheckFeedback
        {
            private readonly MainWindow _owner;

            public ToastUpdateCheckFeedback(MainWindow owner)
            {
                _owner = owner;
            }

            // A toast IS the announcement; there is no interim state to show.
            public void Checking()
            {
            }

            public void AlreadyRunning()
            {
                _owner.ShowRecordingToast(
                    Ntilde.Update.UpdateCheckMessages.AlreadyRunningTitle,
                    Ntilde.Update.UpdateCheckMessages.AlreadyRunningMessage,
                    null,
                    null,
                    autoHide: true);
            }

            public void CoordinatorUnavailable()
            {
                _owner.ShowRecordingToast(
                    Ntilde.Update.UpdateCheckMessages.CoordinatorUnavailableTitle,
                    Ntilde.Update.UpdateCheckMessages.CoordinatorUnavailableMessage,
                    null,
                    null,
                    autoHide: true);
            }

            public void Outcome(Ntilde.Update.UpdateCheckOutcome outcome, string? stagedVersion)
            {
                switch (outcome)
                {
                    case Ntilde.Update.UpdateCheckOutcome.UpdateReady:
                        // Show it here rather than relying on the coordinator's onUpdateReady
                        // callback. That callback fires only when the staged version CHANGES (its
                        // announce-once guard, which exists so a second check does not re-nag about
                        // an update the user already dismissed). So for a user who dismissed the
                        // toast and then asked again, the callback correctly stays silent - and
                        // trusting it here left the manual check answering a direct question with
                        // nothing at all, and no visible way to restart. Re-showing is idempotent:
                        // when the callback did just fire, this sets the same text again.
                        // (Codex P2 on #340.)
                        _owner.ShowUpdateToast(stagedVersion ?? string.Empty);
                        break;
                    case Ntilde.Update.UpdateCheckOutcome.UpToDate:
                    case Ntilde.Update.UpdateCheckOutcome.Unsupported:
                    case Ntilde.Update.UpdateCheckOutcome.Failed:
                        _owner.ShowRecordingToast(
                            Ntilde.Update.UpdateCheckMessages.OutcomeTitle(outcome),
                            Ntilde.Update.UpdateCheckMessages.OutcomeMessage(outcome, stagedVersion),
                            null,
                            null,
                            autoHide: true);
                        break;
                    case Ntilde.Update.UpdateCheckOutcome.Disabled:
                        // Unreachable: a manual check ignores the automatic-checks setting.
                        break;
                }
            }
        }

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

            ApplyThemeToDialogWindow(about);
            await about.ShowDialog(this);
        }

        private void SyncRecordingButtonState()
        {
            UpdateRecordButtonUi(_currentPane?.IsRecording ?? false);
        }

        private void UpdateRecordButtonUi(bool isRecording)
        {
            var btnRecord = FindTitleBarButton("toggle_recording");
            var iconRecord = btnRecord?.Content as PathIcon;

            // Absent whenever Record is hidden, or overflowed and not currently active — both
            // legitimate configurations, so this is a quiet no-op rather than a failure.
            if (btnRecord == null || iconRecord == null)
            {
                return;
            }

            var activeBrush = new SolidColorBrush(Color.Parse("#F1636B"));
            var inactiveBrush = new SolidColorBrush(_settings.ActiveTheme.GetContrastForeground().ToAvaloniaColor());

            btnRecord.Foreground = isRecording ? activeBrush : inactiveBrush;
            iconRecord.Foreground = isRecording ? activeBrush : inactiveBrush;
            btnRecord.Background = isRecording ? new SolidColorBrush(Color.Parse("#30F1636B")) : Brushes.Transparent;
            var recordingShortcut = GetEffectiveShortcutBinding("toggle_recording", "Ctrl+Shift+R");
            ToolTip.SetTip(btnRecord, isRecording
                ? $"Stop Recording ({recordingShortcut})"
                : $"Record Session ({recordingShortcut})");
        }

        /// <summary>
        /// Catalog id to action. Deliberately not sourced from CommandRegistry: SetupCommandPalette()
        /// is lazy — it runs on palette-open and settings-save, never at startup (see the comment
        /// near line 2207) — so a title bar reading the registry would come up dead on a cold start.
        /// </summary>
        private IReadOnlyDictionary<string, Action> BuildTitleBarHandlers()
        {
            return new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase)
            {
                ["new_tab"] = () => AddTab(),
                [TitleBarCatalog.OpenTabListId] = () => PopulateTabListMenu(showFlyout: true),
                ["connections"] = () => ToggleConnections(),
                ["settings"] = () => _ = OpenSettings(0),
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
            // reapplied against the new instance.
            // Same for every other generated button and icon: they are new instances, so the
            // theme foregrounds applied by the last ApplyThemeToUI are gone (and startup applies
            // that BEFORE this rebuild ever runs). Re-apply here, before SyncRecordingButtonState
            // so an active recording's red still wins on the record button.
            ApplyTitleBarForegrounds();
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
