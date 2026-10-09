using System.Diagnostics;

namespace Ntilde.Tests.Shell.Mux.Remote;

/// <summary>
/// Runs a produced remote command line under a real POSIX <c>sh</c>, as the login shell on the remote host would.
/// There is none on a bare Windows box; Git for Windows ships one, which is looked for when PATH has none.
/// </summary>
internal static class PosixShell
{
    private static readonly Lazy<string?> Found = new(Find);

    /// <summary>Skips the calling test when no <c>sh</c> is available.</summary>
    public static string Require()
    {
        Assert.SkipUnless(Found.Value is not null, "no POSIX sh on this machine");
        return Found.Value!;
    }

    /// <summary>Where <paramref name="windowsOrPosixPath"/> is spelled to this sh: a drive path becomes <c>/c/…</c> on Windows.</summary>
    public static string ToShellPath(string path)
    {
        if (!OperatingSystem.IsWindows()) return path;
        string full = Path.GetFullPath(path);
        return "/" + char.ToLowerInvariant(full[0]) + full[2..].Replace('\\', '/');
    }

    /// <summary>
    /// Runs <paramref name="command"/> as the login shell's command line: <c>sh -c "&lt;command&gt;"</c>, with
    /// <paramref name="environment"/> over a clean-ish environment (null removes a variable).
    /// </summary>
    public static (int ExitCode, string Stdout, string Stderr) Run(string command, IReadOnlyDictionary<string, string?> environment)
    {
        var psi = new ProcessStartInfo(Require())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
        // The produced commands run `sh` and the usual tools by name: a Windows PATH may not hold Git's usr/bin.
        string shDir = Path.GetDirectoryName(psi.FileName)!;
        psi.Environment["PATH"] = shDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        foreach ((string key, string? value) in environment)
        {
            if (value is null) psi.Environment.Remove(key);
            else psi.Environment[key] = value;
        }

        using Process process = Process.Start(psi)!;
        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"sh did not finish: {command}");
        }

        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    /// <summary>Writes an executable script that prints its <c>$0</c> and arguments, one line.</summary>
    public static void WriteEchoScript(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "#!/bin/sh\necho \"[$0] $*\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static string? Find()
    {
        string exe = OperatingSystem.IsWindows() ? "sh.exe" : "sh";
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
            }
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (string root in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
            {
                if (string.IsNullOrEmpty(root)) continue;
                string git = Path.Combine(root, "Git", "usr", "bin", "sh.exe");
                if (File.Exists(git)) return git;
            }
        }

        return null;
    }
}
