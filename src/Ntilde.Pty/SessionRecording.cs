using System;
using System.Threading;
using System.Threading.Tasks;
using Ntilde.Replay;

namespace Ntilde.Pty;

/// <summary>Shared raw-session recording lifecycle; callers provide their output ordering boundary.</summary>
public sealed class SessionRecording : IDisposable
{
    private PtyRecorder? _current;
    public PtyRecorder? Current => Volatile.Read(ref _current);

    public PtyRecorder Begin(Func<PtyRecorder> create, Action<RecordingFailure> onFailure)
    {
        if (Current is { Failure: null } existing) return existing;
        PtyRecorder recorder = create();
        Volatile.Write(ref _current, recorder);
        int delivered = 0;
        void Deliver(RecordingFailure failure)
        {
            if (!ReferenceEquals(Current, recorder) || Interlocked.Exchange(ref delivered, 1) != 0) return;
            try { onFailure(failure); }
            catch (Exception ex) { PtyLogger.Warning($"[SessionRecording] Failure observer: {ex.Message}"); }
        }
        recorder.Failed += Deliver;
        // A file can fail before Begin installs its observer. Replay that latched failure off the caller's boundary.
        if (recorder.Failure is { } early) ThreadPool.QueueUserWorkItem(_ => Deliver(early));
        recorder.RecordMarker("START");
        return recorder;
    }

    public Task StopAsync()
    {
        PtyRecorder? recorder = Interlocked.Exchange(ref _current, null);
        if (recorder is null) return Task.CompletedTask;
        recorder.RecordMarker("END");
        return recorder.CompleteAsync();
    }

    public void Discard(PtyRecorder recorder)
    {
        if (ReferenceEquals(Interlocked.CompareExchange(ref _current, null, recorder), recorder)) recorder.Dispose();
    }

    public void Dispose()
    {
        // Lifetime cleanup must not prevent the terminal or native transport from closing.
        try { StopAsync().GetAwaiter().GetResult(); }
        catch (RecordingException) { /* Failure is already published; shutdown remains best-effort. */ }
    }
}
