using System;
using System.Threading.Tasks;
using Ntilde.Replay;

namespace Ntilde.Pty
{
    /// <summary>Raw input/output for a terminal session.</summary>
    public interface ITerminalIO
    {
        void SendInput(string input);
        event Action<string>? OnOutputReceived;
    }

    /// <summary>Optional nonblocking, best-effort terminal device replies; never apply user-input backpressure.</summary>
    public interface ITerminalDeviceReplySink
    {
        bool TrySendDeviceReply(string response);
    }

    /// <summary>Session lifetime: identity, resize, process state.</summary>
    public interface ITerminalLifecycle : IDisposable
    {
        Guid Id { get; }
        void Resize(int cols, int rows);
        bool IsProcessRunning { get; }
        bool HasActiveChildProcesses { get; }
        int? ExitCode { get; }
        event Action<int>? OnExit;
    }

    /// <summary>Descriptive metadata about the process backing this session.</summary>
    public interface ITerminalShellMetadata
    {
        string ShellCommand { get; }
        string? ShellArguments { get; }
    }

    /// <summary>Optional primitive-only recording context supplied by the parser host.</summary>
    public interface ITerminalRecordingCellMetrics
    {
        void SetRecordingCellMetrics(float widthPx, float heightPx);
    }

    /// <summary>Optional parser-host ownership of recording order. Install before recording starts.</summary>
    public interface ITerminalRecordingBoundary : ITerminalRecordingCellMetrics
    {
        void UseHostRecordingBoundary();
        void SubscribeRecordingOutput(Action<string, ReadOnlyMemory<byte>> handler);
        void RecordOutputAtHostBoundary(ReadOnlyMemory<byte> output);
    }

    /// <summary>Output-only recording control. Typed input is never persisted.</summary>
    public interface ITerminalRecorder
    {
        bool IsRecording { get; }
        Guid RecordingId => Guid.Empty;
        void StartRecording(string filePath);
        void StopRecording();
        Task StartRecordingAsync(string filePath) { StartRecording(filePath); return Task.CompletedTask; }
        Task StopRecordingAsync() { StopRecording(); return Task.CompletedTask; }
        // Legacy session implementations have no asynchronous failure source.
        event Action<RecordingFailure>? RecordingFailed { add { _ = value; } remove { _ = value; } }
    }

    /// <summary>
    /// Bounded in-memory "flight recorder" of a session's recent raw output and
    /// resizes, exportable on demand as a standard replay v2 file. Unlike
    /// <see cref="ITerminalRecorder"/>, nothing touches disk until an export is
    /// requested. Like manual recordings, the retained window never includes input events — exports can
    /// be triggered by agents and must not exfiltrate typed secrets (see
    /// docs/plans/2026-07-07-agent-host-a4-replay-design.md).
    /// </summary>
    public interface ITerminalFlightRecorder
    {
        bool IsFlightRecording { get; }

        /// <summary>
        /// Starts retaining recent output/resize events, bounded by
        /// <paramref name="maxTotalBytes"/> (payload bytes plus a fixed per-event
        /// overhead). Idempotent while already enabled.
        /// </summary>
        void EnableFlightRecording(long maxTotalBytes);

        /// <summary>Stops retaining events and discards the current window. Idempotent.</summary>
        void DisableFlightRecording();

        /// <summary>
        /// Writes the retained window to <paramref name="filePath"/> as a replay v2
        /// file. Returns false (without touching disk) when flight recording is not
        /// enabled.
        /// </summary>
        bool TryExportFlightRecording(string filePath, out FlightExportInfo info);

        /// <summary>Freezes recent output without I/O; invoke the returned file writer outside the parser queue.</summary>
        Func<string, FlightExportInfo>? CaptureFlightRecording() => null;
    }

    /// <summary>
    /// Composite contract preserved for backward compatibility. New code should depend on
    /// the narrowest sub-interface it actually needs.
    ///
    /// AttachBuffer(TerminalBuffer) and TakeSnapshot() have been removed: they coupled the
    /// Pty layer to VT and they were vestigial in practice - AttachBuffer was registration-only
    /// (consumers held the buffer ref elsewhere) and TakeSnapshot was never called externally.
    /// Recording continues to work; it just no longer captures a buffer snapshot at start/stop.
    /// See arch test Pty_must_not_depend_on_Vt and Phase 5 of the architecture-foundation plan.
    /// </summary>
    public interface ITerminalSession
        : ITerminalIO, ITerminalLifecycle, ITerminalShellMetadata, ITerminalRecorder, ITerminalFlightRecorder
    {
    }
}
