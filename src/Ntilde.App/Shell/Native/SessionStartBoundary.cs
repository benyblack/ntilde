using System.Globalization;
using System.Runtime.InteropServices;

namespace Ntilde.Shell.Native;

/// <summary>
/// Spec R2's boundary: the time no live local daemon session can predate, because whatever ran before it ended with
/// the machine or the user's logon. A session file saved before it names sessions that are gone for that reason.
/// Null when the OS will not say, which keeps every restore's notices.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Windows: the later of the boot and this logon session's start. Fast Startup (on by default) makes "Shut
/// down" a logoff, which ends the daemon, plus a hibernated kernel, so the boot alone does not move; the logon
/// does.</item>
/// <item>Linux: the <c>btime</c> line of <c>/proc/stat</c>. macOS: <c>kern.boottime</c>. Both are wall-clock boot
/// times; the monotonic clock is not, as it stops while the machine sleeps.</item>
/// </list>
/// </remarks>
internal static class SessionStartBoundary
{
    public static DateTime? Read()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return ForWindows(DateTime.UtcNow, Environment.TickCount64, ReadWindowsLogonUtc());
            if (OperatingSystem.IsLinux()) return FromProcStat(File.ReadAllText("/proc/stat"));
            if (OperatingSystem.IsMacOS()) return ReadMacBootTimeUtc();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            AppLogger.Log($"[SessionStartBoundary] could not read the boot or logon time: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// The later of the boot (<paramref name="utcNow"/> less <paramref name="tickCount64"/>) and the logon. The tick
    /// count includes sleep and hibernation here, so without a logon time the boot is never later than the real one:
    /// an early boundary only ever keeps a notice, never hides one.
    /// </summary>
    internal static DateTime? ForWindows(DateTime utcNow, long tickCount64, DateTime? logonUtc)
    {
        DateTime boot = utcNow - TimeSpan.FromMilliseconds(tickCount64);
        return logonUtc is { } logon && logon > boot ? logon : boot;
    }

    /// <summary>The <c>btime</c> line of <c>/proc/stat</c>: the boot, in seconds since the Unix epoch.</summary>
    internal static DateTime? FromProcStat(string procStat)
    {
        foreach (string line in procStat.Split('\n'))
        {
            if (!line.StartsWith("btime ", StringComparison.Ordinal)) continue;
            return long.TryParse(line.AsSpan(6).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long seconds) && seconds > 0
                ? DateTime.UnixEpoch.AddSeconds(seconds)
                : null;
        }

        return null;
    }

    /// <summary>A <c>struct timeval { long tv_sec; int tv_usec; }</c> in native byte order (16 bytes on 64-bit).</summary>
    internal static DateTime? FromTimeval(ReadOnlySpan<byte> timeval)
    {
        if (timeval.Length < sizeof(long) + sizeof(int)) return null;
        long seconds = BitConverter.ToInt64(timeval);
        int micros = BitConverter.ToInt32(timeval[sizeof(long)..]);
        if (seconds <= 0 || micros is < 0 or >= 1_000_000) return null;
        return DateTime.UnixEpoch.AddSeconds(seconds).AddTicks(micros * TimeSpan.TicksPerMicrosecond);
    }

    // WTSINFOW: eight DWORDs (32 bytes), then WinStationName[32], Domain[17] and UserName[21] (WCHARs, 140 bytes;
    // 172 in all, padded to 176 for the LARGE_INTEGERs), then ConnectTime, DisconnectTime, LastInputTime, LogonTime
    // (at 200) and CurrentTime: 216 bytes.
    private const int WtsInfoLogonTimeOffset = 200;
    private const int WtsInfoSize = 216;

    /// <summary><c>WTSINFOW.LogonTime</c>: a FILETIME (100 ns since 1601, UTC) stored as a LARGE_INTEGER.</summary>
    internal static DateTime? FromWtsInfo(ReadOnlySpan<byte> wtsInfo)
    {
        if (wtsInfo.Length < WtsInfoLogonTimeOffset + sizeof(long)) return null;
        long fileTime = BitConverter.ToInt64(wtsInfo.Slice(WtsInfoLogonTimeOffset, sizeof(long)));
        return fileTime > 0 && fileTime <= DateTime.MaxValue.ToFileTimeUtc() ? DateTime.FromFileTimeUtc(fileTime) : null;
    }

    private const int WtsCurrentSession = -1; // WTS_CURRENT_SESSION
    private const int WtsSessionInfo = 24;    // WTS_INFO_CLASS.WTSSessionInfo

    /// <summary>When the current logon session started (Windows), or null.</summary>
    internal static DateTime? ReadWindowsLogonUtc()
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (WTSQuerySessionInformationW(IntPtr.Zero, WtsCurrentSession, WtsSessionInfo, out IntPtr buffer, out int bytes) == 0)
        {
            AppLogger.Log($"[SessionStartBoundary] WTSQuerySessionInformation failed ({Marshal.GetLastPInvokeError()})");
            return null;
        }

        try
        {
            if (buffer == IntPtr.Zero || bytes < WtsInfoSize) return null;
            var copy = new byte[WtsInfoSize];
            Marshal.Copy(buffer, copy, 0, WtsInfoSize);
            return FromWtsInfo(copy);
        }
        finally
        {
            if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
        }
    }

    private static DateTime? ReadMacBootTimeUtc()
    {
        byte[] name = "kern.boottime\0"u8.ToArray();
        var timeval = new byte[16];
        nuint length = (nuint)timeval.Length;
        if (SysctlByName(name, timeval, ref length, IntPtr.Zero, 0) != 0)
        {
            AppLogger.Log($"[SessionStartBoundary] sysctl kern.boottime failed ({Marshal.GetLastPInvokeError()})");
            return null;
        }

        return FromTimeval(timeval.AsSpan(0, (int)Math.Min(length, (nuint)timeval.Length)));
    }

    [DllImport("wtsapi32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("libc", EntryPoint = "sysctlbyname", SetLastError = true)]
    private static extern int SysctlByName(byte[] name, [Out] byte[] oldValue, ref nuint oldLength, IntPtr newValue, nuint newLength);
}
