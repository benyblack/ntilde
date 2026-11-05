namespace Ntilde.Pty;

/// <summary>Optional passive Unix child-wait fact; unknown is not evidence of a live child.</summary>
public interface ITerminalNativeChildWaitObservation
{
    /// <summary>Copies cached fields only. Null means unsupported, disposed or missing original PID.</summary>
    NativeChildWaitObservation? ReadNativeChildWaitObservation();
}

/// <summary>Native try_wait returned Some for this original session/child. No ownership or whole-run claim.</summary>
public readonly record struct NativeChildWaitObservation(Guid NativeSessionId, int OriginalChildPid,
    bool NativeWaitObserved, int? ExitCode);
