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
            Inject(input, "!!");
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
    private static extern bool GetNumberOfConsoleInputEvents(nint handle, out uint count);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
