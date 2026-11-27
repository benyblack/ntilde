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
        AgentSessionRegistration registration, Func<bool> isCurrent, Action onInputSubmitted, CancellationToken cancellationToken)
    {
        if (!GeneratePaneInputValidation.IsValid(input)) return null;
        return await Dispatcher.InvokeAsync(() => DispatchGeneratedInput(input, registration, isCurrent, onInputSubmitted, cancellationToken),
            DispatcherPriority.Normal, cancellationToken);
    }

    /// <summary>One synchronous UI-thread operation; queued cancellation is checked before any event is created or applied.</summary>
    private GeneratePaneInputResult? DispatchGeneratedInput(GeneratePaneInputParams input,
        AgentSessionRegistration registration, Func<bool> isCurrent, Action onInputSubmitted, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pane = _paneOwnerTab.Keys.FirstOrDefault(candidate => candidate.PaneId == input.PaneId);
        bool pointer = GeneratePaneInputValidation.IsPointer(input);
        var session = registration.ObservationSession;
        // The follow-up text of a key-down pair runs while that key's own one-shot text decision is pending, so only the entry check and
        // the first event's lease include the pending-correlation test; the follow-up keeps every identity and currentness condition.
        bool MatchesTarget() => isCurrent() && pane != null && CanAgentResizePane(pane)
            && pane.IsCurrentAgentRegistration(registration) && ReferenceEquals(pane.Buffer, registration.Buffer) && ReferenceEquals(pane.Session, session)
            && ReferenceEquals(registration.ObservationSession, session)
            && pane.TermView.MatchesGeneratedInputContext(registration.Buffer, session)
            // A wheel notch while reporting is inactive is the view's own scroll: it sends no bytes and cannot activate a link, so it is the one pointer event routed then (#999 viewport control).
            && (!pointer || input.Kind == "mouseWheel" || registration.Buffer.IsMouseReportingActive());
        bool Matches() => MatchesTarget() && pane!.TermView.CanGenerateInputSynchronously(!pointer);
        if (_agentGeneratedInputInProgress || _agentPaneResizeInProgress || pane == null || !Matches()) return null;
        var view = pane.TermView;
        // Without reporting a press can activate a hyperlink. This bounded QA API does not authorize host actions.
        if (!TryGeneratedPointerPosition(view, input, out var position, out var cell)) return null;
        if (!TryObserveGeneratedInput(view, registration, input, cell, false, null, out _)) return null;
        var modifiers = GeneratedModifiers(input.Modifiers);
        var args = CreateGeneratedInputEvent(view, input, modifiers, position);
        if (args == null) return null;
        // A key-down that carries text also raises the backend's TextInput in the same dispatch, exercising the handled-key duplicate guard.
        var followUp = input is { Kind: "keyDown", Text: not null } ? new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = input.Text, Source = view } : null;
        using var pointerLease = (args as PointerEventArgs)?.Pointer as Pointer;
        // No awaits between this final lease check and RaiseEvent. A cancelled queued callback cannot run later.
        cancellationToken.ThrowIfCancellationRequested();
        if (!Matches()) return null;
        _agentGeneratedInputInProgress = true;
        bool inputSubmitted;
        bool accepted;
        try
        {
            accepted = view.RaiseGeneratedInput(args, () => !cancellationToken.IsCancellationRequested && Matches(), out inputSubmitted,
                followUp, () => !cancellationToken.IsCancellationRequested && MatchesTarget());
        }
        finally { _agentGeneratedInputInProgress = false; }
        if (!accepted && !inputSubmitted) return null;
        var facts = GeneratedDispatchFacts(input, args, cell, position, inputSubmitted) with { TextHandled = followUp?.Handled };
        if (!TryNotifyGeneratedSubmission(inputSubmitted, onInputSubmitted)) return facts;
        return ObserveGeneratedInputAfterDispatch(view, registration, input, args, cell, position, facts, Matches);
    }

    private static bool TryNotifyGeneratedSubmission(bool submitted, Action onInputSubmitted)
    {
        try
        {
            if (submitted) onInputSubmitted();
            return true;
        }
        catch (Exception)
        {
            // A fact observer cannot undo an input call or turn a completed action into a safe retry.
            return false;
        }
    }

    /// <summary>
    /// The routed position of a pointer event and the viewport cell it falls in (the same one-based cell the report path derives). A cell
    /// request is routed at that cell's centre; a pixel request at the centre of that device pixel, and is refused outside the grid.
    /// Non-pointer events have neither.
    /// </summary>
    private static bool TryGeneratedPointerPosition(TerminalView view, GeneratePaneInputParams input, out Point? position, out (int Column, int Row)? cell)
    {
        position = null;
        cell = null;
        if (!GeneratePaneInputValidation.IsPointer(input)) return true;
        if (input.PixelX is { } pixelX && input.PixelY is { } pixelY)
        {
            if (!view.TryGetPixelPosition(pixelX, pixelY, out var centre)) return false;
            position = centre;
            cell = view.ToMouseReportCell(centre);
            return true;
        }

        // A double-size line holds half the columns, each two cells wide: a column right of its half is not a cell of that row.
        Point cellCentre = view.MouseReportCellCentre(input.Column.GetValueOrDefault(), input.Row.GetValueOrDefault());
        var requested = (input.Column.GetValueOrDefault(), input.Row.GetValueOrDefault());
        if (view.ToMouseReportCell(cellCentre) != requested && view.IsDoubleSizeViewportRow(requested.Item2)) return false;
        position = cellCentre;
        cell = requested;
        return true;
    }

    private static GeneratePaneInputResult GeneratedDispatchFacts(GeneratePaneInputParams input, RoutedEventArgs args,
        (int Column, int Row)? cell, Point? position, bool inputSubmitted)
    {
        var modifiers = args switch { KeyEventArgs key => key.KeyModifiers, PointerEventArgs pointer => pointer.KeyModifiers, _ => KeyModifiers.None };
        return new GeneratePaneInputResult
        {
            EventDispatched = true,
            Handled = args.Handled,
            InputSubmitted = inputSubmitted,
            Column = cell?.Column,
            Row = cell?.Row,
            X = position?.X,
            Y = position?.Y,
            Modifiers = GeneratedModifierBits(modifiers),
        };
    }

    private static GeneratePaneInputResult ObserveGeneratedInputAfterDispatch(TerminalView view, AgentSessionRegistration registration,
        GeneratePaneInputParams input, RoutedEventArgs args, (int Column, int Row)? cell, Point? position, GeneratePaneInputResult facts, Func<bool> isCurrent)
    {
        try
        {
            if (!isCurrent() || !TryObserveGeneratedInput(view, registration, input, cell, args.Handled, position, out var observed)
                || observed == null || !isCurrent()) return facts;
            return observed with { Modifiers = facts.Modifiers, InputSubmitted = facts.InputSubmitted, TextHandled = facts.TextHandled };
        }
        catch (Exception)
        {
            // An action already ran: unavailable/disposed metadata must not erase its dispatch/submission facts.
            return facts;
        }
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
            // Validation already bounded the name; ordinary keys and keypad keys are two disjoint vocabularies.
            var (key, physical) = GeneratedOrdinaryKey(keyName) ?? GeneratedKey(keyName);
            return new KeyEventArgs
            {
                RoutedEvent = input.Kind == "keyDown" ? InputElement.KeyDownEvent : InputElement.KeyUpEvent,
                Key = key,
                PhysicalKey = physical,
                KeySymbol = input.KeySymbol,
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

    private static (Key, PhysicalKey)? GeneratedOrdinaryKey(string key)
    {
        // Letters and main-row digits are contiguous enum ranges; punctuation and named keys follow the US layout.
        if (key.Length == 1 && key[0] is >= 'a' and <= 'z')
        {
            int offset = key[0] - 'a';
            return ((Key)((int)Key.A + offset), (PhysicalKey)((int)PhysicalKey.A + offset));
        }
        if (key.Length == 6 && key is ['d', 'i', 'g', 'i', 't', ..] && key[5] is >= '0' and <= '9')
        {
            int offset = key[5] - '0';
            return ((Key)((int)Key.D0 + offset), (PhysicalKey)((int)PhysicalKey.Digit0 + offset));
        }
        return key switch
        {
            "grave" => (Key.OemTilde, PhysicalKey.Backquote),
            "minus" => (Key.OemMinus, PhysicalKey.Minus),
            "equal" => (Key.OemPlus, PhysicalKey.Equal),
            "leftBracket" => (Key.OemOpenBrackets, PhysicalKey.BracketLeft),
            "rightBracket" => (Key.OemCloseBrackets, PhysicalKey.BracketRight),
            "backslash" => (Key.OemBackslash, PhysicalKey.Backslash),
            "semicolon" => (Key.OemSemicolon, PhysicalKey.Semicolon),
            "apostrophe" => (Key.OemQuotes, PhysicalKey.Quote),
            "comma" => (Key.OemComma, PhysicalKey.Comma),
            "period" => (Key.OemPeriod, PhysicalKey.Period),
            "slash" => (Key.OemQuestion, PhysicalKey.Slash),
            "tab" => (Key.Tab, PhysicalKey.Tab),
            "backspace" => (Key.Back, PhysicalKey.Backspace),
            "escape" => (Key.Escape, PhysicalKey.Escape),
            "space" => (Key.Space, PhysicalKey.Space),
            "return" => (Key.Enter, PhysicalKey.Enter),
            _ => null,
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
        GeneratePaneInputParams input, (int Column, int Row)? cell, bool handled, Point? position, out GeneratePaneInputResult? result)
    {
        result = null;
        var buffer = registration.Buffer;
        if (!buffer.Lock.TryEnterReadLock(0)) return false;
        try
        {
            if (buffer.Rows != view.Rows || buffer.Cols != view.Cols || view.Metrics.CellWidth <= 0 || view.Metrics.CellHeight <= 0
                || cell?.Column > buffer.Cols || cell?.Row > buffer.Rows) return false;
            var modes = buffer.Modes;
            result = new GeneratePaneInputResult
            {
                EventDispatched = true,
                ObservationAvailable = true,
                Handled = handled,
                Rows = buffer.Rows,
                Cols = buffer.Cols,
                ViewRows = view.Rows,
                ViewCols = view.Cols,
                Column = cell?.Column,
                Row = cell?.Row,
                X = position?.X,
                Y = position?.Y,
                Modifiers = input.Modifiers,
                ApplicationKeypad = modes.IsApplicationKeypad,
                MouseSgr = modes.MouseModeSGR,
                MouseUtf8 = modes.MouseModeUtf8,
                MouseUrxvt = modes.MouseModeUrxvt,
                MousePixel = modes.MouseModePixel,
                MouseTracking = ObservedMouseTracking(modes),
                KittyKeyboardFlags = modes.KittyKeyboard.Flags,
                ModifyOtherKeysLevel = modes.ModifyOtherKeys.Level,
            };
            return true;
        }
        finally { buffer.Lock.ExitReadLock(); }
    }
}
