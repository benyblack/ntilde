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
/// proof that nobody is at the other end counts as ended: no bootstrap port at all (what a shell of such a daemon has), or
/// <c>MACH_SEND_INVALID_DEST</c> (a dead one). A transient failure (a timeout, no buffer) proves nothing and is not reported,
/// nor is a live bootstrap server's answer that it does not know the name (a future macOS renaming it): a restart offered
/// by mistake would close the user's shells. Every other platform, and a macOS whose symbols cannot be found, reports false.
/// </remarks>
public static class LoginSession
{
    /// <summary>The service <c>getpwuid</c> and friends reach through the bootstrap port.</summary>
    internal const string ProbeServiceName = "com.apple.system.opendirectoryd.libinfo";

    /// <summary><see cref="ProbeServiceName"/> NUL-terminated, as <c>bootstrap_look_up</c> takes it: no string marshalling.</summary>
    private static readonly byte[] s_probeServiceName = "com.apple.system.opendirectoryd.libinfo\0"u8.ToArray();

    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    /// <summary><c>MACH_SEND_INVALID_DEST</c> (<c>mach/message.h</c>): the destination port is dead or not a send right.</summary>
    internal const int MachSendInvalidDest = 0x10000003;

    /// <summary>
    /// Where libSystem keeps <c>bootstrap_port</c> and <c>mach_task_self_</c>, looked up once (the library stays loaded for
    /// the process's life, as libSystem always does); null off macOS or when either cannot be found. The values are read on
    /// each probe.
    /// </summary>
    private static readonly Lazy<(IntPtr BootstrapPort, IntPtr TaskSelf)?> s_globals = new(FindGlobals);

    /// <summary>True when the probe found no live bootstrap port (macOS only); false when it answered or proved nothing.</summary>
    public static bool HasEnded()
    {
        if (!OperatingSystem.IsMacOS() || s_globals.Value is not { } globals) return false;
        try
        {
            uint bootstrapPort = (uint)Marshal.ReadInt32(globals.BootstrapPort);
            if (bootstrapPort == 0) return true; // MACH_PORT_NULL: there is no bootstrap server to ask
            int kr = bootstrap_look_up(bootstrapPort, s_probeServiceName, out uint service);
            if (service != 0) _ = mach_port_deallocate((uint)Marshal.ReadInt32(globals.TaskSelf), service);
            return IsDeadDestination(kr);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="kernReturn"/> proves the bootstrap port dead: <c>MACH_SEND_INVALID_DEST</c> only. Success, a
    /// <c>BOOTSTRAP_*</c> answer and transient IPC failures do not.
    /// </summary>
    internal static bool IsDeadDestination(int kernReturn) => kernReturn == MachSendInvalidDest;

    private static (IntPtr BootstrapPort, IntPtr TaskSelf)? FindGlobals()
    {
        if (!OperatingSystem.IsMacOS() || !NativeLibrary.TryLoad(LibSystem, out IntPtr lib)) return null;
        return NativeLibrary.TryGetExport(lib, "bootstrap_port", out IntPtr bootstrapPort)
            && NativeLibrary.TryGetExport(lib, "mach_task_self_", out IntPtr taskSelf)
            ? (bootstrapPort, taskSelf)
            : null;
    }

    [DllImport(LibSystem, EntryPoint = "bootstrap_look_up")]
    private static extern int bootstrap_look_up(uint bootstrapPort, byte[] serviceName, out uint servicePort);

    [DllImport(LibSystem, EntryPoint = "mach_port_deallocate")]
    private static extern int mach_port_deallocate(uint task, uint name);
}
