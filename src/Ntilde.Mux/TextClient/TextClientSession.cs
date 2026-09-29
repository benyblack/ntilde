using System.Runtime.InteropServices;
using System.Text;
using Ntilde.Mux.Contracts;

namespace Ntilde.Mux.TextClient;

public sealed class TextClientOptions
{
    /// <summary>Attach <see cref="MuxAttachMode.ReadOnly"/>: no input, no resize requests, a status line says so.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>At most one frame per interval (~60 Hz).</summary>
    public TimeSpan RenderInterval { get; init; } = TimeSpan.FromMilliseconds(16);

    /// <summary>The CLI registers SIGINT/SIGTERM/SIGHUP/SIGQUIT and a ProcessExit backstop; tests do not.</summary>
    public bool HandleSignals { get; init; }
}

public enum TextClientExit
{
    Detached,
    SessionExited,
    Faulted,
    Disconnected,
    AttachFailed,
    Error,
}

/// <summary>
/// <c>ntilde mux attach</c> (spec §6): renders a mux session into a foreign terminal from its own
/// buffer - never relaying the raw stream, whose device queries the outer terminal would answer.
/// Threads: the client's delivery thread parses; one dedicated render thread paints; one dedicated
/// input thread reads keys. No thread-pool work on the output path. <see cref="Run"/> restores the
/// console on every path.
/// </summary>
public sealed class TextClientSession : IDisposable
{
    private static readonly PosixSignal[] StopSignals = [PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGHUP, PosixSignal.SIGQUIT];

    private readonly MuxClient _client;
    private readonly Guid _sessionId;
    private readonly IConsoleSurface _console;
    private readonly TextClientOptions _options;

    // Auto-reset, and set before the render reads the buffer: a Changed raised mid-render leaves it
    // set, so that change always gets one more frame. A flag cleared after rendering would lose it.
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _done = new(false);
    private readonly object _gate = new();
    private TextClientExit? _exit;
    private string? _detail;
    private int _exitCode;
    private string? _killedBy;
    private volatile bool _stopping;

    public TextClientSession(MuxClient client, Guid sessionId, IConsoleSurface console, TextClientOptions? options = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _sessionId = sessionId;
        _options = options ?? new TextClientOptions();
    }

    public TextClientExit? ExitReason { get { lock (_gate) return _exit; } }

    /// <summary>Any thread. The first reason wins.</summary>
    public void RequestStop(TextClientExit reason, string? detail = null)
    {
        lock (_gate)
        {
            if (_exit is not null) return;
            _exit = reason;
            _detail = detail;
        }

        try { _done.Set(); }
        catch (ObjectDisposedException) { /* Run has returned */ }
        Wake();
    }

    /// <summary>Blocks until detach, exit or failure. Returns 0 detached, 1 the session ended, 2 a connection or usage failure.</summary>
    public int Run(TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(stderr);
        MuxAttachMode mode = _options.ReadOnly ? MuxAttachMode.ReadOnly : MuxAttachMode.Shared;
        MuxClientSession? session = null;
        TextClientModel? model = null;
        Thread? renderThread = null;
        bool entered = false;
        var registrations = new List<IDisposable>();
        try
        {
            session = _client.OpenSession(_sessionId, string.Empty, null, mode);
            model = new TextClientModel(session);
            var renderer = new TextClientRenderer(model.Buffer) { ReadOnly = _options.ReadOnly };
            model.Changed += Wake;
            session.KilledElsewhere += kind => Volatile.Write(ref _killedBy, kind);
            session.OnExit += code =>
            {
                Volatile.Write(ref _exitCode, code);
                RequestStop(TextClientExit.SessionExited);
            };
            session.Faulted += message => RequestStop(TextClientExit.Faulted, message);
            session.Disconnected += reason => RequestStop(TextClientExit.Disconnected, reason);
            _console.Resized += Wake;
            if (_options.HandleSignals) RegisterSignals(registrations);

            _console.EnterRawMode();
            _console.Write(TextClientRenderer.EnterSequence);
            entered = true;

            (int cols, int rows) = _console.Size;
            try
            {
                // The outer terminal sends legacy keys and the mux answers queries: no kitty keyboard.
                session.AttachAsync(mode, 0, new MuxPresentation { Cols = Math.Max(1, cols), Rows = Math.Max(1, rows), KittyKeyboardEnabled = false })
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is MuxProtocolException or IOException or TimeoutException or ObjectDisposedException)
            {
                RequestStop(TextClientExit.AttachFailed, ex.Message);
            }

            if (ExitReason is null)
            {
                MuxClientSession attached = session;
                renderThread = new Thread(() => RenderLoop(attached, renderer)) { IsBackground = true, Name = "MuxAttachRender" };
                var inputThread = new Thread(() => InputLoop(attached)) { IsBackground = true, Name = "MuxAttachInput" };
                Wake(); // the first frame
                renderThread.Start();
                inputThread.Start();
            }

            _done.Wait();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ConsoleUnavailableException)
        {
            RequestStop(TextClientExit.Error, ex.Message);
        }
        finally
        {
            _stopping = true;
            Wake();
            renderThread?.Join(TimeSpan.FromSeconds(2)); // the only writer until now
            _console.Resized -= Wake;
            if (entered) TryWrite(TextClientRenderer.LeaveSequence);
            _console.RestoreMode();
            foreach (IDisposable registration in registrations) registration.Dispose();
            model?.Dispose();

            // A session that ended needs no detach; everything else leaves it running in the daemon.
            if (session is not null && ExitReason is not TextClientExit.SessionExited) session.Dispose();
        }

        return Report(stderr);
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Dispose();
        _done.Dispose();
    }

    /// <summary>Only signals: it runs on the delivery thread, so it must never render, block or throw.</summary>
    private void Wake()
    {
        try { _wake.Set(); }
        catch (ObjectDisposedException) { /* a late delivery after Dispose */ }
    }

    private void RenderLoop(MuxClientSession session, TextClientRenderer renderer)
    {
        try
        {
            long intervalMs = (long)_options.RenderInterval.TotalMilliseconds;
            long lastRenderMs = long.MinValue / 2;
            (int Cols, int Rows) lastSize = _console.Size;
            while (!_stopping)
            {
                _wake.WaitOne();
                if (_stopping) break;
                long wait = lastRenderMs + intervalMs - Environment.TickCount64;
                if (wait > 0 && _done.Wait((int)wait)) break; // throttle, but never outlive a stop

                (int Cols, int Rows) size = _console.Size;
                if (size != lastSize)
                {
                    lastSize = size;
                    if (!_options.ReadOnly) session.Resize(size.Cols, size.Rows);
                    renderer.Invalidate();
                }

                string output = renderer.Render(size.Cols, size.Rows);
                lastRenderMs = Environment.TickCount64;
                if (output.Length > 0) _console.Write(output);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or UnauthorizedAccessException)
        {
            RequestStop(TextClientExit.Error, ex.Message);
        }
    }

    private void InputLoop(MuxClientSession session)
    {
        var chord = new DetachChord();
        var pass = new StringBuilder();
        char[] buffer = new char[1024];
        try
        {
            while (!_stopping)
            {
                int n = _console.Read(buffer);
                if (n <= 0)
                {
                    RequestStop(TextClientExit.Detached, "input closed");
                    return;
                }

                pass.Clear();
                bool detach = chord.Feed(buffer.AsSpan(0, n), pass);
                if (pass.Length > 0 && !_options.ReadOnly) session.SendInput(pass.ToString());
                if (detach)
                {
                    RequestStop(TextClientExit.Detached);
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            RequestStop(TextClientExit.Error, ex.Message);
        }
    }

    private void RegisterSignals(List<IDisposable> registrations)
    {
        // The guard keeps the platform analyzer (CA1416) satisfied: PosixSignalRegistration does not exist there.
        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() || OperatingSystem.IsTvOS() || OperatingSystem.IsBrowser()) return;
        foreach (PosixSignal signal in StopSignals)
        {
            try
            {
                // Cancel: do not let the runtime terminate us before the finally restores the console.
                registrations.Add(PosixSignalRegistration.Create(signal, context =>
                {
                    context.Cancel = true;
                    RequestStop(TextClientExit.Detached, "signal");
                }));
            }
            catch (PlatformNotSupportedException)
            {
                // Not every signal exists everywhere; the others still restore.
            }
        }

        AppDomain.CurrentDomain.ProcessExit += RestoreOnProcessExit;
        registrations.Add(new Unsubscribe(() => AppDomain.CurrentDomain.ProcessExit -= RestoreOnProcessExit));
    }

    private void RestoreOnProcessExit(object? sender, EventArgs e) => _console.RestoreMode();

    private void TryWrite(string text)
    {
        try { _console.Write(text); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { /* the console is gone */ }
    }

    private int Report(TextWriter stderr)
    {
        TextClientExit reason;
        string? detail;
        lock (_gate)
        {
            reason = _exit ?? TextClientExit.Detached;
            detail = _detail;
        }

        switch (reason)
        {
            case TextClientExit.Detached:
                TryWrite($"[detached from {_sessionId}]\r\n");
                return 0;
            case TextClientExit.SessionExited:
                TryWrite(Volatile.Read(ref _killedBy) is not null
                    ? "[session ended from another window]\r\n"
                    : $"[session exited with code {Volatile.Read(ref _exitCode)}]\r\n");
                return 1;
            case TextClientExit.Faulted:
                TryWrite("[session failed in the multiplexer]\r\n");
                return 1;
            case TextClientExit.Disconnected:
                TryWrite("[connection to the multiplexer lost]\r\n");
                return 2;
            case TextClientExit.AttachFailed:
                stderr.WriteLine($"mux: attach failed: {detail}");
                return 2;
            default:
                stderr.WriteLine($"mux: {detail}");
                return 2;
        }
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
