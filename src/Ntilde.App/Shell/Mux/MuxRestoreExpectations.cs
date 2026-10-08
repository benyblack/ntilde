namespace Ntilde.Shell.Mux;

/// <summary>
/// Whether this launch should expect the previous session's daemon sessions to be gone: the session
/// file was saved before the machine last booted, so a reboot ended them (R2).
/// </summary>
/// <remarks>
/// <see cref="Environment.TickCount64"/> counts from boot. On Windows it includes time asleep; on Linux and macOS
/// it does not, so there the boot time it gives is later than the real one by the time spent asleep since. A file
/// saved before a sleep then counts as predating boot, and a session lost to a daemon crash rather than a reboot
/// restores quietly too (R2's cost if wrong: such a loss is not announced).
/// </remarks>
internal static class MuxRestoreExpectations
{
    public static DateTime BootTimeUtc(Func<DateTime> utcNow, Func<long> tickCount64) => utcNow() - TimeSpan.FromMilliseconds(tickCount64());

    /// <summary>
    /// True when <paramref name="sessionSavedUtc"/> is before <paramref name="bootTimeUtc"/>. No save time, or one
    /// after boot (a file from the future, by clock skew, included), is not a reboot: today's notices stand.
    /// </summary>
    public static bool SessionsEndedByReboot(DateTime? sessionSavedUtc, DateTime bootTimeUtc) => sessionSavedUtc is { } saved && saved < bootTimeUtc;
}
