using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Ntilde.Mux.TextClient;

/// <summary>
/// The terminal on fds 0/1 (spec §6.1). Raw mode through libc on an OPAQUE termios buffer
/// (tcgetattr → cfmakeraw → tcsetattr): struct termios differs between Linux (60 bytes) and macOS
/// (72), and cfmakeraw knows its own platform's layout. Input and output use read(0)/write(1)
/// directly: .NET's Console input on Unix runs its own line editing. The size comes from
/// Console.WindowWidth/Height (the runtime's non-variadic shim): ioctl is variadic and not safe to
/// P/Invoke on macOS arm64.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("freebsd")]
public sealed class UnixConsoleSurface : IConsoleSurface
{
    private const int TermiosBytes = 256; // larger than any platform's struct termios
    private const int TcsaNow = 0;
    private const int EINTR = 4;          // the same on Linux, macOS and FreeBSD

    private readonly byte[] _saved = new byte[TermiosBytes];
    private readonly byte[] _readBytes = new byte[1024];
    private readonly TerminalInputDecoder _decoder = new();
    private readonly object _modeGate = new();
    private readonly PosixSignalRegistration _winch;
    private bool _raw;

    public UnixConsoleSurface()
    {
        if (isatty(0) != 1 || isatty(1) != 1)
        {
            throw new ConsoleUnavailableException("mux attach needs an interactive terminal: stdin and stdout must be a TTY.");
        }

        // The first size read initialises .NET's Console layer, which snapshots the termios it restores
        // at exit (and around child processes). Done here, while the terminal is still cooked, so that
        // snapshot can never be our raw mode.
        _ = Size;

        _winch = PosixSignalRegistration.Create(PosixSignal.SIGWINCH, context =>
        {
            context.Cancel = true;
            Resized?.Invoke();
        });
    }

    public event Action? Resized;

    public (int Cols, int Rows) Size
    {
        get
        {
            try
            {
                return (Math.Max(1, Console.WindowWidth), Math.Max(1, Console.WindowHeight));
            }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
            {
                return (80, 24);
            }
        }
    }

    public void EnterRawMode()
    {
        lock (_modeGate)
        {
            if (_raw) return;
            if (tcgetattr(0, _saved) != 0) throw new IOException($"tcgetattr failed (errno {Marshal.GetLastPInvokeError()}).");
            byte[] raw = (byte[])_saved.Clone();
            cfmakeraw(raw);
            if (tcsetattr(0, TcsaNow, raw) != 0) throw new IOException($"tcsetattr failed (errno {Marshal.GetLastPInvokeError()}).");
            _raw = true;
        }
    }

    public void RestoreMode()
    {
        lock (_modeGate)
        {
            if (!_raw) return;
            _ = tcsetattr(0, TcsaNow, _saved);
            _raw = false;
        }
    }

    public void Write(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        int offset = 0;
        while (offset < bytes.Length)
        {
            nint written = write(1, ref bytes[offset], (nuint)(bytes.Length - offset));
            if (written < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                if (errno == EINTR) continue;
                throw new IOException($"write to the terminal failed (errno {errno}).");
            }

            offset += (int)written;
        }
    }

    public int Read(char[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Length < 2) throw new ArgumentException("The buffer must hold a surrogate pair (2 chars).", nameof(buffer));
        int max = TerminalInputDecoder.MaxBytes(buffer.Length, _readBytes.Length);
        while (true)
        {
            nint n = read(0, ref _readBytes[0], (nuint)max);
            if (n < 0)
            {
                if (Marshal.GetLastPInvokeError() == EINTR) continue;
                return 0;
            }

            if (n == 0) return 0;
            int chars = _decoder.Decode(_readBytes, (int)n, buffer);
            if (chars > 0) return chars; // a split UTF-8 sequence completes on the next read
        }
    }

    public void Dispose()
    {
        RestoreMode();
        _winch.Dispose();
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int isatty(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int tcgetattr(int fd, byte[] termios);

    [DllImport("libc", SetLastError = true)]
    private static extern int tcsetattr(int fd, int optionalActions, byte[] termios);

    [DllImport("libc")]
    private static extern void cfmakeraw(byte[] termios);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, ref byte buffer, nuint count);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, ref byte buffer, nuint count);
}
