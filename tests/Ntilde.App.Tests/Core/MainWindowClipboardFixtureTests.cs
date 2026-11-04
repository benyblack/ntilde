using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.AgentHost;
using Ntilde.AgentHost.Contracts;
using Ntilde.Controls;
using Ntilde.Pty;
using Ntilde.Shell;
using Ntilde.Shell.Clipboard;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Controls;
using Ntilde.AppTests.Shell;
using System.Text;

namespace Ntilde.Tests.Core;

[Collection(Ntilde.Tests.Infra.PrivateClipboardProcessScope.Name)]
public sealed class MainWindowClipboardFixtureTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    private const string Run = PrivateClipboardQaModeTests.Run;
    private const string Nonce = "terminfo-osc52-01234567-89ab-cdef-0123-456789abcdef";
    private readonly PrivateClipboardQaModeTests _startup = new();
    private IDisposable? _processModeScope;
    public void Dispose()
    {
        try { TestMainWindowFactory.DisposeCreatedWindows(); }
        finally { _processModeScope?.Dispose(); _startup.Dispose(); }
    }

    private (MainWindow Window, TerminalPane Pane, PrivateClipboardQaMode Mode) Create(bool qa = true)
    {
        var mode = _startup.Create();
        if (qa) _processModeScope = PrivateClipboardQaMode.OverrideProcessForTesting(mode);
        var profile = new TerminalProfile { Name = "Private clipboard local", Type = ConnectionType.Local };
        var settings = new TerminalSettings
        {
            SessionPersistence = SessionPersistenceMode.Off,
            AgentAccessActEnabled = true,
            CommandAssistShellIntegrationEnabled = false,
            Profiles = [profile],
            DefaultProfileId = profile.Id,
        };
        var services = AppServices.BuildForDesigner() with
        { Settings = settings, SessionFactory = new RecordingSessionFactory(new FakeTerminalSession()), CommandAssist = TestCommandAssistServices.Instance };
        var window = TestMainWindowFactory.Create(services);
        window.Show(); Dispatcher.UIThread.RunJobs();
        return (window, Assert.Single(window.AllPanesForTest()), mode);
    }

    private static Task<ClipboardFixtureResult?> Call(MainWindow window, TerminalPane pane, string operation,
        Guid? id = null, string? value = null, Func<bool>? current = null, CancellationToken token = default)
        => ((IAgentActionExecutor)window).ClipboardFixtureAsync(new()
        { PaneId = pane.PaneId, RunId = Run, Operation = operation, FixtureId = id, Value = value },
            pane.AgentRegistrationForTesting!, current ?? (() => true), token);

    [AvaloniaFact]
    public async Task Private_startup_sinks_before_begin_then_actual_parser_writes_only_exact_armed_nonce()
    {
        var (window, pane, mode) = Create();
        var parser = pane.Parser!;
        Assert.False(parser.KittyClipboardEnabled);
        Assert.Null(parser.OnKittyClipboardPacket);
        int scheduled = 0;
        pane.ClipboardSchedulerForTest = _ => { scheduled++; throw new InvalidOperationException("Native backend must never be scheduled."); };
        parser.Process("\u001b]52;c;" + Convert.ToBase64String(Encoding.ASCII.GetBytes(Nonce)) + "\a");
        Assert.Equal(0, pane.ClipboardWriteAttemptsForTest);
        var begun = await Call(window, pane, "begin");
        Assert.NotNull(begun);
        await Call(window, pane, "armWrite", begun.FixtureId, Nonce);
        parser.Process("\u001b]52;c;" + Convert.ToBase64String(Encoding.ASCII.GetBytes(Nonce)) + "\a");
        var read = await Call(window, pane, "read", begun.FixtureId);
        Assert.Equal(Nonce, read?.Value);
        Assert.Equal(1, read?.AcceptedWrites);
        parser.Process("\u001b]5522;type=read;dGV4dC9wbGFpbg==\a");
        Assert.Equal(0, scheduled);
        Assert.Equal(0, pane.ClipboardWriteAttemptsForTest);
        var closed = await Call(window, pane, "close", begun.FixtureId);
        Assert.True(closed?.Complete);
        Assert.True(closed?.Restored);
        Assert.Null(mode.ResolveHostClipboard(() => throw new InvalidOperationException("Never resolve a system provider.")));
        parser.Process("\u001b]52;c;" + Convert.ToBase64String(Encoding.ASCII.GetBytes(Nonce)) + "\a");
        Assert.Equal(0, pane.ClipboardWriteAttemptsForTest);
        Assert.Equal(closed, await Call(window, pane, "close", begun.FixtureId));
    }

    [AvaloniaFact]
    public async Task Armed_different_parser_payload_aborts_without_accepting_a_write()
    {
        var (window, pane, _) = Create();
        var begun = await Call(window, pane, "begin"); Assert.NotNull(begun);
        await Call(window, pane, "armWrite", begun.FixtureId, Nonce);
        const string other = "terminfo-osc52-11111111-2222-3333-4444-555555555555";
        pane.Parser!.Process("\u001b]52;c;" + Convert.ToBase64String(Encoding.ASCII.GetBytes(other)) + "\a");
        Assert.Null(await Call(window, pane, "read", begun.FixtureId));
        var closed = await Call(window, pane, "close", begun.FixtureId);
        Assert.Equal("aborted", closed?.State);
        Assert.Equal(0, closed?.AcceptedWrites);
        Assert.True(closed?.Restored); Assert.False(closed?.Complete);
        Assert.Null(closed?.Value);
        Assert.Equal(0, pane.ClipboardWriteAttemptsForTest);
    }

    [AvaloniaFact]
    public async Task Reconnect_with_zero_geometry_aborts_before_session_release_without_needing_a_new_parser()
    {
        var (window, pane, mode) = Create();
        var begun = await Call(window, pane, "begin"); Assert.NotNull(begun);
        var parser = pane.Parser;
        var fixture = mode.For(pane)!;
        pane.TermView.Arrange(new Avalonia.Rect(0, 0, 0, 0));
        Assert.Equal(0, pane.TermView.Cols); Assert.Equal(0, pane.TermView.Rows);
        pane.Reconnect();
        Assert.Null(pane.Session);
        Assert.Same(parser, pane.Parser);
        Assert.Equal("aborted", fixture.Observe().State);
        Assert.True(fixture.Observe().Restored); Assert.False(fixture.Observe().Complete);
        Assert.Equal(0, pane.ClipboardWriteAttemptsForTest);
    }

    [AvaloniaFact]
    public async Task Read_query_keeps_empty_denial_even_after_independent_private_seed()
    {
        var (window, pane, _) = Create();
        var begun = await Call(window, pane, "begin"); Assert.NotNull(begun);
        await Call(window, pane, "seed", begun.FixtureId, Nonce);
        var replies = new List<string>();
        pane.Parser!.OnResponse += replies.Add;
        pane.Parser.Process("\u001b]52;c;?\a");
        Assert.Equal("\u001b]52;c;\u001b\\", Assert.Single(replies));
        Assert.Equal(Nonce, (await Call(window, pane, "read", begun.FixtureId))?.Value);
        Assert.Equal(0, pane.ClipboardWriteAttemptsForTest);
    }

    [AvaloniaTheory]
    [InlineData("ris")]
    [InlineData("rewire")]
    [InlineData("exit")]
    [InlineData("dispose")]
    public async Task Reset_reconnect_exit_or_disposal_abort_and_cannot_reenable_host_fallback(string interruption)
    {
        var (window, pane, mode) = Create();
        var begun = await Call(window, pane, "begin"); Assert.NotNull(begun);
        var fixture = mode.For(pane)!;
        var parser = pane.Parser!;
        await Call(window, pane, "seed", begun.FixtureId, Nonce);
        if (interruption == "ris") parser.Process("\u001bc");
        if (interruption == "rewire") pane.CreateAndWireParser();
        if (interruption == "exit") pane.HandleSessionExitForTesting(0);
        if (interruption == "dispose") pane.Dispose();
        var observed = fixture.Observe();
        Assert.Equal("aborted", observed.State);
        Assert.True(observed.Restored); Assert.False(observed.Complete);
        parser.Process("\u001b]52;c;" + Convert.ToBase64String(Encoding.ASCII.GetBytes(Nonce)) + "\a");
        Assert.Equal(0, pane.ClipboardWriteAttemptsForTest);
        Assert.Null(mode.ResolveHostClipboard(() => throw new InvalidOperationException("Never inspect clipboard after interruption.")));
        Assert.Null(mode.Begin(Run, new object(), new object(), () => true));
    }

    [AvaloniaFact]
    public async Task Per_probe_restore_returns_fixed_seed_hash_and_keeps_one_fixture_for_later_probe()
    {
        var (window, pane, _) = Create();
        var begun = await Call(window, pane, "begin"); Assert.NotNull(begun);
        await Call(window, pane, "seed", begun.FixtureId, Nonce);
        await Call(window, pane, "armWrite", begun.FixtureId, Nonce);
        var restored = await Call(window, pane, "restore", begun.FixtureId);
        Assert.NotNull(restored); Assert.True(restored.Restored); Assert.False(restored.Complete);
        var storage = await Call(window, pane, "read", begun.FixtureId);
        Assert.Equal("terminfo-private-seed-" + Run, storage?.Value);
        Assert.Equal(storage?.ValueSha256, restored.ValueSha256);
        await Call(window, pane, "armWrite", begun.FixtureId, Nonce);
        pane.Parser!.Process("\u001b]52;c;" + Convert.ToBase64String(Encoding.ASCII.GetBytes(Nonce)) + "\a");
        Assert.Equal(Nonce, (await Call(window, pane, "read", begun.FixtureId))?.Value);
        Assert.True((await Call(window, pane, "restore", begun.FixtureId))?.Restored);
        Assert.True((await Call(window, pane, "close", begun.FixtureId))?.Complete);
        Assert.Null(await Call(window, pane, "restore", begun.FixtureId));
        Assert.Equal(0, pane.ClipboardWriteAttemptsForTest);
    }

    [AvaloniaFact]
    public async Task Paused_old_callback_cannot_enter_a_fixture_bound_after_parser_replacement()
    {
        var (window, pane, mode) = Create();
        var old = pane.Parser!;
        using var checkedSource = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        pane.BeforePrivateClipboardWriteForTest = () =>
        {
            checkedSource.Set();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(5)), "The controller must release the paused callback.");
        };
        var callback = Task.Run(() => old.OnClipboardWrite?.Invoke("c", Encoding.ASCII.GetBytes(Nonce)));
        try
        {
            Assert.True(checkedSource.Wait(TimeSpan.FromSeconds(5)), "The old source check must complete before replacement.");
            Assert.Null(mode.For(pane));
            pane.CreateAndWireParser();
            Assert.NotSame(old, pane.Parser);
            var begun = await Call(window, pane, "begin"); Assert.NotNull(begun);
            await Call(window, pane, "armWrite", begun.FixtureId, Nonce);
            resume.Set();
            await callback.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Null(await Call(window, pane, "read", begun.FixtureId));
            var closed = await Call(window, pane, "close", begun.FixtureId);
            Assert.Equal("aborted", closed?.State);
            Assert.Equal(0, closed?.AcceptedWrites);
            Assert.True(closed?.Restored); Assert.False(closed?.Complete);
            Assert.Equal(0, pane.ClipboardWriteAttemptsForTest);
        }
        finally
        {
            resume.Set();
            pane.BeforePrivateClipboardWriteForTest = null;
            await callback.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [AvaloniaFact]
    public async Task Parser_retired_before_begin_cannot_write_into_a_later_fixture()
    {
        var (window, pane, mode) = Create();
        var stale = pane.Parser!;
        pane.CreateAndWireParser();
        var begun = await Call(window, pane, "begin"); Assert.NotNull(begun);
        await Call(window, pane, "armWrite", begun.FixtureId, Nonce);
        stale.Process("\u001b]52;c;" + Convert.ToBase64String(Encoding.ASCII.GetBytes(Nonce)) + "\a");
        Assert.Equal("aborted", mode.For(pane)?.Observe().State);
        Assert.Equal(0, mode.For(pane)?.Observe().AcceptedWrites);
        Assert.Equal(0, pane.ClipboardWriteAttemptsForTest);
        Assert.False((await Call(window, pane, "close", begun.FixtureId))?.Complete);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Settings_off_aborts_armed_and_unarmed_writes_without_bypassing_normal_policy(bool armed)
    {
        var (window, pane, mode) = Create();
        var begun = await Call(window, pane, "begin"); Assert.NotNull(begun);
        await Call(window, pane, "seed", begun.FixtureId, Nonce);
        if (armed) await Call(window, pane, "armWrite", begun.FixtureId, Nonce);
        pane.ApplySettings(new TerminalSettings { AllowOsc52ClipboardWrite = false });
        pane.Parser!.Process("\u001b]52;c;" + Convert.ToBase64String(Encoding.ASCII.GetBytes(Nonce)) + "\a");
        Assert.Equal("aborted", mode.For(pane)?.Observe().State);
        Assert.Equal(0, mode.For(pane)?.Observe().AcceptedWrites);
        Assert.Null(await Call(window, pane, "read", begun.FixtureId));
        Assert.False((await Call(window, pane, "close", begun.FixtureId))?.Complete);
        Assert.Equal(0, pane.ClipboardWriteAttemptsForTest);
    }

    [AvaloniaTheory]
    [InlineData("normalProcess")]
    [InlineData("guard")]
    [InlineData("hidden")]
    [InlineData("split")]
    public async Task Missing_startup_permission_or_ineligible_layout_cannot_bind(string refusal)
    {
        var (window, pane, mode) = Create(refusal != "normalProcess");
        if (refusal == "hidden") window.Hide();
        if (refusal == "split")
        {
            typeof(MainWindow).GetMethod("SplitPane", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(window, [Avalonia.Layout.Orientation.Horizontal]);
            Dispatcher.UIThread.RunJobs();
        }
        var result = await Call(window, pane, "begin", current: () => refusal != "guard");
        Assert.Null(result);
        Assert.Null(mode.For(pane));
    }

    [AvaloniaFact]
    public async Task Cancelled_queued_restore_cannot_later_restore_storage_or_cancel_the_armed_write()
    {
        var (window, pane, mode) = Create();
        var begun = await Call(window, pane, "begin"); Assert.NotNull(begun);
        await Call(window, pane, "seed", begun.FixtureId, Nonce);
        await Call(window, pane, "armWrite", begun.FixtureId, Nonce);
        Assert.Equal("active", mode.For(pane)?.Observe().State);
        Assert.True(pane.IsCurrentAgentRegistration(pane.AgentRegistrationForTesting!));
        // Avalonia completes InvokeAsync tasks under InstanceLock. Leave that inline
        // setup continuation before deliberately occupying UI and waiting for enqueue.
        await Task.Yield();
        Assert.True(Dispatcher.UIThread.CheckAccess());
        using var cancelled = new CancellationTokenSource();
        using var enqueued = new ManualResetEventSlim();
        var queued = Task.Run(() =>
        {
            try
            {
                var pending = Call(window, pane, "restore", begun.FixtureId, token: cancelled.Token);
                Assert.False(pending.IsCompleted, "The valid executor operation must remain queued while UI is occupied.");
                return pending;
            }
            finally { enqueued.Set(); }
        });
        Assert.True(enqueued.Wait(TimeSpan.FromSeconds(5)), "The executor must enqueue before cancellation.");
        cancelled.Cancel();
        Dispatcher.UIThread.RunJobs();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Dispatcher.UIThread.RunJobs();
        // A delayed restore would cancel this arm and turn this exact write into an abort.
        pane.Parser!.Process("\u001b]52;c;" + Convert.ToBase64String(Encoding.ASCII.GetBytes(Nonce)) + "\a");
        var observed = await Call(window, pane, "read", begun.FixtureId);
        Assert.Equal(Nonce, observed?.Value);
        Assert.Equal(1, observed?.AcceptedWrites);
        Assert.False(observed?.Restored);
        Assert.Equal(0, pane.ClipboardWriteAttemptsForTest);
    }

    [AvaloniaFact]
    public async Task Cancelled_queued_begin_never_binds_later()
    {
        var (window, pane, mode) = Create();
        using var cancelled = new CancellationTokenSource();
        using var enqueued = new ManualResetEventSlim();
        var queued = Task.Run(() =>
        {
            var pending = Call(window, pane, "begin", token: cancelled.Token);
            enqueued.Set();
            return pending;
        });
        Assert.True(enqueued.Wait(TimeSpan.FromSeconds(5)), "The worker must enqueue before cancellation.");
        Assert.Null(mode.For(pane));
        cancelled.Cancel();
        Dispatcher.UIThread.RunJobs();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(mode.For(pane));
    }
}
