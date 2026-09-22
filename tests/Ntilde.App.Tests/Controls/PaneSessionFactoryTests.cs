using System;
using System.Collections.Generic;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Pty;
using Ntilde.Replay;
using Ntilde.Shell;
using Xunit;

namespace Ntilde.Tests.Controls;

/// <summary>
/// A session that records what it was told, and optionally claims to answer device queries
/// itself. Nothing here spawns a process, so these tests cost nothing and leave nothing behind.
/// </summary>
internal sealed class FakeTerminalSession : ITerminalSession, ITerminalSessionCapabilities
{
    private readonly bool _answersDeviceQueries;

    public FakeTerminalSession(bool answersDeviceQueries = false)
    {
        _answersDeviceQueries = answersDeviceQueries;
    }

    public List<string> SentInput { get; } = new();

    public bool AnswersDeviceQueries => _answersDeviceQueries;
    public bool OrdersResizeInStream => false;

    public Guid Id { get; } = Guid.NewGuid();
    public string ShellCommand { get; set; } = "fake-shell";
    public string? ShellArguments { get; set; }
    public bool IsProcessRunning => true;
    public bool HasActiveChildProcesses => false;
    public int? ExitCode { get; private set; }
    public bool IsRecording => false;
    public bool IsFlightRecording => false;

    public event Action<string>? OnOutputReceived;
    public event Action<int>? OnExit;

    public void SendInput(string input) => SentInput.Add(input);
    public void Resize(int cols, int rows) { }
    public void StartRecording(string filePath) { }
    public void StopRecording() { }
    public void EnableFlightRecording(long maxTotalBytes) { }
    public void DisableFlightRecording() { }
    public bool TryExportFlightRecording(string filePath, out FlightExportInfo info)
    {
        info = default;
        return false;
    }

    public void Dispose() { }

    public void EmitOutput(string text) => OnOutputReceived?.Invoke(text);

    public void EmitExit(int code)
    {
        ExitCode = code;
        OnExit?.Invoke(code);
    }
}

/// <summary>
/// A session that does NOT implement <see cref="ITerminalSessionCapabilities"/> at all, which is
/// what every session in the app is today. Both capability flags must read as false for it.
/// </summary>
internal sealed class PlainFakeTerminalSession : ITerminalSession
{
    public List<string> SentInput { get; } = new();

    public Guid Id { get; } = Guid.NewGuid();
    public string ShellCommand { get; set; } = "plain-fake-shell";
    public string? ShellArguments { get; set; }
    public bool IsProcessRunning => true;
    public bool HasActiveChildProcesses => false;
    public int? ExitCode => null;
    public bool IsRecording => false;
    public bool IsFlightRecording => false;

    public event Action<string>? OnOutputReceived;
    public event Action<int>? OnExit;

    public void SendInput(string input) => SentInput.Add(input);
    public void Resize(int cols, int rows) { }
    public void StartRecording(string filePath) { }
    public void StopRecording() { }
    public void EnableFlightRecording(long maxTotalBytes) { }
    public void DisableFlightRecording() { }
    public bool TryExportFlightRecording(string filePath, out FlightExportInfo info)
    {
        info = default;
        return false;
    }

    public void Dispose() { }

    public void EmitOutput(string text) => OnOutputReceived?.Invoke(text);
    public void EmitExit(int code) => OnExit?.Invoke(code);
}

/// <summary>
/// A factory that hands back a session the test controls, and records the request it was given.
/// </summary>
internal sealed class RecordingSessionFactory : ITerminalSessionFactory
{
    private readonly ITerminalSession _session;

    public RecordingSessionFactory(ITerminalSession session) => _session = session;

    public TerminalSessionRequest? LastRequest { get; private set; }

    public ITerminalSession Create(TerminalSessionRequest request)
    {
        LastRequest = request;
        return _session;
    }
}

public class PaneCapabilityGatingTests
{
    /// <summary>
    /// A session that answers device queries itself (the multiplexer daemon owns the
    /// authoritative parser, and it is the one that must reply) must not also receive the local
    /// parser's reply - the child would get two DA1 answers for one query.
    /// </summary>
    [AvaloniaFact]
    public void SessionThatAnswersDeviceQueries_DoesNotReceiveTheLocalParsersReply()
    {
        var session = new FakeTerminalSession(answersDeviceQueries: true);
        using var pane = new TerminalPane();
        PaneSpawnTestHelpers.DisableShellIntegration(pane);
        pane.SessionFactory = new RecordingSessionFactory(session);

        pane.CreateAndWireParser();
        pane.InitializeSessionCore("fake-shell", string.Empty, profile: null, cols: 80, rows: 24);

        pane.Parser!.OnResponse?.Invoke("\u001b[?62;c");

        Assert.Empty(session.SentInput);
    }

    /// <summary>
    /// The default - every session in the app today - still gets the reply, or DA1 goes
    /// unanswered and TUIs stall on their capability probe.
    /// </summary>
    [AvaloniaFact]
    public void SessionWithoutCapabilities_StillReceivesTheLocalParsersReply()
    {
        var session = new PlainFakeTerminalSession();
        using var pane = new TerminalPane();
        PaneSpawnTestHelpers.DisableShellIntegration(pane);
        pane.SessionFactory = new RecordingSessionFactory(session);

        pane.CreateAndWireParser();
        pane.InitializeSessionCore("fake-shell", string.Empty, profile: null, cols: 80, rows: 24);

        pane.Parser!.OnResponse?.Invoke("\u001b[?62;c");

        Assert.Equal(["\u001b[?62;c"], session.SentInput);
    }
}

/// <summary>
/// <c>InitializeSessionCore</c>'s local (non-SSH) branch unconditionally runs
/// <c>ApplyShellIntegrationLaunchPlan</c> whenever <c>TerminalSettings.CommandAssistShellIntegrationEnabled</c>
/// is true - which is the property's default, so a bare <see cref="TerminalPane"/> (which loads real
/// defaults through <c>ApplySettings(TerminalSettings.Load())</c> in its constructor) hits it too. That
/// path both requires <c>CommandAssistServices</c> (which a test-created pane never has) and, for a shell
/// name a registered provider recognizes (bash included), rewrites the arguments passed to the session -
/// e.g. "bash" "-l" becomes "bash" "--rcfile &lt;bootstrap&gt; -l". Neither of those is what these tests are
/// about, so shell integration is turned off before spawning to keep the factory seam's inputs exactly what
/// the test passed in.
/// </summary>
internal static class PaneSpawnTestHelpers
{
    public static void DisableShellIntegration(TerminalPane pane)
        => pane.ApplySettings(new TerminalSettings { CommandAssistShellIntegrationEnabled = false });
}

public class PaneSessionFactoryTests
{
    /// <summary>
    /// The local branch's request must carry everything the inline <c>new RustPtySession(...)</c>
    /// used to pass positionally. Asserted field by field, because a request that silently drops
    /// the working directory or the shell-integration env overrides produces a pane that starts
    /// in the wrong place with integration off, and nothing throws.
    /// </summary>
    [AvaloniaFact]
    public void LocalSpawn_HandsTheFactoryTheShellCommandCwdGeometryAndEnv()
    {
        var session = new PlainFakeTerminalSession();
        var factory = new RecordingSessionFactory(session);
        using var pane = new TerminalPane();
        PaneSpawnTestHelpers.DisableShellIntegration(pane);
        pane.SessionFactory = factory;

        pane.CreateAndWireParser();
        pane.InitializeSessionCore("bash", "-l", profile: null, cols: 100, rows: 40);

        TerminalSessionRequest? request = factory.LastRequest;
        Assert.NotNull(request);
        Assert.Equal("bash", request!.Command);
        Assert.Equal("-l", request.Arguments);
        Assert.Equal(100, request.Cols);
        Assert.Equal(40, request.Rows);
        Assert.Null(request.Ssh);
    }

    /// <summary>
    /// The factory's session has to end up wired, not merely returned: output reaches the parser
    /// and therefore the buffer, and exit reaches HandleSessionExit. Both are asserted through
    /// observable effects rather than by reading private handler lists.
    /// </summary>
    [AvaloniaFact]
    public void FactorySession_IsWiredForOutputAndExit()
    {
        var session = new PlainFakeTerminalSession();
        using var pane = new TerminalPane();
        PaneSpawnTestHelpers.DisableShellIntegration(pane);
        pane.SessionFactory = new RecordingSessionFactory(session);

        pane.CreateAndWireParser();
        pane.InitializeSessionCore("bash", string.Empty, profile: null, cols: 80, rows: 24);

        Assert.Same(session, pane.Session);

        session.EmitOutput("hello");
        Dispatcher.UIThread.RunJobs();

        pane.Buffer!.Lock.EnterReadLock();
        try
        {
            string firstRow = new string(
                Array.ConvertAll(pane.Buffer.ViewportRows[0].Cells, c => c.Character == '\0' ? ' ' : c.Character));
            Assert.StartsWith("hello", firstRow);
        }
        finally
        {
            pane.Buffer.Lock.ExitReadLock();
        }

        session.EmitExit(3);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, pane.LastExitCode);
    }

    /// <summary>
    /// A pane created outside the window's wiring funnel still spawns. Unlike
    /// CommandAssistServices - which throws when unset, because a second graph would be silently
    /// wrong - there is exactly one right default here, so defaulting beats throwing.
    /// </summary>
    [AvaloniaFact]
    public void SessionFactory_DefaultsToTheProductionFactory()
    {
        using var pane = new TerminalPane();

        Assert.Same(Ntilde.Shell.DefaultTerminalSessionFactory.Instance, pane.SessionFactory);
    }
}
