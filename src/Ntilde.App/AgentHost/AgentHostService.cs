using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ntilde.AgentHost.Contracts;
using Ntilde.Replay;
using Ntilde.Shell;
using Ntilde.VT;
using MuxReadScreenResult = Ntilde.Mux.Contracts.ReadScreenResult;

namespace Ntilde.AgentHost
{
    /// <summary>
    /// Local IPC endpoint for the agent-host observe surface (milestone A1/PR3,
    /// docs/plans/2026-07-07-agent-host-a1-observe-design.md).
    ///
    /// Off by default: nothing listens until <see cref="Apply"/> is called with
    /// <c>TerminalSettings.AgentAccessObserveEnabled == true</c>. When enabled it
    /// serves <c>listSessions</c> / <c>readScreen</c> / <c>readScrollback</c> over a
    /// per-user local endpoint — a named pipe with <see cref="PipeOptions.CurrentUserOnly"/>
    /// on Windows, a unix domain socket (mode 0600) elsewhere — and writes an
    /// <see cref="EndpointDescriptor"/> discovery file next to settings.json.
    ///
    /// The protocol is observe-only by construction: no input, spawn, or close
    /// methods exist in v1. Screen reads go through the deterministic
    /// <see cref="BufferSnapshot"/> path under the buffer's read lock — the same
    /// snapshot boundary the replay/parity tests use.
    /// </summary>
    public sealed class AgentHostService : IDisposable
    {
        /// <summary>Process-wide instance used by the app wiring. Tests construct their own.</summary>
        public static AgentHostService Instance => _instanceOverride ?? ProcessInstance;

        private static readonly AgentHostService ProcessInstance = new(AgentSessionRegistry.Instance);

        // Thread-local for the reason AgentSessionRegistry's override is: a window built by one test must not
        // publish into a service another test is using at the same moment.
        [ThreadStatic]
        private static AgentHostService? _instanceOverride;

        /// <summary>
        /// Redirects <see cref="Instance"/> on the current thread to <paramref name="service"/> until the returned
        /// scope is disposed. Test seam: MainWindow publishes its bridges to <see cref="Instance"/> and turns it on when
        /// observe is on, and the process instance would then listen on the real endpoint name. A window test scopes
        /// this around the window's whole life, on the UI thread, with a service of its own.
        /// </summary>
        internal static IDisposable OverrideInstanceForTesting(AgentHostService service)
        {
            ArgumentNullException.ThrowIfNull(service);
            var scope = new InstanceOverrideScope(_instanceOverride);
            _instanceOverride = service;
            return scope;
        }

        private sealed class InstanceOverrideScope : IDisposable
        {
            private readonly AgentHostService? _previous;
            public InstanceOverrideScope(AgentHostService? previous) => _previous = previous;
            public void Dispose() => _instanceOverride = _previous;
        }

        private readonly AgentSessionRegistry _registry;
        private readonly string? _endpointOverride;
        private readonly string? _discoveryDirectoryOverride;
        private readonly string? _exportDirectoryOverride;
        private readonly AgentActivityJournal _journal;
        private readonly Func<DateTimeOffset> _now;
        private readonly object _gate = new();

        private CancellationTokenSource? _cts;
        private Task? _acceptLoop;
        private Socket? _unixListener;
        private string? _unixSocketPath;
        private string? _discoveryFilePath;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Stream, byte> _activeClients = new();

        // A2 status plumbing — exists only while the endpoint is running
        // (off-is-off: no ring, no timer, no subscriptions when disabled).
        private AgentEventRing? _eventRing;
        private Timer? _sweepTimer;
        private readonly Dictionary<AgentSessionRegistration, Action<AgentSessionStatusEvent>> _statusSubscriptions = new();

        public AgentHostService(
            AgentSessionRegistry registry,
            string? endpointOverride = null,
            string? discoveryDirectoryOverride = null,
            string? exportDirectoryOverride = null,
            AgentActivityJournal? journal = null,
            Func<DateTimeOffset>? nowProvider = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _endpointOverride = endpointOverride;
            _discoveryDirectoryOverride = discoveryDirectoryOverride;
            _exportDirectoryOverride = exportDirectoryOverride;
            _journal = journal ?? AgentActivityJournal.Instance;
            _now = nowProvider ?? (() => DateTimeOffset.UtcNow);
        }

        // Volatile: written by the UI thread (settings apply), read by the IPC
        // thread per exportReplay request — the store/load barrier makes a
        // toggle visible to in-flight connections immediately.
        private volatile bool _replayExportEnabled;

        /// <summary>
        /// Second default-off gate for <c>exportReplay</c> (A4): mirrors
        /// <c>TerminalSettings.AgentReplayExportEnabled</c>, pushed by
        /// MainWindow alongside <see cref="Apply"/>. Both the observe toggle
        /// (endpoint running) and this flag must be on for an export to
        /// succeed — the "explicit export action" tier of the DIRECTION
        /// permission table.
        /// </summary>
        public bool ReplayExportEnabled
        {
            get => _replayExportEnabled;
            set => _replayExportEnabled = value;
        }

        // A3 act gate. Volatile for the same UI-writes/IPC-reads reason as the
        // export gate above.
        private volatile bool _actEnabled;

        /// <summary>
        /// Separate default-off opt-in for the acting surface (A3):
        /// <c>sendInput</c> and later spawn/close. Mirrors
        /// <c>TerminalSettings.AgentAccessActEnabled</c>, pushed by MainWindow
        /// alongside <see cref="Apply"/>. On top of observe; SSH targets need
        /// per-profile allowlisting as well (see <see cref="AllowsAgentActOnProfile"/>).
        /// </summary>
        public bool ActEnabled
        {
            get => _actEnabled;
            set
            {
                _actEnabled = value;
                RefreshActability();
            }
        }

        private int _inFlightPolls;

        /// <summary>
        /// How many <c>waitForEvents</c> long polls are parked right now. The
        /// subscription names no pane (WaitForEventsParams carries only
        /// sinceSeq/timeoutMs), so it drives the window-level observe indicator
        /// rather than any pane's tier.
        /// </summary>
        public int InFlightPollCount => Volatile.Read(ref _inFlightPolls);

        // When the window light's windowless half goes out (Phase 5 §3): set on an IPC thread by every windowless read,
        // cleared by the 1 s sweep. Its own lock, never taken with _gate held except by StopLocked's reset.
        private readonly object _windowlessWatchGate = new();
        private DateTimeOffset? _windowlessWatchedUntil;

        /// <summary>
        /// True from a read of a windowless session (Phase 5 §3) until <see cref="AgentAttentionMachine.ReadDecaySeconds"/>
        /// after the last one, when the 1 s sweep clears it. Such a session has no pane, so no pane indicator can show the
        /// read: the window-level light is where it appears.
        /// </summary>
        public bool WindowlessWatched
        {
            get { lock (_windowlessWatchGate) { return _windowlessWatchedUntil.HasValue; } }
        }

        private void NoteWindowlessRead()
        {
            bool lit;
            lock (_windowlessWatchGate)
            {
                lit = !_windowlessWatchedUntil.HasValue;
                _windowlessWatchedUntil = _now() + TimeSpan.FromSeconds(AgentAttentionMachine.ReadDecaySeconds);
            }
            if (lit) RaiseObserveActivityChanged();
        }

        private void SweepWindowlessWatched()
        {
            bool cleared;
            lock (_windowlessWatchGate)
            {
                cleared = _windowlessWatchedUntil is { } until && _now() >= until;
                if (cleared) _windowlessWatchedUntil = null;
            }
            if (cleared) RaiseObserveActivityChanged();
        }

        /// <summary>
        /// Raised when <see cref="InFlightPollCount"/> transitions between zero and non-zero, and when
        /// <see cref="WindowlessWatched"/> changes. On whichever thread made the change (an IPC or timer thread).
        /// </summary>
        public event Action? ObserveActivityChanged;

        /// <summary>
        /// Recomputes and publishes act-reachability onto every registration:
        /// observe (the endpoint actually running), the global act toggle, and
        /// the per-profile allowlist for SSH panes. Called from the 1 s sweep,
        /// immediately whenever the act toggle flips, and from
        /// <see cref="Apply"/>, so the pane chrome cannot lag a permission
        /// change.
        ///
        /// The observe term is not redundant: the two settings checkboxes are
        /// independent, so observe-off/act-on is user-reachable. Without it,
        /// every local pane grew a 22 px "agent access" bar — reflowing its PTY
        /// once — claiming an agent could type into it while nothing was even
        /// listening. <c>IsRunning</c> rather than a mirrored observe flag
        /// because that is what the acting handlers actually require: a request
        /// can only arrive over a live endpoint.
        /// </summary>
        internal void RefreshActability()
        {
            bool act = ActEnabled && IsRunning;
            foreach (var registration in _registry.GetRegistrations())
            {
                bool actable = act;
                if (actable && string.Equals(registration.Kind, "ssh", StringComparison.Ordinal))
                {
                    var profileId = registration.ProfileId;
                    var probe = _sshProfileAllowlist;
                    actable = profileId.HasValue && probe != null && probe(profileId.Value);
                }
                registration.IsAgentActable = actable;
            }
        }

        /// <summary>
        /// Notes a read on <paramref name="registration"/>'s attention machine,
        /// swallowing any subscriber exception. <see cref="AgentAttentionMachine.Changed"/>
        /// handlers run synchronously inside <c>NoteRead</c>'s drain loop and a
        /// throw there rethrows out of <c>NoteRead</c> itself; since every read
        /// handler calls this after the read already succeeded, an unguarded
        /// throw would turn a successful read into an Internal error response
        /// for something that already happened. Same containment pattern as
        /// <see cref="SweepStatuses"/>'s guard around <see cref="RefreshActability"/>.
        /// </summary>
        private static void TryNoteRead(AgentSessionRegistration registration)
        {
            try
            {
                registration.AttentionMachine.NoteRead();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AgentHost] attention NoteRead failed for {registration.PaneId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Notes a successful write on <paramref name="registration"/>'s attention
        /// machine, swallowing any subscriber exception for the same reason as
        /// <see cref="TryNoteRead"/>: every call site here runs after the write
        /// (input sent, session closed, pane spawned) has already happened, so an
        /// unguarded throw would misreport a real success as an Internal error and
        /// — for the acting methods — skip the <see cref="Journaled"/> record of an
        /// attempt that genuinely occurred.
        /// </summary>
        private static void TryNoteWrote(AgentSessionRegistration registration, string method)
        {
            try
            {
                registration.AttentionMachine.NoteWrote(method);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AgentHost] attention NoteWrote failed for {registration.PaneId}: {ex.Message}");
            }
        }

        // Per-profile SSH allowlist probe, published by MainWindow (reads the
        // SSH profile store). Null (no probe wired) denies every SSH profile —
        // fail closed. Volatile: published from the UI thread, read on IPC.
        private volatile Func<Guid, bool>? _sshProfileAllowlist;

        /// <summary>Publishes (or clears) the per-profile SSH allowlist probe. UI thread.</summary>
        public void SetSshProfileAllowlist(Func<Guid, bool>? probe) => _sshProfileAllowlist = probe;

        // UI-thread bridge for spawn/close (A3 PR2), published by MainWindow.
        // Closes over the window, so it is cleared on Stop like the allowlist
        // probe. Volatile: published from UI, read on IPC.
        private volatile IAgentActionExecutor? _actionExecutor;

        /// <summary>Publishes (or clears) the spawn/close UI executor. UI thread.</summary>
        public void SetActionExecutor(IAgentActionExecutor? executor) => _actionExecutor = executor;

        // The window's windowless mux sessions (Phase 5 §3), published by MainWindow
        // while it has mux hosts; the per-session handlers ask it for an id no pane
        // has (see the "Windowless sessions" section). Its shownHere delegate closes
        // over the window, so it is cleared on Stop like the executor: with observe
        // off nothing asks a daemon anything. Volatile: published from UI, read on IPC.
        private volatile IWindowlessSessionSource? _windowlessSource;

        /// <summary>Publishes (or clears) the window's windowless-session source. UI thread.</summary>
        internal void SetWindowlessSource(IWindowlessSessionSource? source) => _windowlessSource = source;

        /// <summary>The published windowless-session source; null when none is.</summary>
        internal IWindowlessSessionSource? WindowlessSource => _windowlessSource;

        private bool AllowsAgentActOnProfile(Guid profileId)
        {
            var probe = _sshProfileAllowlist;
            if (probe == null) return false; // fail closed
            try
            {
                return probe(profileId);
            }
            catch
            {
                return false;
            }
        }

        public bool IsRunning
        {
            get { lock (_gate) { return _cts != null; } }
        }

        /// <summary>The active endpoint (pipe name or socket path), or null when stopped.</summary>
        public string? EndpointName { get; private set; }

        /// <summary>Starts or stops the endpoint to match the observe setting. Safe to call repeatedly.</summary>
        public void Apply(bool enabled)
        {
            if (enabled) Start(); else Stop();
            // Actability includes the observe term (see RefreshActability), so
            // the pane bars have to be republished on both edges: stopping the
            // endpoint must clear them immediately rather than leaving them
            // until the next sweep tick — which, with the endpoint stopped, no
            // longer runs at all.
            RefreshActability();
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_cts != null) return;

                // AgentHostDiscovery mirrors AppPaths.RootDirectory; using it here
                // keeps writer (app) and reader (MCP server) on one path by construction.
                var discoveryDir = _discoveryDirectoryOverride ?? AgentHostDiscovery.GetDefaultDirectory();
                var discoveryPath = Path.Combine(discoveryDir, AgentHostProtocol.DiscoveryFileName);

                // First instance wins: if another live Ntilde already
                // advertises an endpoint, leave it alone.
                if (TryReadForeignLiveDescriptor(discoveryPath))
                {
                    Debug.WriteLine("[AgentHost] Another live instance owns the endpoint; not starting.");
                    return;
                }

                var endpoint = _endpointOverride ?? DefaultEndpointName();
                _cts = new CancellationTokenSource();
                var token = _cts.Token;

                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        _acceptLoop = Task.Run(() => AcceptNamedPipeLoopAsync(endpoint, token), token);
                    }
                    else
                    {
                        StartUnixListener(endpoint);
                        _acceptLoop = Task.Run(() => AcceptUnixLoopAsync(token), token);
                    }

                    Directory.CreateDirectory(discoveryDir);
                    var descriptor = new EndpointDescriptor
                    {
                        Version = AgentHostProtocol.Version,
                        Endpoint = endpoint,
                        Pid = Environment.ProcessId,
                    };
                    File.WriteAllText(
                        discoveryPath,
                        JsonSerializer.Serialize(descriptor, AgentHostJsonContext.Default.EndpointDescriptor));
                    _discoveryFilePath = discoveryPath;
                    EndpointName = endpoint;

                    StartStatusPlumbingLocked();
                }
                catch (Exception ex)
                {
                    // The agent host is an optional surface: a failure to bind
                    // (permissions, stale endpoint, another listener) must never
                    // take the terminal down. Leave the app fully functional.
                    StopLocked();
                    Debug.WriteLine($"[AgentHost] failed to start observe endpoint: {ex.Message}");
                }
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                StopLocked();
            }
        }

        public void Dispose() => Stop();

        private void StopLocked()
        {
            StopStatusPlumbingLocked();

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _acceptLoop = null;
            EndpointName = null;

            // Disabling Agent Access must revoke access *now*: cancelling the
            // accept loop is not enough, because already-accepted connections
            // could otherwise keep reading screens on their open stream.
            foreach (var client in _activeClients.Keys)
            {
                try { client.Dispose(); } catch { /* best effort */ }
            }
            _activeClients.Clear();

            try { _unixListener?.Dispose(); } catch { /* best effort */ }
            _unixListener = null;
            if (_unixSocketPath != null)
            {
                try { File.Delete(_unixSocketPath); } catch { /* best effort */ }
                _unixSocketPath = null;
            }

            if (_discoveryFilePath != null)
            {
                ReleaseDiscoveryFile(_discoveryFilePath);
                _discoveryFilePath = null;
            }

            // Release the SSH allowlist probe. It closes over MainWindow (via the
            // instance method it points at); this static singleton outlives the
            // window, so holding the delegate would pin the closed window (and its
            // tabs, PTYs, controls) in memory. MainWindow re-publishes it before
            // each Apply, so clearing here is safe. Bool gates are value types and
            // do not leak, so they are left as-is.
            _sshProfileAllowlist = null;
            _actionExecutor = null; // closes over the window too — same pinning reason
            _windowlessSource = null; // so does its shownHere

            // The sweep that would put the windowless light out stops with the endpoint. Nobody is told: a stopped
            // endpoint hides the light anyway, and the window refreshes it after every Apply.
            lock (_windowlessWatchGate)
            {
                _windowlessWatchedUntil = null;
            }
        }

        /// <summary>
        /// Retires our discovery descriptor without racing other instances.
        /// The pid check and the release happen under one exclusive file handle
        /// (FileShare.None), so another instance cannot write its descriptor
        /// between "it's ours" and the clear. We truncate instead of deleting:
        /// a delete would have to happen after the handle closes, reopening the
        /// window — while an empty file is already treated as a stale
        /// descriptor by <see cref="TryReadForeignLiveDescriptor"/> and by
        /// clients, and is rewritten in place by the next Start().
        /// </summary>
        private static void ReleaseDiscoveryFile(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                EndpointDescriptor? descriptor = null;
                try
                {
                    using var reader = new StreamReader(fs, leaveOpen: true);
                    descriptor = JsonSerializer.Deserialize(
                        reader.ReadToEnd(), AgentHostJsonContext.Default.EndpointDescriptor);
                }
                catch (JsonException)
                {
                    // Unreadable == stale == safe to clear.
                }

                if (descriptor == null || descriptor.Pid == Environment.ProcessId)
                {
                    fs.SetLength(0);
                    fs.Flush();
                }
                // else: another instance took over the endpoint — leave its
                // descriptor untouched.
            }
            catch
            {
                // Missing file, or a foreign instance holds the lock right now:
                // either way there is nothing of ours left to clean up.
            }
        }

        // ── A2 status plumbing ───────────────────────────────────────────────

        private void StartStatusPlumbingLocked()
        {
            _eventRing = new AgentEventRing();
            _registry.SessionRegistered += OnSessionRegistered;
            _registry.SessionUnregistered += OnSessionUnregistered;

            // Sessions that were already open when the endpoint started get
            // forwarding but no synthetic sessionOpened: a fresh client learns
            // about them via listSessions, not via a burst of stale events.
            // They also start flight recording (A4): the ring exists exactly
            // while the observe endpoint runs — off-is-off.
            foreach (var registration in _registry.GetRegistrations())
            {
                AttachStatusForwarding(registration);
                registration.EnableFlightRecording(AgentHostProtocol.FlightRecorderMaxBytesPerSession);
            }

            _sweepTimer = new Timer(_ => SweepStatuses(), null,
                dueTime: TimeSpan.FromSeconds(1), period: TimeSpan.FromSeconds(1));
        }

        private void StopStatusPlumbingLocked()
        {
            _sweepTimer?.Dispose();
            _sweepTimer = null;

            _registry.SessionRegistered -= OnSessionRegistered;
            _registry.SessionUnregistered -= OnSessionUnregistered;

            // Off-is-off: disabling Agent Access drops every flight ring now —
            // no in-memory retention survives the toggle.
            foreach (var registration in _registry.GetRegistrations())
            {
                registration.DisableFlightRecording();
            }

            foreach (var (registration, handler) in _statusSubscriptions)
            {
                registration.StatusMachine.EventEmitted -= handler;
            }
            _statusSubscriptions.Clear();
            _eventRing = null;
        }

        private void OnSessionRegistered(AgentSessionRegistration registration)
        {
            // Publish act-reachability onto the brand-new registration now.
            // AgentSessionRegistration._isAgentActable defaults to false, so
            // without this a pane created while act is on is laid out with no
            // status bar and only learns otherwise at the next 1 s sweep tick.
            // At that point, flipping `IsAgentActable` raises `ActabilityChanged`,
            // which reaches `ApplyAgentAttention` and then
            // `UpdateStatusBarVisibility`, so the 22 px bar appears and the
            // terminal row shrinks — reflowing the PTY about a second after the
            // pane opened, right on top of whatever full-screen TUI the user
            // just started. The design allows exactly one reflow, at
            // permission-toggle time, which is a deliberate user action — and
            // this is not.
            //
            // Guarded for the same reason SweepStatuses guards its call: this
            // runs synchronously inside AgentSessionRegistry.Register, on the UI
            // thread during TerminalPane construction, and a throw here would
            // take the pane's constructor down with it.
            try
            {
                RefreshActability();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AgentHost] actability refresh failed for {registration.PaneId}: {ex.Message}");
            }

            AgentEventRing? ring;
            lock (_gate)
            {
                ring = _eventRing;
                if (ring == null) return;
                AttachStatusForwardingLocked(registration, ring);
                registration.EnableFlightRecording(AgentHostProtocol.FlightRecorderMaxBytesPerSession);
            }

            var status = registration.StatusMachine.Snapshot();
            ring.Append(registration.PaneId, AgentHostProtocol.EventTypes.SessionOpened, status.Kind.ToWire(), DateTimeOffset.UtcNow);
        }

        private void OnSessionUnregistered(AgentSessionRegistration registration)
        {
            AgentEventRing? ring;
            lock (_gate)
            {
                ring = _eventRing;
                if (_statusSubscriptions.Remove(registration, out var handler))
                {
                    registration.StatusMachine.EventEmitted -= handler;
                }
            }

            var status = registration.StatusMachine.Snapshot();
            ring?.Append(registration.PaneId, AgentHostProtocol.EventTypes.SessionClosed, status.Kind.ToWire(), DateTimeOffset.UtcNow, status.ExitCode);
        }

        private void AttachStatusForwarding(AgentSessionRegistration registration)
        {
            // Caller holds _gate (Start path).
            if (_eventRing is { } ring)
            {
                AttachStatusForwardingLocked(registration, ring);
            }
        }

        private void AttachStatusForwardingLocked(AgentSessionRegistration registration, AgentEventRing ring)
        {
            if (_statusSubscriptions.ContainsKey(registration)) return;

            // PaneId is read at event time (it can be re-keyed by session
            // restore); the ring instance is captured so a stopped endpoint's
            // orphaned events can never land in a newer ring.
            void Handler(AgentSessionStatusEvent evt) => ring.Append(
                registration.PaneId,
                evt.Type.ToWire(),
                evt.Status.ToWire(),
                evt.Timestamp,
                evt.ExitCode,
                evt.Duration is { } d ? (long)d.TotalMilliseconds : null);

            _statusSubscriptions[registration] = Handler;
            registration.StatusMachine.EventEmitted += Handler;
        }

        /// <summary>The 1 s timer's work while the endpoint runs. Internal so a test can drive it with its own clock.</summary>
        internal void SweepStatuses()
        {
            foreach (var registration in _registry.GetRegistrations())
            {
                try
                {
                    registration.StatusMachine.Sweep(registration.ProbeHasActiveChildProcesses());
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AgentHost] status sweep failed for {registration.PaneId}: {ex.Message}");
                }

                // Separate try/catch: a registration whose child-process probe
                // throws on every sweep must not also skip its attention Tick —
                // otherwise a sticky Wrote tier could never retire past the
                // write floor for that pane.
                try
                {
                    registration.AttentionMachine.Tick();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AgentHost] attention tick failed for {registration.PaneId}: {ex.Message}");
                }
            }

            try
            {
                RefreshActability();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AgentHost] actability refresh failed: {ex.Message}");
            }

            try
            {
                SweepWindowlessWatched();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AgentHost] windowless light sweep failed: {ex.Message}");
            }
        }

        private static string DefaultEndpointName()
        {
            if (OperatingSystem.IsWindows())
            {
                // CurrentUserOnly enforces the ACL; the user name only namespaces
                // the pipe so different users on one machine don't collide.
                var user = new string(Array.FindAll(
                    Environment.UserName.ToLowerInvariant().ToCharArray(), char.IsLetterOrDigit));
                return AgentHostProtocol.WindowsPipeNamePrefix + user;
            }

            return Path.Combine(Ntilde.Shell.AppPaths.RootDirectory, AgentHostProtocol.UnixSocketFileName);
        }

        private static bool TryReadForeignLiveDescriptor(string discoveryPath)
        {
            try
            {
                if (!File.Exists(discoveryPath)) return false;
                var descriptor = JsonSerializer.Deserialize(
                    File.ReadAllText(discoveryPath), AgentHostJsonContext.Default.EndpointDescriptor);
                if (descriptor == null || descriptor.Pid == Environment.ProcessId) return false;

                // Guard against PID recycling: the pid must be alive AND be a
                // Ntilde process before we defer to it.
                var process = Process.GetProcessById(descriptor.Pid);
                using var current = Process.GetCurrentProcess();
                return !process.HasExited
                    && string.Equals(process.ProcessName, current.ProcessName, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // Unreadable descriptor or dead pid: stale, safe to replace.
                return false;
            }
        }

        // ── Transport ────────────────────────────────────────────────────────

        private async Task AcceptNamedPipeLoopAsync(string pipeName, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    var connected = server;
                    server = null;
                    _ = Task.Run(() => HandleClientAsync(connected, token), token);
                }
                catch (OperationCanceledException)
                {
                    server?.Dispose();
                    return;
                }
                catch (Exception ex)
                {
                    server?.Dispose();
                    if (token.IsCancellationRequested) return;
                    Debug.WriteLine($"[AgentHost] pipe accept failed: {ex.Message}");
                    try { await Task.Delay(250, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        private void StartUnixListener(string socketPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);

            // Never unlink a live socket: if a file is present, probe it first.
            // Only a socket nobody answers on is stale and safe to remove.
            if (File.Exists(socketPath))
            {
                if (IsUnixSocketAlive(socketPath))
                {
                    throw new IOException($"Another live agent-host endpoint is listening on '{socketPath}'.");
                }
                File.Delete(socketPath);
            }

            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(backlog: 4);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            _unixListener = listener;
            _unixSocketPath = socketPath;
        }

        private static bool IsUnixSocketAlive(string socketPath)
        {
            try
            {
                using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                probe.Connect(new UnixDomainSocketEndPoint(socketPath));
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        private async Task AcceptUnixLoopAsync(CancellationToken token)
        {
            var listener = _unixListener!;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptAsync(token).ConfigureAwait(false);
                    var stream = new NetworkStream(client, ownsSocket: true);
                    _ = Task.Run(() => HandleClientAsync(stream, token), token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return; // Stop() disposed the listener
                }
                catch (Exception ex)
                {
                    // Transient accept failure: log and retry, mirroring the
                    // named-pipe loop, so the service never ends up half-running
                    // (IsRunning true with a dead accept loop).
                    if (token.IsCancellationRequested) return;
                    Debug.WriteLine($"[AgentHost] socket accept failed: {ex.Message}");
                    try { await Task.Delay(250, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        private async Task HandleClientAsync(Stream stream, CancellationToken token)
        {
            _activeClients.TryAdd(stream, 0);
            try
            {
                using var _ = stream;
                using var reader = new StreamReader(stream, new UTF8Encoding(false), leaveOpen: true);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

                while (!token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                    if (line == null) return;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var response = await HandleRequestLineAsync(line, token).ConfigureAwait(false);
                    await writer.WriteLineAsync(
                        JsonSerializer.Serialize(response, AgentHostJsonContext.Default.AgentHostResponse))
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AgentHost] client connection ended: {ex.Message}");
            }
            finally
            {
                _activeClients.TryRemove(stream, out _);
            }
        }

        // ── Request handling ─────────────────────────────────────────────────

        internal async Task<AgentHostResponse> HandleRequestLineAsync(string line, CancellationToken cancellationToken)
        {
            AgentHostRequest? request;
            try
            {
                request = JsonSerializer.Deserialize(line, AgentHostJsonContext.Default.AgentHostRequest);
            }
            catch (JsonException ex)
            {
                return Error(0, AgentHostProtocol.ErrorCodes.MalformedRequest, $"Unparseable frame: {ex.Message}");
            }

            if (request == null)
            {
                return Error(0, AgentHostProtocol.ErrorCodes.MalformedRequest, "Empty frame.");
            }

            if (request.Version != AgentHostProtocol.Version)
            {
                return Error(
                    request.Id,
                    AgentHostProtocol.ErrorCodes.VersionMismatch,
                    $"Protocol version {request.Version} is not supported; this endpoint speaks version {AgentHostProtocol.Version}.");
            }

            try
            {
                return request.Method switch
                {
                    AgentHostProtocol.Methods.ListSessions => await HandleListSessionsAsync(request, cancellationToken).ConfigureAwait(false),
                    AgentHostProtocol.Methods.ReadScreen => await HandleReadScreenAsync(request, cancellationToken).ConfigureAwait(false),
                    AgentHostProtocol.Methods.ReadScrollback => await HandleReadScrollbackAsync(request, cancellationToken).ConfigureAwait(false),
                    AgentHostProtocol.Methods.GetSessionStatus => await HandleGetSessionStatusAsync(request, cancellationToken).ConfigureAwait(false),
                    AgentHostProtocol.Methods.WaitForEvents => await HandleWaitForEventsAsync(request, cancellationToken).ConfigureAwait(false),
                    AgentHostProtocol.Methods.ExportReplay => HandleExportReplay(request),
                    AgentHostProtocol.Methods.CaptureScreen => await HandleCaptureScreenAsync(request, cancellationToken).ConfigureAwait(false),
                    AgentHostProtocol.Methods.SendInput => await HandleSendInputAsync(request, cancellationToken).ConfigureAwait(false),
                    AgentHostProtocol.Methods.SpawnSession => await HandleSpawnSessionAsync(request).ConfigureAwait(false),
                    AgentHostProtocol.Methods.CloseSession => await HandleCloseSessionAsync(request, cancellationToken).ConfigureAwait(false),
                    _ => Error(request.Id, AgentHostProtocol.ErrorCodes.UnknownMethod, $"Unknown method '{request.Method}'."),
                };
            }
            catch (OperationCanceledException)
            {
                throw; // endpoint stopping / client gone — the connection loop owns this
            }
            catch (JsonException ex)
            {
                return Error(request.Id, AgentHostProtocol.ErrorCodes.MalformedRequest, $"Bad params: {ex.Message}");
            }
            catch (Exception ex)
            {
                return Error(request.Id, AgentHostProtocol.ErrorCodes.Internal, ex.Message);
            }
        }

        private async Task<AgentHostResponse> HandleGetSessionStatusAsync(AgentHostRequest request, CancellationToken cancellationToken)
        {
            var p = DeserializeParams(request, AgentHostJsonContext.Default.GetSessionStatusParams);
            if (p == null)
            {
                return Error(request.Id, AgentHostProtocol.ErrorCodes.MalformedRequest, "getSessionStatus requires params with a paneId.");
            }
            if (!_registry.TryGet(p.PaneId, out var registration))
            {
                if (_windowlessSource is { } source)
                {
                    return await WindowlessStatusAsync(request, source, p.PaneId, cancellationToken).ConfigureAwait(false);
                }
                return Error(request.Id, AgentHostProtocol.ErrorCodes.SessionNotFound, $"No live session with paneId '{p.PaneId}'.");
            }

            TryNoteRead(registration);
            var dto = registration.StatusMachine.Snapshot().ToDto(registration.PaneId);
            return Ok(request.Id, JsonSerializer.SerializeToElement(dto, AgentHostJsonContext.Default.SessionStatusDto));
        }

        private async Task<AgentHostResponse> HandleWaitForEventsAsync(AgentHostRequest request, CancellationToken cancellationToken)
        {
            var p = DeserializeParams(request, AgentHostJsonContext.Default.WaitForEventsParams);
            if (p == null)
            {
                return Error(request.Id, AgentHostProtocol.ErrorCodes.MalformedRequest, "waitForEvents requires params with sinceSeq and timeoutMs.");
            }

            var ring = _eventRing;
            if (ring == null)
            {
                return Error(request.Id, AgentHostProtocol.ErrorCodes.Internal, "Event channel is not available.");
            }

            var sinceSeq = Math.Max(0, p.SinceSeq);
            var timeout = TimeSpan.FromMilliseconds(Math.Clamp(p.TimeoutMs, 0, AgentHostProtocol.MaxWaitForEventsTimeoutMs));

            // WaitForEventsParams names no pane (only sinceSeq/timeoutMs), so this
            // drives the window-level observe indicator (a later task's chrome),
            // never any pane's attention tier. Increment happens before the try so
            // the finally below is guaranteed to run and pair it with a decrement
            // even if something between here and the finally throws; the
            // subscriber invoke itself is routed through RaiseObserveActivityChanged
            // so a throwing subscriber can neither escape as an Internal error nor
            // skip the decrement and leak the count.
            var afterIncrement = Interlocked.Increment(ref _inFlightPolls);
            try
            {
                if (afterIncrement == 1)
                {
                    RaiseObserveActivityChanged();
                }

                var result = await ring.WaitSinceAsync(sinceSeq, timeout, cancellationToken).ConfigureAwait(false);
                return Ok(request.Id, JsonSerializer.SerializeToElement(result, AgentHostJsonContext.Default.WaitForEventsResult));
            }
            finally
            {
                if (Interlocked.Decrement(ref _inFlightPolls) == 0)
                {
                    RaiseObserveActivityChanged();
                }
            }
        }

        /// <summary>
        /// Invokes <see cref="ObserveActivityChanged"/> with subscriber exceptions
        /// contained: no subscriber exists yet (Task 7 adds the first one), but a
        /// throw here must never escape into <see cref="HandleWaitForEventsAsync"/>
        /// — that would turn a successful long poll into an Internal error and,
        /// were it to happen on the increment side outside a try/finally, leak
        /// <see cref="InFlightPollCount"/> forever.
        /// </summary>
        private void RaiseObserveActivityChanged()
        {
            try
            {
                ObserveActivityChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AgentHost] ObserveActivityChanged subscriber failed: {ex.Message}");
            }
        }

        private AgentHostResponse HandleExportReplay(AgentHostRequest request)
        {
            var p = DeserializeParams(request, AgentHostJsonContext.Default.ExportReplayParams);
            if (p == null)
            {
                return Error(request.Id, AgentHostProtocol.ErrorCodes.MalformedRequest, "exportReplay requires params with a paneId.");
            }

            // Second default-off gate (DIRECTION permission table: "observe
            // permission + explicit export action"). The observe toggle got the
            // caller this far; replay export needs its own opt-in.
            if (!ReplayExportEnabled)
            {
                return Error(
                    request.Id,
                    AgentHostProtocol.ErrorCodes.ExportDisabled,
                    "Replay export is disabled. Enable Settings → Agent access (observe) → Agent replay export in Ntilde, then retry. Exports contain terminal output and resizes only — never typed input.");
            }

            if (!_registry.TryGet(p.PaneId, out var registration))
            {
                return Error(request.Id, AgentHostProtocol.ErrorCodes.SessionNotFound, $"No live session with paneId '{p.PaneId}'.");
            }

            // An export writes the pane's whole flight recording to a file:
            // strictly more disclosure than readScreen, which marks the pane, and
            // on the same footing as captureScreen, which marks it too. Placed
            // after the sub-toggle check (which returns above) for the same
            // reason captureScreen sequences its own that way: a *denied* export
            // discloses nothing, so it must mark nothing.
            TryNoteRead(registration);

            var exportDir = _exportDirectoryOverride
                ?? Path.Combine(Ntilde.Shell.AppPaths.RecordingsDirectory, AgentHostProtocol.AgentExportsSubdirectory);
            Directory.CreateDirectory(exportDir);
            // Fresh random suffix per export (same scheme as manual recordings):
            // the timestamp alone has one-second resolution, so repeated exports
            // for one pane within a second must not compute the same path — the
            // writer truncates, which would silently destroy the earlier file.
            var fileName = Controls.TerminalPane.BuildRecordingFileName(DateTime.Now, Guid.NewGuid().ToString("N"));
            var filePath = Path.Combine(exportDir, fileName);

            if (!registration.TryExportFlightRecording(filePath, out var info))
            {
                return Error(
                    request.Id,
                    AgentHostProtocol.ErrorCodes.ExportUnavailable,
                    "No flight recording is available for this session right now (the session may still be starting, already closed, or the file could not be written).");
            }

            var result = new ExportReplayResult
            {
                FilePath = Path.GetFullPath(filePath),
                EventCount = info.EventCount,
                FirstEventMs = info.FirstEventMs,
                LastEventMs = info.LastEventMs,
                TruncatedAtStart = info.TruncatedAtStart,
            };
            return Ok(request.Id, JsonSerializer.SerializeToElement(result, AgentHostJsonContext.Default.ExportReplayResult));
        }

        /// <summary>
        /// A5: captures a pane as a PNG and writes it beside the agent replay
        /// exports. Mode 'render' (the default) re-renders from the buffer on this
        /// IPC thread and needs no visual tree, so a hidden, occluded, or minimized
        /// pane captures identically; mode 'live' photographs the on-screen control
        /// through the UI-thread bridge, which carries the background image and
        /// window opacity that the render path structurally cannot.
        /// </summary>
        private async Task<AgentHostResponse> HandleCaptureScreenAsync(AgentHostRequest request, CancellationToken cancellationToken)
        {
            CaptureScreenParams? p;
            try
            {
                p = DeserializeParams(request, AgentHostJsonContext.Default.CaptureScreenParams);
            }
            catch (JsonException)
            {
                // A missing/mistyped paneId throws here rather than returning null.
                p = null;
            }
            if (p == null)
            {
                return Error(request.Id, AgentHostProtocol.ErrorCodes.MalformedRequest, "captureScreen requires params with a paneId.");
            }

            // An unknown mode is malformed rather than coerced to the default: a
            // caller who asked for "wysiwyg" wants pixels off the screen, and
            // quietly handing back a headless render would answer a question they
            // did not ask.
            string requestedMode = string.IsNullOrWhiteSpace(p.Mode)
                ? AgentHostProtocol.CaptureModes.Render
                : p.Mode!.Trim();
            bool live;
            if (string.Equals(requestedMode, AgentHostProtocol.CaptureModes.Render, StringComparison.OrdinalIgnoreCase))
            {
                live = false;
            }
            else if (string.Equals(requestedMode, AgentHostProtocol.CaptureModes.Live, StringComparison.OrdinalIgnoreCase))
            {
                live = true;
            }
            else
            {
                return Error(request.Id, AgentHostProtocol.ErrorCodes.MalformedRequest,
                    $"Unknown capture mode '{p.Mode}'. Use '{AgentHostProtocol.CaptureModes.Render}' (the default) or '{AgentHostProtocol.CaptureModes.Live}'.");
            }

            double scale = p.Scale <= 0 ? 1.0 : Math.Min(p.Scale, AgentHostProtocol.MaxCaptureScale);
            int maxWidth = Math.Max(0, p.MaxWidth);

            if (!_registry.TryGet(p.PaneId, out var registration))
            {
                if (_windowlessSource is { } source)
                {
                    return await WindowlessCaptureAsync(request, source, p, live, maxWidth, scale, cancellationToken).ConfigureAwait(false);
                }
                return Error(request.Id, AgentHostProtocol.ErrorCodes.SessionNotFound, $"No live session with paneId '{p.PaneId}'.");
            }

            // Marks the pane's agent-access indicator (#339) for both modes. This
            // is the visibility surface a capture has: it rides the observe toggle
            // like every other read, so the indicator is what tells the user an
            // agent looked.
            TryNoteRead(registration);

            byte[] png;
            int imageWidth;
            int imageHeight;
            int cols;
            int rows;
            bool downscaled;

            if (live)
            {
                var executor = _actionExecutor;
                if (executor == null)
                {
                    return Error(request.Id, AgentHostProtocol.ErrorCodes.CaptureUnavailable,
                        "Live capture needs the app window, which is not available right now (starting up or shutting down). Retry, or use mode 'render', which does not need it.");
                }

                AgentLiveCapture? shot;
                try
                {
                    shot = await executor.CaptureLiveAsync(p.PaneId, maxWidth, scale).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // The UI-thread hop can fault on a window tearing down. A read
                    // must not surface that as Internal.
                    Debug.WriteLine($"[AgentHost] live capture threw for {p.PaneId}: {ex}");
                    shot = null;
                }
                if (shot is not { } liveShot)
                {
                    return Error(request.Id, AgentHostProtocol.ErrorCodes.CaptureUnavailable,
                        "This pane cannot be photographed right now: it has no on-screen size (a tab that has never been shown), or the image would exceed the per-capture pixel budget. Use mode 'render' to capture it regardless of what is on screen.");
                }

                png = liveShot.Png;
                imageWidth = liveShot.Width;
                imageHeight = liveShot.Height;
                downscaled = liveShot.Downscaled;

                // A live capture is pixels, not a grid, so the grid dimensions come
                // from the buffer - reported anyway so a caller can relate the image
                // back to what read_screen would return.
                var buffer = registration.Buffer;
                buffer.Lock.EnterReadLock();
                try
                {
                    cols = buffer.Cols;
                    rows = buffer.Rows;
                }
                finally
                {
                    buffer.Lock.ExitReadLock();
                }
            }
            else
            {
                if (!registration.TryCapturePng(maxWidth, scale, out var capture, out var captureError))
                {
                    var message = captureError == AgentCaptureError.TooLarge
                        ? $"This pane would render larger than the {AgentHostProtocol.MaxCapturePixels:N0}-pixel per-capture budget at scale {scale:0.##}. Lower the scale, make the window smaller, or raise the font size."
                        : "This session cannot be rendered right now (the pane has not been measured yet, or is being torn down). Retry shortly; ntilde.read_screen works regardless.";
                    return Error(request.Id, AgentHostProtocol.ErrorCodes.CaptureUnavailable, message);
                }

                png = capture.Png;
                imageWidth = capture.Width;
                imageHeight = capture.Height;
                cols = capture.Cols;
                rows = capture.Rows;
                downscaled = capture.Downscaled;
            }

            return CaptureResult(request, png, imageWidth, imageHeight, cols, rows, downscaled, live, scale, p.Inline);
        }

        /// <summary>
        /// The end of every capture, a pane's or a windowless session's: writes the PNG beside the agent replay exports
        /// and builds the result.
        /// </summary>
        private AgentHostResponse CaptureResult(
            AgentHostRequest request, byte[] png, int imageWidth, int imageHeight, int cols, int rows, bool downscaled,
            bool live, double scale, bool inlineRequested)
        {
            string filePath;
            try
            {
                var exportDir = _exportDirectoryOverride
                    ?? Path.Combine(Ntilde.Shell.AppPaths.RecordingsDirectory, AgentHostProtocol.AgentExportsSubdirectory);
                Directory.CreateDirectory(exportDir);
                // Random suffix for the same reason as replay export: the timestamp
                // has one-second resolution, and a second capture within that
                // second must not overwrite the first.
                filePath = Path.Combine(exportDir, BuildCaptureFileName(DateTime.Now, Guid.NewGuid().ToString("N")));
                File.WriteAllBytes(filePath, png);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Error(request.Id, AgentHostProtocol.ErrorCodes.CaptureUnavailable, $"The screenshot could not be written: {ex.Message}");
            }

            var inlineFits = png.Length <= AgentHostProtocol.MaxInlineCaptureBytes;
            var result = new CaptureScreenResult
            {
                FilePath = Path.GetFullPath(filePath),
                Width = imageWidth,
                Height = imageHeight,
                Cols = cols,
                Rows = rows,
                ByteCount = png.Length,
                Downscaled = downscaled,
                Mode = live ? AgentHostProtocol.CaptureModes.Live : AgentHostProtocol.CaptureModes.Render,
                Scale = scale,
                PngBase64 = inlineRequested && inlineFits ? Convert.ToBase64String(png) : null,
                InlineOmitted = inlineRequested && !inlineFits,
            };
            return Ok(request.Id, JsonSerializer.SerializeToElement(result, AgentHostJsonContext.Default.CaptureScreenResult));
        }

        internal static string BuildCaptureFileName(DateTime timestamp, string uniqueSuffix)
        {
            var normalized = string.IsNullOrWhiteSpace(uniqueSuffix)
                ? Guid.NewGuid().ToString("N")
                : uniqueSuffix.Trim().ToLowerInvariant();
            var shortSuffix = normalized.Length > 6 ? normalized[..6] : normalized.PadRight(6, '0');
            return string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"ntilde_screen_{timestamp:yyyyMMdd_HHmmss}_{shortSuffix}.png");
        }

        private async Task<AgentHostResponse> HandleSendInputAsync(AgentHostRequest request, CancellationToken cancellationToken)
        {
            SendInputParams? p;
            try
            {
                p = DeserializeParams(request, AgentHostJsonContext.Default.SendInputParams);
            }
            catch (JsonException)
            {
                // Missing required fields / bad shapes throw here rather than
                // returning null. Journal it: a malformed acting attempt is still
                // an externally reachable acting attempt the user should see.
                p = null;
            }
            if (p == null || p.Text == null)
            {
                return Journaled(request, AgentHostProtocol.Methods.SendInput, p?.PaneId, "input",
                    Error(request.Id, AgentHostProtocol.ErrorCodes.MalformedRequest, "sendInput requires params with a paneId and text."));
            }

            // Optional trailing CR (0x0D) — the byte a console treats as "Enter".
            // Text stays byte-faithful; submit only appends the carriage return
            // that many agent callers cannot emit as a raw 0x0D. See
            // SendInputParams.Submit.
            string payload = p.Submit ? p.Text + "\r" : p.Text;

            // Size cap before any lookup so a flood is rejected cheaply.
            int byteCount = Encoding.UTF8.GetByteCount(payload);
            if (byteCount > AgentHostProtocol.MaxSendInputBytes)
            {
                return Journaled(request, AgentHostProtocol.Methods.SendInput, p.PaneId, "input",
                    Error(request.Id, AgentHostProtocol.ErrorCodes.MalformedRequest,
                        $"Input exceeds the {AgentHostProtocol.MaxSendInputBytes}-byte per-call limit."));
            }

            // Separate act opt-in (DIRECTION: acting never rides the observe toggle).
            if (!_actEnabled)
            {
                return Journaled(request, AgentHostProtocol.Methods.SendInput, p.PaneId, "input",
                    Error(request.Id, AgentHostProtocol.ErrorCodes.ActDisabled,
                        "Acting is disabled. Enable Settings → Agent access (observe) → Agent access (act) in Ntilde, then retry."));
            }

            if (!_registry.TryGet(p.PaneId, out var registration))
            {
                if (_windowlessSource is { } source)
                {
                    // The act check rides inside the source's own look-up (WindowlessActCheck), so the act and its
                    // check share one survey of the daemons and one time budget.
                    var check = new WindowlessActCheck(this, source);
                    var outcome = await source.SendInputAsync(p.PaneId, payload, check.MayAct, cancellationToken).ConfigureAwait(false);
                    return WindowlessActed(request, AgentHostProtocol.Methods.SendInput, p.PaneId, "input", source, check, outcome,
                        Ok(request.Id, JsonSerializer.SerializeToElement(new SendInputResult { BytesSent = byteCount }, AgentHostJsonContext.Default.SendInputResult)));
                }
                return Journaled(request, AgentHostProtocol.Methods.SendInput, p.PaneId, "input",
                    Error(request.Id, AgentHostProtocol.ErrorCodes.SessionNotFound, $"No live session with paneId '{p.PaneId}'."));
            }

            // Per-profile SSH allowlist: acting on a remote reaches another
            // machine with the user's credentials, so it is opt-in per profile.
            if (string.Equals(registration.Kind, "ssh", StringComparison.Ordinal))
            {
                if (registration.ProfileId is not { } profileId || !AllowsAgentActOnProfile(profileId))
                {
                    return Journaled(request, AgentHostProtocol.Methods.SendInput, p.PaneId, registration.ProfileName,
                        Error(request.Id, AgentHostProtocol.ErrorCodes.ProfileNotAllowed,
                            $"The SSH profile '{registration.ProfileName}' is not allowlisted for agent access. Enable it in the connection's settings, then retry."));
                }
            }

            if (!registration.TrySendInput(payload))
            {
                return Journaled(request, AgentHostProtocol.Methods.SendInput, p.PaneId, registration.ProfileName,
                    Error(request.Id, AgentHostProtocol.ErrorCodes.SessionNotRunning,
                        "The session is not accepting input (its process has exited or is being torn down)."));
            }

            TryNoteWrote(registration, AgentHostProtocol.Methods.SendInput);

            var result = new SendInputResult { BytesSent = byteCount };
            return Journaled(request, AgentHostProtocol.Methods.SendInput, p.PaneId, registration.ProfileName,
                Ok(request.Id, JsonSerializer.SerializeToElement(result, AgentHostJsonContext.Default.SendInputResult)));
        }

        private async Task<AgentHostResponse> HandleSpawnSessionAsync(AgentHostRequest request)
        {
            SpawnSessionParams? p;
            try
            {
                // Missing params entirely (null) is legitimate — it means "default
                // local profile". A *present but unparseable* params object is
                // malformed and must NOT silently fall back to a default spawn
                // (that would turn bad input into an acting side effect).
                p = DeserializeParams(request, AgentHostJsonContext.Default.SpawnSessionParams) ?? new SpawnSessionParams();
            }
            catch (JsonException)
            {
                return Journaled(request, AgentHostProtocol.Methods.SpawnSession, null, "(malformed)",
                    Error(request.Id, AgentHostProtocol.ErrorCodes.MalformedRequest, "The spawnSession parameters are malformed."));
            }
            string target = string.IsNullOrWhiteSpace(p.Profile) ? "(default)" : p.Profile!;

            if (!_actEnabled)
            {
                return Journaled(request, AgentHostProtocol.Methods.SpawnSession, null, target,
                    Error(request.Id, AgentHostProtocol.ErrorCodes.ActDisabled,
                        "Acting is disabled. Enable Settings → Agent access (observe) → Agent access (act), then retry."));
            }

            var executor = _actionExecutor;
            if (executor == null)
            {
                return Journaled(request, AgentHostProtocol.Methods.SpawnSession, null, target,
                    Error(request.Id, AgentHostProtocol.ErrorCodes.ActUnavailable,
                        "The app cannot spawn sessions right now (starting up or shutting down). Retry shortly."));
            }

            (AgentSpawnResult? result, AgentSpawnError? error) = await executor.SpawnAsync(p.Profile).ConfigureAwait(false);
            if (result is not { } spawn)
            {
                var (code, message) = error switch
                {
                    AgentSpawnError.ProfileNotFound => (AgentHostProtocol.ErrorCodes.ProfileNotFound, $"No local or SSH profile named '{target}'."),
                    AgentSpawnError.ProfileNotAllowed => (AgentHostProtocol.ErrorCodes.ProfileNotAllowed, $"The SSH profile '{target}' is not allowlisted for agent access."),
                    _ => (AgentHostProtocol.ErrorCodes.SpawnFailed, $"Failed to open a session for '{target}'."),
                };
                return Journaled(request, AgentHostProtocol.Methods.SpawnSession, null, target,
                    Error(request.Id, code, message));
            }

            // The pane did not exist when this call started, so there was
            // nothing to mark until the executor created and registered it.
            if (_registry.TryGet(spawn.PaneId, out var spawned))
            {
                TryNoteWrote(spawned, AgentHostProtocol.Methods.SpawnSession);
            }

            var dto = new SpawnSessionResult
            {
                PaneId = spawn.PaneId,
                TabId = spawn.TabId,
                ProfileName = spawn.ProfileName,
                Kind = spawn.Kind,
            };
            return Journaled(request, AgentHostProtocol.Methods.SpawnSession, spawn.PaneId, spawn.ProfileName,
                Ok(request.Id, JsonSerializer.SerializeToElement(dto, AgentHostJsonContext.Default.SpawnSessionResult)));
        }

        private async Task<AgentHostResponse> HandleCloseSessionAsync(AgentHostRequest request, CancellationToken cancellationToken)
        {
            CloseSessionParams? p;
            try
            {
                p = DeserializeParams(request, AgentHostJsonContext.Default.CloseSessionParams);
            }
            catch (JsonException)
            {
                p = null;
            }
            if (p == null)
            {
                return Journaled(request, AgentHostProtocol.Methods.CloseSession, null, "session",
                    Error(request.Id, AgentHostProtocol.ErrorCodes.MalformedRequest, "closeSession requires params with a paneId."));
            }

            if (!_actEnabled)
            {
                return Journaled(request, AgentHostProtocol.Methods.CloseSession, p.PaneId, "session",
                    Error(request.Id, AgentHostProtocol.ErrorCodes.ActDisabled,
                        "Acting is disabled. Enable Settings → Agent access (observe) → Agent access (act), then retry."));
            }

            // Closing a pane is deliberately not SSH-allowlist-gated: it ends a session
            // the user can see disappear and is journaled; it cannot exfiltrate or run
            // anything. It still requires a live registration, so unknown panes 404.
            // Looked up now, before the executor tears the pane down below, so
            // there is still something to mark once the close succeeds.
            if (!_registry.TryGet(p.PaneId, out var registration))
            {
                if (_windowlessSource is { } source)
                {
                    // A windowless session's kill IS allowlist-gated on a remote endpoint (ruling R5): nobody sees it
                    // go, and it destroys a shell on another machine.
                    var check = new WindowlessActCheck(this, source);
                    var outcome = await source.KillAsync(p.PaneId, check.MayAct, cancellationToken).ConfigureAwait(false);
                    return WindowlessActed(request, AgentHostProtocol.Methods.CloseSession, p.PaneId, "session", source, check, outcome,
                        Ok(request.Id, JsonSerializer.SerializeToElement(new CloseSessionResult { Closed = true }, AgentHostJsonContext.Default.CloseSessionResult)));
                }
                return Journaled(request, AgentHostProtocol.Methods.CloseSession, p.PaneId, "session",
                    Error(request.Id, AgentHostProtocol.ErrorCodes.SessionNotFound, $"No live session with paneId '{p.PaneId}'."));
            }

            var executor = _actionExecutor;
            if (executor == null)
            {
                return Journaled(request, AgentHostProtocol.Methods.CloseSession, p.PaneId, "session",
                    Error(request.Id, AgentHostProtocol.ErrorCodes.ActUnavailable,
                        "The app cannot close sessions right now (starting up or shutting down). Retry shortly."));
            }

            bool closed = await executor.ClosePaneAsync(p.PaneId).ConfigureAwait(false);
            if (!closed)
            {
                return Journaled(request, AgentHostProtocol.Methods.CloseSession, p.PaneId, "session",
                    Error(request.Id, AgentHostProtocol.ErrorCodes.SessionNotFound, $"No live pane with paneId '{p.PaneId}' to close."));
            }

            // Use the registration captured above, not a fresh TryGet: the
            // executor may already have unregistered the pane, and the write
            // still happened to it.
            TryNoteWrote(registration, AgentHostProtocol.Methods.CloseSession);

            var dto = new CloseSessionResult { Closed = true };
            return Journaled(request, AgentHostProtocol.Methods.CloseSession, p.PaneId, "session",
                Ok(request.Id, JsonSerializer.SerializeToElement(dto, AgentHostJsonContext.Default.CloseSessionResult)));
        }

        /// <summary>
        /// Records an attempt to the journal (allowed or denied) and returns the
        /// response unchanged. Outcome = "ok" or the error code, so the user sees
        /// everything an agent tried. Every acting call goes through here, and every
        /// read of a windowless session (ruling R5: it has no pane indicator to show
        /// the read). Pane reads, <c>captureScreen</c> included, are not journaled:
        /// the pane's own indicator shows them.
        /// </summary>
        private AgentHostResponse Journaled(AgentHostRequest request, string method, Guid? paneId, string target, AgentHostResponse response, bool windowless = false)
        {
            _journal.Record(method, paneId, target, response.Error?.Code ?? "ok", windowless);
            return response;
        }

        private async Task<AgentHostResponse> HandleListSessionsAsync(AgentHostRequest request, CancellationToken cancellationToken)
        {
            SessionInfo[] sessions = _registry.ListSessions();
            if (_windowlessSource is { } source)
            {
                var windowless = await source.ListAsync(cancellationToken).ConfigureAwait(false);
                // A pane wins an id it shares with a daemon session (see the "Windowless sessions" section), so one
                // session is never listed twice.
                SessionInfo[] rows = windowless
                    .Where(s => !_registry.TryGet(s.SessionId, out _))
                    .Select(ToSessionInfo)
                    .ToArray();
                if (rows.Length > 0)
                {
                    sessions = [.. sessions, .. rows];
                    // A read, so a polling agent's listings fold into one entry rather than push acts out. It discloses the
                    // rows' titles, hosts and status, so it lights the window as every journaled read does (PR #511 review);
                    // a listing with no windowless row disclosed nothing about one, and does neither.
                    NoteWindowlessRead();
                    _journal.RecordRead(AgentHostProtocol.Methods.ListSessions, null, WindowlessTarget(null), "ok", windowless: true);
                }
            }

            var result = new ListSessionsResult { Sessions = sessions };
            return Ok(request.Id, JsonSerializer.SerializeToElement(result, AgentHostJsonContext.Default.ListSessionsResult));
        }

        private async Task<AgentHostResponse> HandleReadScreenAsync(AgentHostRequest request, CancellationToken cancellationToken)
        {
            var p = DeserializeParams(request, AgentHostJsonContext.Default.ReadScreenParams);
            if (p == null)
            {
                return Error(request.Id, AgentHostProtocol.ErrorCodes.MalformedRequest, "readScreen requires params with a paneId.");
            }
            if (!_registry.TryGet(p.PaneId, out var registration))
            {
                if (_windowlessSource is { } source)
                {
                    return await WindowlessReadScreenAsync(request, source, p, cancellationToken).ConfigureAwait(false);
                }
                return Error(request.Id, AgentHostProtocol.ErrorCodes.SessionNotFound, $"No live session with paneId '{p.PaneId}'.");
            }

            TryNoteRead(registration);
            return Ok(request.Id, ScreenResult(registration.Buffer, p.IncludeAttributes));
        }

        /// <summary>
        /// The <c>readScreen</c> result for <paramref name="buffer"/>: a pane's, or the private buffer a windowless
        /// session's screen was imported into. Read under the buffer's read lock, through the deterministic
        /// <see cref="BufferSnapshot"/> path.
        /// </summary>
        private static JsonElement ScreenResult(TerminalBuffer buffer, bool includeAttributes)
        {
            BufferSnapshot snapshot;
            bool cursorVisible;
            int rows, cols;
            buffer.Lock.EnterReadLock();
            try
            {
                snapshot = BufferSnapshot.Capture(buffer, includeAttributes);
                cursorVisible = buffer.IsCursorVisible;
                rows = buffer.Rows;
                cols = buffer.Cols;
            }
            finally
            {
                buffer.Lock.ExitReadLock();
            }

            var dto = new ScreenSnapshotDto
            {
                Lines = snapshot.Lines,
                AttributeLines = snapshot.AttributeLines,
                CursorRow = snapshot.CursorRow,
                CursorCol = snapshot.CursorCol,
                CursorVisible = cursorVisible,
                Rows = rows,
                Cols = cols,
            };
            return JsonSerializer.SerializeToElement(dto, AgentHostJsonContext.Default.ScreenSnapshotDto);
        }

        private async Task<AgentHostResponse> HandleReadScrollbackAsync(AgentHostRequest request, CancellationToken cancellationToken)
        {
            var p = DeserializeParams(request, AgentHostJsonContext.Default.ReadScrollbackParams);
            if (p == null)
            {
                return Error(request.Id, AgentHostProtocol.ErrorCodes.MalformedRequest, "readScrollback requires params with paneId, startLine, and maxLines.");
            }
            if (!_registry.TryGet(p.PaneId, out var registration))
            {
                if (_windowlessSource is { } source)
                {
                    return await WindowlessScrollbackAsync(request, source, p, cancellationToken).ConfigureAwait(false);
                }
                return Error(request.Id, AgentHostProtocol.ErrorCodes.SessionNotFound, $"No live session with paneId '{p.PaneId}'.");
            }

            TryNoteRead(registration);
            return Ok(request.Id, ScrollbackResult(registration.Buffer, p.StartLine, p.MaxLines));
        }

        /// <summary>
        /// One page of <paramref name="buffer"/>'s scrollback, oldest line = 0, as the <c>readScrollback</c> result: a
        /// pane's buffer, or a windowless session's imported one. Read under the buffer's read lock.
        /// </summary>
        private static JsonElement ScrollbackResult(TerminalBuffer buffer, int startLine, int maxLines)
        {
            string[] lines;
            int effectiveStart;
            int total;
            buffer.Lock.EnterReadLock();
            try
            {
                total = buffer.Scrollback.Count;
                effectiveStart = Math.Clamp(startLine, 0, total);
                var count = Math.Clamp(maxLines, 0, AgentHostProtocol.MaxScrollbackLinesPerRequest);
                count = Math.Min(count, total - effectiveStart);

                lines = new string[count];
                for (var i = 0; i < count; i++)
                {
                    var index = effectiveStart + i;
                    lines[i] = RenderScrollbackRow(
                        buffer.Scrollback.GetRow(index),
                        buffer.Scrollback.GetExtendedTextMap(index));
                }
            }
            finally
            {
                buffer.Lock.ExitReadLock();
            }

            var result = new ReadScrollbackResult
            {
                Lines = lines,
                StartLine = effectiveStart,
                TotalLines = total,
            };
            return JsonSerializer.SerializeToElement(result, AgentHostJsonContext.Default.ReadScrollbackResult);
        }

        // ── Windowless sessions (Phase 5 spec §3, rulings R4 and R5) ────────
        //
        // A windowless session is a multiplexer daemon session no pane of the window shows (MainWindow's
        // IWindowlessSessionSource, published with its other bridges and cleared on Stop). Its paneId on the wire is its
        // mux session id. Each per-session handler asks the registry first and comes here only for an id no pane has,
        // so a pane id never reaches the source; were a daemon session ever to share a pane's id (both are random
        // GUIDs), the pane would win.
        //
        // Reads of a windowless session are journaled, unlike pane reads, and light the window (WindowlessWatched):
        // there is no pane indicator to show them (R5). They go in as reads (RecordRead), which fold: a polling agent
        // leaves one entry per kind of read since its last act, not a ring full of them that pushes the acts out. A read
        // whose id no daemon has named no session and records nothing. Entries name the session's host
        // ("windowless · this computer"), and the dialog calls their id a session's, not a pane's.
        //
        // Acts need the act toggle and, on a remote endpoint, that SSH profile's allowlist, for input and kill alike
        // (R5). The check runs inside the source's own look-up (WindowlessActCheck), so check and act share one survey of
        // the daemons and one time budget, and it is made when the act is about to happen, seconds after the request
        // may have arrived: act turned off, or observe off (the source withdrawn), meanwhile means nothing is done.
        //
        // Known limits, by design:
        // - Scrollback is the newest MuxReadScreenLimits.MaxScrollbackRows (2000) rows the daemon holds: one read
        //   carries no more, and readScrollback pages within that read.
        // - Status is heuristic (the daemon's child-process probe and the alt screen); there is no status machine, so
        //   waitForEvents reports nothing for these sessions.
        // - Render capture borrows a pane's font metrics and theme: the session was never measured.
        // - A brand-new remote tab whose spawn is still running holds no session id yet, so for a call or so its shell
        //   can be listed (and read) as windowless.
        // - Every source call ends within the source's own budget (7 s), inside the MCP server's 10 s round trip; one
        //   that runs out is sessionNotFound, saying a daemon did not answer in time.
        //
        // The source can finish a call on the UI thread (the window says which sessions its panes show). The awaits on
        // it are ConfigureAwait(false), and the runtime never inlines such a continuation on a thread that has a
        // SynchronizationContext, as the UI thread does: the import, render and file write below run on the pool.

        /// <summary>
        /// The journal's target for a windowless session: "windowless · " and its host's display name, which comes from
        /// the user's own profile data (or "this computer"), never from a daemon. Plain "windowless" when no one host is
        /// meant (a listing) or known.
        /// </summary>
        private static string WindowlessTarget(string? hostDisplayName)
            => hostDisplayName is null ? "windowless" : $"windowless · {hostDisplayName}";

        private static SessionInfo ToSessionInfo(WindowlessSessionInfo session) => new()
        {
            PaneId = session.SessionId,
            Title = session.Title,
            ProfileName = session.HostDisplayName,
            Kind = session.SshProfileId is null ? "local" : "ssh",
            Rows = session.Rows,
            Cols = session.Cols,
            IsActive = false,
            Windowless = true,
            Endpoint = session.Endpoint,
            // No status machine: the daemon's exit is the one status known without a read.
            Status = session.Running ? null : AgentHostProtocol.StatusKinds.Exited,
            Confidence = session.Running ? null : AgentHostProtocol.StatusConfidences.Heuristic,
        };

        private async Task<AgentHostResponse> WindowlessReadScreenAsync(
            AgentHostRequest request, IWindowlessSessionSource source, ReadScreenParams p, CancellationToken cancellationToken)
        {
            var screen = await source.ReadScreenAsync(p.PaneId, 0, cancellationToken).ConfigureAwait(false);
            var (buffer, failure) = ImportScreen(request.Id, p.PaneId, AgentHostProtocol.Methods.ReadScreen, screen);
            return WindowlessRead(request, AgentHostProtocol.Methods.ReadScreen, p.PaneId, screen,
                failure ?? Ok(request.Id, ScreenResult(buffer!, p.IncludeAttributes)));
        }

        private async Task<AgentHostResponse> WindowlessScrollbackAsync(
            AgentHostRequest request, IWindowlessSessionSource source, ReadScrollbackParams p, CancellationToken cancellationToken)
        {
            var screen = await source.ReadScreenAsync(
                p.PaneId, Ntilde.Mux.Contracts.MuxReadScreenLimits.MaxScrollbackRows, cancellationToken).ConfigureAwait(false);
            var (buffer, failure) = ImportScreen(request.Id, p.PaneId, AgentHostProtocol.Methods.ReadScrollback, screen);
            return WindowlessRead(request, AgentHostProtocol.Methods.ReadScrollback, p.PaneId, screen,
                failure ?? Ok(request.Id, ScrollbackResult(buffer!, p.StartLine, p.MaxLines)));
        }

        private async Task<AgentHostResponse> WindowlessStatusAsync(
            AgentHostRequest request, IWindowlessSessionSource source, Guid sessionId, CancellationToken cancellationToken)
        {
            // The status comes with a read of the screen (the alt screen is part of it); an old daemon's comes from
            // sessionInfo instead, with no screen.
            var screen = await source.ReadScreenAsync(sessionId, 0, cancellationToken).ConfigureAwait(false);
            AgentHostResponse response;
            if (screen.Outcome is WindowlessOutcome.Ok or WindowlessOutcome.Unsupported && screen.Status is { } status)
            {
                response = Ok(request.Id, JsonSerializer.SerializeToElement(
                    WindowlessStatus(sessionId, status, screen.Snapshot?.IsAltScreenActive == true),
                    AgentHostJsonContext.Default.SessionStatusDto));
            }
            else if (screen.Outcome == WindowlessOutcome.NotRunning && screen.Session is { } session)
            {
                // Listed, then gone from its daemon before the read: it has exited.
                response = Ok(request.Id, JsonSerializer.SerializeToElement(
                    WindowlessStatus(sessionId, new MuxReadScreenResult { Running = false, ExitCode = session.ExitCode }, altScreen: false),
                    AgentHostJsonContext.Default.SessionStatusDto));
            }
            else
            {
                response = WindowlessError(request.Id, sessionId, AgentHostProtocol.Methods.GetSessionStatus,
                    screen.Outcome == WindowlessOutcome.Ok ? WindowlessOutcome.Unreachable : screen.Outcome);
            }
            return WindowlessRead(request, AgentHostProtocol.Methods.GetSessionStatus, sessionId, screen, response);
        }

        /// <summary>
        /// A windowless session's status from its daemon's facts: exited when it is not running; running while it has
        /// child processes or a full-screen app (the alt screen) up; otherwise at a prompt. Always heuristic, never
        /// stalled, and both timestamps are the daemon's last output (0 before any).
        /// </summary>
        internal static SessionStatusDto WindowlessStatus(Guid sessionId, MuxReadScreenResult status, bool altScreen)
        {
            long lastOutputMs = status.LastOutputUnixMs ?? 0;
            return new SessionStatusDto
            {
                PaneId = sessionId,
                Status = !status.Running
                    ? AgentHostProtocol.StatusKinds.Exited
                    : status.HasActiveChildProcesses || altScreen
                        ? AgentHostProtocol.StatusKinds.Running
                        : AgentHostProtocol.StatusKinds.AwaitingInput,
                Confidence = AgentHostProtocol.StatusConfidences.Heuristic,
                ExitCode = status.ExitCode,
                StatusSinceMs = lastOutputMs,
                LastOutputAtMs = lastOutputMs,
                IsStalled = false,
                StallThresholdSeconds = AgentSessionStatusMachine.StallThresholdSeconds,
                IdleThresholdSeconds = AgentSessionStatusMachine.IdleThresholdSeconds,
            };
        }

        private async Task<AgentHostResponse> WindowlessCaptureAsync(
            AgentHostRequest request, IWindowlessSessionSource source, CaptureScreenParams p, bool live, int maxWidth, double scale,
            CancellationToken cancellationToken)
        {
            const string method = AgentHostProtocol.Methods.CaptureScreen;
            var inputs = live ? null : BorrowRenderInputs();
            if (inputs is not { } render)
            {
                // Nothing will be drawn, so nothing is read: the look-up alone says whether there is such a session.
                var (outcome, sshProfileId) = await source.ResolveAsync(p.PaneId, cancellationToken).ConfigureAwait(false);
                if (outcome != WindowlessOutcome.Ok)
                {
                    return WindowlessError(request.Id, p.PaneId, method, outcome);
                }
                return JournaledRead(method, p.PaneId, source.HostDisplayName(sshProfileId), Error(request.Id, AgentHostProtocol.ErrorCodes.CaptureUnavailable, live
                    ? "A windowless session has no window to capture. Use mode 'render', which draws it from its screen."
                    : "There is no open pane to take font metrics from, so this windowless session cannot be rendered. Open a tab, then retry; ntilde.read_screen works regardless."));
            }

            var screen = await source.ReadScreenAsync(p.PaneId, 0, cancellationToken).ConfigureAwait(false);
            var (buffer, failure) = ImportScreen(request.Id, p.PaneId, method, screen, render.Theme);
            if (failure != null)
            {
                return WindowlessRead(request, method, p.PaneId, screen, failure);
            }

            if (!AgentSessionRegistration.TryRenderPng(buffer!, render.Parameters, maxWidth, scale, out var capture, out var captureError))
            {
                var message = captureError == AgentCaptureError.TooLarge
                    ? $"This session would render larger than the {AgentHostProtocol.MaxCapturePixels:N0}-pixel per-capture budget at scale {scale:0.##}. Lower the scale."
                    : "This session's screen could not be rendered right now. Retry shortly; ntilde.read_screen works regardless.";
                return WindowlessRead(request, method, p.PaneId, screen, Error(request.Id, AgentHostProtocol.ErrorCodes.CaptureUnavailable, message));
            }

            return WindowlessRead(request, method, p.PaneId, screen, CaptureResult(
                request, capture.Png, capture.Width, capture.Height, capture.Cols, capture.Rows, capture.Downscaled,
                live: false, scale, p.Inline));
        }

        /// <summary>
        /// What a windowless render borrows, since the session was never measured: the active pane's font metrics and
        /// theme, else any measured pane's. Null when no pane has been measured.
        /// </summary>
        private (PaneRenderParameters Parameters, TerminalTheme Theme)? BorrowRenderInputs()
        {
            PaneRenderParameters? borrowed = null;
            TerminalBuffer? themeFrom = null;
            foreach (var registration in _registry.GetRegistrations())
            {
                if (registration.RenderParameters is not { IsUsable: true } parameters) continue;
                borrowed = parameters;
                themeFrom = registration.Buffer;
                if (registration.IsActive) break;
            }
            if (borrowed is not { } found || themeFrom is null) return null;

            TerminalTheme theme;
            themeFrom.Lock.EnterReadLock();
            try
            {
                theme = themeFrom.Theme;
            }
            finally
            {
                themeFrom.Lock.ExitReadLock();
            }
            return (found, theme);
        }

        /// <summary>
        /// A read's screen in a private buffer of its own, or the response saying why there is none. The buffer takes
        /// <paramref name="theme"/> (a render's borrowed one) before the import.
        /// </summary>
        private static (TerminalBuffer? Buffer, AgentHostResponse? Failure) ImportScreen(
            long requestId, Guid sessionId, string method, WindowlessScreen screen, TerminalTheme? theme = null)
        {
            if (screen.Outcome != WindowlessOutcome.Ok || screen.Snapshot is not { } snapshot)
            {
                var outcome = screen.Outcome == WindowlessOutcome.Ok ? WindowlessOutcome.Unreachable : screen.Outcome;
                return (null, WindowlessError(requestId, sessionId, method, outcome));
            }

            try
            {
                var buffer = new TerminalBuffer(snapshot.Cols, snapshot.Rows);
                if (theme != null)
                {
                    buffer.Theme = theme;
                }
                buffer.ImportState(snapshot);
                return (buffer, null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AgentHost] windowless screen of {sessionId} could not be imported: {ex}");
                return (null, Error(requestId, AgentHostProtocol.ErrorCodes.Internal,
                    $"The daemon's copy of this session's screen could not be read: {ex.Message}"));
            }
        }

        /// <summary>
        /// The end of every windowless read: journaled when the source found the session (R5), whatever the outcome,
        /// and lighting the window when the read succeeded. A read of an id no daemon has named no session, and
        /// records nothing.
        /// </summary>
        private AgentHostResponse WindowlessRead(AgentHostRequest request, string method, Guid sessionId, WindowlessScreen screen, AgentHostResponse response)
        {
            if (screen.Session is not { } session)
            {
                return response;
            }
            return JournaledRead(method, sessionId, session.HostDisplayName, response);
        }

        /// <summary>
        /// Records a read of a windowless session as a read (folding with its like since the last act), and lights the
        /// window when it disclosed the session's content.
        /// </summary>
        private AgentHostResponse JournaledRead(string method, Guid sessionId, string? hostDisplayName, AgentHostResponse response)
        {
            if (response.Error is null)
            {
                NoteWindowlessRead();
            }
            _journal.RecordRead(method, sessionId, WindowlessTarget(hostDisplayName), response.Error?.Code ?? "ok", windowless: true);
            return response;
        }

        /// <summary>
        /// The act check a windowless act hands its source (R5), and what it saw. The source asks it at the end of its
        /// own look-up, which can take seconds (its survey of the daemons), so it reads the gates then rather than when
        /// the request arrived: the act toggle must still be on, and the source still the one the window publishes
        /// (observe off, or the window closing, withdraws it). Then a session on this computer needs nothing more; one on
        /// an SSH host needs that profile allowlisted, as an SSH pane does.
        /// <para>
        /// The source calls <see cref="MayAct"/> synchronously on whichever thread it resumed on, the UI thread possibly.
        /// It reads two volatile fields and, for an SSH host, the allowlist probe (MainWindow.IsSshProfileAgentAllowed),
        /// which reads the SSH profile store under the store's own lock around a small file read: fast, and it never waits
        /// on the dispatcher. It and <see cref="AllowsAgentActOnProfile"/> fail closed. What it records is read by the
        /// handler after it awaited the source, which orders the two.
        /// </para>
        /// </summary>
        private sealed class WindowlessActCheck(AgentHostService service, IWindowlessSessionSource source)
        {
            /// <summary>True once the source found the session and asked: the act named a windowless session.</summary>
            public bool Asked { get; private set; }

            /// <summary>The session's SSH profile id, null for this computer; meaningful once <see cref="Asked"/>.</summary>
            public Guid? SshProfileId { get; private set; }

            /// <summary>The error code a refusal answers with; null when allowed, or not asked.</summary>
            public string? Refusal { get; private set; }

            public bool MayAct(Guid? sshProfileId)
            {
                Asked = true;
                SshProfileId = sshProfileId;
                Refusal = !service._actEnabled ? AgentHostProtocol.ErrorCodes.ActDisabled
                    : !ReferenceEquals(service._windowlessSource, source) ? AgentHostProtocol.ErrorCodes.ActUnavailable
                    : sshProfileId is { } profileId && !service.AllowsAgentActOnProfile(profileId) ? AgentHostProtocol.ErrorCodes.ProfileNotAllowed
                    : null;
                return Refusal is null;
            }
        }

        /// <summary>
        /// The end of a windowless act: its response, journaled as an act. An act the source took on is named by its
        /// session's host; one on an id no daemon has keeps the pane path's <paramref name="notFoundTarget"/>.
        /// </summary>
        private AgentHostResponse WindowlessActed(
            AgentHostRequest request, string method, Guid sessionId, string notFoundTarget, IWindowlessSessionSource source,
            WindowlessActCheck check, WindowlessOutcome outcome, AgentHostResponse success)
        {
            var response = outcome switch
            {
                WindowlessOutcome.Ok => success,
                WindowlessOutcome.NotAllowed => WindowlessRefusal(request.Id, check.Refusal),
                _ => WindowlessError(request.Id, sessionId, method, outcome, reached: check.Asked),
            };
            string target = check.Asked ? WindowlessTarget(source.HostDisplayName(check.SshProfileId)) : notFoundTarget;
            return Journaled(request, method, sessionId, target, response, windowless: check.Asked);
        }

        /// <summary>A windowless act its check refused, by the gate that refused it.</summary>
        private static AgentHostResponse WindowlessRefusal(long requestId, string? refusal) => refusal switch
        {
            AgentHostProtocol.ErrorCodes.ActDisabled => Error(requestId, AgentHostProtocol.ErrorCodes.ActDisabled,
                "Acting was turned off while this request was on its way, so nothing was done. Enable Settings → Agent access (observe) → Agent access (act), then retry."),
            AgentHostProtocol.ErrorCodes.ActUnavailable => Error(requestId, AgentHostProtocol.ErrorCodes.ActUnavailable,
                "Agent access was turned off, or the window is closing, while this request was on its way, so nothing was done."),
            _ => Error(requestId, AgentHostProtocol.ErrorCodes.ProfileNotAllowed, NotAllowedMessage),
        };

        private const string NotAllowedMessage =
            "This windowless session runs on an SSH host whose profile is not allowlisted for agent access. Enable it in the connection's settings, then retry.";

        /// <summary>
        /// A windowless call's failure as the protocol error the agent sees. <paramref name="reached"/>: the source found
        /// the session before the call failed, which tells an act that its daemon was reached and then did not do it.
        /// Every outcome has its own arm; one this does not know throws, which the request loop answers as
        /// <c>internal</c>, so a new outcome can never pass for <c>sessionNotFound</c> unnoticed.
        /// </summary>
        private static AgentHostResponse WindowlessError(long requestId, Guid sessionId, string method, WindowlessOutcome outcome, bool reached = false) => outcome switch
        {
            WindowlessOutcome.NotFound => Error(requestId, AgentHostProtocol.ErrorCodes.SessionNotFound, $"No live session with paneId '{sessionId}'."),
            WindowlessOutcome.Unsupported => Error(requestId, AgentHostProtocol.ErrorCodes.Unsupported,
                "The multiplexer daemon that holds this session is too old to read its screen. Its status is still available from ntilde.get_session_status; a daemon started by a newer ntilde can read it."),
            WindowlessOutcome.NotRunning => Error(requestId, AgentHostProtocol.ErrorCodes.SessionNotRunning, method switch
            {
                AgentHostProtocol.Methods.SendInput => "The session is not accepting input (its process has exited).",
                AgentHostProtocol.Methods.CloseSession => "The session has already exited, so there is nothing to close.",
                _ => "The session has ended, and its daemon no longer holds its screen.",
            }),
            WindowlessOutcome.NotAllowed => Error(requestId, AgentHostProtocol.ErrorCodes.ProfileNotAllowed, NotAllowedMessage),
            WindowlessOutcome.Unreachable => Error(requestId, AgentHostProtocol.ErrorCodes.SessionNotFound, (reached, method) switch
            {
                (true, AgentHostProtocol.Methods.CloseSession) =>
                    "The session's daemon did not confirm the kill in time, so the session may already have ended. List sessions to check.",
                (true, AgentHostProtocol.Methods.SendInput) =>
                    "The connection to the session's daemon closed before the input could go, so nothing was sent. Retry shortly.",
                _ => $"No session with paneId '{sessionId}' could be reached: a multiplexer daemon did not answer in time. Retry shortly.",
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "The agent host has no error for this windowless outcome."),
        };

        /// <summary>
        /// Same text semantics as <see cref="BufferSnapshot.Capture"/>: skip wide
        /// continuations, prefer the row's extended text (emoji, multi-codepoint
        /// graphemes), NUL → space, trim right.
        /// </summary>
        private static string RenderScrollbackRow(ReadOnlySpan<TerminalCell> cells, Ntilde.VT.Storage.SmallMap<string>? extendedText)
        {
            var sb = new StringBuilder(cells.Length);
            for (var col = 0; col < cells.Length; col++)
            {
                ref readonly var cell = ref cells[col];
                if (cell.IsWideContinuation) continue;

                if (extendedText != null && extendedText.TryGet(col, out var ext) && ext != null)
                {
                    sb.Append(ext);
                }
                else
                {
                    sb.Append(cell.Character == '\0' ? ' ' : cell.Character);
                }
            }
            return sb.ToString().TrimEnd();
        }

        private static T? DeserializeParams<T>(AgentHostRequest request, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
            where T : class
        {
            return request.Params is { } element ? element.Deserialize(typeInfo) : null;
        }

        private static AgentHostResponse Ok(long id, JsonElement result) => new()
        {
            Version = AgentHostProtocol.Version,
            Id = id,
            Result = result,
        };

        private static AgentHostResponse Error(long id, string code, string message) => new()
        {
            Version = AgentHostProtocol.Version,
            Id = id,
            Error = new AgentHostError { Code = code, Message = message },
        };
    }
}
