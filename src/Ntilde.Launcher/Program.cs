using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ntilde.Launcher;

/// <summary>
/// <c>ntilde.com</c> (Phase 4 spec §11). <c>Ntilde.exe</c> is a GUI-subsystem program, which a shell starts
/// and forgets: <c>ntilde mux attach</c> typed at a prompt came straight back to it, and the two fought over
/// the keyboard. This console program, published beside it and found first through PATHEXT, runs it in
/// this console, waits for it and exits with its exit code. Ctrl+C and Ctrl+Break reach the child from the
/// console they share; this process only declines to die of them (spec §2 decision 7). A GUI launch sets a
/// release event instead, and then this process exits 0 at once, so <c>ntilde</c> alone does not keep the
/// prompt until the window closes (spec §2 decision 6, §11.3).
/// <para>
/// Unsafe only for the console control handler's function pointer (<c>&amp;OnConsoleControl</c>), which NativeAOT
/// calls without a marshalled delegate (Sonar S6640 reviewed).
/// </para>
/// </summary>
internal static unsafe class Program // NOSONAR - S6640 reviewed: see the remarks above

{
    private const uint CtrlCEvent = 0;
    private const uint CtrlBreakEvent = 1;
    private const uint CtrlCloseEvent = 2;
    private const uint CloseGraceMilliseconds = 4000;

    // The child's process handle for the close handler, which runs on a thread of its own; 0 until it starts.
    private static nint s_child;

    private static int Main()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("ntilde.com runs on Windows only");
            return 1;
        }

        string target = Path.Combine(AppContext.BaseDirectory, "Ntilde.exe");
        if (!File.Exists(target))
        {
            Console.Error.WriteLine("ntilde: Ntilde.exe not found next to ntilde.com");
            return LauncherCommandLine.TargetMissingExitCode;
        }

        // The child reads the event's name from the environment it inherits: CreateProcessW gets no block of
        // its own. A value inherited from further up names another launcher's event, so it is replaced, or
        // removed when this one has no event - and then the GUI cannot release it, and it simply waits.
        string name = @"Local\ntilde-launcher-" + Guid.NewGuid().ToString("N");
        nint release = LauncherNative.CreateEventW(0, manualReset: 1, initialState: 0, name);
        Environment.SetEnvironmentVariable(LauncherCommandLine.ReleaseEventVariable, release != 0 ? name : null);

        _ = LauncherNative.SetConsoleCtrlHandler(&OnConsoleControl, 1);

        string commandLine = LauncherCommandLine.Build(target, LauncherCommandLine.Tail(LauncherNative.GetCommandLine()));
        if (!LauncherNative.TryStart(target, commandLine, out LauncherNative.ProcessInformation child, out int error))
        {
            Console.Error.WriteLine("ntilde: cannot start Ntilde.exe: " + Marshal.GetPInvokeErrorMessage(error));
            return 1;
        }

        _ = LauncherNative.CloseHandle(child.Thread);
        Volatile.Write(ref s_child, child.Process);
        return LauncherNative.WaitForChildOrRelease(child.Process, release);
    }

    /// <summary>
    /// Ctrl+C and Ctrl+Break: handled, so this process keeps waiting while the child - same console, same
    /// process group - gets the event from the console and decides for itself. Closing the console: up to
    /// <see cref="CloseGraceMilliseconds"/> for the child to finish its own shutdown first (Windows ends this
    /// process when the handler returns). Anything else takes the default action.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnConsoleControl(uint controlType)
    {
        switch (controlType)
        {
            case CtrlCEvent:
            case CtrlBreakEvent:
                return 1;
            case CtrlCloseEvent:
                nint child = Volatile.Read(ref s_child);
                if (child != 0) _ = LauncherNative.WaitForSingleObject(child, CloseGraceMilliseconds);
                return 1;
            default:
                return 0;
        }
    }
}
