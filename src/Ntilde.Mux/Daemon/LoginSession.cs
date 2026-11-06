using System.Runtime.InteropServices;

namespace Ntilde.Mux.Daemon;

/// <summary>
/// Whether the daemon has outlived the macOS login session it was started in. The daemon calls <c>setsid()</c> and detaches,
/// so it outlives a logout, but its launchd bootstrap port belonged to that login: once the login ends, the port is dead,
/// and so is every shell's the daemon starts afterwards (they inherit it). In those shells every lookup that goes through
/// launchd fails: <c>getpwuid</c> answers nothing, so <c>whoami</c> prints the uid and <c>ssh</c> says "No user exists for
/// uid 501", and <c>launchctl</c>, the keychain and the pasteboard fail too. Nothing inside the daemon can repair that; a
/// daemon started from the new login can.
/// </summary>
/// <remarks>
/// The probe asks the bootstrap server for opendirectoryd's libinfo service, which is what <c>getpwuid</c> talks to. Only
/// a Mach IPC failure - nobody at the other end of the port, or no port at all - counts as ended: a live bootstrap server
/// that does not know the name (a future macOS renaming it) answers with a <c>BOOTSTRAP_*</c> code and is not reported.
/// Every other platform, and a macOS whose symbols cannot be found, reports false.
/// </remarks>
public static class LoginSession
{
    /// <summary>The service <c>getpwuid</c> and friends reach through the bootstrap port.</summary>
    internal const string ProbeServiceName = "com.apple.system.opendirectoryd.libinfo";

    /// <summary><see cref="ProbeServiceName"/> NUL-terminated, as <c>bootstrap_look_up</c> takes it: no string marshalling.</summary>
    private static readonly byte[] s_probeServiceName = "com.apple.system.opendirectoryd.libinfo\0"u8.ToArray();

    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    /// <summary>The error "system" of Mach IPC send/receive failures (<c>err_mach_ipc</c>, <c>mach/error.h</c>).</summary>
    private const int MachIpcSystem = 4;

    /// <summary>True when the probe found the bootstrap port dead (macOS only); false when it answered or could not be asked.</summary>
    public static bool HasEnded()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            if (!NativeLibrary.TryLoad(LibSystem, out IntPtr lib)) return false;
            if (!NativeLibrary.TryGetExport(lib, "bootstrap_port", out IntPtr bootstrapPortAddress)
                || !NativeLibrary.TryGetExport(lib, "mach_task_self_", out IntPtr taskSelfAddress))
            {
                return false;
            }

            uint bootstrapPort = (uint)Marshal.ReadInt32(bootstrapPortAddress);
            int kr = bootstrap_look_up(bootstrapPort, s_probeServiceName, out uint service);
            if (service != 0) _ = mach_port_deallocate((uint)Marshal.ReadInt32(taskSelfAddress), service);
            return IsIpcFailure(kr);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="kernReturn"/> is a Mach IPC failure (<c>MACH_SEND_*</c>/<c>MACH_RCV_*</c>): the message never
    /// reached a bootstrap server, or none answered. <c>KERN_SUCCESS</c> and <c>BOOTSTRAP_*</c> codes are answers.
    /// </summary>
    internal static bool IsIpcFailure(int kernReturn) => kernReturn != 0 && ((kernReturn >> 26) & 0x3f) == MachIpcSystem;

    [DllImport(LibSystem, EntryPoint = "bootstrap_look_up")]
    private static extern int bootstrap_look_up(uint bootstrapPort, byte[] serviceName, out uint servicePort);

    [DllImport(LibSystem, EntryPoint = "mach_port_deallocate")]
    private static extern int mach_port_deallocate(uint task, uint name);
}
