using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Moq;
using Ntilde.AgentHost;
using Ntilde.AgentHost.Contracts;
using Ntilde.Controls;
using Ntilde.Pty;
using Ntilde.Shell;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Controls;
using Ntilde.VT;

namespace Ntilde.Tests.Core;

public sealed class MainWindowGeneratedInputTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    private static (MainWindow Window, TerminalPane Pane, FakeTerminalSession Session) Create()
    {
        var session = new FakeTerminalSession();
        var (window, pane) = CreateWithSession(session);
        session.SentInput.Clear();
        return (window, pane, session);
    }

    private static (MainWindow Window, TerminalPane Pane) CreateWithSession(ITerminalSession session)
    {
        var profile = new TerminalProfile { Name = "Generated input local", Type = ConnectionType.Local };
        var settings = new TerminalSettings
        {
            SessionPersistence = SessionPersistenceMode.Off,
            AgentAccessActEnabled = true,
            CommandAssistShellIntegrationEnabled = false,
            Profiles = [profile],
            DefaultProfileId = profile.Id,
        };
        var window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        { Settings = settings, SessionFactory = new RecordingSessionFactory(session), CommandAssist = TestCommandAssistServices.Instance });
        window.Show(); Dispatcher.UIThread.RunJobs();
        return (window, Assert.Single(window.AllPanesForTest()));
    }

    private sealed class InputProbe
    {
        internal readonly Mock<ITerminalSession> Session = new();
        internal readonly List<string> Sent = [];
        internal Action? AfterSend;
        internal InputProbe()
        {
            Session.SetupGet(x => x.IsProcessRunning).Returns(true);
            Session.SetupGet(x => x.Id).Returns(Guid.NewGuid());
            Session.SetupGet(x => x.ShellCommand).Returns("fake-shell");
            Session.Setup(x => x.SendInput(It.IsAny<string>())).Callback<string>(text => { Sent.Add(text); AfterSend?.Invoke(); });
        }
    }

    private static void AssertLostObservation(GeneratePaneInputResult? result, bool submitted)
    {
        Assert.NotNull(result); Assert.True(result.EventDispatched); Assert.Equal(submitted, result.InputSubmitted);
        Assert.False(result.ObservationAvailable); Assert.Null(result.Rows); Assert.Null(result.Cols);
        Assert.Null(result.ViewRows); Assert.Null(result.ViewCols); Assert.Null(result.ApplicationKeypad);
        Assert.Null(result.MouseTracking); Assert.Null(result.MouseSgr); Assert.Null(result.KittyKeyboardFlags);
        Assert.Null(result.ModifyOtherKeysLevel);
    }

    private static Task<GeneratePaneInputResult?> Dispatch(MainWindow window, TerminalPane pane, GeneratePaneInputParams input,
        Func<bool>? guard = null, CancellationToken token = default)
        => ((IAgentActionExecutor)window).GeneratePaneInputAsync(input, pane.AgentRegistrationForTesting!, guard ?? (() => true), () => { }, token);

    [AvaloniaTheory]
    [InlineData("0", "\u001bOp")]
    [InlineData("7", "\u001bOw")]
    [InlineData("decimal", "\u001bOn")]
    [InlineData("separator", "\u001bOl")]
    [InlineData("add", "\u001bOk")]
    [InlineData("subtract", "\u001bOm")]
    [InlineData("multiply", "\u001bOj")]
    [InlineData("divide", "\u001bOo")]
    [InlineData("enter", "\u001bOM")]
    public async Task Application_keypad_uses_actual_routed_key_events(string key, string expected)
    {
        var (window, pane, session) = Create();
        new AnsiParser(pane.Buffer!).Process("\u001b=");
        int history = 0;
        pane.TermView.KeyDownInterceptor = (_, _) => throw new InvalidOperationException("Synthetic input must not accept Command Assist suggestions.");
        pane.TermView.EnterObserving += () => history++;
        pane.TermView.EnterObserved += () => history++;
        var result = await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "keyDown", Key = key });
        Assert.NotNull(result); Assert.True(result.EventDispatched); Assert.True(result.Handled);
        Assert.Equal("avalonia-routed", result.Boundary); Assert.True(result.ApplicationKeypad);
        Assert.Equal(expected, Assert.Single(session.SentInput)); Assert.True(result.InputSubmitted); Assert.Equal(0, history);
    }

    [AvaloniaFact]
    public async Task Single_events_clear_synthetic_latches_and_text_is_a_separate_standalone_event()
    {
        var (window, pane, session) = Create();
        var parser = new AnsiParser(pane.Buffer!); parser.Process("\u001b=");
        int history = 0; pane.TermView.TextInputObserved += _ => history++;
        await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "keyDown", Key = "7" });
        await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "text", Text = "7" });
        await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "keyUp", Key = "7" });
        parser.Process("\u001b>");
        var numeric = await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "keyDown", Key = "8" });
        Assert.NotNull(numeric); Assert.False(numeric.Handled);
        await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "text", Text = "8" });
        Assert.Equal(new[] { "\u001bOw", "7", "8" }, session.SentInput); Assert.Equal(0, history);
        pane.TermView.RaiseEvent(new Avalonia.Input.TextInputEventArgs { RoutedEvent = Avalonia.Input.InputElement.TextInputEvent, Text = "human" });
        Assert.Equal(1, history); Assert.Equal("human", session.SentInput[^1]);
    }

    [AvaloniaTheory]
    [InlineData("7", "7", "\u001bOw")]
    [InlineData("add", "+", "\u001bOk")]
    public async Task A_handled_application_keypad_key_suppresses_the_one_backend_text_it_is_followed_by(string key, string text, string expected)
    {
        var (window, pane, session) = Create();
        new AnsiParser(pane.Buffer!).Process("\u001b=");
        int history = 0; pane.TermView.TextInputObserved += _ => history++;
        var plain = await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "keyDown", Key = key });
        Assert.NotNull(plain); Assert.True(plain.Handled); Assert.Null(plain.TextHandled);
        await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "keyUp", Key = key });
        session.SentInput.Clear();
        var result = await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "keyDown", Key = key, Text = text });
        Assert.NotNull(result); Assert.True(result.EventDispatched); Assert.True(result.Handled); Assert.True(result.TextHandled); Assert.True(result.InputSubmitted);
        Assert.Equal("avalonia-routed", result.Boundary);
        Assert.Equal(expected, Assert.Single(session.SentInput)); Assert.Equal(0, history);
        // The one-shot decision was consumed by that text: a later standalone commit of the same character is ordinary text.
        await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "text", Text = text });
        Assert.Equal(new[] { expected, text }, session.SentInput);
    }

    [AvaloniaFact]
    public async Task In_numeric_keypad_mode_the_key_down_is_unhandled_and_its_text_is_delivered_exactly_once()
    {
        var (window, pane, session) = Create();
        var result = await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "keyDown", Key = "7", Text = "7" });
        Assert.NotNull(result); Assert.True(result.EventDispatched); Assert.False(result.Handled); Assert.True(result.TextHandled); Assert.False(result.ApplicationKeypad);
        Assert.Equal("7", Assert.Single(session.SentInput));
    }

    [AvaloniaFact]
    public async Task A_key_down_with_text_is_refused_like_any_keyboard_event_while_another_correlation_is_pending()
    {
        var (window, pane, session) = Create();
        new AnsiParser(pane.Buffer!).Process("\u001b=");
        var args = new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.NumPad7, PhysicalKey = Avalonia.Input.PhysicalKey.NumPad7, Source = pane.TermView };
        pane.TermView.RaiseEvent(args);
        Assert.True(args.Handled); session.SentInput.Clear();
        // A human key left its one-shot text decision pending, so the bounded API must not consume or invent correlation for it.
        var result = await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "keyDown", Key = "7", Text = "7" });
        Assert.Null(result);
        Assert.Empty(session.SentInput);
    }

    [AvaloniaTheory]
    [InlineData("a", 2, "\u001b[27;5;97~")]
    [InlineData("a", 3, "\u001b[27;6;65~")]
    [InlineData("a", 4, "\u001b[27;3;97~")]
    [InlineData("a", 5, "\u001b[27;4;65~")]
    [InlineData("digit1", 2, "\u001b[27;5;49~")]
    [InlineData("digit2", 2, "\u001b[27;5;50~")]
    [InlineData("digit1", 3, "\u001b[27;6;33~")]
    [InlineData("digit1", 4, "\u001b[27;3;49~")]
    [InlineData("leftBracket", 2, "\u001b[27;5;91~")]
    [InlineData("semicolon", 2, "\u001b[27;5;59~")]
    [InlineData("slash", 3, "\u001b[27;6;63~")]
    [InlineData("return", 1, "\u001b[27;2;13~")]
    [InlineData("return", 2, "\u001b[27;5;13~")]
    [InlineData("return", 4, "\u001b[27;3;13~")]
    [InlineData("tab", 2, "\u001b[27;5;9~")]
    [InlineData("tab", 4, "\u001b[27;3;9~")]
    [InlineData("tab", 1, "\u001b[Z")]
    [InlineData("backspace", 1, "\u001b[27;2;127~")]
    [InlineData("backspace", 4, "\u001b[27;3;127~")]
    [InlineData("backspace", 3, "\u001b[27;6;8~")]
    [InlineData("backspace", 2, "\u007f")]
    [InlineData("escape", 2, "\u001b[27;5;27~")]
    [InlineData("escape", 4, "\u001b[27;3;27~")]
    [InlineData("space", 1, "\u001b[27;2;32~")]
    [InlineData("i", 2, "\u001b[27;5;105~")]
    public async Task Ordinary_modified_keys_encode_at_level_two_and_report_the_negotiated_level(string key, int modifiers, string expected)
    {
        var (window, pane, session) = Create();
        new AnsiParser(pane.Buffer!).Process("\u001b[>4;2m");
        var result = await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "keyDown", Key = key, Modifiers = modifiers });
        Assert.NotNull(result); Assert.True(result.Handled); Assert.True(result.InputSubmitted);
        Assert.Equal(expected, Assert.Single(session.SentInput));
        Assert.Equal(2, result.ModifyOtherKeysLevel); Assert.Equal(modifiers, result.Modifiers);
    }

    [AvaloniaTheory]
    [InlineData("a", 2, "\u0001")]
    [InlineData("leftBracket", 2, "\u001b")]
    [InlineData("digit2", 2, "\u0000")]
    [InlineData("slash", 2, "\u001f")]
    [InlineData("digit1", 2, "\u001b[27;5;49~")]
    [InlineData("digit1", 3, "\u001b[27;6;33~")]
    [InlineData("semicolon", 2, "\u001b[27;5;59~")]
    [InlineData("return", 1, "\u001b[27;2;13~")]
    [InlineData("return", 2, "\u001b[27;5;13~")]
    [InlineData("tab", 2, "\u001b[27;5;9~")]
    [InlineData("tab", 1, "\u001b[Z")]
    [InlineData("escape", 1, "\u001b")]
    [InlineData("backspace", 1, "\u007f")]
    [InlineData("a", 4, "\u001ba")]
    public async Task At_level_one_aliases_keep_their_legacy_bytes_and_extended_chords_encode(string key, int modifiers, string expected)
    {
        var (window, pane, session) = Create();
        new AnsiParser(pane.Buffer!).Process("\u001b[>4;1m");
        var result = await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "keyDown", Key = key, Modifiers = modifiers });
        Assert.NotNull(result); Assert.True(result.Handled);
        Assert.Equal(expected, Assert.Single(session.SentInput));
        Assert.Equal(1, result.ModifyOtherKeysLevel);
    }

    [AvaloniaTheory]
    [InlineData("a", 2, "\u0001")]
    [InlineData("return", 1, "\r")]
    [InlineData("tab", 1, "\u001b[Z")]
    [InlineData("backspace", 1, "\u007f")]
    [InlineData("escape", 1, "\u001b")]
    [InlineData("a", 4, "\u001ba")]
    public async Task At_level_zero_every_modified_ordinary_key_keeps_its_legacy_bytes(string key, int modifiers, string expected)
    {
        var (window, pane, session) = Create();
        new AnsiParser(pane.Buffer!).Process("\u001b[>4;2m\u001b[>4m");
        var result = await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "keyDown", Key = key, Modifiers = modifiers });
        Assert.NotNull(result); Assert.True(result.Handled);
        Assert.Equal(expected, Assert.Single(session.SentInput));
        Assert.Equal(0, result.ModifyOtherKeysLevel);
    }

    [AvaloniaTheory]
    [InlineData("a", 8)]
    [InlineData("a", 6)]
    public async Task Meta_and_ctrl_alt_ordinary_chords_stay_unhandled_without_inventing_bytes(string key, int modifiers)
    {
        var (window, pane, session) = Create();
        new AnsiParser(pane.Buffer!).Process("\u001b[>4;2m");
        var result = await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "keyDown", Key = key, Modifiers = modifiers });
        Assert.NotNull(result); Assert.False(result.Handled); Assert.False(result.InputSubmitted);
        Assert.Empty(session.SentInput);
    }

    [AvaloniaFact]
    public async Task A_handled_ordinary_key_suppresses_its_paired_backend_text_once()
    {
        var (window, pane, session) = Create();
        new AnsiParser(pane.Buffer!).Process("\u001b[>4;2m");
        var result = await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "keyDown", Key = "space", Modifiers = 1, Text = " " });
        Assert.NotNull(result); Assert.True(result.Handled); Assert.True(result.TextHandled);
        Assert.Equal("\u001b[27;2;32~", Assert.Single(session.SentInput));
        // The paired decision was consumed: a later standalone commit is ordinary text again, and the next
        // ordinary key can still be dispatched (no correlation latches the fixture).
        var standalone = await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "text", Text = "7" });
        Assert.NotNull(standalone);
        var next = await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "keyDown", Key = "a", Modifiers = 2 });
        Assert.NotNull(next); Assert.True(next.Handled);
        Assert.Equal(new[] { "\u001b[27;2;32~", "7", "\u001b[27;5;97~" }, session.SentInput);
    }

    [AvaloniaTheory]
    [InlineData("a", "A")]
    [InlineData("digit1", "!")]
    public async Task An_unhandled_shifted_ordinary_key_delivers_its_paired_text_exactly_once(string key, string text)
    {
        var (window, pane, session) = Create();
        new AnsiParser(pane.Buffer!).Process("\u001b[>4;2m");
        var result = await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "keyDown", Key = key, Modifiers = 1, Text = text });
        Assert.NotNull(result); Assert.False(result.Handled); Assert.True(result.TextHandled); Assert.True(result.InputSubmitted);
        Assert.Equal(text, Assert.Single(session.SentInput));
    }

    [AvaloniaTheory]
    [InlineData("mousePress", "left", 0, "\u001b[<0;3;2M")]
    [InlineData("mouseRelease", "left", 0, "\u001b[<0;3;2m")]
    [InlineData("mouseMove", "none", 0, "\u001b[<35;3;2M")]
    [InlineData("mouseWheel", null, 1, "\u001b[<64;3;2M")]
    [InlineData("mouseWheel", null, -1, "\u001b[<65;3;2M")]
    public async Task Mouse_events_use_routed_pointer_coordinate_and_wheel_paths(string kind, string? button, int delta, string expected)
    {
        var (window, pane, session) = Create();
        new AnsiParser(pane.Buffer!).Process("\u001b[?1003h\u001b[?1006h");
        var result = await Dispatch(window, pane, new()
        {
            PaneId = pane.PaneId,
            Kind = kind,
            Button = button,
            Column = 3,
            Row = 2,
            WheelDelta = delta == 0 ? null : delta
        });
        Assert.NotNull(result); Assert.True(result.EventDispatched); Assert.True(result.Handled);
        Assert.Equal((3, 2, 1003, true), (result.Column, result.Row, result.MouseTracking, result.MouseSgr));
        Assert.Equal(2.5 * pane.TermView.Metrics.CellWidth, result.X);
        Assert.Equal(1.5 * pane.TermView.Metrics.CellHeight, result.Y);
        Assert.Equal(expected, Assert.Single(session.SentInput));
    }

    [AvaloniaFact]
    public async Task True_X10_routes_presses_and_suppresses_release_and_wheel()
    {
        var (window, pane, session) = Create();
        new AnsiParser(pane.Buffer!).Process("\u001b[?9h\u001b[?1006h");
        foreach (var kind in new[] { "mousePress", "mouseRelease", "mouseMove", "mouseWheel" })
        {
            var result = await Dispatch(window, pane, new()
            {
                PaneId = pane.PaneId,
                Kind = kind,
                Column = 3,
                Row = 2,
                Button = kind == "mouseWheel" ? null : "left",
                WheelDelta = kind == "mouseWheel" ? 1 : null,
                Modifiers = 7
            });
            Assert.NotNull(result); Assert.Equal(9, result.MouseTracking); Assert.Equal(7, result.Modifiers);
            Assert.Equal(kind == "mousePress", result.InputSubmitted);
        }
        Assert.Equal("\u001b[<0;3;2M", Assert.Single(session.SentInput));
    }

    [AvaloniaTheory]
    [InlineData("guard")]
    [InlineData("deferred")]
    [InlineData("hidden")]
    [InlineData("remote")]
    [InlineData("maximized")]
    [InlineData("cell")]
    [InlineData("disabledMouse")]
    [InlineData("viewBuffer")]
    [InlineData("viewSession")]
    public async Task Ineligible_targets_are_refused_without_any_session_input(string reason)
    {
        var (window, pane, session) = Create();
        var input = new GeneratePaneInputParams { PaneId = pane.PaneId, Kind = "keyDown", Key = "7" };
        switch (reason)
        {
            case "viewBuffer": pane.TermView.SetBuffer(new TerminalBuffer(pane.Buffer!.Cols, pane.Buffer.Rows)); break;
            case "viewSession": pane.TermView.SetSession(new FakeTerminalSession()); break;
            case "deferred": pane.TermView.DefersBufferResizeToSession = true; break;
            case "hidden": pane.IsVisible = false; break;
            case "remote": pane.Profile!.Type = ConnectionType.SSH; break;
            case "maximized": window.WindowState = WindowState.Maximized; break;
            case "cell": input = new() { PaneId = pane.PaneId, Kind = "mousePress", Button = "left", Column = 256, Row = 128 }; break;
            case "disabledMouse": input = new() { PaneId = pane.PaneId, Kind = "mousePress", Button = "left", Column = 1, Row = 1 }; break;
        }
        Assert.Null(await Dispatch(window, pane, input, () => reason != "guard"));
        Assert.Empty(session.SentInput);
    }

    [AvaloniaFact]
    public async Task Cancelled_queued_dispatch_cannot_emit_an_event_later()
    {
        var (window, pane, session) = Create();
        new AnsiParser(pane.Buffer!).Process("\u001b=");
        using var cancelled = new CancellationTokenSource();
        var input = new GeneratePaneInputParams { PaneId = pane.PaneId, Kind = "keyDown", Key = "7" };
        // Invoke from a worker while the UI thread is deliberately occupied by this synchronous section.
        using var enqueued = new ManualResetEventSlim();
        var queued = Task.Run(() =>
        {
            var task = Dispatch(window, pane, input, token: cancelled.Token);
            enqueued.Set();
            return task;
        });
        Assert.True(enqueued.Wait(TimeSpan.FromSeconds(5)), "The worker must enqueue its dispatcher operation before cancellation.");
        cancelled.Cancel();
        Dispatcher.UIThread.RunJobs();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Dispatcher.UIThread.RunJobs(); Assert.Empty(session.SentInput);
    }

    [AvaloniaFact]
    public async Task Identity_changed_during_routed_event_retains_dispatch_without_a_successful_observation()
    {
        var (window, pane, session) = Create();
        var registration = pane.AgentRegistrationForTesting!;
        pane.TermView.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, (_, _) => registration.SetLifecycle(new FakeTerminalSession()),
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AssertLostObservation(await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "keyDown", Key = "7" }), false);
        Assert.Empty(session.SentInput);
    }
    [AvaloniaFact]
    public async Task Split_and_inactive_tabs_are_not_activated_or_fed_input()
    {
        var (window, original, session) = Create();
        var executor = (IAgentActionExecutor)window;
        await executor.SpawnAsync(null); Dispatcher.UIThread.RunJobs();
        var tabs = window.FindControl<TabControl>("Tabs")!; var selected = tabs.SelectedItem;
        session.SentInput.Clear();
        Assert.Null(await Dispatch(window, original, new() { PaneId = original.PaneId, Kind = "keyDown", Key = "7" }));
        Assert.Same(selected, tabs.SelectedItem);
        typeof(MainWindow).GetMethod("SplitPane", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(window, [Avalonia.Layout.Orientation.Horizontal]);
        Dispatcher.UIThread.RunJobs(); session.SentInput.Clear();
        foreach (var pane in window.AllPanesForTest())
            Assert.Null(await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "keyDown", Key = "7" }));
        Assert.Empty(session.SentInput);
    }

    [AvaloniaFact]
    public async Task Mouse_tracking_disabled_during_tunnel_cannot_start_selection()
    {
        var (window, pane, session) = Create();
        var parser = new AnsiParser(pane.Buffer!); parser.Process("\u001b[?1000h");
        pane.TermView.AddHandler(Avalonia.Input.InputElement.PointerPressedEvent, (_, _) => parser.Process("\u001b[?1000l"),
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AssertLostObservation(await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "mousePress", Button = "left", Column = 1, Row = 1 }), false);
        Assert.Empty(session.SentInput); Assert.False(pane.TermView.HasSelection());
    }

    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task Modified_keypad_keeps_ordinary_unhandled_routing_without_window_commands(int modifiers)
    {
        var (window, pane, session) = Create();
        new AnsiParser(pane.Buffer!).Process("\u001b=");
        var result = await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "keyDown", Key = "7", Modifiers = modifiers });
        Assert.NotNull(result); Assert.False(result.Handled); Assert.False(result.InputSubmitted); Assert.Equal(modifiers, result.Modifiers);
        Assert.Empty(session.SentInput);
    }
    [AvaloniaTheory]
    [InlineData("keyDown")]
    [InlineData("keyUp")]
    [InlineData("text")]
    public async Task Associated_text_keyboard_scope_is_unavailable_and_never_posts_a_send(string kind)
    {
        var (window, pane, session) = Create();
        pane.Buffer!.Modes.KittyKeyboard.Push(24);
        int host = 0; pane.TermView.TextInputObserved += _ => host++; pane.TermView.EnterObserved += () => host++;
        using var cancelled = new CancellationTokenSource();
        var result = await Dispatch(window, pane, new()
        {
            PaneId = pane.PaneId,
            Kind = kind,
            Key = kind == "text" ? null : "7",
            Text = kind == "text" ? "7" : null
        }, token: cancelled.Token);
        Assert.Null(result); cancelled.Cancel();
        Dispatcher.UIThread.RunJobs(); Assert.Empty(session.SentInput); Assert.Equal(0, host);
    }

    [AvaloniaFact]
    public async Task Associated_mode_enabled_during_tunnel_is_rechecked_before_terminal_handler()
    {
        var (window, pane, session) = Create();
        pane.TermView.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, (_, _) => pane.Buffer!.Modes.KittyKeyboard.Push(24),
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AssertLostObservation(await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "keyDown", Key = "7" }), false);
        Dispatcher.UIThread.RunJobs(); Assert.Empty(session.SentInput);
    }

    [AvaloniaFact]
    public void Deferred_queue_creation_refuses_synthetic_work_even_if_a_preflight_guard_accepts_it()
    {
        var (_, pane, session) = Create();
        pane.TermView.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, (_, _) => pane.Buffer!.Modes.KittyKeyboard.Push(24),
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        var args = new Avalonia.Input.KeyEventArgs
        {
            RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
            Key = Avalonia.Input.Key.NumPad7,
            PhysicalKey = Avalonia.Input.PhysicalKey.NumPad7
        };
        Assert.False(pane.TermView.RaiseGeneratedInput(args, () => true, out _));
        Dispatcher.UIThread.RunJobs(); Assert.Empty(session.SentInput);
        pane.Buffer!.Modes.KittyKeyboard.Pop(1);
        pane.TermView.RaiseEvent(new Avalonia.Input.TextInputEventArgs { RoutedEvent = Avalonia.Input.InputElement.TextInputEvent, Text = "human" });
        Assert.Equal("human", Assert.Single(session.SentInput));
    }

    [AvaloniaFact]
    public async Task Existing_user_associated_key_is_not_drained_or_replaced_by_a_generated_key()
    {
        var (window, pane, session) = Create();
        pane.Buffer!.Modes.KittyKeyboard.Push(24);
        pane.TermView.RaiseEvent(new Avalonia.Input.KeyEventArgs
        {
            RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
            Key = Avalonia.Input.Key.NumPad7,
            PhysicalKey = Avalonia.Input.PhysicalKey.NumPad7
        });
        pane.Buffer.Modes.KittyKeyboard.Pop(1);
        Assert.Empty(session.SentInput);
        Assert.Null(await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "keyDown", Key = "8" }));
        Assert.Empty(session.SentInput);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("\u001b[57406u", Assert.Single(session.SentInput));
    }
    [AvaloniaFact]
    public async Task Standalone_synthetic_key_does_not_swallow_a_later_human_text_commit()
    {
        var (window, pane, session) = Create();
        new AnsiParser(pane.Buffer!).Process("\u001b=");
        Assert.NotNull(await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "keyDown", Key = "7" }));
        pane.TermView.RaiseEvent(new Avalonia.Input.TextInputEventArgs { RoutedEvent = Avalonia.Input.InputElement.TextInputEvent, Text = "human" });
        Assert.Equal(new[] { "\u001bOw", "human" }, session.SentInput);
    }

    [AvaloniaTheory]
    [InlineData("keyDown")]
    [InlineData("keyUp")]
    [InlineData("text")]
    public async Task Preexisting_human_keypad_text_correlation_is_preserved_and_refuses_synthetic_keyboard(string kind)
    {
        var (window, pane, session) = Create();
        new AnsiParser(pane.Buffer!).Process("\u001b=");
        pane.TermView.RaiseEvent(new Avalonia.Input.KeyEventArgs
        {
            RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
            Key = Avalonia.Input.Key.NumPad7,
            PhysicalKey = Avalonia.Input.PhysicalKey.NumPad7
        });
        Assert.Null(await Dispatch(window, pane, new()
        {
            PaneId = pane.PaneId,
            Kind = kind,
            Key = kind == "text" ? null : "8",
            Text = kind == "text" ? "8" : null
        }));
        pane.TermView.RaiseEvent(new Avalonia.Input.TextInputEventArgs { RoutedEvent = Avalonia.Input.InputElement.TextInputEvent, Text = "7" });
        Assert.Equal("\u001bOw", Assert.Single(session.SentInput));
        pane.TermView.RaiseEvent(new Avalonia.Input.TextInputEventArgs { RoutedEvent = Avalonia.Input.InputElement.TextInputEvent, Text = "human" });
        Assert.Equal(new[] { "\u001bOw", "human" }, session.SentInput);
    }

    [AvaloniaTheory]
    [InlineData("mousePress")]
    [InlineData("mouseRelease")]
    [InlineData("mouseMove")]
    [InlineData("mouseWheel")]
    public void Reporting_branch_refuses_synthetic_host_selection_and_scroll_even_if_the_entry_lease_accepts(string kind)
    {
        var (window, pane, session) = Create();
        var view = pane.TermView;
        var parser = new AnsiParser(pane.Buffer!);
        for (int i = 0; i < view.Rows + 8; i++) parser.Process("ABCD\r\n");
        view.ScrollOffset = 0;
        using var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), Avalonia.Input.PointerType.Mouse, true);
        Point At(int col) => view.TranslatePoint(new Point((col + 0.5) * view.Metrics.CellWidth, 0.5 * view.Metrics.CellHeight), window)!.Value;
        var held = new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.LeftButtonPressed);
        var moved = new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.Other);
        var released = new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PointerUpdateKind.LeftButtonReleased);
        Avalonia.Input.PointerEventArgs Move(int col) => new(Avalonia.Input.InputElement.PointerMovedEvent, view, pointer, window, At(col), 0, moved, Avalonia.Input.KeyModifiers.None);
        view.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(view, pointer, window, At(0), 0, held, Avalonia.Input.KeyModifiers.None));
        view.RaiseEvent(Move(1));
        string? before = view.GetSelectedText(); Assert.True(view.HasSelection());
        Avalonia.Interactivity.RoutedEventArgs args = kind switch
        {
            "mousePress" => new Avalonia.Input.PointerPressedEventArgs(view, pointer, window, At(3), 0, held, Avalonia.Input.KeyModifiers.None),
            "mouseRelease" => new Avalonia.Input.PointerReleasedEventArgs(view, pointer, window, At(3), 0, released, Avalonia.Input.KeyModifiers.None, Avalonia.Input.MouseButton.Left),
            "mouseMove" => Move(3),
            _ => new Avalonia.Input.PointerWheelEventArgs(view, pointer, window, At(3), 0, moved, Avalonia.Input.KeyModifiers.None, new Vector(0, 1)),
        };
        Assert.True(view.RaiseGeneratedInput(args, () => true, out bool submitted));
        Assert.False(submitted); Assert.True(args.Handled);
        Assert.Equal(before, view.GetSelectedText()); Assert.True(view.HasSelection());
        for (int i = 0; i < 8; i++) view.AdvanceScrollAnimation();
        Assert.Equal(0, view.ScrollOffset); Assert.Empty(session.SentInput);
        if (kind == "mouseRelease")
        {
            view.RaiseEvent(Move(3));
            Assert.NotEqual(before, view.GetSelectedText());
        }
    }
    [AvaloniaTheory]
    [InlineData("mode")]
    [InlineData("session")]
    [InlineData("buffer")]
    public async Task A_returned_submission_survives_post_send_mode_or_identity_loss(string change)
    {
        var probe = new InputProbe();
        var (window, pane) = CreateWithSession(probe.Session.Object); probe.Sent.Clear();
        var parser = new AnsiParser(pane.Buffer!); parser.Process("\u001b[?1000h\u001b[?1006h");
        probe.AfterSend = () =>
        {
            if (change == "mode") parser.Process("\u001b[?1000l");
            else if (change == "session") pane.AgentRegistrationForTesting!.SetLifecycle(new FakeTerminalSession());
            else pane.TermView.SetBuffer(new TerminalBuffer(pane.Buffer!.Cols, pane.Buffer.Rows));
        };
        var result = await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "mousePress", Button = "left", Column = 3, Row = 2 });
        AssertLostObservation(result, true);
        Assert.Equal("\u001b[<0;3;2M", Assert.Single(probe.Sent));
        probe.AfterSend = null;
    }

    [AvaloniaFact]
    public async Task A_busy_post_send_buffer_preserves_submission_without_stale_geometry_or_a_second_send()
    {
        var probe = new InputProbe();
        var (window, pane) = CreateWithSession(probe.Session.Object); probe.Sent.Clear();
        var buffer = pane.Buffer!; new AnsiParser(buffer).Process("\u001b=");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        Task? writer = null;
        probe.AfterSend = () =>
        {
            writer = Task.Run(() =>
            {
                buffer.Lock.EnterWriteLock();
                try { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); }
                finally { buffer.Lock.ExitWriteLock(); }
            });
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        };
        try
        {
            var result = await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "keyDown", Key = "7" });
            AssertLostObservation(result, true);
            Assert.Equal("\u001bOw", Assert.Single(probe.Sent));
        }
        finally
        {
            probe.AfterSend = null; release.Set();
            if (writer != null) await writer.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }
    [AvaloniaFact]
    public async Task Submission_notification_runs_once_on_UI_only_after_the_production_input_call_returns()
    {
        var (window, pane, session) = Create(); new AnsiParser(pane.Buffer!).Process("\u001b=");
        int notifications = 0;
        var result = await ((IAgentActionExecutor)window).GeneratePaneInputAsync(
            new() { PaneId = pane.PaneId, Kind = "keyDown", Key = "7" }, pane.AgentRegistrationForTesting!, () => true,
            () => { Assert.True(Dispatcher.UIThread.CheckAccess()); Assert.Single(session.SentInput); notifications++; },
            TestContext.Current.CancellationToken);
        Assert.NotNull(result); Assert.True(result.InputSubmitted); Assert.True(result.ObservationAvailable);
        Assert.Equal(1, notifications); Assert.Single(session.SentInput);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refused_or_unhandled_events_never_notify_a_submission(bool unhandled)
    {
        var (window, pane, session) = Create(); new AnsiParser(pane.Buffer!).Process("\u001b=");
        int notifications = 0;
        await ((IAgentActionExecutor)window).GeneratePaneInputAsync(
            new() { PaneId = pane.PaneId, Kind = "keyDown", Key = "7", Modifiers = unhandled ? 1 : 0 },
            pane.AgentRegistrationForTesting!, () => unhandled, () => notifications++, TestContext.Current.CancellationToken);
        Assert.Equal(0, notifications); Assert.Empty(session.SentInput);
    }

    [AvaloniaFact]
    public async Task A_busy_buffer_before_dispatch_has_no_event_submission_or_notification()
    {
        var (window, pane, session) = Create(); var buffer = pane.Buffer!;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var writer = Task.Run(() =>
        {
            buffer.Lock.EnterWriteLock();
            try { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); }
            finally { buffer.Lock.ExitWriteLock(); }
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        int notifications = 0;
        try
        {
            var result = await ((IAgentActionExecutor)window).GeneratePaneInputAsync(
                new() { PaneId = pane.PaneId, Kind = "keyDown", Key = "7" }, pane.AgentRegistrationForTesting!, () => true,
                () => notifications++, TestContext.Current.CancellationToken);
            Assert.Null(result); Assert.Equal(0, notifications); Assert.Empty(session.SentInput);
        }
        finally { release.Set(); await writer.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); }
    }
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Submitted_synthetic_text_poisons_markless_capture_without_firing_human_input_observers(bool throwingObserver)
    {
        var registry = new AgentSessionRegistry();
        using var registryScope = AgentSessionRegistry.OverrideInstanceForTesting(registry);
        using var service = new AgentHostService(registry);
        using var serviceScope = AgentHostService.OverrideInstanceForTesting(service);
        var (window, pane, session) = Create();
        service.ActEnabled = true; service.SetActionExecutor(window);
        const string typed = "echo human";
        pane.NotifyTypedTextObserved(typed); new AnsiParser(pane.Buffer!).Process("$ " + typed);
        var read = typeof(TerminalPane).GetMethod("ReadEchoedMarklessSubmission", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("The markless capture read boundary is unavailable.");
        Assert.Equal(typed, read.Invoke(pane, null));
        int humanObservations = 0; pane.TermView.TextInputObserved += _ => humanObservations++;
        int injections = 0;
        var registration = pane.AgentRegistrationForTesting!; var original = registration.InputInjected;
        registration.InputInjected = () =>
        {
            injections++; original?.Invoke();
            if (throwingObserver) throw new InvalidOperationException("private input observer diagnostic");
        };
        string request = "{\"v\":1,\"id\":1,\"method\":\"generatePaneInput\",\"params\":{\"paneId\":\"" + pane.PaneId + "\",\"kind\":\"text\",\"text\":\"7\"}}";
        var response = await service.HandleRequestLineAsync(request, TestContext.Current.CancellationToken);
        Assert.Null(response.Error);
        Assert.True(response.Result!.Value.GetProperty("inputSubmitted").GetBoolean());
        Assert.Equal("7", Assert.Single(session.SentInput)); Assert.Equal(0, humanObservations); Assert.Equal(1, injections);
        Assert.Equal(AgentAttentionTier.Wrote, registration.AttentionMachine.Snapshot().Tier);
        Assert.Equal(!throwingObserver, response.Result.Value.GetProperty("observationAvailable").GetBoolean());
        // No echo of the synthetic character arrived: the still-readable stale human prefix must not be capturable.
        Assert.Null(read.Invoke(pane, null));
    }
    [AvaloniaFact]
    public async Task Standalone_Kitty_event_type_keyup_cannot_inherit_the_prior_generated_press()
    {
        var (window, pane, session) = Create();
        pane.Buffer!.Modes.KittyKeyboard.Push(10);
        Assert.False(pane.Buffer.Modes.IsApplicationKeypad);
        var down = await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "keyDown", Key = "7" });
        Assert.NotNull(down); Assert.True(down.EventDispatched); Assert.Single(session.SentInput);
        var up = await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "keyUp", Key = "7" });
        Assert.NotNull(up); Assert.True(up.EventDispatched); Assert.False(up.InputSubmitted);
        Assert.Single(session.SentInput);
    }
}
