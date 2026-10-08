using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Ntilde.Mux.Contracts;
using Ntilde.Pty;
using Ntilde.Replay;
using Ntilde.VT;

namespace Ntilde.Mux;

/// <summary>
/// The authoritative side of one multiplexed terminal: a real session plus the headless parser and
/// buffer its raw bytes drive. Clients are downstream copies kept equal by snapshot + ordered events.
/// </summary>
/// <remarks>
/// <para>
/// <b>One parse thread.</b> Everything that touches <see cref="Buffer"/> or <see cref="Parser"/> runs
/// on <c>MuxParse-{id}</c>, which drains two queues with <c>TakeFromAny(control, data)</c>. Control
/// items (resize, presentation, attach, detach, invoke) outrank data and therefore always run
/// <em>between</em> <c>Process()</c> calls; data items (chunks, exit, flush barriers) keep arrival
/// order. Never the thread pool: see the #81 history in RustPtySession.
/// </para>
/// <para>
/// <b>Stream offset.</b> <see cref="StreamPosition"/> counts raw bytes fed to the decoder. An Output
/// frame carries the offset before its chunk; a ResizeEvent the offset it applies at; a snapshot
/// <c>StreamSeq = ConsumedBytes</c> with <c>StreamSeq + DecoderTail.Length == StreamPosition</c>.
/// </para>
/// <para>
/// <b>The mux answers device queries</b>: <c>OnResponse</c> goes to the session and nowhere else.
/// </para>
/// </remarks>
public sealed class HeadlessTerminalSession : IDisposable
{
    internal const int DataQueueCapacity = 256;

    private readonly ITerminalSession _session;
    private readonly ITerminalByteOutput _byteOutput;
    private readonly TerminalBuffer _buffer;
    private readonly AnsiParser _parser;
    private readonly Utf8ChunkDecoder _decoder = new();
    private readonly BlockingCollection<WorkItem> _control = new();
    private readonly BlockingCollection<WorkItem> _data = new(DataQueueCapacity);
    private readonly BlockingCollection<WorkItem>[] _queues;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _parseThread;
    private readonly Action<ReadOnlyMemory<byte>> _onRawOutput;
    private readonly Action<string> _onStringOutput = static _ => { };
    private readonly Action<int> _onExit;
    private readonly Action<string>? _log;
    private readonly List<IMuxFrameSink> _subscribers = new(); // parse thread only
    private readonly HashSet<IMuxFrameSink> _readOnlySinks = new(); // parse thread only: subscribers attached ReadOnly
    private readonly Dictionary<IMuxFrameSink, long> _attachRequestIds = new(); // parse thread only: each subscriber's latest successful attach

    // Parse thread only: what each interactive subscriber last presented as KittyKeyboardEnabled. The
    // parser's flag is the AND over these (Phase 4 spec §4 item 5); see RecomputeKitty.
    private readonly Dictionary<IMuxFrameSink, bool> _kittyBySink = new();
    private char[] _chars = new char[Utf8ChunkDecoder.GetMaxCharCount(4096)];
    private long _rawOffset;
    private long _lastOutputUnixMs; // 0 until the first chunk; written by the parse thread only, once per chunk
    private string _title;
    private string? _cwd;
    private int _cols;
    private int _rows;
    private int _attached;
    private int _interactive;
    private int _exited;
    private int _exitCode;
    private int _faulted;
    private int _disposed;
    private int _unsubscribed;
    private int _detachedByUser; // written on the parse thread only

    // Parse thread only: the interactive subscriber whose drop for refusing a frame left no interactive
    // subscriber, with its latest attach, until its own detach says what kind of departure that was
    // (spec §7.7). See DropRefusingSink and DecideForDroppedSink.
    // Accepted residual: a sink dropped this way stops counting as an interactive subscriber at once,
    // before its detach has run. When two interactive clients leave within one parse item (one is
    // dropped while the other's detach is still queued), the decision goes to whichever departure the
    // session processes last. That need not match the order in which the two detaches were queued.
    private (IMuxFrameSink Sink, long AttachRequestId)? _undecidedDrop;

    // sessionChanged coalescing (spec §4), parse thread only: at most one per interval, the trailing
    // one sent by the parse loop's own bounded wait (see ParseLoop).
    private readonly long _sessionChangedIntervalMs;
    private bool _sessionChangePending;
    private long _lastSessionChangeSentMs = long.MinValue / 2;
    private int _lastPublishedAttached;
    private int _lastPublishedInteractive;

    // Kill(by, kind): written before the child is disposed, read by ProcessExit on the parse thread.
    private IMuxFrameSink? _killedBy;
    private string _killedByKind = string.Empty;
    private int _killRequested;

    // Input writer (spec §4): SendInput runs on the connection reader thread; a child that stops
    // reading stdin would otherwise stall every session sharing that connection. One thread per
    // session keeps per-session order; the byte cap keeps a wedged child from growing the queue
    // without bound. Never the thread pool (a blocked write would pin a pool thread).
    private readonly BlockingCollection<string> _input = new();
    private readonly Thread _inputThread;
    private readonly long _maxQueuedInputBytes;
    private long _queuedInputBytes;
    private long _exitedAtMs;
    private int _inputDropLogged;

    // PostResize coalescing (any thread -> parse thread); see PostResize.
    private readonly object _pendingResizeGate = new();
    private (int Cols, int Rows)? _pendingResize;
    private MuxPresentation? _pendingPresentation;
    private Dictionary<IMuxFrameSink, bool>? _pendingKitty; // each sender's newest kitty flag in the pending burst
    private bool _resizeQueued;

    /// <summary>Tests only: raised with the session id once the input writer thread has started.</summary>
    internal static event Action<Guid, Thread>? InputThreadStartedForTest;

    public HeadlessTerminalSession(Guid id, ITerminalSession session, HeadlessSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        _byteOutput = session as ITerminalByteOutput ?? throw new ArgumentException(
            $"{session.GetType().Name} does not implement {nameof(ITerminalByteOutput)}; the mux needs the raw bytes.",
            nameof(session));

        Id = id;
        _session = session;
        Command = options.Command;
        Arguments = options.Arguments;
        ForceConPtyFiltering = options.ForceConPtyFiltering;
        _title = options.Title;
        _log = options.Log;
        _cols = options.Cols;
        _rows = options.Rows;
        _maxQueuedInputBytes = options.MaxQueuedInputBytes;
        _buffer = new TerminalBuffer(options.Cols, options.Rows);
        _parser = new AnsiParser(_buffer, options.ForceConPtyFiltering)
        {
            ImageDecoder = null,
            AllowNativeKittyGraphics = false,
        };
        _parser.OnResponse = EnqueueInput; // through the writer too: the parse thread must never block on stdin
        _sessionChangedIntervalMs = (long)Math.Max(0, options.SessionChangedInterval.TotalMilliseconds);
        _parser.OnTitleChanged = title =>
        {
            if (title == _title) return; // a shell re-sending its title at every prompt is no change
            Volatile.Write(ref _title, title);
            MarkSessionChanged();
        };
        _parser.OnWorkingDirectoryChanged = cwd =>
        {
            if (cwd == _cwd) return;
            Volatile.Write(ref _cwd, cwd);
            MarkSessionChanged();
        };
        _queues = [_control, _data];
        _onRawOutput = OnRawOutput;
        _onExit = code => TryEnqueue(_data, WorkItem.ForExit(code));

        // The thread first: subscribing replays whatever arrived since spawn straight into the
        // bounded data queue, which would block this constructor if nobody were draining it.
        _parseThread = new Thread(ParseLoop) { IsBackground = true, Name = $"MuxParse-{id:N}" };
        _parseThread.Start();
        _inputThread = new Thread(InputLoop) { IsBackground = true, Name = $"MuxInput-{id:N}" };
        _inputThread.Start();
        InputThreadStartedForTest?.Invoke(id, _inputThread);

        // Raw FIRST, then string. The string subscription is a no-op whose only job is to release
        // the session's own string replay buffer (nothing else ever subscribes to it here, so it
        // would grow for the session's lifetime) - and subscribing it first would make
        // RawOutputTap drop the retained startup bytes.
        try
        {
            _byteOutput.OnRawOutputReceived += _onRawOutput;
            _session.OnOutputReceived += _onStringOutput;
            _session.OnExit += _onExit;

            // The factory hands over an already-running session, and a short command can be gone
            // before the line above ran - RustPtySession does not replay OnExit to a late
            // subscriber. Reconcile once, after subscribing: queued behind the replayed output, so
            // clients still see the last bytes before Exited. If OnExit did fire too, the second
            // exit item is ignored.
            if (!_session.IsProcessRunning) TryEnqueue(_data, WorkItem.ForExit(_session.ExitCode));
        }
        catch
        {
            // The parse and input threads are already running: stop them (and drop whatever
            // subscriptions did succeed) so a constructor that throws leaves no thread behind. The
            // caller still owns the session and disposes it.
            Volatile.Write(ref _disposed, 1);
            _cts.Cancel();
            _input.CompleteAdding();
            try { Unsubscribe(); }
            catch (Exception ex) { Log($"[Mux] session {id}: unsubscribing after a failed construction threw: {ex.Message}"); }
            _parseThread.Join(TimeSpan.FromSeconds(5));
            _inputThread.Join(TimeSpan.FromSeconds(5));
            throw;
        }
    }

    public Guid Id { get; }
    public string Title => Volatile.Read(ref _title);

    /// <summary>The last OSC 7 directory the mux parser saw; null until the shell reports one.</summary>
    public string? Cwd => Volatile.Read(ref _cwd);

    public string Command { get; }
    public string? Arguments { get; }
    public bool ForceConPtyFiltering { get; }
    public int Cols => Volatile.Read(ref _cols);
    public int Rows => Volatile.Read(ref _rows);
    public int AttachedClients => Volatile.Read(ref _attached);

    /// <summary><see cref="AttachedClients"/> without the read-only observers.</summary>
    public int InteractiveClients => Volatile.Read(ref _interactive);
    public bool IsExited => Volatile.Read(ref _exited) != 0;
    public int? ExitCode => IsExited ? Volatile.Read(ref _exitCode) : null;
    public bool IsFaulted => Volatile.Read(ref _faulted) != 0;
    public long StreamPosition => Interlocked.Read(ref _rawOffset);

    /// <summary>
    /// Wall-clock time (Unix milliseconds) the parse thread last took a chunk of the child's output; null before the
    /// first. Stamped once per chunk, not per byte.
    /// </summary>
    public long? LastOutputUnixMs
    {
        get
        {
            long ms = Volatile.Read(ref _lastOutputUnixMs);
            return ms == 0 ? null : ms;
        }
    }

    /// <summary>
    /// The detach that left the session with no interactive subscribers was a user detach (spec §7.7);
    /// the next interactive attach clears it. Read-only observers neither set nor clear it.
    /// </summary>
    public bool DetachedByUser => Volatile.Read(ref _detachedByUser) != 0;

    /// <summary><see cref="Environment.TickCount64"/> when the mux saw the exit; 0 while running. Set before <see cref="IsExited"/>.</summary>
    public long ExitedAtMs => Interlocked.Read(ref _exitedAtMs);

    internal ITerminalSession Inner => _session;

    /// <summary>Tests only, and only while the parse thread is idle (after <see cref="FlushAsync"/>).</summary>
    internal TerminalBuffer Buffer => _buffer;

    /// <inheritdoc cref="Buffer"/>
    internal AnsiParser Parser => _parser;

    /// <summary>Tests: the parser's kitty keyboard flag, read on the parse thread after every control item queued before it.</summary>
    internal Task<bool> KittyKeyboardEnabled => InvokeAsync(() => _parser.KittyKeyboardEnabled);

    internal int QueuedDataCount => _data.Count;

    internal int QueuedControlCount => _control.Count;

    /// <summary>Bytes (UTF-16) queued for the input writer or being written by it.</summary>
    internal long QueuedInputBytes => Interlocked.Read(ref _queuedInputBytes);

    /// <summary>Tests only: overrides <see cref="HeadlessSessionOptions.MaxQueuedInputBytes"/> when positive.</summary>
    internal long MaxQueuedInputBytesForTest { get; set; }

    internal bool IsInputThreadAliveForTest => _inputThread.IsAlive;

    internal bool IsParseThreadAliveForTest => _parseThread.IsAlive;

    internal bool IsDisposedForTest => Volatile.Read(ref _disposed) != 0;

    private long MaxQueuedInputBytes => MaxQueuedInputBytesForTest > 0 ? MaxQueuedInputBytesForTest : _maxQueuedInputBytes;

    /// <summary>
    /// Thread-safe and non-blocking: queued for this session's input writer (input is independent
    /// of the output stream). Past <see cref="HeadlessSessionOptions.MaxQueuedInputBytes"/> of
    /// unwritten input, further input is dropped - the child has stopped reading stdin.
    /// </summary>
    public void SendInput(string text)
    {
        if (IsExited || Volatile.Read(ref _disposed) != 0 || string.IsNullOrEmpty(text)) return;
        EnqueueInput(text);
    }

    private void EnqueueInput(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        long bytes = (long)text.Length * sizeof(char);
        if (Interlocked.Add(ref _queuedInputBytes, bytes) > MaxQueuedInputBytes)
        {
            Interlocked.Add(ref _queuedInputBytes, -bytes);
            // Once per stall, not per keystroke: a client typing into a wedged child would flood the log.
            if (Interlocked.Exchange(ref _inputDropLogged, 1) == 0)
            {
                Log($"[Mux] session {Id}: dropping input ({text.Length} chars, and any more until the queue drains); the child is not reading stdin.");
            }

            return;
        }

        try
        {
            // Unbounded, so Add never blocks and there is nothing to cancel; teardown is signalled by
            // CompleteAdding/Dispose, caught below.
            _input.Add(text, CancellationToken.None);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // CompleteAdding (disposing) or already disposed: the session is going away.
            Interlocked.Add(ref _queuedInputBytes, -bytes);
        }
    }

    /// <summary>
    /// The only caller of the child's SendInput. A throw is logged and the loop goes on: one failed
    /// write must not silently end input for the rest of the session's life.
    /// </summary>
    private void InputLoop()
    {
        try
        {
            foreach (string text in _input.GetConsumingEnumerable(_cts.Token))
            {
                try
                {
                    _session.SendInput(text);
                    Volatile.Write(ref _inputDropLogged, 0);
                }
                catch (Exception ex)
                {
                    Log($"[Mux] session {Id}: SendInput failed: {ex.Message}");
                }
                finally
                {
                    // Charged until the write returns (PR #489 follow-up): a write blocked on a child
                    // that stopped reading is still input the child has not taken, and must count
                    // against the cap - un-charging it on take let one more cap's worth queue behind it.
                    Interlocked.Add(ref _queuedInputBytes, -(long)text.Length * sizeof(char));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose, a failed constructor or the terminal exit cancelled the token: the normal end.
        }
    }

    /// <summary>
    /// Latest request wins. Presentation (if any) is applied first, so an in-band report uses the
    /// new cell size.
    /// </summary>
    /// <remarks>
    /// Coalesced: resize requests carry no reply, so nothing throttles a peer that sends them faster
    /// than the parse thread applies them. Only the newest size matters, so a burst collapses into a
    /// single queued item that applies whatever is pending when it runs - never an unbounded backlog
    /// of stale resizes starving output. The newest presentation any coalesced request carried is
    /// kept, since a later size-only request must not discard it.
    /// <para>
    /// No client is named here (in-process callers), so the presentation's kitty flag takes no part in
    /// the AND over interactive clients; see the overload that names the sender.
    /// </para>
    /// </remarks>
    public void PostResize(int cols, int rows, MuxPresentation? presentation) => PostResize(from: null, cols, rows, presentation);

    /// <summary>
    /// <paramref name="from"/> is the client that sent the resize. Its presentation's kitty flag is
    /// that client's own (Phase 4 spec §4 item 5), so a burst keeps each sender's newest one: latest
    /// wins only for the size and the session-wide fields.
    /// </summary>
    internal void PostResize(IMuxFrameSink? from, int cols, int rows, MuxPresentation? presentation)
    {
        lock (_pendingResizeGate)
        {
            _pendingResize = (cols, rows);
            if (presentation is not null)
            {
                _pendingPresentation = presentation;
                if (from is not null) (_pendingKitty ??= new())[from] = presentation.KittyKeyboardEnabled;
            }

            if (_resizeQueued) return;
            _resizeQueued = true;
        }

        EnqueueControl(ApplyPendingResize);
    }

    private void ApplyPendingResize()
    {
        (int Cols, int Rows)? size;
        MuxPresentation? presentation;
        Dictionary<IMuxFrameSink, bool>? kitty;
        lock (_pendingResizeGate)
        {
            size = _pendingResize;
            presentation = _pendingPresentation;
            kitty = _pendingKitty;
            _pendingResize = null;
            _pendingPresentation = null;
            _pendingKitty = null;
            _resizeQueued = false;
        }

        if (presentation is not null) ApplyPresentation(presentation);
        if (kitty is not null)
        {
            foreach ((IMuxFrameSink sink, bool enabled) in kitty) RecordKitty(sink, enabled);
            RecomputeKitty();
        }

        if (size is { } s) ApplyResize(s.Cols, s.Rows);
    }

    internal void PostAttach(IMuxFrameSink sink, long requestId, int maxScrollbackRows, MuxPresentation presentation, int maxSnapshotBytes, MuxAttachMode mode = MuxAttachMode.Shared)
    {
        // Unlike PostResize/PostDetach, a dropped attach must still answer: the sink is a client
        // waiting on this specific requestId, and silence would leave it hung rather than told the
        // session is gone. OnDropped runs whether TryEnqueue refuses synchronously (below) or the
        // item is later found undelivered by DrainAfterStop.
        var item = WorkItem.ForAction(
            () => ExecuteAttach(sink, requestId, maxScrollbackRows, presentation, maxSnapshotBytes, mode),
            onDropped: () => ReplySessionExited(sink, requestId));
        if (!TryEnqueue(_control, item)) item.OnDropped!();
    }

    /// <summary>
    /// Phase 5 <c>readScreen</c>: the screen is captured on the parse thread, which also answers
    /// <paramref name="requestId"/> on <paramref name="sink"/>, as an attach does, but nothing is subscribed. Like an
    /// attach it is always answered: an item dropped because the session stopped gets <c>session_exited</c>.
    /// <paramref name="hasActiveChildProcesses"/> is probed by the caller, off this thread.
    /// </summary>
    internal void PostReadScreen(IMuxFrameSink sink, long requestId, int maxScrollbackRows, int maxSnapshotBytes, bool hasActiveChildProcesses)
    {
        var item = WorkItem.ForAction(
            () => ExecuteReadScreen(sink, requestId, maxScrollbackRows, maxSnapshotBytes, hasActiveChildProcesses),
            onDropped: () => Reply(sink, requestId, MuxErrorCodes.SessionExited, $"Session {Id} has exited; its screen cannot be read."));
        if (!TryEnqueue(_control, item)) item.OnDropped!();
    }

    /// <summary>
    /// <paramref name="attachRequestId"/> names the attach this detach undoes; if the sink has since
    /// subscribed through a newer attach, the detach is stale and ignored. Decided here, not on the
    /// connection: only the parse thread knows which attaches took effect, and a newer attach that was
    /// refused or failed must not supersede anything.
    /// </summary>
    internal void PostDetach(IMuxFrameSink sink, bool userDetached = false, long? attachRequestId = null) =>
        EnqueueControl(() =>
        {
            if (attachRequestId is long undone && _attachRequestIds.TryGetValue(sink, out long latest) && latest > undone) return;
            if (!_subscribers.Remove(sink))
            {
                DecideForDroppedSink(sink, userDetached, attachRequestId);
                return;
            }

            bool wasReadOnly = _readOnlySinks.Contains(sink);
            Forget(sink);

            // Only the detach that leaves no interactive subscriber decides; a connection closing is
            // never a user detach, and a read-only observer leaving decides nothing (spec §7.7).
            if (!wasReadOnly && !HasOtherInteractiveSubscriber(sink)) Volatile.Write(ref _detachedByUser, userDetached ? 1 : 0);
            PublishAttachedCount();
        });

    internal Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = WorkItem.ForAction(
            () =>
            {
                try { tcs.TrySetResult(func()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            },
            onDropped: () => tcs.TrySetException(new ObjectDisposedException(nameof(HeadlessTerminalSession))));
        if (!TryEnqueue(_control, item)) item.OnDropped!();
        return tcs.Task;
    }

    /// <summary>Completes once every data item queued before it (chunks, exit) has been processed.</summary>
    internal Task FlushAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = WorkItem.ForAction(() => tcs.TrySetResult(), onDropped: () => tcs.TrySetResult());
        if (!TryEnqueue(_data, item)) tcs.TrySetResult();
        return tcs.Task;
    }

    /// <summary>
    /// Ends the session. Clients learn it through Exited, in stream order after every byte already
    /// queued; then the parse thread stops and unsubscribes. No client is named (the shutdown path),
    /// so no <c>killed</c> is sent.
    /// </summary>
    public void Kill() => Kill(by: null, byClientKind: null);

    /// <summary>
    /// A client killed it: the other v2 subscribers get <c>killed</c> before <c>exited</c> (spec §5).
    /// The killer is recorded BEFORE the child is disposed, because the child's own OnExit may be the
    /// exit item that reaches the parse thread first.
    /// </summary>
    internal void Kill(IMuxFrameSink? by, string? byClientKind)
    {
        if (byClientKind is not null)
        {
            Volatile.Write(ref _killedBy, by);
            Volatile.Write(ref _killedByKind, byClientKind);
            Volatile.Write(ref _killRequested, 1);
        }

        try { _session.Dispose(); }
        catch (Exception ex) { Log($"[Mux] session {Id}: disposing the child failed: {ex.Message}"); }
        TryEnqueue(_data, WorkItem.ForExit(null, terminal: true));
    }

    internal TerminalStateSnapshot CaptureSnapshot(int maxScrollbackRows)
    {
        if (Thread.CurrentThread != _parseThread)
        {
            throw new InvalidOperationException("CaptureSnapshot must run on the session's parse thread; post it with InvokeAsync.");
        }

        TerminalStateSnapshot snapshot = _buffer.ExportState(maxScrollbackRows);
        snapshot.Parser = _parser.ExportState();
        snapshot.DecoderTail = _decoder.PendingTail.ToArray();
        snapshot.StreamSeq = _decoder.ConsumedBytes;
        Debug.Assert(snapshot.StreamSeq + snapshot.DecoderTail.Length == Interlocked.Read(ref _rawOffset),
            "decoder position and raw offset disagree: some byte bypassed the decoder");
        return snapshot;
    }

    internal ExportFlightResult ExportFlight()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ntilde-mux-flight-{Guid.NewGuid():N}.rec");
        try
        {
            if (!_session.TryExportFlightRecording(path, out FlightExportInfo info))
            {
                return new ExportFlightResult { Exported = false };
            }

            return new ExportFlightResult
            {
                Exported = true,
                Bytes = File.ReadAllBytes(path),
                EventCount = info.EventCount,
                FirstEventMs = info.FirstEventMs,
                LastEventMs = info.LastEventMs,
                TruncatedAtStart = info.TruncatedAtStart,
            };
        }
        finally
        {
            // Best-effort cleanup of our own temp file: the export already succeeded or failed on
            // its own terms, and a leftover file in %TEMP% is not worth failing the reply over.
            try { File.Delete(path); }
            catch (IOException) { /* in use or already gone - leave it to the OS temp sweep */ }
            catch (UnauthorizedAccessException) { /* same: cleanup is best-effort */ }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Cancel FIRST: a producer parked in _data.Add holds RawOutputTap's lock while it waits, and
        // the unsubscribe below needs that lock. Cancelling throws it out of Add. It also ends the
        // input writer's wait; CompleteAdding makes a late SendInput fail fast instead of queueing.
        _cts.Cancel();
        _input.CompleteAdding();
        Unsubscribe();
        bool parseThreadStopped = Thread.CurrentThread == _parseThread
            || _parseThread.Join(TimeSpan.FromSeconds(5));

        // Ordinarily redundant with ParseLoop's own finally (Join waited for it to run), but cheap
        // and idempotent, and it still protects the join-timeout edge case: nothing posted around
        // this point should be able to sit in a queue forever.
        _control.CompleteAdding();
        _data.CompleteAdding();
        DrainAfterStop();
        try { _session.Dispose(); }
        catch (Exception ex) { Log($"[Mux] session {Id}: disposing the child failed: {ex.Message}"); }

        // Joined AFTER the child is disposed: a writer blocked in a write to a child that stopped
        // reading stdin is released by the child going away, not by the token. An idle writer has
        // already left on the cancellation above. A write that races the child's disposal fails
        // and is logged by InputLoop, as it would have been on the reader thread before.
        bool inputThreadStopped = Thread.CurrentThread == _inputThread
            || _inputThread.Join(TimeSpan.FromSeconds(2));

        // Only once the parse thread is gone: it is the one thread that still reads the token and
        // takes from the queues. A late producer (the tap handler, OnExit) that slipped past the
        // _disposed check meets ObjectDisposedException in TryEnqueue, which treats it as closed.
        // If the join timed out, the thread is wedged and these are left to the finalizer rather
        // than yanked out from under it.
        if (parseThreadStopped && Thread.CurrentThread != _parseThread)
        {
            _control.Dispose();
            _data.Dispose();
        }

        // Same rule for the input writer's collection; the token is read by both threads.
        bool inputThreadGone = inputThreadStopped && Thread.CurrentThread != _inputThread;
        if (inputThreadGone) _input.Dispose();
        if (inputThreadGone && parseThreadStopped && Thread.CurrentThread != _parseThread) _cts.Dispose();
    }

    private void OnRawOutput(ReadOnlyMemory<byte> chunk)
    {
        if (chunk.IsEmpty) return;

        // The tap hands over an array the producer never touches again (ITerminalByteOutput), so it
        // is queued as-is: the one copy this path makes is the tap's own.
        byte[] data = MemoryMarshal.TryGetArray(chunk, out ArraySegment<byte> segment)
            && segment.Offset == 0 && segment.Count == segment.Array!.Length
            ? segment.Array
            : chunk.ToArray();
        TryEnqueue(_data, WorkItem.ForData(data));
    }

    private void EnqueueControl(Action action) => TryEnqueue(_control, WorkItem.ForAction(action));

    private bool TryEnqueue(BlockingCollection<WorkItem> queue, WorkItem item)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        try
        {
            queue.Add(item, _cts.Token);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (ObjectDisposedException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private void ParseLoop()
    {
        try
        {
            while (true)
            {
                WorkItem item;
                if (_sessionChangePending)
                {
                    // The delayed flush: while a change is pending, wait at most until it is due. An
                    // idle session wakes at the deadline - no timer, no thread pool (spec §4).
                    int wait = SessionChangeWaitMs();
                    if (wait == 0)
                    {
                        FlushSessionChanged();
                        continue;
                    }

                    // A timed-out wait only re-checks the deadline: an OS wait can return up to a tick
                    // before TickCount64 agrees the time is up, and flushing then would undercut the interval.
                    if (BlockingCollection<WorkItem>.TryTakeFromAny(_queues, out item, wait, _cts.Token) < 0) continue;
                }
                else
                {
                    BlockingCollection<WorkItem>.TakeFromAny(_queues, out item, _cts.Token);
                }

                Execute(item);
                if (item.Kind == WorkKind.Exit && item.Terminal) break;
                if (_sessionChangePending && SessionChangeWaitMs() == 0) FlushSessionChanged();
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose (or the terminal exit) cancelled the token: the normal way this loop ends.
        }
        catch (Exception ex)
        {
            Log($"[Mux] session {Id}: parse loop terminated: {ex}");
            EnterFaulted($"The session's parse loop terminated: {ex.GetType().Name}.");
        }
        finally
        {
            // Covers every stop path: a natural terminal-exit break (Kill) and the
            // OperationCanceledException a Dispose()-triggered cancellation throws both land here.
            // CompleteAdding waits for any in-flight Add and releases anything blocked on a full
            // queue (which TryEnqueue's catch turns into a dropped item), so nothing posted after
            // this point can be silently queued forever - it either fails TryEnqueue synchronously
            // or is picked up, un-executed, by the drain right after.
            _control.CompleteAdding();
            _data.CompleteAdding();
            DrainAfterStop();
        }
    }

    private void Execute(WorkItem item)
    {
        switch (item.Kind)
        {
            case WorkKind.Data:
                ProcessChunk(item.Data!);
                break;
            case WorkKind.Action:
                try { item.Action!(); }
                catch (Exception ex) { Log($"[Mux] session {Id}: control action failed: {ex}"); }
                break;
            case WorkKind.Exit:
                ProcessExit(item.ExitCode, item.Terminal);
                break;
        }
    }

    private void ProcessChunk(byte[] data)
    {
        long seq = Interlocked.Read(ref _rawOffset);
        Interlocked.Exchange(ref _rawOffset, seq + data.Length);
        Volatile.Write(ref _lastOutputUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        // A faulted parser no longer tracks the child; feeding clients a stream the mux cannot
        // vouch for would only make them diverge from something that is itself wrong.
        if (IsFaulted) return;

        int maxChars = Utf8ChunkDecoder.GetMaxCharCount(data.Length);
        if (_chars.Length < maxChars) _chars = new char[maxChars];
        int charCount = _decoder.Decode(data, _chars);
        if (charCount > 0)
        {
            try
            {
                _parser.Process(new string(_chars, 0, charCount));
            }
            catch (Exception ex)
            {
                Log($"[Mux] session {Id} faulted: the parser threw at stream offset {seq}: {ex}");
                EnterFaulted($"The parser threw at stream offset {seq}: {ex.GetType().Name}.");
                return;
            }
        }

        if (_subscribers.Count > 0) Broadcast(MuxFrames.Output(Id, seq, data));
    }

    private void ProcessExit(int? code, bool terminal)
    {
        if (!IsExited)
        {
            Volatile.Write(ref _exitCode, code ?? _session.ExitCode ?? -1);
            Interlocked.Exchange(ref _exitedAtMs, Environment.TickCount64); // before _exited: a reaper that sees the exit sees its time
            Volatile.Write(ref _exited, 1);
            if (_subscribers.Count > 0)
            {
                if (Volatile.Read(ref _killRequested) != 0) SendKilled();
                Broadcast(ExitedFrame());
            }
        }

        if (terminal)
        {
            ForgetAll();
            PublishAttachedCount();

            // Cancel FIRST, same as Dispose and for the same reason: a producer parked in
            // _data.Add (reached from inside RawOutputTap.Publish, which invokes handlers while
            // holding its own lock) blocks holding that lock until its Add is released or
            // cancelled. Unsubscribe needs that same lock to remove the handler, so unsubscribing
            // before cancelling can deadlock this thread against its own producer.
            _cts.Cancel();
            Unsubscribe();
        }
    }

    /// <summary>
    /// Every exit path reports the subscription that actually resulted (<see cref="IMuxFrameSink.OnSubscriptionState"/>).
    /// On success that repeats the report made just before the snapshot was enqueued; the report is
    /// idempotent state, not an event, so the repeat is harmless and keeps one rule for every path.
    /// </summary>
    private void ExecuteAttach(IMuxFrameSink sink, long requestId, int maxScrollbackRows, MuxPresentation presentation, int maxSnapshotBytes, MuxAttachMode mode)
    {
        try
        {
            ExecuteAttachCore(sink, requestId, maxScrollbackRows, presentation, maxSnapshotBytes, mode);
        }
        finally
        {
            ReportSubscription(sink, _subscribers.Contains(sink), _readOnlySinks.Contains(sink));
        }
    }

    private void ExecuteAttachCore(IMuxFrameSink sink, long requestId, int maxScrollbackRows, MuxPresentation presentation, int maxSnapshotBytes, MuxAttachMode mode)
    {
        // A re-attach keeps the sink's existing subscription until its new snapshot is actually
        // enqueued: if this attempt fails (snapshot_too_large once the scrollback has grown), the
        // client keeps its old position and must go on receiving the stream it already has - not
        // silently starve while still believing it is attached. Until then it is an ordinary
        // subscriber, so it also gets this attach's ResizeEvent, then the snapshot that supersedes it.
        if (IsFaulted)
        {
            Reply(sink, requestId, MuxErrorCodes.Internal, $"Session {Id} is faulted; its state no longer tracks the child.");
            return;
        }

        // Exclusivity is decided here, inside the one attach item on the one parse thread: every
        // attach to this session from any connection runs serially on this thread, so nothing can
        // subscribe between this check and the subscription below (Phase 3 spec §3).
        if (mode == MuxAttachMode.IfUnattached && HasOtherInteractiveSubscriber(sink))
        {
            Reply(sink, requestId, MuxErrorCodes.SessionAttached, $"Session {Id} is attached to another client.");
            return;
        }

        bool readOnly = mode == MuxAttachMode.ReadOnly;

        // Attaching is a resize to the attaching client's size (latest wins). The other clients get
        // the ResizeEvent; this one's snapshot already reflects it. Guarded like everything else in
        // here: an attach must always be answered, never left to the client's request timeout. A
        // read-only observer changes neither: its snapshot is at the session's current size.
        if (!readOnly)
        {
            try
            {
                ApplyPresentation(presentation);
                ApplyResize(presentation.Cols, presentation.Rows);
            }
            catch (Exception ex)
            {
                Reply(sink, requestId, MuxErrorCodes.Internal, $"Preparing the attach failed: {ex.Message}");
                return;
            }
        }

        byte[] json;
        long seq;
        try
        {
            TerminalStateSnapshot snapshot = CaptureSnapshot(maxScrollbackRows);
            seq = snapshot.StreamSeq;
            json = TerminalStateSerializer.ToBytes(snapshot);
        }
        catch (Exception ex)
        {
            Reply(sink, requestId, MuxErrorCodes.Internal, $"Snapshot capture failed: {ex.Message}");
            return;
        }

        // No caller can allow more than one frame carries: past that, MuxFrames.Snapshot throws.
        int limit = Math.Min(maxSnapshotBytes, MuxProtocol.MaxFrameBytes - MuxFrames.SnapshotHeaderBytes);
        if (json.Length > limit)
        {
            Reply(sink, requestId, MuxErrorCodes.SnapshotTooLarge,
                $"Snapshot of session {Id} is {json.Length} bytes; the limit is {limit}.");
            return;
        }

        // Fail safe regardless: an attach must always be answered, never left to the client's timeout.
        MuxOutboundFrame frame;
        try
        {
            frame = MuxFrames.Snapshot(requestId, Id, seq, json);
        }
        catch (Exception ex)
        {
            Reply(sink, requestId, MuxErrorCodes.Internal, $"Snapshot frame could not be built: {ex.Message}");
            return;
        }

        // Reported BEFORE the snapshot is handed over: a client reacting to its snapshot must find the
        // connection already enforcing (or no longer enforcing) read-only for its next input or resize.
        // If the enqueue is refused, the finally in ExecuteAttach reports the real outcome afterwards.
        ReportSubscription(sink, subscribed: true, readOnly);

        bool accepted;
        try { accepted = sink.TryEnqueue(frame); }
        finally { frame.Release(); }
        if (!accepted)
        {
            // A sink that refuses a frame is gone (Broadcast drops it the same way).
            if (_subscribers.Remove(sink)) DropRefusingSink(sink);
            return;
        }

        if (!_subscribers.Contains(sink)) _subscribers.Add(sink);
        _attachRequestIds[sink] = requestId;

        // The kitty flag is recorded only once the subscription has taken effect: a refused or failed
        // attach changes no client's say in the AND. A re-attach as an observer gives its say up.
        if (readOnly)
        {
            _readOnlySinks.Add(sink);
            _kittyBySink.Remove(sink);
        }
        else
        {
            _readOnlySinks.Remove(sink);
            _kittyBySink[sink] = presentation.KittyKeyboardEnabled;
            Volatile.Write(ref _detachedByUser, 0); // a read-only peek must not undo a deliberate detach (spec §7.7)
            _undecidedDrop = null;                  // decided: a dropped sink's late detach no longer has a say
        }

        RecomputeKitty();
        PublishAttachedCount();
        if (IsExited) Offer(sink, ExitedFrame());
    }

    /// <summary>
    /// Parse thread: the screen and the status are taken together, between two chunks, so they agree. Every path
    /// answers; an exited session that is still listed answers with its last screen.
    /// </summary>
    private void ExecuteReadScreen(IMuxFrameSink sink, long requestId, int maxScrollbackRows, int maxSnapshotBytes, bool hasActiveChildProcesses)
    {
        if (IsFaulted)
        {
            Reply(sink, requestId, MuxErrorCodes.Internal, $"Session {Id} is faulted; its state no longer tracks the child.");
            return;
        }

        byte[] json;
        try
        {
            json = TerminalStateSerializer.ToBytes(CaptureSnapshot(maxScrollbackRows));
        }
        catch (Exception ex)
        {
            Reply(sink, requestId, MuxErrorCodes.Internal, $"Snapshot capture failed: {ex.Message}");
            return;
        }

        // The reply is a Response frame, charged to the connection's stream budget: past the cap it is refused,
        // never risked against that budget's client_too_slow.
        if (json.Length > maxSnapshotBytes)
        {
            Reply(sink, requestId, MuxErrorCodes.SnapshotTooLarge,
                $"The screen of session {Id} is {json.Length} bytes; the limit is {maxSnapshotBytes}. Ask for fewer scrollback rows.");
            return;
        }

        MuxOutboundFrame frame;
        try
        {
            bool exited = IsExited;
            frame = MuxFrames.Response(new MuxResponse
            {
                Id = requestId,
                Result = MuxFrames.ToElement(new ReadScreenResult
                {
                    Snapshot = json,
                    Running = !exited,
                    ExitCode = ExitCode,
                    HasActiveChildProcesses = !exited && hasActiveChildProcesses,
                    AttachedClients = AttachedClients,
                    InteractiveClients = sink.WantsSessionEvents ? InteractiveClients : null, // v2 only, as in sessionInfo
                    Title = Title,
                    Cwd = Cwd,
                    LastOutputUnixMs = LastOutputUnixMs,
                }, MuxJsonContext.Default.ReadScreenResult),
            });
        }
        catch (Exception ex)
        {
            Reply(sink, requestId, MuxErrorCodes.Internal, $"The screen reply could not be built: {ex.Message}");
            return;
        }

        Offer(sink, frame);
    }

    /// <summary>Parse thread only. A read-only observer does not count: it must not make a GUI abandon its own shell (spec §3).</summary>
    private bool HasOtherInteractiveSubscriber(IMuxFrameSink sink) =>
        _subscribers.Any(s => !ReferenceEquals(s, sink) && !_readOnlySinks.Contains(s));

    /// <summary>
    /// Parse thread only, for a sink just removed from <see cref="_subscribers"/>: drops its per-sink
    /// state and tells it. Every removal path (detach, a sink dropped for refusing a frame) ends here,
    /// so this is also where the sink leaves the kitty AND.
    /// </summary>
    private void Forget(IMuxFrameSink sink)
    {
        _readOnlySinks.Remove(sink);
        _attachRequestIds.Remove(sink);
        if (_kittyBySink.Remove(sink)) RecomputeKitty();
        ReportSubscription(sink, subscribed: false, readOnly: false);
    }

    /// <summary>
    /// Parse thread only, for a sink just removed from <see cref="_subscribers"/> because it refused a
    /// frame (a broadcast, or its own re-attach's snapshot). A connection refuses once it has closed,
    /// and that is not a detach. If this removal left no interactive subscriber, it is the departure
    /// that decides <see cref="DetachedByUser"/>, but it cannot say whether the user meant it. The
    /// sink's own detach can (<see cref="DecideForDroppedSink"/>), so the decision waits for it.
    /// </summary>
    private void DropRefusingSink(IMuxFrameSink sink)
    {
        bool wasInteractive = !_readOnlySinks.Contains(sink);
        long attachRequestId = _attachRequestIds.GetValueOrDefault(sink);
        Forget(sink);
        if (wasInteractive && !HasOtherInteractiveSubscriber(sink)) _undecidedDrop = (sink, attachRequestId);
        PublishAttachedCount();
    }

    /// <summary>
    /// Parse thread only: a detach for a sink that is no longer subscribed. A client that detaches
    /// and then hangs up posts its detach before it closes, but an item queued ahead of the detach
    /// can broadcast after the close and drop the sink first (<see cref="DropRefusingSink"/>). If that
    /// drop emptied the session, this detach is the one that says what kind of departure it was: a
    /// user detach sets <see cref="DetachedByUser"/>, and the connection's own close-time detach
    /// leaves it clear (spec §7.7). A detach naming an attach the dropped subscription had superseded
    /// is stale, as it would have been for a live subscriber.
    /// </summary>
    private void DecideForDroppedSink(IMuxFrameSink sink, bool userDetached, long? attachRequestId)
    {
        if (_undecidedDrop is not { } drop || !ReferenceEquals(drop.Sink, sink)) return;
        if (attachRequestId is long undone && drop.AttachRequestId > undone) return;
        _undecidedDrop = null;
        Volatile.Write(ref _detachedByUser, userDetached ? 1 : 0);
    }

    /// <summary>
    /// Parse thread only: every subscriber leaves at once (fault, terminal exit). Nobody is left to
    /// decide the kitty flag, so it keeps its last value and there is nothing to recompute.
    /// </summary>
    private void ForgetAll()
    {
        foreach (IMuxFrameSink sink in _subscribers) ReportSubscription(sink, subscribed: false, readOnly: false);
        _subscribers.Clear();
        _readOnlySinks.Clear();
        _attachRequestIds.Clear();
        _kittyBySink.Clear();
    }

    private void ReportSubscription(IMuxFrameSink sink, bool subscribed, bool readOnly)
    {
        // The sink is arbitrary code on the parse thread (a connection in production); a throw must
        // not abandon the item that called it halfway through its bookkeeping.
        try { sink.OnSubscriptionState(Id, subscribed, readOnly); }
        catch (Exception ex) { Log($"[Mux] session {Id}: reporting a subscription failed: {ex.Message}"); }
    }

    /// <summary>
    /// The session-wide fields: the latest client wins. Not <see cref="MuxPresentation.KittyKeyboardEnabled"/>,
    /// which is each interactive client's own (<see cref="RecordKitty"/>, <see cref="RecomputeKitty"/>).
    /// </summary>
    private void ApplyPresentation(MuxPresentation presentation)
    {
        if (presentation.CellWidthPx > 0) _parser.CellWidth = presentation.CellWidthPx;
        if (presentation.CellHeightPx > 0) _parser.CellHeight = presentation.CellHeightPx;
        _parser.DefaultForeground = presentation.DefaultFg is uint fg ? TermColor.FromUint(fg) : null;
        _parser.DefaultBackground = presentation.DefaultBg is uint bg ? TermColor.FromUint(bg) : null;
    }

    /// <summary>
    /// Parse thread only: a presentation-carrying resize replaces its sender's kitty flag; call
    /// <see cref="RecomputeKitty"/> after. Only an interactive subscriber has a say: a read-only
    /// observer's resize is dropped by its connection, but the session does not rely on that, and a
    /// sender that is not subscribed (yet, or any more) would leave an entry nothing removes.
    /// </summary>
    private void RecordKitty(IMuxFrameSink sink, bool enabled)
    {
        if (!_subscribers.Contains(sink) || _readOnlySinks.Contains(sink)) return;
        _kittyBySink[sink] = enabled;
    }

    /// <summary>
    /// Parse thread only. The parser answers a kitty query with flags 0 unless every interactive
    /// client can speak the protocol, so a text client (which presents false) turns it off only while
    /// it is attached (Phase 4 spec §4 item 5). With no interactive subscriber the last value stays:
    /// nobody is typing, and the next interactive attach decides again.
    /// </summary>
    private void RecomputeKitty()
    {
        bool any = false, all = true;
        foreach (IMuxFrameSink s in _subscribers)
        {
            if (_readOnlySinks.Contains(s) || !_kittyBySink.TryGetValue(s, out bool k)) continue;
            any = true;
            all &= k;
        }

        if (any) _parser.KittyKeyboardEnabled = all;
    }

    private void ApplyResize(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0 || (cols == _buffer.Cols && rows == _buffer.Rows)) return;

        // Buffer, then event, then child: bytes the child writes after learning the new size sort
        // after the event every client applies it at.
        _buffer.Resize(cols, rows);
        Volatile.Write(ref _cols, cols);
        Volatile.Write(ref _rows, rows);

        // Never while faulted: the offset has moved on without Output frames, so an event at it
        // would read as a gap (EnterFaulted already dropped every subscriber; this is the belt).
        if (_subscribers.Count > 0 && !IsFaulted) Broadcast(MuxFrames.ResizeEvent(Id, Interlocked.Read(ref _rawOffset), cols, rows));
        if (IsExited) return;

        // The buffer and every client have already moved to the new size, so a child that fails to
        // learn it (a native transport gone bad) is logged, not propagated: propagating would leave
        // an attach unanswered and the mux half-resized for no gain - the size cannot be un-sent.
        try
        {
            _session.Resize(cols, rows);
        }
        catch (Exception ex)
        {
            Log($"[Mux] session {Id}: the child's Resize({cols}x{rows}) failed: {ex.Message}");
            return;
        }

        if (_parser.InBandResizeReportsEnabled)
        {
            _parser.SendInBandResize(rows, cols,
                (int)Math.Round(cols * _parser.CellWidth), (int)Math.Round(rows * _parser.CellHeight));
        }
    }

    /// <summary>Parse thread only. Offers <paramref name="frame"/> to every subscriber <paramref name="to"/> accepts (all when null); a refusing sink is dropped.</summary>
    private void Broadcast(MuxOutboundFrame frame, Func<IMuxFrameSink, bool>? to = null)
    {
        try
        {
            for (int i = _subscribers.Count - 1; i >= 0; i--)
            {
                IMuxFrameSink sink = _subscribers[i];
                if (to is not null && !to(sink)) continue;
                if (!sink.TryEnqueue(frame))
                {
                    _subscribers.RemoveAt(i);
                    DropRefusingSink(sink);
                }
            }
        }
        finally
        {
            frame.Release();
        }
    }

    /// <summary>
    /// Parse thread only. From here the offset advances with no Output frames (a faulted parser no
    /// longer tracks the child), so every subscriber must hear an explicit end - never a silent gap,
    /// which a client can only read as corruption of the whole connection. Each gets a
    /// <see cref="MuxMethods.Faulted"/> notification and is dropped; a later attach is refused.
    /// </summary>
    private void EnterFaulted(string reason)
    {
        if (Interlocked.Exchange(ref _faulted, 1) != 0 || _subscribers.Count == 0) return;
        try
        {
            MuxOutboundFrame frame = MuxFrames.Notification(new MuxNotification
            {
                Method = MuxMethods.Faulted,
                Params = MuxFrames.ToElement(new FaultedNotification { SessionId = Id, Message = reason }, MuxJsonContext.Default.FaultedNotification),
            });
            try
            {
                foreach (IMuxFrameSink sink in _subscribers) sink.TryEnqueue(frame);
            }
            finally
            {
                frame.Release();
            }
        }
        catch (Exception ex)
        {
            Log($"[Mux] session {Id}: announcing the fault failed: {ex.Message}");
        }
        finally
        {
            ForgetAll();
            PublishAttachedCount();
        }
    }

    private MuxOutboundFrame ExitedFrame() =>
        MuxFrames.Notification(new MuxNotification
        {
            Method = MuxMethods.Exited,
            Params = MuxFrames.ToElement(
                new ExitedNotification { SessionId = Id, ExitCode = Volatile.Read(ref _exitCode) },
                MuxJsonContext.Default.ExitedNotification),
        });

    private void ReplySessionExited(IMuxFrameSink sink, long requestId) =>
        Reply(sink, requestId, MuxErrorCodes.SessionExited, $"Session {Id} has exited; it cannot be attached.");

    private static void Reply(IMuxFrameSink sink, long requestId, string code, string message) =>
        Offer(sink, MuxFrames.Response(new MuxResponse
        {
            Id = requestId,
            Error = new MuxError { Code = code, Message = message },
        }));

    private static void Offer(IMuxFrameSink sink, MuxOutboundFrame frame)
    {
        try { sink.TryEnqueue(frame); }
        finally { frame.Release(); }
    }

    /// <summary>
    /// Parse thread only. A changed pair (attached, interactive) is a session change (spec §4; Phase 4
    /// spec §4 item 9), so the same sink re-attaching ReadOnly → Shared is one although the attached
    /// count stays. Read-only observers count in <c>attached</c>: the number of connected clients, the
    /// same figure <c>listSessions</c> reports; <c>interactive</c> leaves them out.
    /// </summary>
    private void PublishAttachedCount()
    {
        int count = _subscribers.Count;
        int interactive = count - _readOnlySinks.Count; // read-only sinks are always subscribers too
        Volatile.Write(ref _attached, count);
        Volatile.Write(ref _interactive, interactive);
        if (count != _lastPublishedAttached || interactive != _lastPublishedInteractive)
        {
            _lastPublishedAttached = count;
            _lastPublishedInteractive = interactive;
            MarkSessionChanged();
        }
    }

    /// <summary>Parse thread only (every caller runs inside an item: attach/detach, the parser's OSC callbacks).</summary>
    private void MarkSessionChanged() => _sessionChangePending = true;

    /// <summary>Milliseconds until a pending change may be sent; 0 = now.</summary>
    private int SessionChangeWaitMs()
    {
        long due = _lastSessionChangeSentMs + _sessionChangedIntervalMs - Environment.TickCount64;
        return due <= 0 ? 0 : (int)Math.Min(due, int.MaxValue);
    }

    /// <summary>
    /// Parse thread only. Sends the session's current facts to every v2 subscriber. The interval is
    /// measured from AFTER the handover, not before the frame is built: building can take tens of
    /// milliseconds (the first serialization, a loaded machine), and stamping first let the next
    /// notification reach a sink less than one interval after this one.
    /// </summary>
    private void FlushSessionChanged()
    {
        _sessionChangePending = false;
        try
        {
            if (IsFaulted || !_subscribers.Exists(static s => s.WantsSessionEvents)) return;
            if (BuildNotification(MuxMethods.SessionChanged, new SessionChangedNotification
            {
                SessionId = Id,
                AttachedClients = _subscribers.Count,
                InteractiveClients = _interactive, // parse thread: its only writer
                Title = Title,
                Cwd = Cwd,
            }, MuxJsonContext.Default.SessionChangedNotification) is not { } frame) return;

            Broadcast(frame, static sink => sink.WantsSessionEvents);
        }
        finally
        {
            _lastSessionChangeSentMs = Environment.TickCount64;
        }
    }

    /// <summary>Parse thread only: <c>killed</c> goes to every v2 subscriber except the killer, which already knows.</summary>
    private void SendKilled()
    {
        if (BuildNotification(MuxMethods.Killed,
                new KilledNotification { SessionId = Id, ByClientKind = Volatile.Read(ref _killedByKind) },
                MuxJsonContext.Default.KilledNotification) is not { } frame) return;

        IMuxFrameSink? killer = Volatile.Read(ref _killedBy);
        Broadcast(frame, sink => sink.WantsSessionEvents && !ReferenceEquals(sink, killer));
    }

    /// <summary>Never throws (parse thread): a notification that cannot be built is logged and skipped.</summary>
    private MuxOutboundFrame? BuildNotification<T>(string method, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        try
        {
            return MuxFrames.Notification(new MuxNotification { Method = method, Params = MuxFrames.ToElement(value, typeInfo) });
        }
        catch (Exception ex)
        {
            Log($"[Mux] session {Id}: building {method} failed: {ex.Message}");
            return null;
        }
    }

    private void Unsubscribe()
    {
        if (Interlocked.Exchange(ref _unsubscribed, 1) != 0) return;
        _byteOutput.OnRawOutputReceived -= _onRawOutput;
        _session.OnOutputReceived -= _onStringOutput;
        _session.OnExit -= _onExit;
    }

    /// <summary>Completes waiters on items that will never run (flush barriers, invokes).</summary>
    private void DrainAfterStop()
    {
        while (_control.TryTake(out WorkItem item) || _data.TryTake(out item))
        {
            item.OnDropped?.Invoke();
        }
    }

    /// <summary>Never throws: it is called from the parse thread's own catch blocks, where a throwing logger would escape the thread.</summary>
    private void Log(string message)
    {
        // The host's logger is arbitrary code running on the parse thread; a logger that throws
        // (disk full, closed sink) must not fault the session it is only reporting on.
        try { _log?.Invoke(message); }
        catch (Exception) { /* deliberately swallowed - see above */ }
    }

    private enum WorkKind : byte { Data, Action, Exit }

    private readonly struct WorkItem
    {
        private WorkItem(WorkKind kind, byte[]? data, Action? action, Action? onDropped, int? exitCode, bool terminal)
        {
            Kind = kind;
            Data = data;
            Action = action;
            OnDropped = onDropped;
            ExitCode = exitCode;
            Terminal = terminal;
        }

        public WorkKind Kind { get; }
        public byte[]? Data { get; }
        public Action? Action { get; }
        public Action? OnDropped { get; }
        public int? ExitCode { get; }
        public bool Terminal { get; }

        public static WorkItem ForData(byte[] data) => new(WorkKind.Data, data, null, null, null, false);
        public static WorkItem ForAction(Action action, Action? onDropped = null) => new(WorkKind.Action, null, action, onDropped, null, false);
        public static WorkItem ForExit(int? exitCode, bool terminal = false) => new(WorkKind.Exit, null, null, null, exitCode, terminal);
    }
}
