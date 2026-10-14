namespace Ntilde.Shell.Mux;

/// <summary>
/// Whether this launch should expect the previous session's daemon sessions to be gone: the session file was saved
/// before the boundary no live daemon session can predate (the boot, or on Windows the later of the boot and this
/// logon; <see cref="Ntilde.Shell.Native.SessionStartBoundary"/>), so a reboot or a logoff ended them (R2).
/// </summary>
internal static class MuxRestoreExpectations
{
    /// <summary>
    /// True when both times are known and <paramref name="sessionSavedUtc"/> is before
    /// <paramref name="sessionsCannotPredateUtc"/>. A save time after it (a file from the future, by clock skew,
    /// included), or a boundary the OS would not give, keeps today's notices.
    /// </summary>
    public static bool SessionsEndedByReboot(DateTime? sessionSavedUtc, DateTime? sessionsCannotPredateUtc) =>
        sessionSavedUtc is { } saved && sessionsCannotPredateUtc is { } boundary && saved < boundary;
}
