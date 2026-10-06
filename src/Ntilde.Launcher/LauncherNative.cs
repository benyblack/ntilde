using System.Runtime.InteropServices;

namespace Ntilde.Launcher;

/// <summary>
/// The kernel32 calls <c>ntilde.com</c> makes (Phase 4 spec §10.1, §11). Signatures are blittable - handles
/// as <c>nint</c>, <c>BOOL</c> as <c>int</c>, structs and buffers by pointer - so NativeAOT adds no marshalling
/// beyond pinning the two string names, and the console control handler is a function pointer, not a
/// marshalled delegate.
/// </summary>
internal static unsafe class LauncherNative
{
    private const string Kernel32 = "kernel32.dll";
    private const uint Infinite = 0xFFFFFFFF;
    private const uint WaitObject0 = 0;

    /// <summary><c>STARTUPINFOW</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfo
    {
        public uint Cb;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort CbReserved2;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    /// <summary><c>PROCESS_INFORMATION</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    /// <summary>This process's command line exactly as its creator passed it - not argv, which has lost the quoting.</summary>
    internal static string GetCommandLine() => new(GetCommandLineW());

    /// <summary>
    /// Starts <paramref name="target"/> with <paramref name="commandLine"/> as a plain child of this process:
    /// handles inherited, no creation flags (so no new console and no new process group: the child shares
    /// this console and receives its Ctrl+C itself, spec §2 decision 7), this environment and this working
    /// directory. It also gets this process's own startup info, so whatever standard handles the caller
    /// gave <c>ntilde.com</c> - a pipe, a file, the console - are the ones <c>Ntilde.exe</c> gets, the
    /// GUI-subsystem image included, as if the caller had started it directly.
    /// </summary>
    internal static bool TryStart(string target, string commandLine, out ProcessInformation child, out int error)
    {
        StartupInfo startup;
        GetStartupInfoW(&startup);

        // CreateProcessW may write to the command line, so it gets a private, NUL-terminated copy.
        char[] buffer = new char[commandLine.Length + 1];
        commandLine.CopyTo(buffer);

        ProcessInformation info;
        fixed (char* line = buffer)
        {
            if (CreateProcessW(target, line, 0, 0, inheritHandles: 1, creationFlags: 0, 0, 0, &startup, &info) == 0)
            {
                error = Marshal.GetLastPInvokeError();
                child = default;
                return false;
            }
        }

        error = 0;
        child = info;
        return true;
    }

    /// <summary>
    /// Waits until the child exits - its exit code, the same 32 bits even above <c>int.MaxValue</c> - or the
    /// release event is set - 0, without waiting for the child (spec §11.3). With no event (0) only the
    /// child is waited for. Should the wait fail, the child alone is waited for, and an exit code that cannot
    /// be read is 1.
    /// </summary>
    internal static int WaitForChildOrRelease(nint process, nint releaseEvent)
    {
        nint* handles = stackalloc nint[2];
        handles[0] = process;
        handles[1] = releaseEvent;
        uint result = WaitForMultipleObjects(releaseEvent == 0 ? 1u : 2u, handles, waitAll: 0, Infinite);
        if (result == WaitObject0 + 1) return 0;
        if (result != WaitObject0) _ = WaitForSingleObject(process, Infinite);

        uint exitCode;
        return GetExitCodeProcess(process, &exitCode) != 0 ? unchecked((int)exitCode) : 1;
    }

    [DllImport(Kernel32, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern char* GetCommandLineW();

    [DllImport(Kernel32, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void GetStartupInfoW(StartupInfo* startupInfo);

    [DllImport(Kernel32, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CreateProcessW(
        string applicationName,
        char* commandLine,
        nint processAttributes,
        nint threadAttributes,
        int inheritHandles,
        uint creationFlags,
        nint environment,
        nint currentDirectory,
        StartupInfo* startupInfo,
        ProcessInformation* processInformation);

    [DllImport(Kernel32, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern nint CreateEventW(nint eventAttributes, int manualReset, int initialState, string name);

    [DllImport(Kernel32, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int SetConsoleCtrlHandler(delegate* unmanaged[Stdcall]<uint, int> handler, int add);

    [DllImport(Kernel32, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint WaitForMultipleObjects(uint count, nint* handles, int waitAll, uint milliseconds);

    [DllImport(Kernel32, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport(Kernel32, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetExitCodeProcess(nint process, uint* exitCode);

    [DllImport(Kernel32, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int CloseHandle(nint handle);
}
