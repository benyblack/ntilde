using Avalonia.Controls;
using Avalonia.Threading;
using Ntilde.AppTests.Shell;
using Ntilde.Shell.Clipboard;
using System.Diagnostics;

namespace Ntilde.ClipboardQa;

/// <summary>Real App.Initialize in fresh headless children; distinct from native clipboard interchange.</summary>
internal static class HeadlessStartupProbe
{
    private const string SelfTest = "--headless-startup-selftest";
    private const string Child = "--headless-startup-child";
    private const string ResultPrefix = "NTILDE_HEADLESS_STARTUP_RESULT|";
    internal static bool IsHeadlessMode(string[] args) => args.Length > 0 && args[0] is SelfTest or Child;

    internal static int Run(string[] args)
    {
        try
        {
            if (args[0] == SelfTest)
                return args.Length == 2 && Path.IsPathFullyQualified(args[1]) && Directory.Exists(args[1])
                    ? VerifyInSubprocesses(args[1]) : 2;
            return RunChild(args);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Headless startup guard proof failed: " + error);
            return 1;
        }
    }

    internal static int VerifyInSubprocesses(string root)
    {
        string scratch = Path.Combine(root, "headless-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(scratch, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string report = Path.Combine(root, "headless-startup.txt");
        File.Delete(report); // A failed repeat must not leave an earlier success marker.
        try
        {
            RunControl(scratch, guarded: false);
            RunControl(scratch, guarded: true);
            Directory.Delete(scratch, recursive: true);
            File.WriteAllText(report, "ordinary=fake-provider-write;private=provider-refused;native-interchange=not-measured\n");
            return 0;
        }
        catch (Exception error)
        {
            if (Directory.Exists(scratch)) File.WriteAllText(Path.Combine(scratch, "failure.txt"), error.ToString());
            Console.Error.WriteLine("Headless startup proof failed; retained " + scratch);
            return 1;
        }
    }

    private static void RunControl(string root, bool guarded)
    {
        string runId = Guid.NewGuid().ToString("N");
        string appData = Path.Combine(root, runId);
        Directory.CreateDirectory(appData);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(appData, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add(Child);
        start.ArgumentList.Add(guarded ? "private" : "ordinary");
        if (guarded)
        {
            start.ArgumentList.Add("--ntilde-terminfo-owner=" + runId);
            start.ArgumentList.Add(PrivateClipboardQaMode.Flag + runId);
        }
        start.Environment["TERMINFO_RUN_ID"] = runId;
        start.Environment["NTILDE_APPDATA_ROOT"] = appData;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the headless child.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5_000);
            throw new TimeoutException("Headless startup child did not exit.");
        }
        string output = stdout.GetAwaiter().GetResult();
        string errors = stderr.GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(appData, "stdout.txt"), output);
        File.WriteAllText(Path.Combine(appData, "stderr.txt"), errors);
        string expected = ResultPrefix + (guarded ? "private|0|0" : "ordinary|1|1");
        if (process.ExitCode != 0 || !output.Split('\n').Any(line => line.TrimEnd('\r') == expected))
            throw new InvalidOperationException("Headless startup control failed; exit=" + process.ExitCode + "; expected=" + expected);
    }

    private static int RunChild(string[] args)
    {
        bool guarded = args.Length == 4 && args[1] == "private";
        if (!guarded && !(args.Length == 2 && args[1] == "ordinary")) return 2;
        // This is the actual production application class and its Initialize call.
        // Do not install mode/control guards here or use a scoped policy override.
        Program.InitializeHeadlessAppForStartupProof();
        if (PrivateClipboardQaMode.Process.IsEnabled != guarded) return 2;
        var (window, recorder) = RecordingClipboardWindow.Create();
        using var clipboard = recorder;
        try
        {
            var text = new TextBox { Text = "source-known headless startup text" };
            window.Content = text;
            window.Show(); Dispatcher.UIThread.RunJobs();
            text.SelectionStart = 0; text.SelectionEnd = 12;
            text.Copy(); Dispatcher.UIThread.RunJobs();
            Console.WriteLine(ResultPrefix + (guarded ? "private" : "ordinary") + "|" + recorder.Resolutions + "|" + recorder.Writes);
            int expected = guarded ? 0 : 1;
            bool matches = recorder.Resolutions == expected && recorder.Writes == expected;
            return matches ? 0 : 1;
        }
        finally { window.Close(); }
    }
}
