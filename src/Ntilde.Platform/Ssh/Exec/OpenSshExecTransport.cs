using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Ntilde.Platform.Ssh.Launch;
using Ntilde.Platform.Ssh.Models;
using Ntilde.VT;

namespace Ntilde.Platform.Ssh.Exec;

/// <summary>
/// Runs a remote command through the system OpenSSH client (Phase 4 spec §8.2):
/// <c>ssh -T -o ClearAllForwardings=yes -o BatchMode=no &lt;plan&gt; -- &lt;command&gt;</c>, with
/// redirected stdio, no window and no shell, and prompts sent to the askpass helper. In batch mode
/// (<see cref="BatchMode"/>, for automatic reconnects) it is <c>BatchMode=yes</c> and there is no
/// askpass: ssh fails rather than prompt.
/// </summary>
/// <remarks>
/// <see cref="Start"/> returns once ssh is running: connect and auth happen in ssh, while the caller
/// reads <see cref="ISshExecChannel.Stdout"/>, and its prompts go to askpass. A failure shows as
/// <see cref="ISshExecChannel.Completion"/> 255 with ssh's message in
/// <see cref="ISshExecChannel.StderrTail"/>.
/// </remarks>
public sealed class OpenSshExecTransport : ISshExecTransport
{
    private readonly SshProfile _profile;
    private readonly string _executablePath;
    private readonly Func<string, IReadOnlyList<string>> _buildArguments;
    private readonly string? _askPassHelperPath;
    private readonly Action<string> _log;

    /// <param name="profile">The profile: its name and address for askpass and <see cref="DisplayName"/>.</param>
    /// <param name="sshExecutablePath">The OpenSSH client (<see cref="SshLaunchPlanner.ResolveSshExecutablePath"/>).</param>
    /// <param name="planArguments">The launch plan's arguments: <c>["-F", cfg, alias, ...ExtraSshArgs]</c>.</param>
    /// <param name="askPassHelperPath">The askpass helper (the ntilde executable), or null for none: ssh then cannot prompt.</param>
    /// <param name="diagnosticsArguments">ssh's verbosity flags; they go first.</param>
    /// <param name="log">Where start and exit are logged; <see cref="TerminalLogger.Log(string)"/> by default.</param>
    /// <param name="batchMode">
    /// True for an attempt nobody is waiting on (an automatic reconnect): ssh runs with
    /// <c>BatchMode=yes</c> and without askpass, so it fails instead of prompting. Keys, the agent and
    /// an existing ControlMaster still work.
    /// </param>
    public OpenSshExecTransport(
        SshProfile profile,
        string sshExecutablePath,
        IReadOnlyList<string> planArguments,
        string? askPassHelperPath,
        IReadOnlyList<string>? diagnosticsArguments = null,
        Action<string>? log = null,
        bool batchMode = false)
        : this(profile, sshExecutablePath, ArgumentsFor(planArguments, diagnosticsArguments, log ?? TerminalLogger.Log, batchMode), askPassHelperPath, log, batchMode)
    {
    }

    /// <summary>
    /// The argv seam, for tests: a real process with this transport's plumbing but another argument
    /// list. ssh's own layout (<c>-T -o … -- &lt;command&gt;</c>) is not one a stand-in shell can run.
    /// </summary>
    internal OpenSshExecTransport(
        SshProfile profile,
        string executablePath,
        Func<string, IReadOnlyList<string>> buildArguments,
        string? askPassHelperPath,
        Action<string>? log,
        bool batchMode = false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(buildArguments);

        _profile = profile;
        _executablePath = executablePath;
        _buildArguments = buildArguments;
        // Batch mode never prompts, so it has no use for a helper; leaving it out means no dialog can
        // appear even if a future ssh consulted askpass despite BatchMode.
        _askPassHelperPath = batchMode || string.IsNullOrWhiteSpace(askPassHelperPath) ? null : askPassHelperPath;
        _log = log ?? TerminalLogger.Log;
        BatchMode = batchMode;
    }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(_profile.User) ? _profile.Host : $"{_profile.User}@{_profile.Host}";

    /// <summary>True when ssh runs with <c>BatchMode=yes</c> and no askpass: it never prompts.</summary>
    public bool BatchMode { get; }

    public ISshExecChannel Start(string remoteCommand, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteCommand);
        ct.ThrowIfCancellationRequested();

        ProcessStartInfo startInfo = CreateStartInfo(remoteCommand);
        _log($"[OpenSshExec] {DisplayName}: {startInfo.FileName} {SshArgBuilder.SanitizeForLog(SshArgBuilder.BuildCommandLine(startInfo.ArgumentList))}"
            + (BatchMode ? " (batch mode: ssh will not prompt)"
                : _askPassHelperPath is null ? " (no askpass helper: ssh cannot prompt)" : string.Empty));

        var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        _log(string.Create(CultureInfo.InvariantCulture, $"[OpenSshExec] {DisplayName}: ssh started (pid {process.Id})"));
        var channel = new OpenSshExecChannel(process, DisplayName, _log);
        if (ct.IsCancellationRequested)
        {
            // Cancelled while ssh was starting: nobody has the channel yet, so stop it outright.
            channel.Abort();
            ct.ThrowIfCancellationRequested();
        }

        return channel;
    }

    /// <summary>The process to start: ssh itself (no shell), all three streams piped, askpass when there is a helper and no askpass at all in batch mode.</summary>
    internal ProcessStartInfo CreateStartInfo(string remoteCommand)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (string argument in _buildArguments(remoteCommand))
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (BatchMode)
        {
            // Nothing this ssh starts - a ProxyJump hop's ssh among them - may prompt either.
            SshAskPassEnvironment.Suppress(startInfo.Environment);
        }
        else if (_askPassHelperPath is not null)
        {
            SshAskPassEnvironment.Apply(startInfo.Environment, _askPassHelperPath, _profile);
        }

        return startInfo;
    }

    private static Func<string, IReadOnlyList<string>> ArgumentsFor(
        IReadOnlyList<string> planArguments, IReadOnlyList<string>? diagnosticsArguments, Action<string> log, bool batchMode)
    {
        ArgumentNullException.ThrowIfNull(planArguments);
        string[] plan = [.. planArguments];
        string[] diagnostics = diagnosticsArguments is null ? [] : [.. diagnosticsArguments];
        return command => OpenSshExecCommandLine.Build(diagnostics, plan, command, log, batchMode);
    }
}

/// <summary>
/// An ssh process as an <see cref="ISshExecChannel"/>. Stdout and stdin are the process's own pipes -
/// nothing copies the bytes - and one dedicated thread drains stderr into a <see cref="BoundedTail"/>,
/// so a chatty <c>ssh -v</c> never blocks on a full stderr pipe.
/// </summary>
internal sealed class OpenSshExecChannel : ISshExecChannel
{
    /// <summary>How long <see cref="Dispose"/> lets ssh exit on stdin's EOF before stopping it.</summary>
    internal static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(2);

    /// <summary>How long a stopped process may take to go, so its pipes are closed before stdout is released.</summary>
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(5);

    /// <summary>How long <see cref="Completion"/> waits, after the exit, for stderr's last bytes.</summary>
    private static readonly TimeSpan StderrDrainGrace = TimeSpan.FromSeconds(2);

    private const int StderrTailBytes = 8192;

    private readonly Process _process;
    private readonly string _displayName;
    private readonly Action<string> _log;
    private readonly Stream _stdout;
    private readonly ProcessStdinStream _stdin;
    private readonly BoundedTail _stderrTail = new(StderrTailBytes);
    private readonly TaskCompletionSource _stderrDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The Process object is released once both sides are done with it: Completion has read the exit
    // code, and Close has stopped waiting on (or stopping) the process. Whichever finishes last does it.
    private readonly object _lifetime = new();
    private bool _exitRead;
    private bool _closed;

    private int _closing;
    private volatile bool _stoppedByChannel;

    public OpenSshExecChannel(Process process, string displayName, Action<string> log)
    {
        _process = process;
        _displayName = displayName;
        _log = log;
        _stdout = process.StandardOutput.BaseStream;
        _stdin = new ProcessStdinStream(process.StandardInput.BaseStream);

        Stream stderr = process.StandardError.BaseStream;
        new Thread(() => DrainStderr(stderr)) { IsBackground = true, Name = "SshExecStderr" }.Start();
        Completion = WaitForExitAsync();
    }

    public Stream Stdout => _stdout;
    public Stream Stdin => _stdin;
    public string StderrTail => _stderrTail.ToString();
    public Task<int?> Completion { get; }

    /// <summary>
    /// Ends the command without hanging: stdin's EOF (ssh closes the channel and exits), at most
    /// <see cref="ExitGrace"/> for that, then the process tree is stopped. Only then is stdout
    /// released: on Windows, closing a pipe's read end does not wake a read pending on it - the
    /// writer's exit does - so a reader blocked on <see cref="Stdout"/> (the mux client's) returns.
    /// Blocks for up to <see cref="ExitGrace"/> plus the stop's bounded wait: never on the UI thread.
    /// </summary>
    public void Dispose() => Close(ExitGrace);

    /// <summary>Stops the command at once, with no grace period: for a channel nobody has seen yet.</summary>
    internal void Abort() => Close(TimeSpan.Zero);

    private void Close(TimeSpan grace)
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0)
        {
            return;
        }

        _stdin.Dispose();
        if (!_process.WaitForExit(grace))
        {
            Stop();
            _process.WaitForExit(StopWait);
        }

        try { _stdout.Dispose(); }
        catch (IOException) { /* nothing left to read that anyone wants */ }

        lock (_lifetime)
        {
            _closed = true;
            if (_exitRead) _process.Dispose();
        }
    }

    /// <summary>The exit code of a process <see cref="Process.Kill(bool)"/> stopped: TerminateProcess's -1 on Windows, 128 + SIGKILL elsewhere.</summary>
    internal static int KilledExitCode => OperatingSystem.IsWindows() ? -1 : 128 + 9;

    /// <summary>
    /// What <see cref="Completion"/> reports for <paramref name="exitCode"/>: null only when the channel
    /// issued a kill and the process ended the way a kill ends it. A process that exited on its own in
    /// the moment before the kill landed - the kill then throws, or on Windows quietly does nothing -
    /// keeps its real code: a daemon-stopped 3 (§7.3) must not read as unknown.
    /// </summary>
    internal static int? ReportedExitCode(int exitCode, bool killIssued) =>
        killIssued && exitCode == KilledExitCode ? null : exitCode;

    private void Stop()
    {
        // Set before the kill, not after: the exit the kill causes may be observed on another thread
        // before Kill even returns. ReportedExitCode then tells a kill from a natural exit by its code.
        _stoppedByChannel = true;
        try
        {
            // The tree: ssh's own children (a ProxyCommand, the askpass helper) hold its pipes too.
            _process.Kill(entireProcessTree: true);
            _log($"[OpenSshExec] {_displayName}: ssh did not exit on EOF; stopped it");
        }
        catch (InvalidOperationException ex)
        {
            // It exited on its own first: nothing was stopped.
            _stoppedByChannel = false;
            _log($"[OpenSshExec] {_displayName}: ssh exited before it was stopped ({ex.Message})");
        }
        catch (Exception ex) when (ex is Win32Exception or AggregateException)
        {
            // Part of its tree could not be stopped. Its pipes close once it is gone, and the wait
            // after this is bounded.
            _log($"[OpenSshExec] {_displayName}: stopping ssh: {ex.Message}");
        }
    }

    private async Task<int?> WaitForExitAsync()
    {
        await _process.WaitForExitAsync().ConfigureAwait(false);

        // StderrTail must hold all ssh said before Completion resolves: callers classify a failure
        // from both. Bounded, in case a grandchild that outlived ssh still holds stderr open.
        try
        {
            await _stderrDrained.Task.WaitAsync(StderrDrainGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Report what arrived.
        }

        int exitCode;
        lock (_lifetime)
        {
            exitCode = _process.ExitCode;
            _exitRead = true;
            if (_closed) _process.Dispose();
        }

        int? reported = ReportedExitCode(exitCode, _stoppedByChannel);
        _log(reported is null
            ? $"[OpenSshExec] {_displayName}: ssh stopped by the channel"
            : string.Create(CultureInfo.InvariantCulture, $"[OpenSshExec] {_displayName}: ssh exited with {exitCode}"));
        return reported;
    }

    private void DrainStderr(Stream stderr)
    {
        byte[] buffer = new byte[4096];
        try
        {
            int read;
            while ((read = stderr.Read(buffer, 0, buffer.Length)) > 0)
            {
                _stderrTail.Append(buffer.AsSpan(0, read));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe went with the process.
        }
        finally
        {
            try { stderr.Dispose(); }
            catch (IOException) { /* closing a read end */ }
            _stderrDrained.TrySetResult();
        }
    }
}

/// <summary>
/// A process's stdin as the channel hands it out: every write reaches the pipe at once, and disposing
/// sends EOF without ever blocking.
/// </summary>
/// <remarks>
/// On Windows the process's own stdin stream is a <see cref="FileStream"/> with a 4 KiB buffer. A
/// buffer would hold a small write (a mux frame) back; worse, disposing a buffered stream flushes,
/// and a flush into a full pipe that ssh no longer reads blocks the disposer - the channel's
/// <see cref="ISshExecChannel.Dispose"/> among them. So the pipe is written through an unbuffered
/// stream over the same handle. Elsewhere it is a pipe stream, which never buffers.
/// </remarks>
internal sealed class ProcessStdinStream : Stream
{
    private readonly Stream _pipe;
    private int _disposed;

    public ProcessStdinStream(Stream processStdin)
    {
        _pipe = processStdin is FileStream file
            ? new FileStream(file.SafeFileHandle, FileAccess.Write, bufferSize: 0)
            : processStdin;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => Volatile.Read(ref _disposed) == 0;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _pipe.Write(buffer);
        _pipe.Flush();
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _pipe.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A no-op, disposed or not: every write has already been flushed, so there is never anything to
    /// flush. Not touching the pipe means a writer racing the channel's Dispose
    /// (<c>DuplexStdioStream</c> flushes after each write) sees at most its write fail, never its flush.
    /// </summary>
    public override void Flush()
    {
    }

    /// <inheritdoc cref="Flush"/>
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try { _pipe.Dispose(); }
            catch (IOException) { /* ssh is already gone: there is nobody left to send EOF to */ }
        }

        base.Dispose(disposing);
    }
}
