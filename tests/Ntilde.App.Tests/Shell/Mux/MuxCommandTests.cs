using System.Runtime.InteropServices;
using Ntilde.Mux.Cli;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

/// <summary>
/// The App's adapter over <c>Ntilde.Mux.Cli</c> (Phase 4 spec §6.4): dispatch, the usage text it
/// must keep, the console hint, and the root override reaching serve. The verbs
/// themselves are tested where they live, in Ntilde.Mux.Tests' <c>MuxCliTests</c>.
/// </summary>
public sealed class MuxCommandTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nmxc" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        int code = MuxCommand.Execute(args, o, e, _root);
        return (code, o.ToString(), e.ToString());
    }

    [Fact] public void Dispatch_matches_only_mux() { Assert.True(MuxCommand.IsSupportedCliMode(["mux", "ls"])); Assert.False(MuxCommand.IsSupportedCliMode(["backup"])); Assert.False(MuxCommand.IsSupportedCliMode([])); }
    [Fact] public void Serve_is_recognised() { Assert.True(MuxCommand.IsServe(["mux", "serve"])); Assert.False(MuxCommand.IsServe(["mux", "ls"])); }

    [Fact]
    public void Attach_is_recognised()
    {
        Assert.True(MuxCommand.IsAttach(["mux", "attach", "abcd"]));
        Assert.False(MuxCommand.IsAttach(["mux", "ls"]));
        Assert.False(MuxCommand.IsAttach(["backup"]));
    }

    // The App's text before the verbs moved to Ntilde.Mux.Cli (Phase 4 Task 9), copied verbatim from
    // the old MuxCommand. The adapter prints it with the platform's newline throughout: on Windows
    // that is the old output byte for byte (these literals carry the checkout's CRLF, as the old ones
    // did); elsewhere the old output had that CRLF inside the text, which the CLI now normalises. The
    // comparisons below are exact, so a stray \r on Linux fails them.
    private const string PreMoveUsageLiteral = """
        Usage:
          ntilde mux serve [--idle-exit-minutes N] [--foreground]
          ntilde mux ls [--json]
          ntilde mux kill <sessionId>
          ntilde mux kill-server [--force]
          ntilde mux attach <sessionId|prefix> [--read-only]
        """;

    private static readonly string PreMoveUsage = PreMoveUsageLiteral.ReplaceLineEndings();
    private static readonly string PreMoveAttachUsage = PreMoveAttachUsageLiteral.ReplaceLineEndings();

    private const string PreMoveAttachUsageLiteral = """
        Usage: ntilde mux attach <sessionId|prefix> [--read-only]

          Shows a multiplexer session in this terminal. The id (or a unique prefix of at least
          4 characters) comes from `ntilde mux ls`. Detach with Ctrl+\ then d (Ctrl may stay held);
          Ctrl+\ Ctrl+\ sends a literal Ctrl+\. --read-only shows the session without sending
          input (a convenience, not a security boundary). Exit codes: 0 detached, 1 the session
          ended, 2 an error.

          Windows: from PowerShell, or any prompt that does not wait for GUI programs, run
            cmd /c ntilde mux attach <id>
          so the prompt does not compete for your keystrokes.
        """;

    [Theory]
    [InlineData("mux")]
    [InlineData("mux", "frobnicate")]
    [InlineData("mux", "ls", "--bogus")]
    public void The_usage_text_is_unchanged(params string[] args)
    {
        var (code, output, err) = Run(args);

        Assert.Equal(2, code);
        Assert.Equal(string.Empty, output);
        Assert.Equal(PreMoveUsage + Environment.NewLine, err);
    }

    [Fact]
    public void An_empty_command_line_prints_the_usage()
    {
        var (code, _, err) = Run();

        Assert.Equal(2, code);
        Assert.Equal(PreMoveUsage + Environment.NewLine, err);
    }

    [Fact]
    public void A_serve_parse_error_keeps_its_text()
    {
        var (code, _, err) = Run("mux", "serve", "--bogus");

        Assert.Equal(2, code);
        Assert.Equal("Unknown option '--bogus'." + Environment.NewLine + PreMoveUsage + Environment.NewLine, err);
    }

    [Fact]
    public void The_attach_help_is_unchanged()
    {
        var (code, output, err) = Run("mux", "attach", "--help");

        Assert.Equal(0, code);
        Assert.Equal(PreMoveAttachUsage + Environment.NewLine, output);
        Assert.Equal(string.Empty, err);
    }

    [Fact]
    public void The_probe_console_usage_is_unchanged()
    {
        var (code, _, err) = Run("mux", "probe-console", "--bogus");

        Assert.Equal(2, code);
        Assert.Equal("usage: ntilde mux probe-console [--keys]" + Environment.NewLine, err);
    }

    /// <summary>The adapter offers each verb: with no daemon at the root, each one gets as far as looking for it.</summary>
    [Theory]
    [InlineData(1, "mux", "ls")]
    [InlineData(1, "mux", "kill", "00000000-0000-0000-0000-000000000001")]
    [InlineData(1, "mux", "kill-server")]
    [InlineData(2, "mux", "attach", "abcd1234")]
    public void Every_App_verb_reaches_its_body(int exitCode, params string[] args)
    {
        var (code, _, err) = Run(args);

        Assert.Equal(exitCode, code);
        Assert.Equal("No multiplexer is running." + Environment.NewLine, err);
    }

    [Theory]
    [InlineData(true, true, true)]     // the GUI exe, attached to a parent console: the only case that shares the keyboard
    [InlineData(true, false, false)]   // allocated its own console (Explorer), or Ntilde.Cli.exe
    [InlineData(false, true, false)]   // not Windows
    public void The_console_hint_is_printed_only_for_the_GUI_exe_on_a_parent_console(bool isWindows, bool attachedToParent, bool expected)
    {
        bool previous = MuxCommand.AttachedToParentConsole;
        MuxCommand.AttachedToParentConsole = attachedToParent;   // as Program.cs sets it from PrepareInteractive
        try
        {
            string? hint = MuxCli.AttachConsoleHint(isWindows, MuxCommand.CreateHost(_root), "abcd1234");

            Assert.Equal(expected, hint is not null);
            if (expected) Assert.Equal("mux: if keystrokes are lost, run via cmd /c ntilde mux attach abcd1234 (ignore if already under cmd /c)", hint);
        }
        finally
        {
            MuxCommand.AttachedToParentConsole = previous;
        }
    }

    /// <summary>
    /// The adapter's root override reaches <c>serve</c> (Phase 4 spec §6.4): the daemon advertises
    /// under it, and <c>kill-server</c> at the same root stops it.
    /// </summary>
    [Fact]
    public async Task Serve_honours_the_root_override()
    {
        // serve runs in this process: keep what it rebinds - the console writers (--foreground binds
        // the console), the PTY log sink, ConPTY's passthrough switch - from leaking into later tests.
        // The binding also attaches this process to its parent's console on Windows (AttachConsole(-1)).
        // A test host normally has none, and left attached, every later ConPTY spawn in this process
        // would take the passthrough path (rusty_pty's host_has_real_console: GetConsoleWindow() != 0).
        bool hadConsoleWindow = OperatingSystem.IsWindows() && GetConsoleWindow() != IntPtr.Zero;
        TextWriter consoleOut = Console.Out, consoleError = Console.Error;
        Action<Ntilde.Pty.PtyLogLevel, string>? ptySink = Ntilde.Pty.PtyLogger.Sink;
        string? noPassthrough = Environment.GetEnvironmentVariable("NTILDE_PTY_NO_PASSTHROUGH");
        var serveLog = new StringWriter();
        TextWriter serveErr = TextWriter.Synchronized(serveLog);
        Task<int> serve = Task.Run(() => MuxCommand.Execute(["mux", "serve", "--foreground", "--idle-exit-minutes", "0"], TextWriter.Null, serveErr, _root), Ct);
        try
        {
            await TestWait.UntilAsync(() => File.Exists(MuxDiscovery.GetDescriptorPath(_root)) || serve.IsCompleted, "serve advertised itself under the override root");
            Assert.False(serve.IsCompleted, $"serve exited early: {serveLog}");

            var (code, output, err) = Run("mux", "kill-server");

            Assert.Equal(0, code);
            Assert.Contains("Multiplexer stopped.", output, StringComparison.Ordinal);
            Assert.Equal(string.Empty, err);
            Assert.Equal(0, await serve.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        }
        finally
        {
            if (!serve.IsCompleted) Run("mux", "kill-server");
            Console.SetOut(consoleOut);
            Console.SetError(consoleError);
            Ntilde.Pty.PtyLogger.Sink = ptySink;
            Environment.SetEnvironmentVariable("NTILDE_PTY_NO_PASSTHROUGH", noPassthrough);
            // Undo only what serve did - a console window that was not there before. A console the
            // host already had, with or without a window, is left alone.
            if (OperatingSystem.IsWindows() && !hadConsoleWindow && GetConsoleWindow() != IntPtr.Zero) FreeConsole();
        }

        if (OperatingSystem.IsWindows()) Assert.Equal(hadConsoleWindow, GetConsoleWindow() != IntPtr.Zero);
    }

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();
}
