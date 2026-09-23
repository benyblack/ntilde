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
    public Action<int, int>? OnResizeCalled { get; set; }
    public bool Disposed { get; private set; }

    public event Action<string>? OnOutputReceived { add { } remove { } }
    public event Action<int>? OnExit;

    public event Action<ReadOnlyMemory<byte>>? OnRawOutputReceived
    {
        add { lock (_gate) _raw += value; }
        remove { lock (_gate) _raw -= value; }
    }

    public void Emit(byte[] chunk)
    {
        Action<ReadOnlyMemory<byte>>? handler;
        lock (_gate)
        {
            handler = _raw;
            _flight?.RecordChunk(chunk, chunk.Length);
        }

        handler?.Invoke(chunk.ToArray());
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
        Cols = cols;
        Rows = rows;
        Resizes.Enqueue((cols, rows));
        OnResizeCalled?.Invoke(cols, rows);
        lock (_gate) _flight?.RecordResize(cols, rows);
    }

    public bool IsRecording { get; private set; }
    public string? RecordingPath { get; private set; }
    public void StartRecording(string filePath) { IsRecording = true; RecordingPath = filePath; }
    public void StopRecording() => IsRecording = false;

    public bool IsFlightRecording { get { lock (_gate) return _flight is not null; } }
    public void EnableFlightRecording(long maxTotalBytes) { lock (_gate) _flight ??= new FlightRecordingBuffer(maxTotalBytes, Cols, Rows); }
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
