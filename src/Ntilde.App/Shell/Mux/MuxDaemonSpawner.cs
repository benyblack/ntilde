using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ntilde.Shell.Mux;

internal interface IMuxDaemonSpawner
{
    void Spawn();
}

/// <summary>
/// Starts <c>&lt;exe&gt; mux serve</c> fully detached from the caller's stdio (spec §6): the daemon
/// outlives us and must never hold a pipe a parent is waiting on for EOF - the MSBuild-node hang
/// CLAUDE.md describes.
/// </summary>
internal sealed partial class ProcessMuxDaemonSpawner : IMuxDaemonSpawner
{
    private readonly string _executable;
    private readonly IReadOnlyList<string> _leadingArgs;

    public ProcessMuxDaemonSpawner(string executable, IReadOnlyList<string> leadingArgs)
    {
        _executable = executable;
        _leadingArgs = leadingArgs;
    }

    /// <summary>
    /// $APPIMAGE when running from an AppImage: Environment.ProcessPath is inside the runtime's FUSE
    /// mount, which is unmounted when the GUI exits and would pull the daemon's files out from under it.
    /// </summary>
    public static ProcessMuxDaemonSpawner CreateDefault()
    {
        string? appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        string exe = !string.IsNullOrEmpty(appImage) && File.Exists(appImage)
            ? appImage
            : Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unknown.");
        return new ProcessMuxDaemonSpawner(exe, []);
    }

    public void Spawn()
    {
        var psi = new ProcessStartInfo(_executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (string a in _leadingArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("mux");
        psi.ArgumentList.Add("serve");

        if (OperatingSystem.IsWindows()) ClearStdHandleInheritance();

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("mux serve did not start.");
        // Our ends of the three pipes: each closed independently, so one throwing (e.g. the child
        // already exited and its end of the pipe is gone) never leaves another of ours open - that
        // would be the exact hang class this class exists to prevent.
        CloseQuietly(process.StandardInput.Close);
        CloseQuietly(process.StandardOutput.Close);
        CloseQuietly(process.StandardError.Close);
    }

    private static void CloseQuietly(Action close)
    {
        try { close(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    // With redirection, CreateProcess runs with bInheritHandles=TRUE and the child inherits every
    // inheritable handle we hold - including std handles a parent (a CLI, a test runner) gave us.
    private static void ClearStdHandleInheritance()
    {
        foreach (int std in new[] { StdInputHandle, StdOutputHandle, StdErrorHandle })
        {
            IntPtr h = GetStdHandle(std);
            if (h != IntPtr.Zero && h != new IntPtr(-1)) SetHandleInformation(h, HandleFlagInherit, 0);
        }
    }

    private const int StdInputHandle = -10, StdOutputHandle = -11, StdErrorHandle = -12;
    private const uint HandleFlagInherit = 0x1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);
}
