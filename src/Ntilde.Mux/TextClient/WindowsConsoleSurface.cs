using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ntilde.Mux.TextClient;

/// <summary>
/// The process's console (spec §6.1, §6.7). Opens CONIN$/CONOUT$ itself, so it works in the WinExe
/// after AttachConsole/AllocConsole, where the std handles are not set. Windows has no SIGWINCH: a
/// dedicated background thread polls the size every 200 ms.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsConsoleSurface : IConsoleSurface
{
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;
    private const uint EnableProcessedInput = 0x1, EnableLineInput = 0x2, EnableEchoInput = 0x4, EnableWindowInput = 0x8, EnableMouseInput = 0x10, EnableVirtualTerminalInput = 0x200;
    private const uint EnableProcessedOutput = 0x1, EnableVirtualTerminalProcessing = 0x4, DisableNewlineAutoReturn = 0x8;

    private readonly nint _in;
    private readonly nint _out;
    private readonly object _modeGate = new();
    private readonly Thread _sizePoll;
    private uint _savedIn;
    private uint _savedOut;
    private bool _raw;
    private volatile bool _disposed;
    private int _handlesClosed;
    private char? _pendingHigh;

    public WindowsConsoleSurface()
    {
        _in = CreateFileW("CONIN$", GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
        _out = CreateFileW("CONOUT$", GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
        if (_in == -1 || _out == -1 || !GetConsoleMode(_in, out _) || !GetConsoleMode(_out, out _))
        {
            CloseHandles();
            throw new ConsoleUnavailableException("mux attach needs an interactive console.");
        }

        _sizePoll = new Thread(PollSize) { IsBackground = true, Name = "MuxAttachSizePoll" };
        _sizePoll.Start();
    }

    public event Action? Resized;

    public (int Cols, int Rows) Size =>
        GetConsoleScreenBufferInfo(_out, out ConsoleScreenBufferInfo info)
            ? (Math.Max(1, info.Window.Right - info.Window.Left + 1), Math.Max(1, info.Window.Bottom - info.Window.Top + 1))
            : (80, 24);

    public void EnterRawMode()
    {
        lock (_modeGate)
        {
            if (_raw) return;
            if (!GetConsoleMode(_in, out _savedIn) || !GetConsoleMode(_out, out _savedOut)) throw new IOException("GetConsoleMode failed.");
            uint input = (_savedIn & ~(EnableLineInput | EnableEchoInput | EnableProcessedInput | EnableWindowInput | EnableMouseInput)) | EnableVirtualTerminalInput;
            uint output = _savedOut | EnableProcessedOutput | EnableVirtualTerminalProcessing | DisableNewlineAutoReturn;
            if (!SetConsoleMode(_in, input) || !SetConsoleMode(_out, output))
            {
                _ = SetConsoleMode(_in, _savedIn);
                _ = SetConsoleMode(_out, _savedOut);
                throw new IOException($"SetConsoleMode failed ({Marshal.GetLastPInvokeError()}); this console may not support VT sequences.");
            }

            _raw = true;
        }
    }

    public void RestoreMode()
    {
        lock (_modeGate)
        {
            if (!_raw) return;
            _ = SetConsoleMode(_in, _savedIn);
            _ = SetConsoleMode(_out, _savedOut);
            _raw = false;
        }
    }

    /// <summary>The cursor in the screen buffer (0-based), or null when it cannot be read. For probe-console.</summary>
    public (int Col, int Row)? CursorPosition =>
        GetConsoleScreenBufferInfo(_out, out ConsoleScreenBufferInfo info) ? (info.CursorPosition.X, info.CursorPosition.Y) : null;

    public void Write(string text) => WriteAll(_out, text);

    /// <summary>The whole text through WriteConsoleW, as UTF-16 whatever the code page. Any console output handle.</summary>
    internal static void WriteAll(nint output, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int offset = 0;
        while (offset < text.Length)
        {
            ReadOnlySpan<char> rest = text.AsSpan(offset);
            if (!WriteConsoleW(output, ref MemoryMarshal.GetReference(rest), (uint)rest.Length, out uint written, 0) || written == 0)
            {
                throw new IOException($"WriteConsoleW failed ({Marshal.GetLastPInvokeError()}).");
            }

            offset += (int)written;
        }
    }

    /// <summary>
    /// A trailing high surrogate is held back for the next read, so a pair split across two reads
    /// never reaches the caller as a lone half; 0 only when the console is gone (see
    /// <see cref="ConsoleReadAssembler.Read"/>). Input thread only.
    /// </summary>
    public int Read(char[] buffer) => ConsoleReadAssembler.Read(buffer, ref _pendingHigh, ReadChunk);

    private bool ReadChunk(char[] buffer, int offset, int count, out int read)
    {
        bool ok = ReadConsoleW(_in, ref buffer[offset], (uint)count, out uint got, 0);
        read = ok ? (int)got : 0;
        return ok;
    }

    public void Dispose()
    {
        _disposed = true;
        RestoreMode();
        CloseHandles();
    }

    private void PollSize()
    {
        (int Cols, int Rows) last = Size;
        while (!_disposed)
        {
            Thread.Sleep(200);
            if (_disposed) return;
            (int Cols, int Rows) now = Size;
            if (now == last) continue;
            last = now;
            Resized?.Invoke();
        }
    }

    private void CloseHandles()
    {
        // Once only: a second Dispose must not close handle values the OS may have reused since.
        if (Interlocked.Exchange(ref _handlesClosed, 1) != 0) return;
        if (_in != -1 && _in != 0) _ = CloseHandle(_in);
        if (_out != -1 && _out != 0) _ = CloseHandle(_out);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SmallRect
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConsoleScreenBufferInfo
    {
        public Coord Size;
        public Coord CursorPosition;
        public ushort Attributes;
        public SmallRect Window;
        public Coord MaximumWindowSize;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint CreateFileW(string fileName, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(nint handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(nint handle, uint mode);

    // CharSet.Unicode is load-bearing: under the default (Ansi) a `char` is not blittable, so `ref char`
    // is marshalled through a ONE-BYTE temporary - Write prints neighbouring memory and Read lets the
    // API write the whole buffer into that byte. Pinned by WindowsConsoleSurfaceTests and probe-console.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteConsoleW(nint handle, ref char buffer, uint count, out uint written, nint reserved);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadConsoleW(nint handle, ref char buffer, uint count, out uint read, nint control);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleScreenBufferInfo(nint handle, out ConsoleScreenBufferInfo info);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
