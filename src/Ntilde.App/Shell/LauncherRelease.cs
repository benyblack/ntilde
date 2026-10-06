using System;
using System.Runtime.InteropServices;

namespace Ntilde.Shell;

/// <summary>
/// The App's half of <c>ntilde.com</c>'s GUI release (Phase 4 spec §2 decision 6, §11.3). The launcher waits
/// for this process and forwards its exit code, which is what a CLI verb needs and the GUI does not: the
/// prompt would stay blocked until the window closed. So it hands down the name of an event in
/// <see cref="Variable"/>, and a GUI launch sets it; the launcher then exits 0 and the prompt returns.
/// </summary>
internal static class LauncherRelease
{
    /// <summary>
    /// The launcher's <c>LauncherCommandLine.ReleaseEventVariable</c>, repeated because the App does not
    /// reference the launcher; <c>LauncherReleaseTests</c> pins the two together.
    /// </summary>
    internal const string Variable = "NTILDE_LAUNCHER_RELEASE";

    private const uint EventModifyState = 0x0002;

    /// <summary>
    /// Sets the event <see cref="Variable"/> names and removes the variable from this process's environment,
    /// so no shell this GUI starts inherits it. A no-op off Windows and without the variable; when the event
    /// is gone (its launcher already exited) only the variable goes. Program.Main calls it on the GUI path,
    /// once no CLI mode has matched.
    /// </summary>
    public static void Signal()
    {
        if (!OperatingSystem.IsWindows()) return;
        string? name = Take();
        if (name is null) return;

        nint handle = OpenEventW(EventModifyState, 0, name);
        if (handle == 0) return;
        _ = SetEvent(handle);
        _ = CloseHandle(handle);
    }

    /// <summary>
    /// Removes the variable without setting the event, for the <c>mux</c> verbs: their launcher keeps waiting
    /// for the exit code, and the daemon <c>mux attach</c> may start inherits this environment and would hand
    /// the variable to every shell it hosts - where a GUI started from one would release a launcher whose
    /// attach is still running. A no-op off Windows.
    /// </summary>
    public static void Discard()
    {
        if (!OperatingSystem.IsWindows()) return;
        _ = Take();
    }

    private static string? Take()
    {
        string? name = Environment.GetEnvironmentVariable(Variable);
        if (name is null) return null;
        Environment.SetEnvironmentVariable(Variable, null);
        return name.Length > 0 ? name : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint OpenEventW(uint desiredAccess, int inheritHandle, string name);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SetEvent(nint handle);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CloseHandle(nint handle);
}
