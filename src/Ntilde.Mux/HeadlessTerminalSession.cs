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
    private char[] _chars = new char[Utf8ChunkDecoder.GetMaxCharCount(4096)];
    private long _rawOffset;
    private string _title;
    private int _cols;
    private int _rows;
    private int _attached;
    private int _exited;
    private int _exitCode;
    private int _faulted;
    private int _disposed;
    private int _unsubscribed;

    // PostResize coalescing (any thread -> parse thread); see PostResize.
    private readonly object _pendingResizeGate = new();
    private (int Cols, int Rows)? _pendingResize;
    private MuxPresentation? _pendingPresentation;
    private bool _resizeQueued;

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
        _buffer = new TerminalBuffer(options.Cols, options.Rows);
        _parser = new AnsiParser(_buffer, options.ForceConPtyFiltering)
        {
            ImageDecoder = null,
            AllowNativeKittyGraphics = false,
        };
        _parser.OnResponse = reply => _session.SendInput(reply);
        _parser.OnTitleChanged = title => Volatile.Write(ref _title, title);
        _queues = [_control, _data];
        _onRawOutput = OnRawOutput;
        _onExit = code => TryEnqueue(_data, WorkItem.ForExit(code));

        // The thread first: subscribing replays whatever arrived since spawn straight into the
        // bounded data queue, which would block this constructor if nobody were draining it.
        _parseThread = new Thread(ParseLoop) { IsBackground = true, Name = $"MuxParse-{id:N}" };
        _parseThread.Start();

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
            // The parse thread is already running: stop it (and drop whatever subscriptions did
            // succeed) so a constructor that throws leaves no thread behind. The caller still owns
            // the session and disposes it.
            Volatile.Write(ref _disposed, 1);
            _cts.Cancel();
            try { Unsubscribe(); }
            catch (Exception ex) { Log($"[Mux] session {id}: unsubscribing after a failed construction threw: {ex.Message}"); }
            _parseThread.Join(TimeSpan.FromSeconds(5));
            throw;
        }
    }

    public Guid Id { get; }
    public string Title => Volatile.Read(ref _title);
    public string Command { get; }
    public string? Arguments { get; }
    public bool ForceConPtyFiltering { get; }
    public int Cols => Volatile.Read(ref _cols);
    public int Rows => Volatile.Read(ref _rows);
    public int AttachedClients => Volatile.Read(ref _attached);
    public bool IsExited => Volatile.Read(ref _exited) != 0;
    public int? ExitCode => IsExited ? Volatile.Read(ref _exitCode) : null;
    public bool IsFaulted => Volatile.Read(ref _faulted) != 0;
    public long StreamPosition => Interlocked.Read(ref _rawOffset);

    internal ITerminalSession Inner => _session;

    /// <summary>Tests only, and only while the parse thread is idle (after <see cref="FlushAsync"/>).</summary>
    internal TerminalBuffer Buffer => _buffer;

    /// <inheritdoc cref="Buffer"/>
    internal AnsiParser Parser => _parser;

    internal int QueuedDataCount => _data.Count;

    internal int QueuedControlCount => _control.Count;

    /// <summary>Thread-safe; goes straight to the session (input is independent of the output stream).</summary>
    public void SendInput(string text)
    {
        if (IsExited || Volatile.Read(ref _disposed) != 0 || string.IsNullOrEmpty(text)) return;
        _session.SendInput(text);
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
    /// </remarks>
    public void PostResize(int cols, int rows, MuxPresentation? presentation)
    {
        lock (_pendingResizeGate)
        {
            _pendingResize = (cols, rows);
            if (presentation is not null) _pendingPresentation = presentation;
            if (_resizeQueued) return;
            _resizeQueued = true;
        }

        EnqueueControl(ApplyPendingResize);
    }

    private void ApplyPendingResize()
    {
        (int Cols, int Rows)? size;
        MuxPresentation? presentation;
        lock (_pendingResizeGate)
        {
            size = _pendingResize;
            presentation = _pendingPresentation;
            _pendingResize = null;
            _pendingPresentation = null;
            _resizeQueued = false;
        }

        if (presentation is not null) ApplyPresentation(presentation);
        if (size is { } s) ApplyResize(s.Cols, s.Rows);
    }

    internal void PostAttach(IMuxFrameSink sink, long requestId, int maxScrollbackRows, MuxPresentation presentation, int maxSnapshotBytes)
    {
        // Unlike PostResize/PostDetach, a dropped attach must still answer: the sink is a client
        // waiting on this specific requestId, and silence would leave it hung rather than told the
        // session is gone. OnDropped runs whether TryEnqueue refuses synchronously (below) or the
        // item is later found undelivered by DrainAfterStop.
        var item = WorkItem.ForAction(
            () => ExecuteAttach(sink, requestId, maxScrollbackRows, presentation, maxSnapshotBytes),
            onDropped: () => ReplySessionExited(sink, requestId));
        if (!TryEnqueue(_control, item)) item.OnDropped!();
    }

    internal void PostDetach(IMuxFrameSink sink) =>
        EnqueueControl(() =>
        {
            if (_subscribers.Remove(sink)) PublishAttachedCount();
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
    /// queued; then the parse thread stops and unsubscribes.
    /// </summary>
    public void Kill()
    {
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
        // the unsubscribe below needs that lock. Cancelling throws it out of Add.
        _cts.Cancel();
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

        // Only once the parse thread is gone: it is the one thread that still reads the token and
        // takes from the queues. A late producer (the tap handler, OnExit) that slipped past the
        // _disposed check meets ObjectDisposedException in TryEnqueue, which treats it as closed.
        // If the join timed out, the thread is wedged and these are left to the finalizer rather
        // than yanked out from under it.
        if (parseThreadStopped && Thread.CurrentThread != _parseThread)
        {
            _cts.Dispose();
            _control.Dispose();
            _data.Dispose();
        }
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
                BlockingCollection<WorkItem>.TakeFromAny(_queues, out WorkItem item, _cts.Token);
                Execute(item);
                if (item.Kind == WorkKind.Exit && item.Terminal) break;
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
            Volatile.Write(ref _exited, 1);
            if (_subscribers.Count > 0) Broadcast(ExitedFrame());
        }

        if (terminal)
        {
            _subscribers.Clear();
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

    private void ExecuteAttach(IMuxFrameSink sink, long requestId, int maxScrollbackRows, MuxPresentation presentation, int maxSnapshotBytes)
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

        // Attaching is a resize to the attaching client's size (latest wins). The other clients get
        // the ResizeEvent; this one's snapshot already reflects it. Guarded like everything else in
        // here: an attach must always be answered, never left to the client's request timeout.
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

        bool accepted;
        try { accepted = sink.TryEnqueue(frame); }
        finally { frame.Release(); }
        if (!accepted)
        {
            // A sink that refuses a frame is gone (Broadcast drops it the same way).
            if (_subscribers.Remove(sink)) PublishAttachedCount();
            return;
        }

        if (!_subscribers.Contains(sink)) _subscribers.Add(sink);
        PublishAttachedCount();
        if (IsExited) Offer(sink, ExitedFrame());
    }

    private void ApplyPresentation(MuxPresentation presentation)
    {
        if (presentation.CellWidthPx > 0) _parser.CellWidth = presentation.CellWidthPx;
        if (presentation.CellHeightPx > 0) _parser.CellHeight = presentation.CellHeightPx;
        _parser.DefaultForeground = presentation.DefaultFg is uint fg ? TermColor.FromUint(fg) : null;
        _parser.DefaultBackground = presentation.DefaultBg is uint bg ? TermColor.FromUint(bg) : null;
        _parser.KittyKeyboardEnabled = presentation.KittyKeyboardEnabled;
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

    private void Broadcast(MuxOutboundFrame frame)
    {
        try
        {
            for (int i = _subscribers.Count - 1; i >= 0; i--)
            {
                if (!_subscribers[i].TryEnqueue(frame))
                {
                    _subscribers.RemoveAt(i);
                    PublishAttachedCount();
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
            _subscribers.Clear();
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

    private void PublishAttachedCount() => Volatile.Write(ref _attached, _subscribers.Count);

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
