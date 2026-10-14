using System.Runtime.InteropServices;
using Ntilde.Mux;
using Ntilde.Mux.Cli;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

/// <summary>
/// The App's adapter over <c>Ntilde.Mux.Cli</c> (Phase 4 spec §6.4): dispatch, the usage text it
/// must keep, the absence of the old console hint, and the root override reaching serve. The verbs
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
    // the old MuxCommand, plus ls's [--all] (Phase 5 Task 27). The adapter prints it with the platform's newline throughout: on Windows
    // that is the old output byte for byte (these literals carry the checkout's CRLF, as the old ones
    // did); elsewhere the old output had that CRLF inside the text, which the CLI now normalises. The
    // comparisons below are exact, so a stray \r on Linux fails them.
    private const string PreMoveUsageLiteral = """
        Usage:
          ntilde mux serve [--idle-exit-minutes N] [--foreground]
          ntilde mux ls [--json] [--all]
          ntilde mux kill <sessionId>
          ntilde mux kill-server [--force]
          ntilde mux attach <sessionId|prefix> [--read-only]
        """;

    private static readonly string PreMoveUsage = PreMoveUsageLiteral.ReplaceLineEndings();
    private static readonly string AttachUsage = AttachUsageLiteral.ReplaceLineEndings();

    // The pre-move attach help, less its Windows paragraph: the `cmd /c` workaround went when ntilde.com
    // arrived (Phase 4 spec §11.4), which waits like any console program, so the prompt no longer competes.
    private const string AttachUsageLiteral = """
        Usage: ntilde mux attach <sessionId|prefix> [--read-only]

          Shows a multiplexer session in this terminal. The id (or a unique prefix of at least
          4 characters) comes from `ntilde mux ls`. Detach with Ctrl+\ then d (Ctrl may stay held);
          Ctrl+\ Ctrl+\ sends a literal Ctrl+\. --read-only shows the session without sending
          input (a convenience, not a security boundary). Exit codes: 0 detached, 1 the session
          ended, 2 an error.
        """;

    [Theory]
    [InlineData("mux")]
    [InlineData("mux", "frobnicate")]
    [InlineData("mux", "ls", "--bogus")]
    [InlineData("mux", "ls", "--all", "--bogus")]   // ls --all is the App's only with nothing but --json beside it
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
    public void The_attach_help_is_the_pre_move_text_without_the_workaround()
    {
        var (code, output, err) = Run("mux", "attach", "--help");

        Assert.Equal(0, code);
        Assert.Equal(AttachUsage + Environment.NewLine, output);
        Assert.Equal(string.Empty, err);
    }

    [Fact]
    public void Attach_usage_no_longer_mentions_cmd_c()
    {
        var (_, help, _) = Run("mux", "attach", "--help");
        var (_, _, usage) = Run("mux", "attach");

        Assert.DoesNotContain("cmd /c", help, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cmd /c", usage, StringComparison.OrdinalIgnoreCase);
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
    [InlineData(1, "mux", "spawn-for-test", "cmd.exe")]   // hidden: for verification runs (Phase 5 Task 24)
    public void Every_App_verb_reaches_its_body(int exitCode, params string[] args)
    {
        var (code, _, err) = Run(args);

        Assert.Equal(exitCode, code);
        Assert.Equal("No multiplexer is running." + Environment.NewLine, err);
    }

    /// <summary>
    /// Phase 3 printed a <c>cmd /c</c> hint here, before raw mode, for the GUI exe on a parent console - the
    /// one case that shared the keyboard with the prompt. ntilde.com ended that (spec §11.4), so an attach
    /// in exactly that case now writes nothing to stderr.
    /// </summary>
    [Fact]
    public async Task Attach_from_a_parent_console_prints_no_hint()
    {
        var server = new MuxServer(new ScriptedSessionFactory(), new MuxServerOptions { ForceConPtyFiltering = false });
        using var daemon = new MuxDaemonHost(server, new MuxDaemonOptions
        {
            Endpoint = MuxDiscovery.GetDefaultEndpoint(_root),
            DescriptorPath = MuxDiscovery.GetDescriptorPath(_root),
            IdleExitAfter = TimeSpan.Zero,
        });
        daemon.Start();
        Guid id;
        using (Stream s = Ntilde.Mux.Transport.MuxEndpointConnector.Connect(MuxDiscovery.GetDefaultEndpoint(_root), TimeSpan.FromSeconds(5)))
        using (MuxClient c = await MuxClient.ConnectAsync(s, null, Ct))
        {
            id = await MuxTestHost.SpawnAsync(c);
        }

        using var console = new FakeConsoleSurface(80, 24);
        bool previous = MuxCommand.AttachedToParentConsole;
        MuxCommand.AttachedToParentConsole = true;   // as Program.cs sets it from PrepareInteractive
        MuxCli.ConsoleFactoryForTest = () => console;
        try
        {
            Task<(int Code, string Out, string Err)> run = Task.Run(() => Run("mux", "attach", id.ToString("N")[..8]), Ct);
            await TestWait.UntilAsync(() => console.IsRaw, "the text client took the console");
            console.Type("\u001cd");

            var (code, _, err) = await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal(0, code);
            Assert.Equal(string.Empty, err);
        }
        finally
        {
            MuxCli.ConsoleFactoryForTest = null;
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
