using System.Collections.Concurrent;
using System.Text;
using Ntilde.Pty;
using Ntilde.Replay;

namespace Ntilde.Mux.Tests.Support;

/// <summary>
/// A deterministic ITerminalSession + ITerminalByteOutput: emits exactly the chunks a test gives
/// it, synchronously on the caller's thread, and records SendInput / Resize. Flight recording is a
/// real FlightRecordingBuffer so exports replay through the real ReplayRunner.
/// </summary>
internal sealed class ScriptedTerminalSession : ITerminalSession, ITerminalByteOutput
{
    private readonly object _gate = new();
    private Action<ReadOnlyMemory<byte>>? _raw;
    private FlightRecordingBuffer? _flight;
    private int _exited;

    public ScriptedTerminalSession(TerminalSessionRequest request)
    {
        Request = request;
        Cols = request.Cols;
        Rows = request.Rows;
    }

    public TerminalSessionRequest Request { get; }
    public Guid Id { get; } = Guid.NewGuid();
    public string ShellCommand => Request.Command;
    public string? ShellArguments => Request.Arguments;
    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public ConcurrentQueue<string> SentInput { get; } = new();
    public ConcurrentQueue<(int Cols, int Rows)> Resizes { get; } = new();
    public bool ThrowOnSendInput { get; set; }

    /// <summary>Makes Resize fail the way a native transport can (the PTY handle went bad).</summary>
    public bool ThrowOnResize { get; set; }

    /// <summary>Subscribing to the raw tap throws: a session the mux fails to wrap after the factory built it.</summary>
    public bool ThrowOnRawSubscribe { get; init; }

    public Action<int, int>? OnResizeCalled { get; set; }
    public bool Disposed { get; private set; }

    public event Action<string>? OnOutputReceived { add { } remove { } }
    public event Action<int>? OnExit;

    public event Action<ReadOnlyMemory<byte>>? OnRawOutputReceived
    {
        add
        {
            if (ThrowOnRawSubscribe) throw new InvalidOperationException("scripted raw-tap subscription failure");
            lock (_gate) _raw += value;
        }
        remove { lock (_gate) _raw -= value; }
    }

    public void Emit(byte[] chunk)
    {
        // Invokes the handler while holding _gate, matching RawOutputTap.Publish: that is the
        // contract ITerminalByteOutput subscribers are written against (a blocked Add inside the
        // handler holds this lock), and a fake that released the lock first would hide any
        // deadlock a real subscriber could hit against RawOutputTap.
        lock (_gate)
        {
            _flight?.RecordChunk(chunk, chunk.Length);
            _raw?.Invoke(chunk.ToArray());
        }
    }

    public void Emit(string text) => Emit(Encoding.UTF8.GetBytes(text));

    public void EmitInChunks(ReadOnlySpan<byte> bytes, int chunkSize)
    {
        for (int i = 0; i < bytes.Length; i += chunkSize)
        {
            Emit(bytes.Slice(i, Math.Min(chunkSize, bytes.Length - i)).ToArray());
        }
    }

    public void Exit(int code)
    {
        if (Interlocked.Exchange(ref _exited, 1) != 0) return;
        ExitCode = code;
        OnExit?.Invoke(code);
    }

    public bool IsProcessRunning => Volatile.Read(ref _exited) == 0;
    public bool HasActiveChildProcesses { get; set; }
    public int? ExitCode { get; private set; }

    public void SendInput(string input)
    {
        if (ThrowOnSendInput) throw new InvalidOperationException("scripted SendInput failure");
        SentInput.Enqueue(input);
    }

    public void Resize(int cols, int rows)
    {
        if (ThrowOnResize) throw new IOException("scripted resize failure: the PTY handle is gone");
        Cols = cols;
        Rows = rows;
        Resizes.Enqueue((cols, rows));
        OnResizeCalled?.Invoke(cols, rows);
        lock (_gate) _flight?.RecordResize(cols, rows);
    }

    public bool IsRecording { get; private set; }
    public string? RecordingPath { get; private set; }
    /// <summary>Makes StartRecording fail the way RustPtySession's does on an unwritable path.</summary>
    public bool ThrowOnStartRecording { get; set; }

    public void StartRecording(string filePath)
    {
        if (ThrowOnStartRecording) throw new UnauthorizedAccessException($"Access to the path '{filePath}' is denied.");
        IsRecording = true;
        RecordingPath = filePath;
    }
    public void StopRecording() => IsRecording = false;

    public bool IsFlightRecording { get { lock (_gate) return _flight is not null; } }
    /// <summary>The budget the mux actually passed to the last EnableFlightRecording call.</summary>
    public long? LastFlightBudget { get; private set; }

    public void EnableFlightRecording(long maxTotalBytes)
    {
        lock (_gate)
        {
            LastFlightBudget = maxTotalBytes;
            _flight ??= new FlightRecordingBuffer(maxTotalBytes, Cols, Rows);
        }
    }
    public void DisableFlightRecording() { lock (_gate) _flight = null; }

    public bool TryExportFlightRecording(string filePath, out FlightExportInfo info)
    {
        FlightRecordingBuffer? flight;
        lock (_gate) flight = _flight;
        if (flight is null) { info = default; return false; }
        info = flight.ExportTo(filePath, ShellCommand);
        return true;
    }

    public void Dispose()
    {
        Disposed = true;
        Exit(-1);
    }
}
