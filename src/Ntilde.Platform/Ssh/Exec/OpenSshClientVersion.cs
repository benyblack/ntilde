using System.Diagnostics;
using System.Globalization;

namespace Ntilde.Platform.Ssh.Exec;

/// <summary>
/// The OpenSSH client's version, as <c>ssh -V</c> prints it to stderr (<c>OpenSSH_9.5p1, LibreSSL 3.8.2</c>,
/// <c>OpenSSH_for_Windows_8.1p1, LibreSSL 3.0.2</c>, <c>OpenSSH_8.2p1 Ubuntu-4ubuntu0.11, OpenSSL 1.1.1f  31 Mar 2020</c>),
/// and what it says about the prompts that client writes.
/// </summary>
public static class OpenSshClientVersion
{
    /// <summary>
    /// The first release that puts <c>(user@host) </c> in front of a keyboard-interactive prompt. Before it such a prompt
    /// is the server's text alone (<c>Password: </c>), which does not say which user and host it is for.
    /// </summary>
    public static readonly Version KeyboardInteractivePrefixSince = new(8, 4);

    /// <summary>How long <see cref="Probe(string)"/> lets <c>ssh -V</c> run: it prints one line and exits at once.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long the output is waited for after the exit, at least, however little of the timeout is left.</summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(1);

    private const string Product = "OpenSSH_";
    private const string WindowsPort = "for_Windows_";

    /// <summary>
    /// Whether a client of <paramref name="version"/> names the target in its keyboard-interactive prompts
    /// (<see cref="KeyboardInteractivePrefixSince"/>). No version - one that could not be read - does not.
    /// </summary>
    public static bool PrefixesKeyboardInteractivePrompts(Version? version) => version is not null && version >= KeyboardInteractivePrefixSince;

    /// <summary>
    /// The client's major and minor version from <paramref name="output"/>, what <c>ssh -V</c> printed; null when no line
    /// of it starts with OpenSSH's version: <c>OpenSSH_</c>, on the Windows port <c>OpenSSH_for_Windows_</c>, then
    /// <c>major.minor</c>, then the end, a blank, a comma or a suffix such as <c>p1</c> - anything but another digit or dot.
    /// Another client (Dropbear, Sun SSH) has none.
    /// </summary>
    public static Version? Parse(string? output)
    {
        if (output is null) return null;

        foreach (string line in output.Split('\n'))
        {
            ReadOnlySpan<char> text = line.AsSpan().Trim();
            if (text.StartsWith(Product, StringComparison.Ordinal)) return ParseRelease(text[Product.Length..]);
        }

        return null;
    }

    private static Version? ParseRelease(ReadOnlySpan<char> text)
    {
        if (text.StartsWith(WindowsPort, StringComparison.Ordinal)) text = text[WindowsPort.Length..];
        if (!TakeNumber(ref text, out int major) || text.IsEmpty || text[0] != '.') return null;
        text = text[1..];
        if (!TakeNumber(ref text, out int minor) || (!text.IsEmpty && text[0] == '.')) return null;
        return new Version(major, minor);
    }

    /// <summary>The ASCII digits at the start of <paramref name="text"/>, at least one and few enough for an int.</summary>
    private static bool TakeNumber(ref ReadOnlySpan<char> text, out int number)
    {
        int digits = 0;
        while (digits < text.Length && char.IsAsciiDigit(text[digits])) digits++;
        if (digits is 0 or > 6 || !int.TryParse(text[..digits], NumberStyles.None, CultureInfo.InvariantCulture, out number))
        {
            number = 0;
            return false;
        }

        text = text[digits..];
        return true;
    }

    /// <summary>
    /// Runs <c>ssh -V</c> with <paramref name="sshExecutablePath"/> - the client an attempt is about to run - and reads its
    /// version; null when it cannot be read (<see cref="Probe(string, IReadOnlyList{string}, TimeSpan)"/>). Blocks for up
    /// to <see cref="ProbeTimeout"/> and a second for the output: never on the UI thread.
    /// </summary>
    public static Version? Probe(string sshExecutablePath) => Probe(sshExecutablePath, ["-V"], ProbeTimeout);

    /// <summary>
    /// Runs <paramref name="executablePath"/> with <paramref name="arguments"/> and parses what it printed, stderr first
    /// (<see cref="Parse"/>). It cannot prompt: stdin is closed at once, there is no window, every stream is piped, and the
    /// askpass variables say never (<see cref="SshAskPassEnvironment.Suppress"/>). Null when it does not start, does not
    /// exit 0 within <paramref name="timeout"/> - its process tree is then stopped - or prints no OpenSSH version. The
    /// arguments are the test seam: a shell stands in for ssh there.
    /// </summary>
    internal static Version? Probe(string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        SshAskPassEnvironment.Suppress(startInfo.Environment);

        var timer = Stopwatch.StartNew();
        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("no process started");
        }
        catch (Exception)
        {
            return null;
        }

        using (process)
        {
            Task<string> stderr;
            Task<string> stdout;
            try
            {
                process.StandardInput.Close();
                stderr = process.StandardError.ReadToEndAsync();
                stdout = process.StandardOutput.ReadToEndAsync();
            }
            catch (Exception)
            {
                Stop(process);
                return null;
            }

            if (!process.WaitForExit(Remaining(timer, timeout)))
            {
                Stop(process);
                return null;
            }

            try
            {
                // Its last bytes may still be in the pipes when the exit is seen; a pipe something it started still holds
                // open is not waited out.
                TimeSpan drain = Remaining(timer, timeout);
                if (!Task.WaitAll([stderr, stdout], drain > DrainGrace ? drain : DrainGrace)) return null;
            }
            catch (AggregateException)
            {
                return null;
            }

            return process.ExitCode == 0 ? Parse(stderr.Result) ?? Parse(stdout.Result) : null;
        }
    }

    private static TimeSpan Remaining(Stopwatch timer, TimeSpan timeout)
    {
        TimeSpan left = timeout - timer.Elapsed;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>Stops the probe's process tree; one that already exited needs nothing.</summary>
    private static void Stop(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // It exited first, or part of its tree could not be stopped: its pipes close once it is gone.
        }
    }
}
