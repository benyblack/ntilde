using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Ntilde.Launcher;

namespace Ntilde.Tests.Launcher;

/// <summary>
/// The launcher as built, run as a process (Phase 4 spec §11, §12.2). Its framework-dependent build - the
/// exe, its dll and runtimeconfig, which the ProjectReference copies beside these tests - goes into a
/// temporary directory beside a copy of <c>cmd.exe</c> named <c>Ntilde.exe</c>, so every run goes through
/// the same raw command line, <c>CreateProcessW</c> and wait that the published <c>ntilde.com</c> does.
/// </summary>
public sealed class LauncherEndToEndTests : IDisposable
{
    private static readonly string[] RequiredLauncherFiles =
        ["Ntilde.Launcher.exe", "Ntilde.Launcher.dll", "Ntilde.Launcher.runtimeconfig.json"];

    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(60);

    private readonly string _dir = Directory.CreateTempSubdirectory("ntilde-launcher-").FullName;
    private int? _orphanPid;

    private string Launcher => Path.Combine(_dir, "Ntilde.Launcher.exe");

    private void CopyLauncher()
    {
        foreach (string name in RequiredLauncherFiles)
        {
            string source = Path.Combine(AppContext.BaseDirectory, name);
            Assert.True(File.Exists(source),
                $"{source} is missing: App.Tests' ProjectReference to Ntilde.Launcher should copy the launcher's build beside the tests.");
            File.Copy(source, Path.Combine(_dir, name));
        }

        string deps = Path.Combine(AppContext.BaseDirectory, "Ntilde.Launcher.deps.json");
        if (File.Exists(deps)) File.Copy(deps, Path.Combine(_dir, "Ntilde.Launcher.deps.json"));
    }

    private void CopyCmdAsNtilde() =>
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(_dir, "Ntilde.exe"));

    /// <summary>Every handle the launcher gets is ours, so nothing it starts can hold the test runner's pipes.</summary>
    private Process Start(string arguments, string? launcher = null)
    {
        var psi = new ProcessStartInfo(launcher ?? Launcher, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _dir,
        };
        Process process = Process.Start(psi)!;
        process.StandardInput.Close();
        return process;
    }

    private (int ExitCode, string Stdout, string Stderr) Run(string arguments, string? launcher = null)
    {
        using Process process = Start(arguments, launcher);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(RunTimeout))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"The launcher did not exit within {RunTimeout} for: {arguments}");
        }

        process.WaitForExit();
        return (process.ExitCode, stdout.Result, stderr.Result);
    }

    [Fact]
    public void Forwards_the_childs_exit_code()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ntilde.com is Windows-only.");
        CopyLauncher();
        CopyCmdAsNtilde();

        Assert.Equal(7, Run("/d /c exit 7").ExitCode);
        Assert.Equal(0, Run("/d /c exit 0").ExitCode);
    }

    /// <summary>
    /// Re-parsing argv and re-quoting it would give cmd <c>"a  b&amp;exit"</c> as one word; only the raw
    /// tail reaches it as written.
    /// </summary>
    [Fact]
    public void Passes_the_arguments_byte_identical()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ntilde.com is Windows-only.");
        CopyLauncher();
        CopyCmdAsNtilde();

        var (exitCode, stdout, _) = Run("/d /c echo \"a  b\"&exit 0");

        Assert.Equal(0, exitCode);
        Assert.Contains("\"a  b\"", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Hands_the_child_its_release_event()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ntilde.com is Windows-only.");
        CopyLauncher();
        CopyCmdAsNtilde();

        var (exitCode, stdout, _) = Run($"/d /c echo [%{LauncherCommandLine.ReleaseEventVariable}%]");

        Assert.Equal(0, exitCode);
        Assert.Matches(new Regex(@"\[Local\\ntilde-launcher-[0-9a-f]{32}\]"), stdout);
    }

    [Fact]
    public void Without_Ntilde_exe_beside_it_exits_9009()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ntilde.com is Windows-only.");
        CopyLauncher();

        var (exitCode, _, stderr) = Run("mux ls");

        Assert.Equal(LauncherCommandLine.TargetMissingExitCode, exitCode);
        Assert.Contains("ntilde: Ntilde.exe not found next to ntilde.com", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// Spec §11.1: off Windows the launcher only refuses. The ProjectReference copies its apphost beside these
    /// tests on every OS (extensionless off Windows; CI restores its execute bit with the test host's), and it
    /// runs from there: nothing is beside it, and the refusal comes before it looks.
    /// </summary>
    [Fact]
    public void Off_Windows_it_refuses_with_exit_1()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The refusal is the launcher's non-Windows path.");
        string apphost = Path.Combine(AppContext.BaseDirectory, "Ntilde.Launcher");
        Assert.True(File.Exists(apphost),
            $"{apphost} is missing: App.Tests' ProjectReference to Ntilde.Launcher should copy the launcher's apphost beside the tests.");

        var (exitCode, stdout, stderr) = Run("mux ls", apphost);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("ntilde.com runs on Windows only", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// Spec §11.3: a child that signals the event it was handed releases the launcher at once with 0, while
    /// the child keeps running - which is what the GUI does so the prompt comes back. The child here is
    /// Windows PowerShell under the cmd copy; it records its pid before signalling, so the test can show it
    /// is still alive after the launcher returned, and then stop it.
    /// </summary>
    [Fact]
    public void Returns_0_once_the_child_signals_the_release_event()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ntilde.com is Windows-only.");
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.SkipUnless(File.Exists(powershell), "Windows PowerShell is not installed.");
        CopyLauncher();
        CopyCmdAsNtilde();

        string pidFile = Path.Combine(_dir, "child.pid");
        string script =
            $"Set-Content -LiteralPath '{pidFile}' -Value $PID; " +
            $"[System.Threading.EventWaitHandle]::OpenExisting($env:{LauncherCommandLine.ReleaseEventVariable}).Set() | Out-Null; " +
            "Start-Sleep -Seconds 120";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        // Not read to the end: the still-running child holds the pipes until it is stopped below.
        using Process launcher = Start($"/d /c \"{powershell}\" -NoProfile -NonInteractive -EncodedCommand {encoded}");
        bool exited = launcher.WaitForExit(RunTimeout);
        if (File.Exists(pidFile)) _orphanPid = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        if (!exited) launcher.Kill(entireProcessTree: true);

        Assert.True(exited, "The launcher kept waiting after the child signalled the release event.");
        Assert.Equal(0, launcher.ExitCode);
        Assert.NotNull(_orphanPid);
        using Process child = Process.GetProcessById(_orphanPid.Value);
        Assert.False(child.HasExited, "The child should still be running: the launcher returned on the event, not on its exit.");
    }

    public void Dispose()
    {
        if (_orphanPid is int pid)
        {
            try
            {
                using Process orphan = Process.GetProcessById(pid);
                orphan.Kill(entireProcessTree: true);
                orphan.WaitForExit(10_000);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
        }

        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A process that just exited may still map the copied exe for a moment.
                Thread.Sleep(200);
            }
        }
    }
}
