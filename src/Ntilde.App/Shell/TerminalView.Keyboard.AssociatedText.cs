using Avalonia.Input;
using Avalonia.Threading;
using Ntilde.Pty;
using Ntilde.VT;

namespace Ntilde.Shell;

public partial class TerminalView
{
    private readonly record struct AssociatedInputContext(ITerminalSession Session, TerminalBuffer Buffer,
        KittyKeyboardState KeyboardState, int KeyboardGeneration, int Epoch);
    private sealed record PendingAssociatedKey(Key Key, KeyModifiers Modifiers, PhysicalKey Physical,
        KittyKeyIdentity Identity, string Frame, AssociatedInputContext Context);

    private PendingAssociatedKey? _pendingAssociatedKey;
    private int? _pendingKittyTextFlags;
    private int _kittyTextContextEpoch;
    private bool _associatedFlushScheduled;
    private char? _pendingKittyHighSurrogate;
    private int _pendingKittyHighFlags;

    /// <summary>
    /// Leaves a text-producing keydown unhandled so all Avalonia backends can produce text.
    /// At most one input is pending and one dispatcher fallback is queued per view.
    /// </summary>
    private bool TryQueueKittyAssociatedKey(Key key, KeyModifiers modifiers, PhysicalKey physical,
        KittyKeyIdentity identity, TerminalKeyEventKind kind, int flags, string? symbol)
    {
        if (!TerminalInputModeEncoder.ReportsKittyAssociatedText(flags)
            || !TerminalInputModeEncoder.CanProduceKittyAssociatedText(key, modifiers, symbol)
            || _session == null || _buffer == null) return false;
        string? frame = TerminalInputModeEncoder.EncodeKittyKey(key, modifiers, flags, kind, identity);
        if (frame == null || !frame.EndsWith('u')) return false;
        var state = _buffer.Modes.KittyKeyboard;
        var context = new AssociatedInputContext(_session, _buffer, state, state.Generation, _kittyTextContextEpoch);
        _pendingAssociatedKey = new(key, modifiers, physical, identity, frame, context);
        if (!_associatedFlushScheduled)
        {
            _associatedFlushScheduled = true;
            Dispatcher.UIThread.Post(() =>
            {
                _associatedFlushScheduled = false;
                FlushPendingKittyAssociatedKey();
            }, DispatcherPriority.Input);
        }
        return true;
    }

    /// <summary>Expires stale fragments and drains fallback sends before reserving another text slot.</summary>
    private void PrepareKittyTextForKeyDown(Key key, string? keySymbol)
    {
        // Unicode packet keys have no logical identity; their two text callbacks
        // may be separated by packet key events. A real new key ends that fragment.
        if (key != Key.None || (!string.IsNullOrEmpty(keySymbol)
            && !(keySymbol.Length == 1 && char.IsLowSurrogate(keySymbol[0])))) ClearKittyTextFragment();
        // Sending a fallback can reenter keydown and queue another frame. Drain
        // those frames before replacing their correlation with this key's state.
        while (_pendingAssociatedKey != null) FlushPendingKittyAssociatedKey();
    }

    /// <summary>One actual text callback owns the pending key or becomes a pure text event with code zero.</summary>
    private bool TrySendKittyAssociatedText(string text)
    {
        int flags = _pendingKittyTextFlags ?? (_enableKittyKeyboardProtocol ? _buffer?.Modes.KittyKeyboard.Flags ?? 0 : 0);
        // Win32 WM_CHAR delivers one UTF-16 unit per callback. Keep at most one high
        // surrogate across adjacent callbacks, never substitute a replacement character.
        if (_pendingKittyHighSurrogate is { } high)
        {
            int firstUnitFlags = _pendingKittyHighFlags;
            ClearKittyTextFragment();
            if (char.IsLowSurrogate(text[0]))
            {
                text = string.Concat(high.ToString(), text);
                flags = firstUnitFlags;
            }
        }
        if (!TerminalInputModeEncoder.ReportsKittyAssociatedText(flags)) return false;
        if (text.Length == 1 && char.IsHighSurrogate(text[0]))
        {
            _pendingKittyHighSurrogate = text[0];
            _pendingKittyHighFlags = flags;
            _kittyTextInputGeneration++;
            return true;
        }
        PendingAssociatedKey? pending = _pendingAssociatedKey;
        ClearPendingKittyTextInput();
        ResetCursorBlink();
        if (ForwardKittyTextPayload(pending, text)) TextInputObserved?.Invoke(text);
        return true;
    }

    /// <summary>Returns true only when validated generated text was forwarded, not a key-only fallback.</summary>
    private bool ForwardKittyTextPayload(PendingAssociatedKey? pending, string text)
    {
        PendingAssociatedKey? key = pending != null && IsAssociatedInputContextCurrent(pending) ? pending : null;
        string? frame = TerminalInputModeEncoder.AppendKittyAssociatedText(key?.Frame ?? "\x1b[0u", text);
        if (key != null) SendAssociatedKeyFrame(key, frame ?? key.Frame);
        else if (frame != null) SendUserInput(frame);
        return frame != null;
    }

    /// <summary>Context changes and a new known key cannot inherit an incomplete scalar.</summary>
    private void ClearKittyTextFragment()
    {
        _pendingKittyHighSurrogate = null;
        _pendingKittyHighFlags = 0;
    }

    /// <summary>
    /// Sends a key without guessed text when no callback arrived, before the next key or release.
    /// A later independent text callback is represented as a zero-key event, not a repeated key.
    /// </summary>
    private void FlushPendingKittyAssociatedKey()
    {
        PendingAssociatedKey? pending = _pendingAssociatedKey;
        _pendingAssociatedKey = null;
        if (pending != null && IsAssociatedInputContextCurrent(pending)) SendAssociatedKeyFrame(pending, pending.Frame);
    }

    private bool IsAssociatedInputContextCurrent(PendingAssociatedKey pending)
        => ReferenceEquals(pending.Context.Session, _session) && ReferenceEquals(pending.Context.Buffer, _buffer)
            && pending.Context.Epoch == _kittyTextContextEpoch;

    /// <summary>A mode change retains the captured input but cannot acquire a release in a newer stack.</summary>
    private void SendAssociatedKeyFrame(PendingAssociatedKey pending, string frame)
        => SendKeyInput(pending.Key, pending.Modifiers, pending.Physical, frame, pending.Identity,
            trackKittyEvents: ReferenceEquals(pending.Context.KeyboardState, _buffer?.Modes.KittyKeyboard)
                && pending.Context.KeyboardGeneration == pending.Context.KeyboardState.Generation);
}
