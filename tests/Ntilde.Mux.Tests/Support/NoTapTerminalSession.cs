using Ntilde.Pty;
using Ntilde.Replay;

namespace Ntilde.Mux.Tests.Support;

/// <summary>A session with no raw-byte tap: the mux must refuse to host it.</summary>
internal sealed class NoTapTerminalSession : ITerminalSession
{
    public bool Disposed { get; private set; }
    public Guid Id { get; } = Guid.NewGuid();
    public string ShellCommand => "no-tap";
    public string? ShellArguments => null;
    public bool IsProcessRunning => !Disposed;
    public bool HasActiveChildProcesses => false;
    public int? ExitCode => null;
    public bool IsRecording => false;
    public bool IsFlightRecording => false;
    public event Action<string>? OnOutputReceived { add { } remove { } }
    public event Action<int>? OnExit { add { } remove { } }
    public void SendInput(string input) { }
    public void Resize(int cols, int rows) { }
    public void StartRecording(string filePath) { }
    public void StopRecording() { }
    public void EnableFlightRecording(long maxTotalBytes) { }
    public void DisableFlightRecording() { }
    public bool TryExportFlightRecording(string filePath, out FlightExportInfo info) { info = default; return false; }
    public void Dispose() => Disposed = true;
}
