using System.Diagnostics;
using Ntilde.Launcher;

namespace Ntilde.Tests.Launcher;

/// <summary>
/// The launcher's wait (Phase 4 spec §11.1 step 4, §11.3): the child's exit gives its exit code, the
/// release event gives 0 at once. Driven with real process handles and a real event, in-process.
/// </summary>
public sealed class LauncherWaitTests
{
    private static Process StartCmd(string arguments)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        Process process = Process.Start(psi)!;
        process.StandardInput.Close();
        return process;
    }

    private static void Stop(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already exited, or exiting as it was killed.
        }
    }

    [Fact]
    public void The_release_event_returns_0_while_the_child_still_runs()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ntilde.com is Windows-only.");
        using Process child = StartCmd("/d /c ping -n 60 127.0.0.1 >nul");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset);
        try
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            Task<int> wait = Task.Run(() => LauncherNative.WaitForChildOrRelease(child.Handle, release.SafeWaitHandle.DangerousGetHandle()), ct);
            Assert.False(wait.Wait(300, ct), "The wait returned before the child exited or the event was set.");

            release.Set();

            Assert.True(wait.Wait(10_000, ct), "The wait did not return on the release event.");
            Assert.Equal(0, wait.Result);
            Assert.False(child.HasExited);
        }
        finally
        {
            Stop(child);
        }
    }

    [Theory]
    [InlineData(7)]
    [InlineData(0)]
    // STATUS_CONTROL_C_EXIT: a DWORD above int.MaxValue comes back as the same 32 bits.
    [InlineData(-1073741510)]
    public void The_child_exiting_returns_its_exit_code(int exitCode)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ntilde.com is Windows-only.");
        using Process child = StartCmd($"/d /c exit {exitCode}");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset);
        try
        {
            Assert.Equal(exitCode, LauncherNative.WaitForChildOrRelease(child.Handle, release.SafeWaitHandle.DangerousGetHandle()));
        }
        finally
        {
            Stop(child);
        }
    }

    /// <summary>No event (its creation failed): the launcher still waits for the child and forwards its code.</summary>
    [Fact]
    public void Without_an_event_it_waits_for_the_child_alone()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ntilde.com is Windows-only.");
        using Process child = StartCmd("/d /c exit 5");
        try
        {
            Assert.Equal(5, LauncherNative.WaitForChildOrRelease(child.Handle, 0));
        }
        finally
        {
            Stop(child);
        }
    }
}
