using Avalonia;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ntilde.AgentHost;
using Ntilde.AgentHost.Contracts;
using Ntilde.Shell;

namespace Ntilde;

public partial class MainWindow
{
    private bool _agentGeneratedInputInProgress;
    /// <summary>Dispatch one synthetic Avalonia event without focus changes, OS injection or direct byte injection.</summary>
    async Task<GeneratePaneInputResult?> IAgentActionExecutor.GeneratePaneInputAsync(GeneratePaneInputParams input,
        AgentSessionRegistration registration, Func<bool> isCurrent, CancellationToken cancellationToken)
    {
        if (!GeneratePaneInputValidation.IsValid(input)) return null;
        return await Dispatcher.InvokeAsync(() => DispatchGeneratedInput(input, registration, isCurrent, cancellationToken),
            DispatcherPriority.Normal, cancellationToken);
    }

    /// <summary>One synchronous UI-thread operation; queued cancellation is checked before any event is created or applied.</summary>
    private GeneratePaneInputResult? DispatchGeneratedInput(GeneratePaneInputParams input,
        AgentSessionRegistration registration, Func<bool> isCurrent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pane = _paneOwnerTab.Keys.FirstOrDefault(candidate => candidate.PaneId == input.PaneId);
        var session = registration.ObservationSession;
        bool Matches() => isCurrent() && pane != null && CanAgentResizePane(pane)
            && pane.IsCurrentAgentRegistration(registration) && ReferenceEquals(pane.Buffer, registration.Buffer) && ReferenceEquals(pane.Session, session)
            && ReferenceEquals(registration.ObservationSession, session)
            && pane.TermView.MatchesGeneratedInputContext(registration.Buffer, session)
            && pane.TermView.CanGenerateInputSynchronously(input.Column == null)
            && (input.Column == null || registration.Buffer.IsMouseReportingActive());
        if (_agentGeneratedInputInProgress || _agentPaneResizeInProgress || pane == null || !Matches()) return null;
        var view = pane.TermView;
        if (!TryObserveGeneratedInput(view, registration, input, false, null, out _)) return null;
        bool pointer = input.Column != null;
        // Without reporting a press can activate a hyperlink. This bounded QA API does not authorize host actions.
        Point? position = pointer ? new Point((input.Column.GetValueOrDefault() - 0.5) * view.Metrics.CellWidth,
            (input.Row.GetValueOrDefault() - 0.5) * view.Metrics.CellHeight) : null;
        var modifiers = GeneratedModifiers(input.Modifiers);
        var args = CreateGeneratedInputEvent(view, input, modifiers, position);
        if (args == null) return null;
        using var pointerLease = (args as PointerEventArgs)?.Pointer as Pointer;
        // No awaits between this final lease check and RaiseEvent. A cancelled queued callback cannot run later.
        cancellationToken.ThrowIfCancellationRequested();
        if (!Matches()) return null;
        _agentGeneratedInputInProgress = true;
        bool inputSubmitted;
        try { if (!view.RaiseGeneratedInput(args, () => !cancellationToken.IsCancellationRequested && Matches(), out inputSubmitted)) return null; }
        finally { _agentGeneratedInputInProgress = false; }
        if (!Matches() || !TryObserveGeneratedInput(view, registration, input, args.Handled, position, out var result) || result == null) return null;
        var actualModifiers = args switch { KeyEventArgs key => key.KeyModifiers, PointerEventArgs pointerEvent => pointerEvent.KeyModifiers, _ => KeyModifiers.None };
        return result with { Modifiers = GeneratedModifierBits(actualModifiers), InputSubmitted = inputSubmitted };
    }

    private static int GeneratedModifierBits(KeyModifiers value)
        => ((value & KeyModifiers.Shift) != 0 ? 1 : 0) | ((value & KeyModifiers.Control) != 0 ? 2 : 0)
        | ((value & KeyModifiers.Alt) != 0 ? 4 : 0) | ((value & KeyModifiers.Meta) != 0 ? 8 : 0);

    private static KeyModifiers GeneratedModifiers(int value)
        => ((value & 1) != 0 ? KeyModifiers.Shift : 0) | ((value & 2) != 0 ? KeyModifiers.Control : 0)
        | ((value & 4) != 0 ? KeyModifiers.Alt : 0) | ((value & 8) != 0 ? KeyModifiers.Meta : 0);

    private RoutedEventArgs? CreateGeneratedInputEvent(TerminalView view, GeneratePaneInputParams input, KeyModifiers modifiers, Point? position)
    {
        if (input.Kind == "text") return new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = input.Text, Source = view };
        if (input.Kind is "keyDown" or "keyUp")
        {
            if (input.Key is not { } keyName) return null;
            var (key, physical) = GeneratedKey(keyName);
            return new KeyEventArgs
            {
                RoutedEvent = input.Kind == "keyDown" ? InputElement.KeyDownEvent : InputElement.KeyUpEvent,
                Key = key,
                PhysicalKey = physical,
                KeyModifiers = modifiers,
                Source = view
            };
        }
        if (position is not { } point) return null;
        var rootPosition = view.TranslatePoint(point, this);
        if (rootPosition == null) return null;
        // A fresh framework pointer describes this single event; it is not an OS device or persistent button state.
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        var (rawButton, update, button) = GeneratedButton(input.Button, input.Kind == "mouseRelease");
        var properties = new PointerPointProperties(rawButton, input.Kind is "mouseMove" or "mouseWheel" ? PointerUpdateKind.Other : update);
        return input.Kind switch
        {
            "mousePress" => new PointerPressedEventArgs(view, pointer, this, rootPosition.Value, 0, properties, modifiers),
            "mouseRelease" => new PointerReleasedEventArgs(view, pointer, this, rootPosition.Value, 0, properties, modifiers, button),
            "mouseMove" => new PointerEventArgs(InputElement.PointerMovedEvent, view, pointer, this, rootPosition.Value, 0, properties, modifiers),
            "mouseWheel" => new PointerWheelEventArgs(view, pointer, this, rootPosition.Value, 0, properties, modifiers, new Vector(0, input.WheelDelta.GetValueOrDefault())),
            _ => null,
        };
    }

    private static (RawInputModifiers, PointerUpdateKind, MouseButton) GeneratedButton(string? button, bool release)
    {
        return button switch
        {
            "left" => (release ? RawInputModifiers.None : RawInputModifiers.LeftMouseButton,
                release ? PointerUpdateKind.LeftButtonReleased : PointerUpdateKind.LeftButtonPressed, MouseButton.Left),
            "middle" => (release ? RawInputModifiers.None : RawInputModifiers.MiddleMouseButton,
                release ? PointerUpdateKind.MiddleButtonReleased : PointerUpdateKind.MiddleButtonPressed, MouseButton.Middle),
            "right" => (release ? RawInputModifiers.None : RawInputModifiers.RightMouseButton,
                release ? PointerUpdateKind.RightButtonReleased : PointerUpdateKind.RightButtonPressed, MouseButton.Right),
            _ => (RawInputModifiers.None, PointerUpdateKind.Other, MouseButton.None),
        };
    }

    private static (Key, PhysicalKey) GeneratedKey(string key) => key switch
    {
        "0" => (Key.NumPad0, PhysicalKey.NumPad0),
        "1" => (Key.NumPad1, PhysicalKey.NumPad1),
        "2" => (Key.NumPad2, PhysicalKey.NumPad2),
        "3" => (Key.NumPad3, PhysicalKey.NumPad3),
        "4" => (Key.NumPad4, PhysicalKey.NumPad4),
        "5" => (Key.NumPad5, PhysicalKey.NumPad5),
        "6" => (Key.NumPad6, PhysicalKey.NumPad6),
        "7" => (Key.NumPad7, PhysicalKey.NumPad7),
        "8" => (Key.NumPad8, PhysicalKey.NumPad8),
        "9" => (Key.NumPad9, PhysicalKey.NumPad9),
        "decimal" => (Key.Decimal, PhysicalKey.NumPadDecimal),
        "separator" => (Key.Separator, PhysicalKey.NumPadComma),
        "add" => (Key.Add, PhysicalKey.NumPadAdd),
        "subtract" => (Key.Subtract, PhysicalKey.NumPadSubtract),
        "multiply" => (Key.Multiply, PhysicalKey.NumPadMultiply),
        "divide" => (Key.Divide, PhysicalKey.NumPadDivide),
        "enter" => (Key.Enter, PhysicalKey.NumPadEnter),
        _ => throw new ArgumentException("Not a bounded keypad key.", nameof(key)),
    };

    private static int ObservedMouseTracking(Ntilde.VT.ModeState modes) => modes switch
    {
        { MouseModeAnyEvent: true } => 1003,
        { MouseModeButtonEvent: true } => 1002,
        { MouseModeX10: true } => 1000,
        { MouseModeX10PressOnly: true } => 9,
        _ => 0,
    };

    private static bool TryObserveGeneratedInput(TerminalView view, AgentSessionRegistration registration,
        GeneratePaneInputParams input, bool handled, Point? position, out GeneratePaneInputResult? result)
    {
        result = null;
        var buffer = registration.Buffer;
        if (!buffer.Lock.TryEnterReadLock(0)) return false;
        try
        {
            if (buffer.Rows != view.Rows || buffer.Cols != view.Cols || view.Metrics.CellWidth <= 0 || view.Metrics.CellHeight <= 0
                || input.Column > buffer.Cols || input.Row > buffer.Rows) return false;
            var modes = buffer.Modes;
            result = new GeneratePaneInputResult
            {
                EventDispatched = true,
                Handled = handled,
                Rows = buffer.Rows,
                Cols = buffer.Cols,
                ViewRows = view.Rows,
                ViewCols = view.Cols,
                Column = input.Column,
                Row = input.Row,
                X = position?.X,
                Y = position?.Y,
                Modifiers = input.Modifiers,
                ApplicationKeypad = modes.IsApplicationKeypad,
                MouseSgr = modes.MouseModeSGR,
                MouseTracking = ObservedMouseTracking(modes),
                KittyKeyboardFlags = modes.KittyKeyboard.Flags,
            };
            return true;
        }
        finally { buffer.Lock.ExitReadLock(); }
    }
}
