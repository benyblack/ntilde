using Ntilde.Shell;
using Ntilde.Shell.Theming;
using Avalonia.Styling;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia;
using Ntilde.VT;
using System.Collections.ObjectModel;
using Avalonia.Automation;
using System.Reflection;
using Ntilde.CommandAssist.Application;
using Ntilde.CommandAssist.ViewModels;
using Ntilde.CommandAssist.ShellIntegration.Runtime;
using Ntilde.Platform.Ssh.Launch;
using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Pty;
using Ntilde.Mux;

namespace Ntilde.Controls
{
    public enum PaneAction
    {
        SplitVertical,
        SplitHorizontal,
        Equalize,
        ToggleZoom,
        ToggleBroadcast,
        Close
    }

    public enum RecordingNotificationKind
    {
        Started,
        Stopped,
        Failed
    }

    public sealed class RecordingNotificationEventArgs : EventArgs
    {
        public required RecordingNotificationKind Kind { get; init; }
        public required bool IsRecording { get; init; }
        public required string RecordingsDirectory { get; init; }
        public string? FilePath { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public readonly record struct SidebarTransferRequest(
        TransferDirection Direction,
        TransferKind Kind,
        string RemotePath);

    public partial class TerminalPane : UserControl, IDisposable
    {
        public ITerminalSession? Session { get; private set; }
        public TerminalBuffer? Buffer { get; private set; }
        public AnsiParser? Parser { get; private set; }
        public string ShellCommand { get; private set; } = string.Empty;
        public string ShellArgs { get; private set; } = string.Empty;
        public TerminalProfile? Profile { get; private set; }
        private Guid _paneId = Guid.NewGuid();

        /// <remarks>
        /// Session restore assigns a persisted id after construction
        /// (SessionManager.RestorePaneTree), i.e. after this pane already
        /// registered with the agent-session registry — so the setter re-keys
        /// the registry entry to keep it addressable under the current id.
        /// If re-keying fails (the entry stayed under the old id), the pane
        /// keeps the old id too: pane and registry must never disagree.
        /// </remarks>
        public Guid PaneId
        {
            get => _paneId;
            set
            {
                if (_paneId == value) return;
                var oldId = _paneId;
                if ((_agentRegistry ?? Ntilde.AgentHost.AgentSessionRegistry.Instance).Rekey(oldId, value))
                {
                    _paneId = value;
                }
            }
        }

        public event Action<TerminalPane, SidebarTransferRequest>? RequestRemoteFilesSidebarTransfer;
        public event Action<bool>? RecordingStateChanged;
        public event Action<RecordingNotificationEventArgs>? RecordingNotification;
        public event Action<TerminalPane, string>? WorkingDirectoryChanged;
        public event Action<TerminalPane, string>? TitleChanged;
        public event Action<TerminalPane, PaneAction>? PaneActionRequested;
        public event Action<TerminalPane>? OutputReceived;
        public event Action<TerminalPane>? BellReceived;
        public event Action<TerminalPane>? CommandStarted;
        public event Action<TerminalPane, int?>? CommandFinished;
        public event Action<TerminalPane, int>? ProcessExited;
        /// <summary>A command ran at least <see cref="LongCommandNotificationPolicy.ThresholdSeconds"/>: (pane, command, exitCode, duration). Policy (setting, focus) is the window's call.</summary>
        public event Action<TerminalPane, string?, int?, TimeSpan>? LongCommandCompleted;
        /// <summary>OSC 9 desktop-notification text arrived (issue #271). Whether to show it
        /// (settings gate, focus policy) and how is the window's call — same split as
        /// <see cref="LongCommandCompleted"/>.</summary>
        public event Action<TerminalPane, string>? OscNotificationReceived;
        /// <summary>An OSC 9;4 progress state arrived (issue #271); <see langword="null"/>
        /// withdraws the pane's indication. Where it renders (tab header, taskbar) is the
        /// window's call.</summary>
        public event Action<TerminalPane, TerminalProgressReport?>? ProgressReported;

        private TerminalSettings? _settings;
        private bool _isUpdatingScroll = false;
        private bool _disposed;

        /// <summary>
        /// 1 while an output-driven UI refresh is already sitting in the dispatcher queue.
        /// </summary>
        /// <remarks>
        /// Written from the PTY/SSH read thread and cleared on the UI thread, hence the
        /// interlocked access. See <see cref="QueueOutputUiRefresh"/> for what it buys.
        /// </remarks>
        private int _outputUiRefreshQueued;
        private Ntilde.AgentHost.AgentSessionRegistration? _agentRegistration;
        // The registry this pane registered with, captured at SetupCommon so
        // Rekey and Unregister always target the same registry Register did —
        // even when a test redirected AgentSessionRegistry.Instance for the
        // construction (#357). Pane and registry must never disagree.
        private Ntilde.AgentHost.AgentSessionRegistry? _agentRegistry;
        private bool _agentActable;
        private DateTimeOffset? _lastCommandStartedAtUtc;
        private Action<int, int>? _onTermViewResize;
        private Action<float, float>? _onTermViewMetricsChanged;
        private Action<float, float>? _onTermViewMetricsLayout;
        private DispatcherTimer? _statusTimer;
        private bool _hasUserInteraction;
        private readonly SshDiagnosticsLevel _sshDiagnosticsLevel;
        private string? _pendingPasteFilePath;
        private string? _pendingEscapedPath;
        private CommandAssistController? _commandAssistController;
        private CommandAssistServices? _commandAssistServices;

        /// <summary>
        /// The in-surface Command Assist chords, resolved from the shortcut catalogue plus the user's
        /// overrides. Replaced whenever settings are applied; defaults until then.
        /// </summary>
        private AssistKeyBindings _commandAssistKeyBindings = AssistKeyBindings.Default;

        private ShellLifecycleTracker? _shellLifecycleTracker;

        /// <summary>
        /// True when <em>we</em> injected a bootstrap into this shell. Never true for SSH: the
        /// injection mechanisms (an <c>--rcfile</c> path, a <c>ZDOTDIR</c>/<c>XDG_CONFIG_HOME</c>
        /// override, a <c>-File</c> argument) all die at the SSH boundary.
        /// </summary>
        private bool _isShellIntegrationActive;

        /// <summary>
        /// True once this session has emitted any OSC 133 mark, whoever installed the thing that
        /// emits it. The runtime half of V2 Phase 2b's remote story: a remote host that sources the
        /// shipped snippet proves itself here rather than through
        /// <see cref="_isShellIntegrationActive"/>, which it can never set.
        /// </summary>
        /// <remarks>
        /// Written from the PTY read thread (the parser callbacks) and read from the UI thread
        /// (<see cref="UpdateCommandAssistContext"/>); <c>volatile</c> and bool-sized, so a reader
        /// sees either the old value or the new one. Latching - it is only ever set - so the
        /// double-set a concurrent first mark could produce is harmless, and the redundant context
        /// update it posts is idempotent.
        /// </remarks>
        private volatile bool _hasObservedShellIntegrationMark;
        private IReadOnlyDictionary<string, string>? _shellIntegrationEnvOverrides;
        private readonly OrderedAsyncEventDispatcher _shellIntegrationEventDispatcher = new();
        private readonly CommandAssistAnchorCalculator _commandAssistAnchorCalculator = new();

        /// <summary>
        /// Newest <c>OSC 133;B</c> mark (prompt end), written from the PTY read thread and read
        /// from the UI thread. <see langword="null"/> when there is no live mark.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Stored on the buffer, not in a field here.</b> A width-changing resize reflows the
        /// buffer, which rebuilds the absolute-row coordinate space and bumps
        /// <c>ScrollbackPages.Generation</c>; a mark held outside the buffer can only notice that
        /// its epoch went stale and be discarded. That is the whole of the "the first prompt of a
        /// session is dead" bug: PSReadLine repaints the input line on a resize but does not re-run
        /// the prompt function, so no fresh <c>B</c> arrives and the pane is markless until the
        /// user submits something. <see cref="TerminalBuffer.CommandStartMark"/> is re-anchored by
        /// the reflow itself, which is the only code that knows where the marked cell moved to, and
        /// it comes back carrying the new generation - so the generation contract is enforced
        /// exactly as before and there is simply no stale epoch left to reject.
        /// </para>
        /// <para>
        /// The buffer guards the slot with its own lock, which is what the pane's old
        /// <c>_commandStartMarkGate</c> was for: a <see cref="ShellIntegrationMark"/> is five
        /// fields wide and a plain nullable field could be torn across the two threads.
        /// </para>
        /// </remarks>
        private ShellIntegrationMark? LatestCommandStartMark
        {
            get => Buffer?.CommandStartMark;
            set
            {
                if (Buffer is { } buffer)
                {
                    buffer.CommandStartMark = value;
                }
            }
        }

        // Where the running command's output region begins (OSC 133;C) is
        // TerminalBuffer.CommandOutputStartMark, on the buffer for the same reason as
        // LatestCommandStartMark above: a resize between C and D would otherwise cost the failing
        // command's captured output. The two call sites (CaptureCommandOutputRegionStart and
        // TryCaptureFailureOutputTail) already hold a non-null buffer, so they read it directly.

        // The last output tail captured at OSC 133;D, already capped and already redacted. A test
        // seam only: the value the controller sees is passed as an argument, not read from here.
        private string? _lastFailureOutputTailForTest;
        private string? _lastRelevantCommandText;
        private CommandAssistBarViewModel? _boundCommandAssistViewModel;
        private readonly CommandAssistBubbleViewModel _hiddenCommandAssistBubbleViewModel = new() { IsVisible = false };
        private readonly CommandAssistPopupViewModel _hiddenCommandAssistPopupViewModel = new(new ObservableCollection<CommandAssistSuggestionItemViewModel>()) { IsVisible = false };
        private string? _currentRecordingFilePath;
        private int _clipboardWriteAttemptsForTest;
        internal CommandAssistBarViewModel? CommandAssistViewModel => _commandAssistController?.ViewModel;

        /// <summary>
        /// The Command Assist dependency graph this pane uses. Assigned by <c>MainWindow.WirePane</c>,
        /// from the single instance built at the App composition root.
        /// </summary>
        /// <remarks>
        /// A property rather than a constructor parameter because this control has four public
        /// constructors plus two internal settings-carrying overloads, and panes are built from
        /// three different places (new tab, split, session restore); property injection at the one
        /// wiring funnel keeps all of them working without threading the graph through every
        /// signature. (It is not about XAML: <c>TerminalPane.axaml</c> only declares
        /// <c>x:Class</c> - no markup anywhere instantiates the type.) Not defaulted: a pane that
        /// reaches Command Assist initialization without one throws (see
        /// <c>RequireCommandAssistServices</c>) instead of quietly building a second graph, which is
        /// exactly the failure mode the removed static locator made invisible.
        /// </remarks>
        internal CommandAssistServices? CommandAssistServices
        {
            get => _commandAssistServices;
            set => _commandAssistServices = value;
        }

        private ITerminalSessionFactory? _sessionFactory;

        /// <summary>
        /// Opens this pane's terminal session. Assigned by <c>MainWindow.WirePane</c> from the
        /// instance in <c>AppServiceBundle</c>; defaults to
        /// <see cref="Ntilde.Shell.DefaultTerminalSessionFactory.Instance"/>.
        /// </summary>
        /// <remarks>
        /// Defaulted rather than throwing-when-unset, which is where this differs from
        /// <see cref="CommandAssistServices"/>. That one throws because building a second graph
        /// is silently wrong; here there is exactly one right production implementation, and the
        /// pane has four public constructors reached from three creation sites plus session
        /// restore, so a default keeps every one of them spawning without a wiring edit.
        /// </remarks>
        internal ITerminalSessionFactory SessionFactory
        {
            get => _sessionFactory ??= Ntilde.Shell.DefaultTerminalSessionFactory.Instance;
            set => _sessionFactory = value;
        }

        public bool IsRecording => Session?.IsRecording ?? false;
        public string? CurrentWorkingDirectory { get; private set; }
        public string? CurrentOscTitle { get; private set; }
        public int? LastExitCode { get; private set; }
        public bool IsProcessRunning => Session?.IsProcessRunning ?? false;
        public bool HasActiveChildProcesses => Session?.HasActiveChildProcesses ?? false;
        public bool HasUserInteraction => _hasUserInteraction;

        private bool _isActivePane = false;
        public bool IsActivePane
        {
            get => _isActivePane;
            set
            {
                if (_isActivePane != value)
                {
                    _isActivePane = value;
                    UpdateFocusVisuals(IsKeyboardFocusWithin);
                    UpdateAgentSessionSnapshot();
                }
            }
        }

        public string GetBaseTabTitle()
        {
            if (!string.IsNullOrWhiteSpace(CurrentOscTitle))
            {
                return CurrentOscTitle!;
            }

            string profileName = Profile?.Name ?? "Terminal";
            if (!string.IsNullOrWhiteSpace(CurrentWorkingDirectory))
            {
                string normalized = CurrentWorkingDirectory!.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                string leaf = System.IO.Path.GetFileName(normalized);
                if (!string.IsNullOrWhiteSpace(leaf))
                {
                    return $"{profileName} \u00B7 {leaf}";
                }
            }

            return profileName;
        }

        public void ToggleRecording()
        {
            if (Session == null) return;

            string recordingsDirectory = AppPaths.RecordingsDirectory;

            if (Session.IsRecording)
            {
                Session.StopRecording();
                RecordingNotification?.Invoke(new RecordingNotificationEventArgs
                {
                    Kind = RecordingNotificationKind.Stopped,
                    IsRecording = Session.IsRecording,
                    FilePath = _currentRecordingFilePath,
                    RecordingsDirectory = recordingsDirectory
                });
                _currentRecordingFilePath = null;
            }
            else
            {
                try
                {
                    if (!System.IO.Directory.Exists(recordingsDirectory))
                    {
                        System.IO.Directory.CreateDirectory(recordingsDirectory);
                    }

                    string filename = BuildRecordingFileName(DateTime.Now, Guid.NewGuid().ToString("N"));
                    string path = System.IO.Path.Combine(recordingsDirectory, filename);

                    Session.StartRecording(path);
                    _currentRecordingFilePath = path;
                    RecordingNotification?.Invoke(new RecordingNotificationEventArgs
                    {
                        Kind = RecordingNotificationKind.Started,
                        IsRecording = Session.IsRecording,
                        FilePath = path,
                        RecordingsDirectory = recordingsDirectory
                    });
                }
                catch (Exception ex)
                {
                    _currentRecordingFilePath = null;
                    RecordingNotification?.Invoke(new RecordingNotificationEventArgs
                    {
                        Kind = RecordingNotificationKind.Failed,
                        IsRecording = Session.IsRecording,
                        FilePath = _currentRecordingFilePath,
                        RecordingsDirectory = recordingsDirectory,
                        ErrorMessage = ex.Message
                    });
                }
            }

            RecordingStateChanged?.Invoke(IsRecording);
        }

        internal static string BuildRecordingFileName(DateTime timestamp, string uniqueSuffix)
        {
            string normalizedSuffix = string.IsNullOrWhiteSpace(uniqueSuffix)
                ? Guid.NewGuid().ToString("N")
                : uniqueSuffix.Trim().ToLowerInvariant();

            string shortSuffix = normalizedSuffix.Length > 6
                ? normalizedSuffix[..6]
                : normalizedSuffix.PadRight(6, '0');

            return $"ntilde_rec_{timestamp:yyyyMMdd_HHmmss}_{shortSuffix}.rec";
        }

        public void UpdateProfile(TerminalProfile profile)
        {
            Profile = profile;
            UpdateAgentSessionSnapshot();
            TermView.ShellOverride = profile.ShellOverride;
            UpdateCommandAssistContext();
            UpdateRemoteFilesSidebarHostIdentity();
            if (!IsRemoteFilesSidebarSupported())
            {
                CloseRemoteFilesSidebar();
            }

            UpdateRemoteFilesSidebarCurrentDirectoryState();
            UpdateRemoteFilesSidebarEntryPointState();
        }

        public Control ActiveControl => TermView;
        public ISshInteractionHandler? SshInteractionHandler { get; set; }

        public TerminalPane()
        {
            InitializeComponent();
            StartupPerformanceTracker.Current?.TryMarkCheckpoint("TerminalPane.Ctor.AfterInitializeComponent");
            _sshDiagnosticsLevel = SshDiagnosticsLevel.None;
            Buffer = new TerminalBuffer(80, 24);
            TermView.SetBuffer(Buffer);
            TermView.Ready += (c, r) => InitializeSession(null, null, c, r);
            SetupCommon(null);
            StartupPerformanceTracker.Current?.TryMarkCheckpoint("TerminalPane.Ctor.AfterSetupCommon");
        }

        public TerminalPane(string shell)
            : this(shell, initialSettings: null)
        {
        }

        internal TerminalPane(string shell, TerminalSettings? initialSettings)
        {
            InitializeComponent();
            StartupPerformanceTracker.Current?.TryMarkCheckpoint("TerminalPane.Ctor.AfterInitializeComponent");
            _sshDiagnosticsLevel = SshDiagnosticsLevel.None;
            Buffer = new TerminalBuffer(80, 24);
            TermView.SetBuffer(Buffer);
            // Record the requested command NOW, not only once the session starts. Until
            // InitializeSessionCore ran, the shell lived solely in this lambda's closure,
            // while ShellCommand — the property the OnAttachedToVisualTree fallback,
            // Reconnect() and session persistence all read — was still string.Empty. Any of
            // those reaching the pane first therefore lost the requested shell entirely.
            ShellCommand = shell ?? string.Empty;
            TermView.Ready += (c, r) => InitializeSession(shell, null, c, r);
            SetupCommon(initialSettings);
            StartupPerformanceTracker.Current?.TryMarkCheckpoint("TerminalPane.Ctor.AfterSetupCommon");
        }

        public TerminalPane(string shell, string args)
            : this(shell, args, initialSettings: null)
        {
        }

        internal TerminalPane(string shell, string args, TerminalSettings? initialSettings)
        {
            InitializeComponent();
            StartupPerformanceTracker.Current?.TryMarkCheckpoint("TerminalPane.Ctor.AfterInitializeComponent");
            _sshDiagnosticsLevel = SshDiagnosticsLevel.None;
            Buffer = new TerminalBuffer(80, 24);
            TermView.SetBuffer(Buffer);
            // See the single-argument overload: the requested command must be observable
            // before the session starts, or the attach-time fallback spawns an empty one.
            ShellCommand = shell ?? string.Empty;
            ShellArgs = args ?? string.Empty;
            TermView.Ready += (c, r) => InitializeSession(shell, null, c, r, args);
            SetupCommon(initialSettings);
            StartupPerformanceTracker.Current?.TryMarkCheckpoint("TerminalPane.Ctor.AfterSetupCommon");
        }

        public TerminalPane(TerminalProfile profile)
            : this(profile, initialSettings: null, SshDiagnosticsLevel.None, useInitialSettings: false)
        {
        }

        public TerminalPane(TerminalProfile profile, SshDiagnosticsLevel sshDiagnosticsLevel)
            : this(profile, initialSettings: null, sshDiagnosticsLevel, useInitialSettings: false)
        {
        }

        internal TerminalPane(TerminalProfile profile, TerminalSettings initialSettings, SshDiagnosticsLevel sshDiagnosticsLevel = SshDiagnosticsLevel.None)
            : this(profile, initialSettings, sshDiagnosticsLevel, useInitialSettings: true)
        {
        }

        private TerminalPane(TerminalProfile profile, TerminalSettings? initialSettings, SshDiagnosticsLevel sshDiagnosticsLevel, bool useInitialSettings)
        {
            Profile = profile;
            InitializeComponent();
            StartupPerformanceTracker.Current?.TryMarkCheckpoint("TerminalPane.Ctor.AfterInitializeComponent");
            _sshDiagnosticsLevel = sshDiagnosticsLevel;
            Buffer = new TerminalBuffer(80, 24);
            TermView.SetBuffer(Buffer);
            TermView.ShellOverride = profile.ShellOverride;
            // As above: keep the profile's command visible to the attach-time fallback,
            // Reconnect() and persistence before the first session exists.
            ShellCommand = profile.Command ?? string.Empty;
            ShellArgs = profile.Arguments ?? string.Empty;
            TermView.Ready += (c, r) => InitializeSession(profile.Command, profile, c, r);
            SetupCommon(useInitialSettings ? initialSettings : null);
            StartupPerformanceTracker.Current?.TryMarkCheckpoint("TerminalPane.Ctor.AfterSetupCommon");
        }

        private void SetupCommon(TerminalSettings? initialSettings)
        {
            // Agent-host observe surface (docs/agent-host/DIRECTION.md, A1):
            // inert bookkeeping until the IPC endpoint queries it. The
            // registration holds a lock-protected metadata snapshot (never a
            // live delegate into this control), pushed from the UI thread on
            // every relevant change; the entry is removed in DetachFromUiThread.
            _agentRegistration = new Ntilde.AgentHost.AgentSessionRegistration(
                PaneId,
                Buffer!,
                GetBaseTabTitle(),
                Profile?.Name ?? "Terminal",
                Profile?.Type == ConnectionType.SSH ? "ssh" : "local",
                IsActivePane,
                profileId: Profile?.Id);
            // A3 act: an agent typing into this pane is text the keyboard path never saw.
            _agentRegistration.InputInjected = NotifyExternalInputSent;
            _agentRegistry = Ntilde.AgentHost.AgentSessionRegistry.Instance;
            _agentRegistry.Register(_agentRegistration);
            // Seed act-reachability from the registration instead of waiting for
            // the first ActabilityChanged. AgentHostService.OnSessionRegistered
            // publishes it synchronously inside Register above — i.e. one line
            // before the subscription below exists, so the event is already gone
            // by the time we would hear it. Subscribing first would not fix it
            // either: OnAgentActabilityChanged only *posts* to the dispatcher, so
            // the bar would still appear a frame after the pane's first layout
            // and resize the PTY, which is the reflow this is here to remove.
            // Reading the value is synchronous, so the pane is laid out with its
            // bar already in place and the terminal row is never re-measured.
            //
            // Routed through ApplyAgentAttention rather than assigning
            // _agentActable directly so the segment is rendered by its one
            // renderer: a seeded-actable pane shows the idle "agent access"
            // label immediately instead of a bare dot with empty text until the
            // first attention event.
            //
            // Guarded rather than unconditional: _agentActable already defaults
            // to false, so a non-actable pane has nothing to seed, and calling
            // UpdateStatusBarVisibility() for it would only re-assert the SSH
            // half of the bar's visibility OR earlier than anything expects.
            if (_agentRegistration.IsAgentActable)
            {
                ApplyAgentAttention(_agentRegistration.AttentionMachine.Snapshot(), isActable: true);
            }
            _agentRegistration.AttentionMachine.Changed += OnAgentAttentionChanged;
            _agentRegistration.ActabilityChanged += OnAgentActabilityChanged;
            TitleChanged += (_, _) => UpdateAgentSessionSnapshot();
            WorkingDirectoryChanged += (_, _) => UpdateAgentSessionSnapshot();

            // A2 status signals (docs/plans/2026-07-07-agent-host-a2-status-design.md):
            // PTY lifecycle events feed the per-session status machine. Command
            // lifecycle (started/finished) and prompt/accepted signals are wired
            // synchronously at the parser hooks in InitializeSession so their
            // relative order is preserved; alt-screen in HandleAltScreenChanged.
            OutputReceived += _ => _agentRegistration?.StatusMachine.NotifyOutput();
            BellReceived += _ => _agentRegistration?.StatusMachine.NotifyBell();
            ProcessExited += (_, exitCode) => _agentRegistration?.StatusMachine.NotifyExited(exitCode);

            TermView.KeyDownInterceptor = TryHandleCommandAssistKey;

            // Mouse support for the popup rows (V2 Phase 3a). Wired here rather than in
            // BindCommandAssistViews because the view is a fixed part of the pane's XAML tree while the
            // view-model comes and goes with the feature flag: subscribing on every bind would add a
            // second handler per rebind.
            if (CommandAssistPopup != null)
            {
                CommandAssistPopup.SuggestionPointerSelected += OnCommandAssistSuggestionPointerSelected;
                CommandAssistPopup.SuggestionPointerAccepted += OnCommandAssistSuggestionPointerAccepted;
            }

            TermView.TextInput += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Text))
                {
                    _hasUserInteraction = true;
                }
            };
            TermView.KeyDown += (_, e) =>
            {
                if (e.Key != Key.LeftShift &&
                    e.Key != Key.RightShift &&
                    e.Key != Key.LeftCtrl &&
                    e.Key != Key.RightCtrl &&
                    e.Key != Key.LeftAlt &&
                    e.Key != Key.RightAlt)
                {
                    _hasUserInteraction = true;
                }
            };

            // Wire up ScrollBar
            TermScrollBar.ValueChanged += ScrollBar_ValueChanged;

            TermView.ScrollStateChanged += (offset, max) =>
            {
                // Dispatch to UI thread to update ScrollBar value
                this.Dispatcher.Post(() =>
                {
                    _isUpdatingScroll = true;
                    try
                    {
                        TermScrollBar.Maximum = max;
                        TermScrollBar.Value = max - offset;
                    }
                    finally
                    {
                        _isUpdatingScroll = false;
                    }
                }, DispatcherPriority.Render);
            };

            // Search UI
            SetupSearch();

            // Port Forwarding Status Timer
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _statusTimer.Tick += (s, e) => UpdateForwardingStatus();
            _statusTimer.Start();

            // SFTP Status
            SftpService.Instance.JobUpdated += Sftp_JobUpdated;

            // Wire up focus syncing
            TermView.GotFocus += (s, e) => HandleTermViewGotFocus();
            TermView.LostFocus += (s, e) => UpdateFocusVisuals(false);
            // Cached so DetachFromUiThread can remove it. As an uncached lambda it was the one
            // TermView handler left attached after disposal, which contradicted the claim that a
            // disposed pane stops reacting — harmless in practice (it only re-runs this pane's own
            // layout) but it made the invariant untestable, and an untestable invariant is one edit
            // away from being false (#102).
            _onTermViewMetricsLayout = (cw, ch) =>
            {
                UpdateMinimumSizeConstraints();
                UpdateCommandAssistOverlayPlacement();
            };
            TermView.MetricsChanged += _onTermViewMetricsLayout;
            TermView.CommandAssistAnchorHintChanged += () => UpdateCommandAssistOverlayPlacement();
            SizeChanged += (_, _) => UpdateCommandAssistOverlayPlacement();
            // Keystrokes are triggers, not content, for the *query* (V2 Phase 1c): Command Assist
            // re-reads the command line out of the grid on the refresh these queue, which is the
            // only source that also knows about the arrow keys, Ctrl+U, history recall and
            // shell-side Tab completion.
            //
            // They are content for one narrow purpose, added in V2 Phase 1 task 7: the markless
            // submission accumulator, which supplies Enter-time history capture for the sessions
            // the grid cannot serve. See NotifyTypedTextObserved and friends.
            TermView.TextInputObserved += NotifyTypedTextObserved;
            TermView.BackspaceObserved += NotifyBackspaceObserved;
            TermView.EnterObserving += OnCommandAssistEnterObserving;
            TermView.EnterObserved += OnCommandAssistEnterObserved;
            TermView.PasteObserved += NotifyPasteObserved;

            if (Buffer != null)
            {
                Buffer.OnScreenSwitched += OnBufferScreenSwitched;
            }

            // Agent Output (markdown side panel). The tracker hangs off the same buffer-lifetime
            // subscription point as OnScreenSwitched above; OnInvalidate fires on the parse thread
            // and the tracker only re-arms a debounce timer there. Notification wiring into the
            // parser lives in CaptureCommandOutputRegionStart (C) and the OnCommandFinished
            // handler (D) - both already parse-thread, both already at the right instant.
            _agentOutput = new AgentOutput.AgentOutputViewModel();
            _agentOutputTracker = new AgentOutput.AgentOutputRegionTracker(
                () => Buffer,
                () => Buffer?.CommandOutputStartMark,
                dispatch: action =>
                {
                    if (this.Dispatcher.CheckAccess())
                    {
                        action();
                    }
                    else
                    {
                        this.Dispatcher.Post(action);
                    }
                },
                onUpdate: (text, streaming) => _agentOutput.SetUpdate(text, streaming),

                // The MD toggle only exists when the recent on-screen output actually looks like
                // markdown - detection runs on the tracker's lazy closed-panel cadence, and this
                // callback fires on the UI thread only when the verdict changes.
                markdownPresenceChanged: present => AgentOutputToggle.IsVisible = present);
            if (Buffer != null)
            {
                Buffer.OnInvalidate += _agentOutputTracker.NotifyInvalidate;
            }
            // Property hook rather than Click: the panel's ✕ unchecks the toggle programmatically
            // (host.CloseRequested), and that path must close the panel exactly like a user click.
            AgentOutputToggle.PropertyChanged += (_, e) =>
            {
                if (e.Property == ToggleButton.IsCheckedProperty)
                {
                    SetAgentOutputPanelOpen(AgentOutputToggle.IsChecked ?? false);
                }
            };
            TermView.EnterObserving += OnAgentOutputEnterObserved;

            // Load Settings
            ApplySettings(initialSettings ?? TerminalSettings.Load());
            UpdateMinimumSizeConstraints();
            AutomationProperties.SetName(TermView, "Terminal Pane");
            AutomationProperties.SetName(this, "Terminal Pane");

            // The pane segment is the agent surface the user actually looks at,
            // so it opens the Agent Activity journal exactly the way the
            // window-level AgentObserveIndicator does (MainWindow.axaml.cs's
            // agentObserveIndicator.Click). Null-safe on VisualRoot: the pane is
            // constructed before it is attached, and unit tests never attach it
            // to a window at all.
            //
            // async void by necessity (Click is an EventHandler), so the await
            // must not be left bare: an exception out of an async void handler
            // is re-raised on the UI thread's synchronization context as an
            // unhandled exception rather than contained, and a click that
            // should quietly do nothing would terminate the app instead - the
            // journal dialog failing to construct while the owning window is
            // closing is enough. Contained the same way the rest of this file
            // contains failures on the attention path.
            AgentStatusButton.Click += async (_, _) =>
            {
                try
                {
                    if (VisualRoot is MainWindow mainWindow)
                    {
                        await mainWindow.ShowAgentActivityJournalAsync();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[TerminalPane] Opening the agent activity journal failed: {ex.Message}");
                }
            };

            // Smart Paste Action setup
            TermView.TextFileDropped += (s, args) =>
            {
                _pendingPasteFilePath = args.FilePath;
                _pendingEscapedPath = args.EscapedPath;
                string fileName = System.IO.Path.GetFileName(args.FilePath);
                // A notice riding along with the drop (currently only the WSL mapping
                // fallback) is appended rather than shown separately, because it would
                // otherwise overwrite this actionable prompt in the shared panel.
                ToastMessageText.Text = string.IsNullOrEmpty(args.Notice)
                    ? fileName
                    : $"{fileName} \u2014 {args.Notice}";
                // Restore the action buttons: an informational drop notice (below) hides
                // them, and the panel is shared between both uses.
                ToastPastePathBtn.IsVisible = true;
                ToastActionBtn.IsVisible = true;
                ToastPanel.IsVisible = true;
            };

            // Drop refused, or accepted with a caveat. Reuses the same panel - the message
            // belongs in the pane the drop landed on, not in a window-level toast - but with
            // the action buttons hidden, because there is nothing to act on: the text was
            // either never sent, or already sent.
            TermView.DropNotice += message =>
            {
                _pendingPasteFilePath = null;
                _pendingEscapedPath = null;
                ToastMessageText.Text = message;
                ToastPastePathBtn.IsVisible = false;
                ToastActionBtn.IsVisible = false;
                ToastPanel.IsVisible = true;
            };

            ToastCloseBtn.Click += (s, e) =>
            {
                ToastPanel.IsVisible = false;
                _pendingPasteFilePath = null;
                _pendingEscapedPath = null;
            };

            ToastPastePathBtn.Click += (s, e) =>
            {
                ToastPanel.IsVisible = false;
                if (!string.IsNullOrEmpty(_pendingEscapedPath) && Session != null)
                {
                    NotifyExternalInputSent();
                    TermView.ScrollToInputLine();
                    Session.SendInput(_pendingEscapedPath);
                    _pendingPasteFilePath = null;
                    _pendingEscapedPath = null;
                }
            };

            ToastActionBtn.Click += async (s, e) =>
            {
                ToastPanel.IsVisible = false;
                if (!string.IsNullOrEmpty(_pendingPasteFilePath) && Session != null)
                {
                    try
                    {
                        string content = await System.IO.File.ReadAllTextAsync(_pendingPasteFilePath);
                        NotifyExternalInputSent();
                        TermView.ScrollToInputLine();
                        Ntilde.Platform.Input.TerminalInputSender.SendBracketedPaste(Session, content);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to paste file contents: {ex.Message}");
                    }
                    _pendingPasteFilePath = null;
                    _pendingEscapedPath = null;
                }
            };

            // SFTP Context Menu
            var contextMenu = RootGrid.ContextMenu;
            if (contextMenu != null)
            {
                contextMenu.Opening += (_, _) => UpdatePaneContextMenuState();

                var paneMenu = contextMenu.Items.OfType<MenuItem>().FirstOrDefault(m => (string?)m.Header == "Pane");
                if (paneMenu != null)
                {
                    foreach (var sub in paneMenu.Items.OfType<MenuItem>())
                    {
                        if (sub.Name == "MenuPaneSplitVertical") sub.Click += (s, e) => PaneActionRequested?.Invoke(this, PaneAction.SplitVertical);
                        if (sub.Name == "MenuPaneSplitHorizontal") sub.Click += (s, e) => PaneActionRequested?.Invoke(this, PaneAction.SplitHorizontal);
                        if (sub.Name == "MenuPaneEqualize") sub.Click += (s, e) => PaneActionRequested?.Invoke(this, PaneAction.Equalize);
                        if (sub.Name == "MenuPaneToggleZoom") sub.Click += (s, e) => PaneActionRequested?.Invoke(this, PaneAction.ToggleZoom);
                        if (sub.Name == "MenuPaneToggleBroadcast") sub.Click += (s, e) => PaneActionRequested?.Invoke(this, PaneAction.ToggleBroadcast);
                        if (sub.Name == "MenuPaneClose") sub.Click += (s, e) => PaneActionRequested?.Invoke(this, PaneAction.Close);
                    }
                }

                var explainSelectionItem = contextMenu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "MenuExplainSelection");
                if (explainSelectionItem != null)
                {
                    explainSelectionItem.Click += async (_, _) => await ExplainSelectionAsync();
                }
            }

            InitializeRemoteFilesSidebar();
        }

        private void OnBufferScreenSwitched(bool isAltScreen)
        {
            this.Dispatcher.Post(() => HandleAltScreenChanged(isAltScreen));
        }

        private void HandleAltScreenChanged(bool isAltScreen)
        {
            // A full-screen app owns the keyboard, and the keys it eats are not line edits. Drop
            // whatever half-line was pending on the way in so a TUI session cannot leave text
            // behind that a later Enter captures, and drop it again on the way out because the
            // prompt underneath was redrawn by something the accumulator never saw.
            _marklessSubmission.Reset();

            _agentRegistration?.StatusMachine.NotifyAltScreenChanged(isAltScreen);
            _commandAssistController?.HandleAltScreenChanged(isAltScreen);
            UpdateRemoteFilesSidebarVisibility();
            UpdateRemoteFilesSidebarEntryPointState();
            if (_agentOutput != null)
            {
                _agentOutput.IsAltScreenSuppressed = isAltScreen;
                UpdateAgentOutputPanelVisibility();
            }

            // Showing the panel again is not enough on the way out: the final update for a
            // command that finished under the full-screen program was refused while the
            // alternate screen was up, so the tracker owes the panel a corrective read.
            _agentOutputTracker?.NotifyAltScreenChanged(isAltScreen);
        }

        /// <summary>
        /// Queues the after-output UI work — scrollbar extent, cursor visibility, and the
        /// <see cref="OutputReceived"/> notification the window uses for tab activity — collapsing
        /// any number of output chunks into one pending dispatcher job.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Called on the PTY/SSH read thread, once per chunk read from the session. Posting
        /// unconditionally (what this replaced) makes the dispatcher queue grow with the remote's
        /// send rate rather than the UI's drain rate: a busy SSH session delivers chunks far faster
        /// than the UI thread retires them, and each one queued a closure that did the same work as
        /// the one in front of it. <see cref="UpdateScrollUI"/> then posts a second job of its own,
        /// so the cost was two dispatcher items and an async state machine per chunk.
        /// </para>
        /// <para>
        /// Every consumer here is a level, not an edge — the scrollbar reads the buffer's current
        /// extent, <c>OnPaneOutputReceived</c> stamps "output seen at", and the agent status machine
        /// records a last-output time — so the intermediate passes had nothing to contribute. What
        /// matters is that one runs after the last chunk, which this guarantees.
        /// </para>
        /// <para>
        /// The flag is cleared at the <em>start</em> of the job, not the end: a chunk that arrives
        /// while the job is running must be able to queue the next one. Clearing at the end would
        /// fold that chunk into a pass that had already read the buffer, and the newest output
        /// would sit unreflected until something else happened to refresh.
        /// </para>
        /// </remarks>
        private void QueueOutputUiRefresh()
        {
            if (Interlocked.Exchange(ref _outputUiRefreshQueued, 1) == 1)
            {
                return;
            }

            this.Dispatcher.Post(() =>
            {
                Volatile.Write(ref _outputUiRefreshQueued, 0);

                // The pane can be torn down between the post and the run.
                if (_disposed)
                {
                    return;
                }

                UpdateScrollUI();
                OutputReceived?.Invoke(this);
            });
        }

        /// <summary>
        /// Pixel size the kitty mode-2048 in-band resize report carries. Cell metrics are in DIPs;
        /// the terminal actually occupies DIPs times the view's effective render scale (monitor
        /// scale times interface scale) in device pixels. Multiplying by the DIP size alone was
        /// only right at 100% on a 96-dpi monitor. A non-positive or NaN scale means "unknown" and
        /// falls back to unscaled, never to zero.
        /// </summary>
        internal static (int Width, int Height) InBandPixelDimensions(int cols, int rows, float cellWidthDips, float cellHeightDips, double scale)
        {
            double s = double.IsNaN(scale) || scale <= 0 ? 1.0 : scale;
            return ((int)Math.Round(cols * cellWidthDips * s), (int)Math.Round(rows * cellHeightDips * s));
        }

        /// <summary>
        /// Subscribes the two <see cref="TermView"/> handlers that belong to session setup, in a way
        /// that is safe to call repeatedly.
        /// </summary>
        /// <remarks>
        /// Every other subscription made by <c>InitializeSession</c> targets an object that is
        /// recreated with the session — a fresh <see cref="AnsiParser"/> (see the assignment at the
        /// top of that method) or the new <see cref="ITerminalSession"/> — so those handler lists
        /// start empty and cannot accumulate. These two are the exception: <c>TermView</c> is the
        /// pane's own control and outlives any individual session, so <c>Reconnect()</c> would add a
        /// second copy of each on every reconnect.
        ///
        /// Hence the cached delegates plus remove-before-add: the delegate identity has to be stable
        /// for <c>-=</c> to match, which a fresh lambda per call would not give. Extracted into its
        /// own method so the idempotence can be asserted directly (#102) rather than only through a
        /// full session spin-up.
        /// </remarks>
        internal void WireReusedTermViewHandlers()
        {
            _onTermViewResize ??= (c, r) =>
            {
                if (Parser != null)
                {
                    float cwResize = TermView.Metrics.CellWidth;
                    float chResize = TermView.Metrics.CellHeight;
                    if (cwResize > 0) Parser.CellWidth = cwResize;
                    if (chResize > 0) Parser.CellHeight = chResize;
                }
                Session?.Resize(c, r);

                // Kitty in-band resize (mode 2048): a client that enabled the mode —
                // terminal-browser on Windows has no SIGWINCH and never polls console size —
                // only learns about a new geometry from this report, so it must follow the
                // PTY resize. Pixel dims come from the same metrics the grid draws with.
                if (Parser is { InBandResizeReportsEnabled: true } && !SessionAnswersDeviceQueries(Session))
                {
                    float cwReport = TermView.Metrics.CellWidth;
                    float chReport = TermView.Metrics.CellHeight;
                    if (cwReport > 0 && chReport > 0)
                    {
                        var (pxW, pxH) = InBandPixelDimensions(c, r, cwReport, chReport, TermView.EffectiveRenderScaling);
                        Parser.SendInBandResize(r, c, pxW, pxH);
                    }
                }
            };
            TermView.OnResize -= _onTermViewResize;
            TermView.OnResize += _onTermViewResize;

            _onTermViewMetricsChanged ??= (cwMetric, chMetric) =>
            {
                if (Parser != null && cwMetric > 0 && chMetric > 0)
                {
                    Parser.CellWidth = cwMetric;
                    Parser.CellHeight = chMetric;
                }

                // The daemon's parser answers size reports, so it needs the new cell pixels too.
                if (Session is MuxClientSession { IsAttached: true } mux) mux.UpdatePresentation(BuildMuxPresentation());

                // Cell geometry just changed, so the agent-host's copy of this
                // pane's render inputs is stale (A5 captureScreen).
                UpdateAgentRenderParameters();

                // Kitty in-band resize (mode 2048): a font or monitor-scaling change can alter
                // the pane's pixel geometry WITHOUT changing the integer grid, so OnResize
                // never fires and this is the only path that notices. A mode-2048 client keeps
                // rendering at the stale pixel dimensions until it is told.
                if (Parser is { InBandResizeReportsEnabled: true } && Buffer != null
                    && !SessionAnswersDeviceQueries(Session) && cwMetric > 0 && chMetric > 0)
                {
                    var (pxW, pxH) = InBandPixelDimensions(Buffer.Cols, Buffer.Rows, cwMetric, chMetric, TermView.EffectiveRenderScaling);
                    Parser.SendInBandResize(Buffer.Rows, Buffer.Cols, pxW, pxH);
                }
            };
            TermView.MetricsChanged -= _onTermViewMetricsChanged;
            TermView.MetricsChanged += _onTermViewMetricsChanged;

            // The view may already have measured its font before this subscription
            // existed, in which case the MetricsChanged that would have published
            // the render parameters has already been and gone.
            UpdateAgentRenderParameters();
        }

        private void HandleWorkingDirectoryChanged(string cwd)
        {
            CurrentWorkingDirectory = cwd;
            UpdateCommandAssistContext();
            UpdateRemoteFilesSidebarCurrentDirectoryState();
            WorkingDirectoryChanged?.Invoke(this, cwd);
        }
        /// <summary>
        /// Points <paramref name="property"/> of a code-built control at a theme token through a
        /// resource observable, not a brush read once: the pane's own scope (spec D7) can appear or
        /// go while the control lives, and only an observable binding follows that. A later call on
        /// the same property replaces the binding.
        /// </summary>
        private static void BindToken(Control target, AvaloniaProperty property, string tokenKey)
            => target.Bind(property, target.GetResourceObservable(tokenKey));

        /// <summary>
        /// The pane's own theme tokens (spec D7), present only while the profile names a theme
        /// other than the app's. Merged into the pane's resources, so every DynamicResource and
        /// resource observable inside the pane resolves it before the app's tokens.
        /// </summary>
        private ResourceDictionary? _paneThemeTokens;

        /// <summary>True while this pane carries its own theme scope (tests).</summary>
        internal bool HasPaneThemeScope => _paneThemeTokens != null;

        /// <summary>
        /// Gives the pane its own token scope when its effective theme differs from the app's, and
        /// removes it when they match again (or the profile theme is cleared), so the pane follows
        /// the app. Runs on every ApplySettings, which an app theme switch triggers for every pane,
        /// so an overriding pane is recoloured from its own theme rather than clobbered. Fluent's
        /// palette is not scoped (best effort, spec D7): stock controls follow the scope's variant.
        /// </summary>
        private void UpdatePaneThemeScope(TerminalSettings settings, TerminalSettings effectiveSettings)
        {
            bool overrides = !string.IsNullOrEmpty(Profile?.ThemeName)
                && !string.Equals(effectiveSettings.ThemeName, settings.ThemeName, StringComparison.Ordinal);

            if (!overrides)
            {
                if (_paneThemeTokens != null)
                {
                    Resources.MergedDictionaries.Remove(_paneThemeTokens);
                    _paneThemeTokens = null;
                }

                PaneVariantScope.ClearValue(ThemeVariantScope.RequestedThemeVariantProperty);
                return;
            }

            var tokens = ThemeTokens.Derive(effectiveSettings.ActiveTheme, settings.WindowOpacity);
            if (_paneThemeTokens == null)
            {
                _paneThemeTokens = new ResourceDictionary();
                Resources.MergedDictionaries.Add(_paneThemeTokens);
            }

            ThemeService.ApplyTo(_paneThemeTokens, tokens);
            PaneVariantScope.RequestedThemeVariant = tokens.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        }

        internal TerminalSettings BuildEffectiveSettings(TerminalSettings settings)
        {
            // We create a "copy" for the view to use, but we only override specific visual fields
            return new TerminalSettings
            {
                FontSize = Profile?.FontSize ?? settings.FontSize,
                FontFamily = Profile?.FontFamily ?? settings.FontFamily,
                ThemeName = Profile?.ThemeName ?? settings.ThemeName,
                CursorStyle = Profile?.CursorStyle ?? settings.CursorStyle,
                CursorBlink = Profile?.CursorBlink ?? settings.CursorBlink,

                // Inherit everything else from global
                MaxHistory = settings.MaxHistory,
                WindowOpacity = settings.WindowOpacity,
                UiScale = settings.UiScale,
                BlurEffect = settings.BlurEffect,
                BackgroundImagePath = settings.BackgroundImagePath,
                BackgroundImageOpacity = settings.BackgroundImageOpacity,
                BackgroundImageStretch = settings.BackgroundImageStretch,
                BellAudioEnabled = settings.BellAudioEnabled,
                BellVisualEnabled = settings.BellVisualEnabled,
                SmoothScrolling = settings.SmoothScrolling,
                EnableLinkDetection = settings.EnableLinkDetection,
                EnableKittyKeyboardProtocol = settings.EnableKittyKeyboardProtocol,
                AllowNativeKittyGraphics = settings.AllowNativeKittyGraphics,
                AllowOsc52ClipboardWrite = settings.AllowOsc52ClipboardWrite,
                CommandAssistEnabled = settings.CommandAssistEnabled,
                CommandAssistHistoryEnabled = settings.CommandAssistHistoryEnabled,
                CommandAssistMaxHistoryEntries = settings.CommandAssistMaxHistoryEntries,
                CommandAssistPassiveBubbleEnabled = settings.CommandAssistPassiveBubbleEnabled,
                CommandAssistShellIntegrationEnabled = settings.CommandAssistShellIntegrationEnabled,
                CommandAssistPowerShellIntegrationEnabled = settings.CommandAssistPowerShellIntegrationEnabled,
                Profiles = settings.Profiles,
                DefaultProfileId = settings.DefaultProfileId
            };
        }

        public void ApplySettings(TerminalSettings settings)
        {
            _settings = settings;

            // Merge global settings with profile overrides
            var effectiveSettings = BuildEffectiveSettings(settings);

            TermView.ApplySettings(effectiveSettings);
            UpdatePaneThemeScope(settings, effectiveSettings);
            if (_commandAssistController != null || !IsCommandAssistFeatureEnabled())
            {
                InitializeCommandAssist();
            }
            UpdateMinimumSizeConstraints();

            // Sync metrics to parser after settings change (font size, etc.)
            if (Parser != null)
            {
                float cw = TermView.Metrics.CellWidth;
                float ch = TermView.Metrics.CellHeight;
                if (cw > 0) Parser.CellWidth = cw;
                if (ch > 0) Parser.CellHeight = ch;

                // Keep OSC 10/11 (fg/bg color query) responses in sync with the active theme,
                // including profile-specific theme overrides (see effectiveSettings above).
                Parser.DefaultForeground = effectiveSettings.ActiveTheme.Foreground;
                Parser.DefaultBackground = effectiveSettings.ActiveTheme.Background;

                // Kill switch (Blocker 2, #277 review): keep the query-reply gate in sync with
                // the setting on every settings change, not just at parser creation.
                Parser.KittyKeyboardEnabled = effectiveSettings.EnableKittyKeyboardProtocol;

                // Native kitty graphics on Windows (ConPTY pass-through opt-in): same live-sync
                // reasoning as the kill switch above.
                // Never for a mux-backed parser: it must not decode what the daemon's parser does not.
                Parser.AllowNativeKittyGraphics = Session is not MuxClientSession && effectiveSettings.AllowNativeKittyGraphics;
            }

            // Theme colours, cell metrics and the kitty-keyboard gate live in the daemon's parser too.
            if (Session is MuxClientSession { IsAttached: true } attachedMux) attachedMux.UpdatePresentation(BuildMuxPresentation());

            // Font family/size and shaping toggles just moved with the settings.
            UpdateAgentRenderParameters();
        }


        private void ScrollBar_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_isUpdatingScroll || Buffer == null) return;

            // ScrollBar Top (0) -> History Top (Max Offset)
            // ScrollBar Bottom (Max) -> History Bottom (0 Offset)
            int inverted = (int)(TermScrollBar.Maximum - e.NewValue);
            TermView.ScrollOffset = inverted;
            TermView.InvalidateVisual();
        }

        private void UpdateScrollUI()
        {
            if (Buffer == null) return;

            _isUpdatingScroll = true;
            try
            {
                int total = Buffer.TotalLines;
                int view = Buffer.Rows;
                int maxScroll = Math.Max(0, total - view);

                TermScrollBar.Maximum = maxScroll;
                TermScrollBar.ViewportSize = view;

                // Current Value
                // Offset 0 (Bottom) -> Value = Max
                // Offset Max (Top) -> Value = 0
                TermScrollBar.Value = maxScroll - TermView.ScrollOffset;
            }
            finally
            {
                _isUpdatingScroll = false;
            }

            // When new output arrives, ensure the cursor is visible
            // If we just switched from alt screen (like after exiting mc), ensure we're scrolled to show the cursor
            this.Dispatcher.Post(async () =>
            {

                if (TermView.JustSwitchedFromAltScreen)
                {
                    // Small delay to ensure screen switch processing is complete
                    await Task.Delay(10);
                    // Ensure cursor is visible after screen switch
                    TermView.EnsureCursorVisible();
                    // Note: EnsureCursorVisible() handles resetting the flag internally
                }
                else
                {
                    // For normal output, ensure the cursor is visible
                    TermView.EnsureCursorVisible();
                }

            }, DispatcherPriority.Render);

            // Failsafe: Force render on output
            TermView.InvalidateVisual();
        }

        private void SetupSearch()
        {
            void OnSearchTriggered(object? s, global::Avalonia.Interactivity.RoutedEventArgs e) => PerformSearch();

            SearchBox.TextChanged += (s, e) => PerformSearch();

            // Re-run search when options change
            SearchCaseSensitive.Click += OnSearchTriggered;
            SearchRegex.Click += OnSearchTriggered;

            SearchPrev.Click += (s, e) => TermView.PrevMatch();
            SearchNext.Click += (s, e) => TermView.NextMatch();
            SearchClose.Click += (s, e) =>
            {
                SearchPanel.IsVisible = false;
                TermView.ClearSearch();
                TermView.Focus();
            };

            TermView.SearchStateChanged += (idx, total) =>
            {
                this.Dispatcher.Post(() => SearchCount.Text = $"{idx}/{total}");
            };
        }

        private void PerformSearch()
        {
            if (!string.IsNullOrEmpty(SearchBox.Text))
            {
                bool useRegex = SearchRegex.IsChecked ?? false;
                bool caseSensitive = SearchCaseSensitive.IsChecked ?? false;
                TermView.Search(SearchBox.Text, useRegex, caseSensitive);
            }
            else
            {
                TermView.ClearSearch();
            }
        }

        public void ToggleSearch()
        {
            SearchPanel.IsVisible = !SearchPanel.IsVisible;
            if (SearchPanel.IsVisible)
            {
                SearchBox.Focus();
                PerformSearch();
            }
            else
            {
                TermView.ClearSearch();
                TermView.Focus();
            }
        }

        internal bool CanExplainSelection(string? selectedTextOverride = null)
        {
            if (!IsCommandAssistFeatureEnabled())
            {
                return false;
            }

            string? selectedText = selectedTextOverride ?? TermView.GetSelectedText();
            return !string.IsNullOrWhiteSpace(selectedText);
        }

        internal async Task<bool> ExplainSelectionAsync(string? selectedTextOverride = null)
        {
            if (!EnsureCommandAssistInitialized())
            {
                return false;
            }

            string? selectedText = selectedTextOverride ?? TermView.GetSelectedText();
            if (string.IsNullOrWhiteSpace(selectedText))
            {
                return false;
            }

            return await _commandAssistController.ExplainSelectionAsync(selectedText);
        }

        public void ToggleRenderHud()
        {
            TermView.ShowRenderHud = !TermView.ShowRenderHud;
        }

        protected override void OnGotFocus(FocusChangedEventArgs e)
        {
            base.OnGotFocus(e);
            UpdateFocusVisuals(true);
            TermView.InvalidateVisual();
        }

        protected override void OnLostFocus(FocusChangedEventArgs e)
        {
            base.OnLostFocus(e);
            UpdateFocusVisuals(false);
        }

        private void UpdateFocusVisuals(bool focused)
        {
            if (InactiveOverlay != null)
            {
                bool dimEnabled = true;
                InactiveOverlay.IsVisible = dimEnabled && !IsActivePane;
            }

            if (FocusBorder != null)
            {
                FocusBorder.IsVisible = false;
            }

            // Keep rendering crisp; dimming is now handled by overlay.
            TermView.Opacity = 1.0;
            AutomationProperties.SetName(TermView, focused ? "Terminal Pane Active" : "Terminal Pane");

            // Re-render to ensure cursor state updates
            TermView.InvalidateVisual();
        }

        public (double MinWidth, double MinHeight) GetMinimumPaneSize()
        {
            UpdateMinimumSizeConstraints();
            return (MinWidth, MinHeight);
        }

        private void UpdateMinimumSizeConstraints()
        {
            float cellWidth = TermView.Metrics.CellWidth > 0 ? TermView.Metrics.CellWidth : 8f;
            float cellHeight = TermView.Metrics.CellHeight > 0 ? TermView.Metrics.CellHeight : 18f;

            // UX spec: minimum 20 cols x 5 rows.
            MinWidth = Math.Ceiling((cellWidth * 20) + 4);
            MinHeight = Math.Ceiling(cellHeight * 5);

        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // Focus Handling: Ensure we are focused
            if (!IsKeyboardFocusWithin) return;

            var modifiers = e.KeyModifiers;
            bool isCtrl = (modifiers & KeyModifiers.Control) != 0;
            bool isShift = (modifiers & KeyModifiers.Shift) != 0;

            // Search Shortcut (Ctrl+Shift+F)
            if (isCtrl && isShift && e.Key == Key.F)
            {
                ToggleSearch();
                e.Handled = true;
                return;
            }

            // Copy/Paste (Ctrl+Shift+C/V) - TBD
            // Font Zoom - TBD

            // Phase 4 spec §7.4. A remote connect is in flight (it may be waiting on a prompt): keys have nowhere
            // to go, and another Enter would only join it. While the host's loop reconnects, input is dropped,
            // never queued - the first key of the episode says so - and Enter tries the host at once.
            if (_remoteConnecting)
            {
                e.Handled = true;
                return;
            }

            if (_muxReconnecting)
            {
                e.Handled = true;
                if (e.Key == Key.Enter) RetryRemoteNow();
                else if (!IsModifierKey(e.Key)) NoteRemoteInputDropped();
                return;
            }

            // Reconnect if dead
            // _muxConnectionLost covers what the session cannot report itself: an attach that failed
            // over a connection that is still up leaves a running, connected, never-attached session.
            if ((_muxConnectionLost || ShouldReconnectOnEnter(Session)) && e.Key == Key.Enter)
            {
                e.Handled = true;
                Reconnect();
                return;
            }

            // Forward to PTY common handler
            // For now, we rely on Window forwarding, OR we implement it here.
            // PLAN: We will implement full OnKeyDown here in Phase 2.

            base.OnKeyDown(e);
        }

        private static bool IsModifierKey(Key key) => key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

        internal void HandleAltScreenChangedForTest(bool isAltScreen)
        {
            if (Buffer != null)
            {
                FieldInfo? field = typeof(TerminalBuffer).GetField("_isAltScreen", BindingFlags.Instance | BindingFlags.NonPublic);
                field?.SetValue(Buffer, isAltScreen);
            }

            HandleAltScreenChanged(isAltScreen);
        }

        internal void HandleWorkingDirectoryChangedForTest(string cwd)
        {
            HandleWorkingDirectoryChanged(cwd);
        }

        /// <summary>
        /// Count of OSC 52 clipboard-write payloads that made it past the
        /// <see cref="TerminalSettings.AllowOsc52ClipboardWrite"/> gate and decoding in
        /// <see cref="CreateAndWireParser"/>'s <c>Parser.OnClipboardWrite</c> handler (issue #268).
        /// A synchronous seam for tests: the real path continues on to
        /// <c>Dispatcher.UIThread.Post</c> + <c>TermView.SetClipboardTextAsync</c>, which needs a
        /// live UI-thread TopLevel/Clipboard that headless tests do not provide, so this counter
        /// is what the settings-gate test asserts against instead.
        /// </summary>
        internal int ClipboardWriteAttemptsForTest => _clipboardWriteAttemptsForTest;

        /// <summary>
        /// Whether the pane has latched "this session emits OSC 133" - i.e. what
        /// <see cref="UpdateCommandAssistContext"/> would publish as <c>isShellIntegrated</c>.
        /// </summary>
        /// <remarks>
        /// A seam rather than an observation of the published context, because the publish is posted
        /// to the UI thread and the thing under test (the setting gate on
        /// <see cref="NoteShellIntegrationMarkObserved"/>) decides whether it is posted at all. A
        /// test asserting "nothing was published" against an async post would be asserting a
        /// timeout.
        /// </remarks>
        internal bool HasObservedShellIntegrationMarkForTest => _hasObservedShellIntegrationMark;

        /// <summary>
        /// The command text the pane would hand to Fix mode and the long-command notification. Read
        /// by the tests that pin what a <c>133;C</c> payload is and is not allowed to overwrite.
        /// </summary>
        internal string? LastRelevantCommandTextForTest => _lastRelevantCommandText;
    }
}
