using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Ntilde.Pty;
using Ntilde.Shell;
using Ntilde.VT;

namespace Ntilde.Tests.Input;

[Collection(PtyRealShellCollection.Name)]
public sealed class LegacyMouseByteInputPtyTests
{
    [AvaloniaFact]
    [Trait("Category", "PtySmoke")]
    public async Task High_coordinate_mouse_bytes_reach_a_real_raw_child_without_UTF8_expansion()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("This is a POSIX raw-PTY fixture; ConPTY byte behavior needs separate runtime qualification.");
            return;
        }
        Assert.SkipUnless(File.Exists("/bin/sh") && File.Exists("/bin/stty") && File.Exists("/bin/dd") && File.Exists("/usr/bin/od"),
            "Requires /bin/sh, /bin/stty, /bin/dd and /usr/bin/od for the POSIX raw-child fixture.");
        const string command = "/bin/stty raw -echo; printf 'NT_BYTE_READY\\n'; /bin/dd bs=1 count=6 2>/dev/null | /usr/bin/od -An -tx1; printf 'NT_BYTE_DONE\\n'";
        using var session = new RustPtySession("/bin/sh", 300, 250, args: "-c \"" + command + "\"", skipPowerShellPostLaunchInit: true);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new StringBuilder();
        session.OnOutputReceived += text =>
        {
            lock (output)
            {
                output.Append(text);
                string value = output.ToString();
                if (value.Contains("NT_BYTE_READY", StringComparison.Ordinal)) ready.TrySetResult();
                if (value.Contains("NT_BYTE_DONE", StringComparison.Ordinal)) done.TrySetResult();
            }
        };
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var buffer = new TerminalBuffer(300, 250);
        buffer.Modes.MouseModeX10 = true; // Existing ?1000 normal tracking, legacy coordinate encoding.
        var view = new TerminalView(); view.SetBuffer(buffer); view.SetSession(session); view.SetMetricsForTest(8, 16);
        view.HandleMousePressAt(new Point(95.5 * 8, 222.5 * 16), TerminalMouseButton.Left, KeyModifiers.None);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        string received;
        lock (output) received = output.ToString();
        string[] tokens = received.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { "NT_BYTE_READY", "1b", "5b", "4d", "20", "80", "ff", "NT_BYTE_DONE" }, tokens);
    }
}
