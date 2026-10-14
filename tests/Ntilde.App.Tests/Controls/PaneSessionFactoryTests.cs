using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Platform.Ssh.Launch;
using Ntilde.Pty;
using Ntilde.Replay;
using Ntilde.Shell;
using Ntilde.VT;
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

/// <summary>
/// A factory that refuses, and records what it was asked for. The SSH branch's whole contract on
/// failure is negative - no session, no fall-through - so the request log is the only way to say
/// "and it did not then quietly try again as a local shell".
/// </summary>
internal sealed class ThrowingSessionFactory : ITerminalSessionFactory
{
    public List<TerminalSessionRequest> Requests { get; } = new();

    public ITerminalSession Create(TerminalSessionRequest request)
    {
        Requests.Add(request);
        throw new InvalidOperationException("ssh-connect-failed-in-test");
    }
}

/// <summary>A handler that is never invoked; only its identity crosses the seam.</summary>
internal sealed class FakeSshInteractionHandler : ISshInteractionHandler
{
    public Task<SshInteractionResponse> HandleAsync(
        SshInteractionRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("the factory seam never runs the handler");
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

        // Pins that the spawn actually succeeded and the factory's session is the one wired up -
        // without this, a spawn that silently failed (Session stays null, nothing gets wired)
        // would leave SentInput empty too, and this test would pass for the wrong reason.
        Assert.Same(session, pane.Session);

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

        // A non-default working directory, so StartingDirectory is asserted against a value that
        // has to have travelled rather than one an empty request would satisfy by accident. It
        // comes off the profile because that is the only way into the request - InitializeSessionCore
        // reads `profile?.StartingDirectory ?? ""`. Type stays Local so this remains the local branch.
        var profile = new TerminalProfile
        {
            Type = ConnectionType.Local,
            StartingDirectory = "/tmp/pane-session-factory",
        };

        pane.CreateAndWireParser();
        pane.InitializeSessionCore("bash", "-l", profile, cols: 100, rows: 40);

        TerminalSessionRequest? request = factory.LastRequest;
        Assert.NotNull(request);
        Assert.Equal("bash", request!.Command);
        Assert.Equal("-l", request.Arguments);
        Assert.Equal(100, request.Cols);
        Assert.Equal(40, request.Rows);
        Assert.Null(request.Ssh);

        // The three the summary above calls dangerous, and which this test used not to check.
        Assert.Equal("/tmp/pane-session-factory", request.StartingDirectory);

        // Shell integration is off for this pane (see PaneSpawnTestHelpers), so both of its
        // outputs must be off too: no env overrides to inject, and therefore no reason to suppress
        // PowerShell's post-launch init. A request that carried either anyway would mean the pane
        // decided it was integrated when it was not.
        //
        // These are the integration-OFF values, which is all this test can pin: the only producer
        // of a non-null EnvironmentOverrides is ApplyShellIntegrationLaunchPlan, which needs
        // CommandAssistServices and writes a bootstrap file to disk. What they establish is that
        // the fields are populated FROM the pane rather than left at a constant - the request used
        // to be asserted without them at all, so a seam that dropped them silently would have
        // passed.
        Assert.Null(request.EnvironmentOverrides);
        Assert.False(request.SkipPowerShellPostLaunchInit);
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

/// <summary>
/// The SSH half of the seam, which is the half with the untyped crossing in it.
/// </summary>
/// <remarks>
/// <para>
/// Shell integration is deliberately NOT disabled here, unlike the local-branch tests. The SSH
/// path does not call <c>ApplyShellIntegrationLaunchPlan</c> at all - it calls
/// <c>ArmRemoteShellIntegrationTracker</c>, which allocates a <c>ShellLifecycleTracker</c> and
/// subscribes to it and needs no <c>CommandAssistServices</c>. Turning integration off would
/// therefore suppress a real production behaviour for no benefit, so these tests run the branch
/// as the app runs it.
/// </para>
/// <para>
/// Every profile here leaves <c>SshBackendKind</c> at its <c>OpenSsh</c> default, which keeps
/// <c>RegisterActiveSshSession</c> a no-op: the Native branch writes into the process-wide
/// <c>ActiveSshSessionRegistry</c> singleton, and a test that leaves an entry behind there is a
/// test that pollutes the rest of the lane.
/// </para>
/// </remarks>
public class PaneSshSessionFactoryTests
{
    private static TerminalProfile SshProfile() => new()
    {
        Name = "remote-host",
        Type = ConnectionType.SSH,
        SshHost = "example.invalid",
        SshUser = "someone",
    };

    /// <summary>
    /// The descriptor has to carry all four fields the SSH branch used to read off the pane
    /// directly, and the diagnostics level has to survive the trip as a number.
    /// </summary>
    /// <remarks>
    /// <c>SshSessionDescriptor.DiagnosticsLevel</c> is an <c>int</c> because naming
    /// <c>SshDiagnosticsLevel</c> in Ntilde.Pty would invert the dependency on Ntilde.Platform.
    /// That is the seam's one untyped crossing and the entire reason the layering constraint is
    /// paid for, so the round trip - <c>(int)</c> on the way in, <c>(SshDiagnosticsLevel)</c> on
    /// the way out - is asserted rather than assumed. A level silently reduced to <c>None</c>
    /// drops the <c>-v</c> flags and takes the diagnostics the user asked for with it, and nothing
    /// throws.
    /// </remarks>
    [AvaloniaFact]
    public void SshSpawn_HandsTheFactoryTheProfileDiagnosticsHandlerAndNativeFlag()
    {
        TerminalProfile profile = SshProfile();
        var handler = new FakeSshInteractionHandler();
        var session = new PlainFakeTerminalSession { ShellCommand = "ssh someone@example.invalid" };
        var factory = new RecordingSessionFactory(session);

        using var pane = new TerminalPane(
            profile,
            new TerminalSettings { ExperimentalNativeSshEnabled = true },
            SshDiagnosticsLevel.VeryVerbose);
        pane.SshInteractionHandler = handler;
        pane.SessionFactory = factory;

        pane.CreateAndWireParser();
        pane.InitializeSessionCore(profile.Command, profile.Arguments, profile, cols: 90, rows: 30);

        Assert.Same(session, pane.Session);

        TerminalSessionRequest? request = factory.LastRequest;
        Assert.NotNull(request);
        Assert.Equal(90, request!.Cols);
        Assert.Equal(30, request.Rows);

        SshSessionDescriptor? ssh = request.Ssh;
        Assert.NotNull(ssh);
        Assert.Equal(profile.Id, ssh!.ProfileId);
        Assert.Same(handler, ssh.InteractionHandler);
        Assert.True(ssh.NativeSshEnabled);

        // Both directions of the untyped crossing, named explicitly so a failure says which end
        // of it broke.
        Assert.Equal((int)SshDiagnosticsLevel.VeryVerbose, ssh.DiagnosticsLevel);
        Assert.Equal(SshDiagnosticsLevel.VeryVerbose, (SshDiagnosticsLevel)ssh.DiagnosticsLevel);

        // The fix-up after a successful connect: the session decides what command actually ran
        // (the SSH factory builds the command line, including -v flags and ProxyJump), and the
        // pane adopts it. SessionManager persists ShellCommand, so a pane that kept the profile's
        // placeholder would write the wrong thing to disk.
        Assert.Equal("ssh someone@example.invalid", pane.ShellCommand);
        Assert.Equal(string.Empty, pane.ShellArgs);
    }

    /// <summary>
    /// A factory that throws must leave the pane with no session, a visible banner, and - above
    /// all - no second attempt.
    /// </summary>
    /// <remarks>
    /// Falling back to <c>RustPtySession</c> after a failed SSH connect is the specific failure
    /// this branch's "fail loudly" comment exists to prevent: the fallback would spawn a LOCAL
    /// shell in a tab the user opened to reach a remote host, with the SSH arguments dropped, and
    /// it would look like a successful connection. The request log is asserted to hold exactly one
    /// entry, and that entry to be the SSH one.
    /// </remarks>
    [AvaloniaFact]
    public void SshSpawn_WhenTheFactoryThrows_LeavesNoSessionAndDoesNotFallBackToALocalShell()
    {
        TerminalProfile profile = SshProfile();
        var factory = new ThrowingSessionFactory();

        using var pane = new TerminalPane(profile, new TerminalSettings(), SshDiagnosticsLevel.Verbose);
        pane.SessionFactory = factory;

        pane.CreateAndWireParser();
        pane.InitializeSessionCore(profile.Command, profile.Arguments, profile, cols: 80, rows: 24);

        Assert.Null(pane.Session);

        TerminalSessionRequest request = Assert.Single(factory.Requests);
        Assert.NotNull(request.Ssh);

        Assert.Contains("SSH Connection Failed", ScreenText(pane), StringComparison.Ordinal);
    }

    private static string ScreenText(TerminalPane pane)
    {
        pane.Buffer!.Lock.EnterReadLock();
        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (TerminalRow row in pane.Buffer.ViewportRows)
            {
                foreach (TerminalCell cell in row.Cells)
                {
                    sb.Append(cell.Character == '\0' ? ' ' : cell.Character);
                }

                sb.Append('\n');
            }

            return sb.ToString();
        }
        finally
        {
            pane.Buffer.Lock.ExitReadLock();
        }
    }
}
