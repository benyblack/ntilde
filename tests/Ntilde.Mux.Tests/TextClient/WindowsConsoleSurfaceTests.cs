using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ntilde.Mux.TextClient;

namespace Ntilde.Mux.Tests.TextClient;

/// <summary>
/// The Windows surface against a REAL console (review round 1: WriteConsoleW/ReadConsoleW were
/// marshalled as Ansi, so `ref char` went through a one-byte temporary). Skipped when the test host
/// has no console. The test-side P/Invokes use ushort, never char, so they cannot share that bug.
/// </summary>
public sealed class WindowsConsoleSurfaceTests
{
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;
    private const uint ConsoleTextModeBuffer = 1;
    private const ushort KeyEvent = 1;
    private const uint CookedInput = 0x01F7; // processed, line, echo, window, mouse, insert, QuickEdit, extended

    [Fact]
    public void Write_puts_the_exact_UTF16_text_into_a_console_screen_buffer()
    {
        if (OperatingSystem.IsWindows()) WriteRoundTrip();
        else Assert.Skip("Windows console only");
    }

    [Fact]
    public void Read_returns_typed_chars_and_holds_back_a_split_surrogate_pair()
    {
        if (OperatingSystem.IsWindows()) ReadRoundTrip();
        else Assert.Skip("Windows console only");
    }

    [Theory]
    [InlineData(0x03E0u, true)]  // what EnterRawMode leaves on a fresh conhost
    [InlineData(0x0200u, true)]
    [InlineData(0x0992u, false)] // line input on, VT input off
    [InlineData(0x01F7u, false)] // a cooked prompt
    [InlineData(0x01E0u, false)] // no line input, but no VT input either
    [InlineData(0x0201u, false)] // Ctrl+C still processed
    public void Raw_input_needs_line_echo_and_processing_off_and_VT_input_on(uint mode, bool raw)
    {
        if (OperatingSystem.IsWindows()) Assert.Equal(raw, WindowsConsoleSurface.IsRawInputMode(mode));
        else Assert.Skip("Windows console only");
    }

    [Fact]
    public void Raw_mode_is_put_back_when_something_sharing_the_console_resets_it()
    {
        if (OperatingSystem.IsWindows()) ReassertRoundTrip();
        else Assert.Skip("Windows console only");
    }

    [SupportedOSPlatform("windows")]
    private static void ReassertRoundTrip()
    {
        WindowsConsoleSurface surface;
        try
        {
            surface = new WindowsConsoleSurface();
        }
        catch (ConsoleUnavailableException)
        {
            Assert.Skip("the test host has no console");
            return;
        }

        nint input = CreateFileW("CONIN$", GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
        try
        {
            surface.EnterRawMode();
            try
            {
                Assert.True(GetConsoleMode(input, out uint entered));
                Assert.True(WindowsConsoleSurface.IsRawInputMode(entered), $"after EnterRawMode: 0x{entered:X4}");

                // What a shell or console program sharing the console does: cooked mode, line input on.
                Assert.True(SetConsoleMode(input, CookedInput));
                uint now = CookedInput;
                var deadline = Environment.TickCount64 + 3000;
                while (Environment.TickCount64 < deadline)
                {
                    Thread.Sleep(50);
                    if (GetConsoleMode(input, out now) && WindowsConsoleSurface.IsRawInputMode(now)) break;
                }

                Assert.True(WindowsConsoleSurface.IsRawInputMode(now), $"still 0x{now:X4} after 3 s");
                Assert.True(surface.RawModeReasserts >= 1);
            }
            finally
            {
                surface.RestoreMode();
            }

            // And never after the restore: the poll must not put raw mode back on a restored console.
            Assert.True(GetConsoleMode(input, out uint restored));
            Thread.Sleep(500);
            Assert.True(GetConsoleMode(input, out uint later));
            Assert.Equal(restored, later);
        }
        finally
        {
            _ = CloseHandle(input);
            surface.Dispose();
        }
    }

    [SupportedOSPlatform("windows")]
    private static void WriteRoundTrip()
    {
        // A private screen buffer: nothing appears on the host's visible console.
        nint buffer = CreateConsoleScreenBuffer(GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, ConsoleTextModeBuffer, 0);
        Assert.SkipWhen(buffer == -1 || buffer == 0, "the test host has no console");
        try
        {
            const string Text = "héllo wörld ñΩ中";
            WindowsConsoleSurface.WriteAll(buffer, Text);

            ushort[] cells = new ushort[Text.Length + 4];
            Assert.True(ReadConsoleOutputCharacterW(buffer, cells, (uint)cells.Length, default, out uint read));
            string back = new(Array.ConvertAll(cells, c => (char)c), 0, (int)read);
            Assert.StartsWith(Text.Replace("中", string.Empty, StringComparison.Ordinal), back, StringComparison.Ordinal);
            Assert.Contains("中", back, StringComparison.Ordinal); // a wide char: its trailing cell may repeat it
        }
        finally
        {
            _ = CloseHandle(buffer);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ReadRoundTrip()
    {
        WindowsConsoleSurface surface;
        try
        {
            surface = new WindowsConsoleSurface();
        }
        catch (ConsoleUnavailableException)
        {
            Assert.Skip("the test host has no console");
            return;
        }

        nint input = CreateFileW("CONIN$", GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
        try
        {
            Assert.SkipUnless(GetNumberOfConsoleInputEvents(input, out uint pending) && pending == 0, "the console has pending input that is not ours");
            surface.EnterRawMode();
            try
            {
                var chars = new char[2048];
                Inject(input, "xy\ud83d");
                Assert.Equal("xy", ReadOrFail(surface, input, chars));

                Inject(input, "\ude00z");
                Assert.Equal("😀z", ReadOrFail(surface, input, chars));

                // Raw mode has no end-of-file: Ctrl+Z is a char for the shell, not "input closed".
                Inject(input, "\u001a");
                Assert.Equal("\u001a", ReadOrFail(surface, input, chars));

                // A read keeps the mode it started in, so a read begun after something reset the console
                // to line mode would hold the detach chord until Enter. Each read puts raw mode back
                // first, without waiting for the size poll.
                Assert.True(SetConsoleMode(input, CookedInput));
                Inject(input, "\u001cd");
                Assert.Equal("\u001cd", ReadOrFail(surface, input, chars));
            }
            finally
            {
                surface.RestoreMode();
            }
        }
        finally
        {
            _ = CloseHandle(input);
            surface.Dispose();
        }
    }

    /// <summary>On a dedicated thread: a wrong read would block forever, so a timeout unblocks it and fails.</summary>
    [SupportedOSPlatform("windows")]
    private static string ReadOrFail(WindowsConsoleSurface surface, nint input, char[] chars)
    {
        int n = -1;
        var reader = new Thread(() => n = surface.Read(chars)) { IsBackground = true };
        reader.Start();
        if (!reader.Join(TimeSpan.FromSeconds(5)))
        {
            Inject(input, "!!\r"); // the Enter also releases a read stuck in line mode
            reader.Join(TimeSpan.FromSeconds(5));
            Assert.Fail("Read did not return the injected input.");
        }

        return new string(chars, 0, n);
    }

    [SupportedOSPlatform("windows")]
    private static void Inject(nint input, string text)
    {
        var records = new InputRecord[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            records[i] = new InputRecord { EventType = KeyEvent, KeyDown = 1, RepeatCount = 1, UnicodeChar = text[i] };
        }

        Assert.True(WriteConsoleInputW(input, records, (uint)records.Length, out uint written));
        Assert.Equal((uint)records.Length, written);
    }

    /// <summary>INPUT_RECORD holding a KEY_EVENT_RECORD (20 bytes).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct InputRecord
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public int KeyDown;
        [FieldOffset(8)] public ushort RepeatCount;
        [FieldOffset(10)] public ushort VirtualKeyCode;
        [FieldOffset(12)] public ushort VirtualScanCode;
        [FieldOffset(14)] public ushort UnicodeChar;
        [FieldOffset(16)] public uint ControlKeyState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern nint CreateConsoleScreenBuffer(uint access, uint share, nint security, uint flags, nint reserved);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern nint CreateFileW(string fileName, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadConsoleOutputCharacterW(nint handle, [Out] ushort[] chars, uint length, Coord readCoord, out uint read);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteConsoleInputW(nint handle, InputRecord[] records, uint length, out uint written);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(nint handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(nint handle, uint mode);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfConsoleInputEvents(nint handle, out uint count);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
