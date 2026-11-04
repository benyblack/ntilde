using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
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
        var profile = new TerminalProfile { Name = "Generated input local", Type = ConnectionType.Local };
        var session = new FakeTerminalSession();
        var settings = new TerminalSettings
        {
            SessionPersistence = SessionPersistenceMode.Off,
            AgentAccessActEnabled = true,
            CommandAssistShellIntegrationEnabled = false,
            Profiles = [profile],
            DefaultProfileId = profile.Id
        };
        var window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        { Settings = settings, SessionFactory = new RecordingSessionFactory(session), CommandAssist = TestCommandAssistServices.Instance });
        window.Show(); Dispatcher.UIThread.RunJobs();
        session.SentInput.Clear();
        return (window, Assert.Single(window.AllPanesForTest()), session);
    }

    private static Task<GeneratePaneInputResult?> Dispatch(MainWindow window, TerminalPane pane, GeneratePaneInputParams input,
        Func<bool>? guard = null, CancellationToken token = default)
        => ((IAgentActionExecutor)window).GeneratePaneInputAsync(input, pane.AgentRegistrationForTesting!, guard ?? (() => true), token);

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
    public async Task Identity_changed_during_routed_event_has_no_successful_observation()
    {
        var (window, pane, session) = Create();
        var registration = pane.AgentRegistrationForTesting!;
        pane.TermView.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, (_, _) => registration.SetLifecycle(new FakeTerminalSession()),
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Assert.Null(await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "keyDown", Key = "7" }));
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
        Assert.Null(await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "mousePress", Button = "left", Column = 1, Row = 1 }));
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
        Assert.Null(await Dispatch(window, pane, new() { PaneId = pane.PaneId, Kind = "keyDown", Key = "7" }));
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
}
