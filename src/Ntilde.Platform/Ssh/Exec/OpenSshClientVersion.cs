using System.Diagnostics;
using System.Globalization;
using System.Text;

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

    /// <summary>How much of what one stream printed is kept: <c>ssh -V</c> prints one short line.</summary>
    private const int OutputKept = 8192;

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
    /// Runs <c>ssh -V</c> with <paramref name="sshExecutablePath"/> - the client an attempt is about to run - and says what
    /// it learned (<see cref="Probe(string, IReadOnlyList{string}, TimeSpan, TimeProvider?, Func{ProcessStartInfo, Process?}?)"/>).
    /// Blocks for up to <see cref="ProbeTimeout"/> after the start, and a second for the output: never on the UI thread.
    /// </summary>
    public static OpenSshClientProbe Probe(string sshExecutablePath) => Probe(sshExecutablePath, ["-V"], ProbeTimeout);

    /// <summary>
    /// Runs <paramref name="executablePath"/> with <paramref name="arguments"/> and reads what it printed, stderr first
    /// (<see cref="Parse"/>). It cannot prompt: stdin is closed at once, there is no window, every stream is piped, and the
    /// askpass variables say never (<see cref="SshAskPassEnvironment.Suppress"/>). The answer is
    /// <see cref="OpenSshClientProbeKind.Version"/> when it exits 0 having printed OpenSSH's version, and
    /// <see cref="OpenSshClientProbeKind.NotOpenSsh"/> when it exits otherwise or prints none. It is
    /// <see cref="OpenSshClientProbeKind.Indeterminate"/> when it does not start, does not exit within
    /// <paramref name="timeout"/> of its start (its process tree is then stopped), or its output cannot be read. The
    /// timeout counts from the start's return: a slow exec, such as an antivirus scan of the executable, is not the client
    /// being slow to answer.
    /// </summary>
    /// <param name="time">The clock the timeout is measured by; the system's by default. A test seam.</param>
    /// <param name="start">Starts the process; <see cref="Process.Start(ProcessStartInfo)"/> by default. A test seam.</param>
    internal static OpenSshClientProbe Probe(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        TimeProvider? time = null,
        Func<ProcessStartInfo, Process?>? start = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        time ??= TimeProvider.System;

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

        Process? started;
        try
        {
            started = (start ?? Process.Start)(startInfo);
        }
        catch (Exception ex)
        {
            return OpenSshClientProbe.Indeterminate($"it could not start: {ex.Message}");
        }

        if (started is null) return OpenSshClientProbe.Indeterminate("it could not start");
        long began = time.GetTimestamp();

        using Process process = started;
        Task<string> stderr;
        Task<string> stdout;
        try
        {
            process.StandardInput.Close();
            stderr = ReadOnItsOwnThread(process.StandardError, "SshVersionStderr");
            stdout = ReadOnItsOwnThread(process.StandardOutput, "SshVersionStdout");
        }
        catch (Exception ex)
        {
            Stop(process);
            return OpenSshClientProbe.Indeterminate($"its output could not be read: {ex.Message}");
        }

        if (!process.WaitForExit(Remaining(time, began, timeout)))
        {
            Stop(process);
            return OpenSshClientProbe.Indeterminate(string.Create(CultureInfo.InvariantCulture, $"it did not exit within {timeout.TotalSeconds:0.#} s"));
        }

        try
        {
            // Its last bytes may still be in the pipes when the exit is seen; a pipe something it started still holds
            // open is not waited out.
            TimeSpan drain = Remaining(time, began, timeout);
            if (!Task.WaitAll([stderr, stdout], drain > DrainGrace ? drain : DrainGrace))
            {
                return OpenSshClientProbe.Indeterminate("its output did not end when it exited");
            }
        }
        catch (AggregateException ex)
        {
            return OpenSshClientProbe.Indeterminate($"its output could not be read: {ex.InnerException?.Message ?? ex.Message}");
        }

        int exitCode = process.ExitCode;
        if (exitCode != 0) return OpenSshClientProbe.NotOpenSsh(string.Create(CultureInfo.InvariantCulture, $"it exited with code {exitCode}"));
        return (Parse(stderr.Result) ?? Parse(stdout.Result)) is { } version
            ? OpenSshClientProbe.Found(version)
            : OpenSshClientProbe.NotOpenSsh("it printed no OpenSSH version");
    }

    /// <summary>
    /// Reads <paramref name="reader"/> to its end on a thread of its own, keeping the first <see cref="OutputKept"/>
    /// characters, and disposes it there. A redirected pipe on Windows is a synchronous stream, so an async read would
    /// hold a pool thread for as long as the pipe stays open; a stopped process tree closes it.
    /// </summary>
    private static Task<string> ReadOnItsOwnThread(StreamReader reader, string name)
    {
        var read = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try
            {
                var kept = new StringBuilder();
                char[] buffer = new char[1024];
                int count;
                while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    kept.Append(buffer, 0, Math.Min(count, OutputKept - kept.Length));
                }

                read.TrySetResult(kept.ToString());
            }
            catch (Exception ex)
            {
                read.TrySetException(ex);
            }
            finally
            {
                reader.Dispose();
            }
        })
        { IsBackground = true, Name = name }.Start();
        return read.Task;
    }

    private static TimeSpan Remaining(TimeProvider time, long began, TimeSpan timeout)
    {
        TimeSpan left = timeout - time.GetElapsedTime(began);
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
