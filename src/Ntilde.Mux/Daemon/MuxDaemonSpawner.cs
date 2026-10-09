using System.Diagnostics;
using System.Runtime.InteropServices;
using Ntilde.Mux.Contracts;

namespace Ntilde.Mux.Daemon;

public interface IMuxDaemonSpawner
{
    void Spawn();

    /// <summary>
    /// The exit code of the daemon the latest <see cref="Spawn"/> started, once it has exited; null
    /// while it runs, or when the spawner cannot tell.
    /// </summary>
    int? LastSpawnExitCode => null;
}

/// <summary>
/// Starts <c>&lt;exe&gt; &lt;serve arguments&gt;</c> (<c>mux serve</c> for the GUI's exe, Phase 4 spec §6.2)
/// fully detached from the caller's stdio (Phase 2 spec §6): the daemon outlives us and must never hold
/// a pipe a parent is waiting on for EOF - the MSBuild-node hang CLAUDE.md describes.
/// </summary>
public sealed partial class ProcessMuxDaemonSpawner : IMuxDaemonSpawner
{
    private readonly string _executable;
    private readonly IReadOnlyList<string> _leadingArgs;
    private readonly IReadOnlyList<string> _serveArguments;
    private readonly MuxPaths? _paths;
    private readonly Func<string, string>? _imageResolver;
    private readonly object _gate = new();
    private Process? _last; // the latest daemon started, kept to read its exit code; guarded by _gate

    /// <param name="leadingArgs">Before the serve arguments: e.g. the dll, when <paramref name="executable"/> is <c>dotnet</c>.</param>
    /// <param name="serveArguments">What makes the executable serve: <c>["mux","serve"]</c> for the GUI's exe.</param>
    /// <param name="paths">
    /// The paths the daemon serves - the GUI's or <c>ntilde-mux</c>'s (<see cref="MuxPaths.IsStandalone"/>); null =
    /// whatever the daemon resolves from the environment it inherits. See <see cref="CreateStartInfo"/>.
    /// </param>
    /// <param name="imageResolver">
    /// Maps <paramref name="executable"/> to the file actually started, asked at each spawn: the App's copy outside a
    /// Windows install root, which an update would otherwise kill (Phase 5 spec R9). Null = the executable itself.
    /// </param>
    public ProcessMuxDaemonSpawner(string executable, IReadOnlyList<string> leadingArgs, IReadOnlyList<string> serveArguments, MuxPaths? paths = null,
        Func<string, string>? imageResolver = null)
    {
        _executable = executable;
        _leadingArgs = leadingArgs;
        _serveArguments = serveArguments;
        _paths = paths;
        _imageResolver = imageResolver;
    }

    /// <summary>
    /// $APPIMAGE when running from an AppImage: Environment.ProcessPath is inside the runtime's FUSE
    /// mount, which is unmounted when the GUI exits and would pull the daemon's files out from under it.
    /// </summary>
    /// <param name="paths">The paths the daemon serves; null = the ones it resolves from the inherited environment.</param>
    /// <param name="imageResolver">See the constructor: the App's copy of its executable outside a Windows install root.</param>
    public static ProcessMuxDaemonSpawner CreateDefault(IReadOnlyList<string> serveArguments, MuxPaths? paths = null, Func<string, string>? imageResolver = null)
    {
        string exe = ResolveDaemonExecutable(
            Environment.GetEnvironmentVariable("APPIMAGE"),
            Environment.GetEnvironmentVariable("APPDIR"),
            Environment.ProcessPath,
            File.Exists)
            ?? throw new InvalidOperationException("The executable path is unknown.");
        return new ProcessMuxDaemonSpawner(exe, [], serveArguments, paths, imageResolver);
    }

    /// <summary>
    /// The executable to run as the daemon. <paramref name="appImage"/> only when we really run from
    /// that AppImage - our own path inside its mount (<paramref name="appDir"/>). Both variables are
    /// inherited by every child process: an ntilde started from a shell inside an AppImage-launched
    /// ntilde (or any process with a stray/hostile $APPIMAGE) would otherwise spawn whatever file
    /// $APPIMAGE names as the daemon that runs every shell. Case-sensitive on Linux, where paths are.
    /// </summary>
    internal static string? ResolveDaemonExecutable(string? appImage, string? appDir, string? processPath, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (!string.IsNullOrEmpty(appImage) && !string.IsNullOrEmpty(appDir) && !string.IsNullOrEmpty(processPath)
            && IsUnder(processPath, appDir) && fileExists(appImage))
        {
            return appImage;
        }

        return processPath;
    }

    private static bool IsUnder(string path, string directory)
    {
        StringComparison comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        string dir = Path.TrimEndingDirectorySeparator(directory);
        if (dir.Length == 0) return false;
        return path.Length > dir.Length + 1
            && path.StartsWith(dir, comparison)
            && (path[dir.Length] == Path.DirectorySeparatorChar || path[dir.Length] == Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// The daemon's command line and environment; nothing is started. The daemon resolves its root by its
    /// executable's rule - the GUI's from <see cref="MuxDiscovery.RootOverrideEnvVar"/> or
    /// <see cref="MuxDiscovery.GetRootDirectory"/>, <c>ntilde-mux</c>'s from
    /// <see cref="MuxPaths.StandaloneRootOverrideEnvVar"/> or <see cref="MuxPaths.StandaloneRootDirectory"/> - so
    /// a root other than the one that rule finds in this environment is handed down through that rule's
    /// variable (<see cref="MuxPaths.RootHandDown"/>); otherwise the launcher would wait at one root for a daemon
    /// serving another. The root this environment already resolves to is left alone: the GUI's spawns stay
    /// exactly as before, and the daemon's shells do not gain the variable (it would make a GUI started
    /// from one of them skip <c>AppPaths</c>' legacy-root migration).
    /// </summary>
    internal ProcessStartInfo CreateStartInfo()
    {
        // Asked at each spawn, not once: a copy whose staging failed is tried again by the next daemon start.
        var psi = new ProcessStartInfo(_imageResolver is null ? _executable : _imageResolver(_executable))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = GetDaemonWorkingDirectory(),
        };
        foreach (string a in _leadingArgs) psi.ArgumentList.Add(a);
        foreach (string a in _serveArguments) psi.ArgumentList.Add(a);

        if (_paths?.RootHandDown() is { } handDown)
        {
            psi.Environment[handDown.Variable] = handDown.Root;
        }

        return psi;
    }

    public void Spawn()
    {
        ProcessStartInfo psi = CreateStartInfo();

        if (OperatingSystem.IsWindows()) ClearStdHandleInheritance();

        Process process = Process.Start(psi) ?? throw new InvalidOperationException("The multiplexer daemon did not start.");
        // Our ends of the three pipes: each closed independently, so one throwing (e.g. the child
        // already exited and its end of the pipe is gone) never leaves another of ours open - that
        // would be the exact hang class this class exists to prevent.
        CloseQuietly(process.StandardInput.Close);
        CloseQuietly(process.StandardOutput.Close);
        CloseQuietly(process.StandardError.Close);

        // Kept (only the latest: one process handle) so the launcher can tell a daemon that refused
        // to start because another holds the lock from one that is merely slow.
        Process? previous;
        lock (_gate)
        {
            previous = _last;
            _last = process;
        }

        previous?.Dispose();
    }

    public int? LastSpawnExitCode
    {
        get
        {
            lock (_gate)
            {
                try
                {
                    return _last is { HasExited: true } p ? p.ExitCode : null;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    return null;
                }
            }
        }
    }

    /// <summary>
    /// The daemon's own cwd - not a shared, world-writable one like the temp directory. Shells never
    /// inherit it: each spawn request carries its own starting directory.
    /// </summary>
    internal static string GetDaemonWorkingDirectory() =>
        GetDaemonWorkingDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Directory.Exists, MuxDiscovery.GetRootDirectory);

    /// <summary>
    /// <paramref name="profile"/> when it exists, else <paramref name="appDataRoot"/> (created). Never the caller's own
    /// cwd, nor the executable's directory: Velopack starts the GUI in its install's <c>current\</c>, and a process
    /// whose cwd is in there makes every update fail (Phase 5 spec R9).
    /// </summary>
    internal static string GetDaemonWorkingDirectory(string? profile, Func<string, bool> directoryExists, Func<string> appDataRoot)
    {
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(appDataRoot);
        if (!string.IsNullOrEmpty(profile) && directoryExists(profile)) return profile;
        string root = appDataRoot();
        Directory.CreateDirectory(root);
        return root;
    }

    private static void CloseQuietly(Action close)
    {
        try { close(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe end is already gone (e.g. the child exited): nothing of ours is left open.
        }
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

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);
}
