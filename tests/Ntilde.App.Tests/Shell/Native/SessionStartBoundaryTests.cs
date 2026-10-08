using System.Diagnostics;
using Ntilde.Shell.Native;

namespace Ntilde.Tests.Shell.Native;

/// <summary>
/// R2's boundary: the time no live daemon session can predate. Windows: the later of the boot and this logon
/// (Fast Startup resumes the kernel, so the boot alone survives a "shut down"). Linux: /proc/stat's btime. macOS:
/// kern.boottime. The parsers are pinned on bytes; the real providers get one smoke test on their own OS.
/// </summary>
public sealed class SessionStartBoundaryTests
{
    private static readonly DateTime Year2000 = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── Linux: /proc/stat ──

    private const string ProcStat =
        "cpu  10132153 290696 3084719 46828483 16683 0 25195 0 0 0\n" +
        "cpu0 1393280 32966 572056 13343292 6130 0 17875 0 0 0\n" +
        "intr 1462898 0 0 0\n" +
        "ctxt 115315\n" +
        "btime 1696752000\n" +
        "processes 49842\n" +
        "procs_running 1\n";

    [Fact]
    public void Proc_stat_btime_is_the_boot_in_unix_seconds() =>
        Assert.Equal(new DateTime(2023, 10, 8, 8, 0, 0, DateTimeKind.Utc), SessionStartBoundary.FromProcStat(ProcStat));

    [Theory]
    [InlineData("cpu  1 2 3\nctxt 5\n")]          // no btime line
    [InlineData("btime\n")]                         // no value
    [InlineData("btime abc\n")]                     // not a number
    [InlineData("btime 0\n")]                       // not a boot
    [InlineData("btimes 1696752000\n")]             // a different key
    [InlineData("")]
    public void Proc_stat_without_a_usable_btime_gives_none(string text) => Assert.Null(SessionStartBoundary.FromProcStat(text));

    // ── macOS: struct timeval { long tv_sec; int tv_usec; } (16 bytes on 64-bit, padded) ──

    private static byte[] Timeval(long seconds, int micros)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(seconds).CopyTo(bytes, 0);
        BitConverter.GetBytes(micros).CopyTo(bytes, 8);
        return bytes;
    }

    [Fact]
    public void A_timeval_is_seconds_and_microseconds_since_the_unix_epoch() =>
        Assert.Equal(new DateTime(2026, 10, 8, 7, 30, 0, DateTimeKind.Utc).AddMilliseconds(250),
            SessionStartBoundary.FromTimeval(Timeval(1_791_444_600, 250_000)));

    [Fact]
    public void A_short_or_impossible_timeval_gives_none()
    {
        Assert.Null(SessionStartBoundary.FromTimeval(Timeval(1_791_444_600, 0).AsSpan(0, 8)));
        Assert.Null(SessionStartBoundary.FromTimeval(Timeval(0, 0)));
        Assert.Null(SessionStartBoundary.FromTimeval(Timeval(1_791_444_600, 1_000_000)));
        Assert.Null(SessionStartBoundary.FromTimeval(Timeval(1_791_444_600, -1)));
    }

    // ── Windows: WTSINFOW, LogonTime a FILETIME (LARGE_INTEGER) at offset 200 of 216 bytes ──

    private const long FileTime_2026_10_08_0730Z = 134_359_182_000_000_000;

    private static byte[] WtsInfo(long logonFileTime)
    {
        var bytes = new byte[216];
        // The fields around it hold other times: the parser must read LogonTime, not its neighbours.
        BitConverter.GetBytes(logonFileTime - 1).CopyTo(bytes, 192);   // LastInputTime
        BitConverter.GetBytes(logonFileTime).CopyTo(bytes, 200);       // LogonTime
        BitConverter.GetBytes(logonFileTime + 1).CopyTo(bytes, 208);   // CurrentTime
        return bytes;
    }

    [Fact]
    public void Wtsinfo_logon_time_is_a_utc_filetime() =>
        Assert.Equal(new DateTime(2026, 10, 8, 7, 30, 0, DateTimeKind.Utc), SessionStartBoundary.FromWtsInfo(WtsInfo(FileTime_2026_10_08_0730Z)));

    [Fact]
    public void A_short_or_empty_wtsinfo_gives_none()
    {
        Assert.Null(SessionStartBoundary.FromWtsInfo(WtsInfo(FileTime_2026_10_08_0730Z).AsSpan(0, 200)));
        Assert.Null(SessionStartBoundary.FromWtsInfo(WtsInfo(0)));    // no logon recorded
        Assert.Null(SessionStartBoundary.FromWtsInfo(WtsInfo(-5)));
    }

    // ── Windows: the later of the boot and the logon ──

    private static readonly DateTime Now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly long ThreeDays = (long)TimeSpan.FromDays(3).TotalMilliseconds;

    /// <summary>Fast Startup: the kernel was booted days ago and resumed; the user logged on this morning.</summary>
    [Fact]
    public void Windows_takes_the_logon_when_it_is_later_than_the_boot() =>
        Assert.Equal(Now - TimeSpan.FromHours(4), SessionStartBoundary.ForWindows(Now, ThreeDays, Now - TimeSpan.FromHours(4)));

    [Fact]
    public void Windows_takes_the_boot_when_it_is_later_than_the_logon() =>
        Assert.Equal(Now - TimeSpan.FromDays(3), SessionStartBoundary.ForWindows(Now, ThreeDays, Now - TimeSpan.FromDays(5)));

    /// <summary>
    /// No logon time: the boot alone. Ticks include sleep and hibernation on Windows, so it is never later than the
    /// real boot - an earlier boundary only ever keeps a notice, never hides one.
    /// </summary>
    [Fact]
    public void Windows_without_a_logon_time_takes_the_boot() =>
        Assert.Equal(Now - TimeSpan.FromDays(3), SessionStartBoundary.ForWindows(Now, ThreeDays, null));

    // ── The real providers, each on its own OS ──

    private static void AssertPlausible(DateTime? boundary)
    {
        DateTime value = Assert.NotNull(boundary);
        Assert.Equal(DateTimeKind.Utc, value.Kind);
        Assert.InRange(value, Year2000, DateTime.UtcNow);
    }

    [Fact]
    public void The_windows_boundary_is_read()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        AssertPlausible(SessionStartBoundary.Read());
    }

    [Fact]
    public void The_windows_logon_time_is_read()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        // Session 0 (a service) has no interactive logon to report.
        Assert.SkipWhen(Process.GetCurrentProcess().SessionId == 0, "Session 0 has no interactive logon.");
        AssertPlausible(SessionStartBoundary.ReadWindowsLogonUtc());
    }

    [Fact]
    public void The_linux_boundary_is_read()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        AssertPlausible(SessionStartBoundary.Read());
    }

    [Fact]
    public void The_macos_boundary_is_read()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS only.");
        AssertPlausible(SessionStartBoundary.Read());
    }
}
