using Avalonia.Interactivity;

namespace Ntilde.Shell;

public partial class TerminalView
{
    internal bool MatchesGeneratedInputContext(Ntilde.VT.TerminalBuffer buffer, Ntilde.Pty.ITerminalSession? session)
        => ReferenceEquals(_buffer, buffer) && ReferenceEquals(_session, session);

    /// <summary>This API deliberately excludes deferred associated-text keyboard work and existing pending key correlation.</summary>
    internal bool CanGenerateInputSynchronously(bool keyboard)
        => !keyboard || !HasPendingInputCorrelation && (!_enableKittyKeyboardProtocol
            || !TerminalInputModeEncoder.ReportsKittyAssociatedText(_buffer?.Modes.KittyKeyboard.Flags ?? 0));

    private bool HasPendingInputCorrelation => _pendingAssociatedKey != null || _pendingKittyTextKey != null
        || _pendingKittyTextSuppression.HasValue || _pendingKittyTextFlags.HasValue
        || _pendingKittyHighSurrogate.HasValue || _reportedKittyKeys?.Count > 0;

    private bool _generatedInputInProgress;
    private bool _generatedInputSubmitted;

    private void NoteGeneratedInputSubmitted()
    {
        if (_generatedInputInProgress) _generatedInputSubmitted = true;
    }

    private bool _generatedInputRefused;
    private Func<bool>? _generatedInputLease;
    private bool CanApplyGeneratedInput => !_generatedInputInProgress || _generatedInputLease?.Invoke() == true;

    /// <summary>Keep routing and encoding intact while excluding synthetic input from user-command history observers.</summary>
    /// <param name="followUp">Optional second event raised right after <paramref name="args"/> inside the same dispatch, before the one-shot text
    /// correlation is cleared: the backend TextInput that can follow a handled key-down.</param>
    /// <param name="followUpIsCurrent">The lease for <paramref name="followUp"/>; it replaces <paramref name="isCurrent"/> once the first event returned,
    /// because that event's own pending text decision is the correlation the follow-up exists to exercise.</param>
    internal bool RaiseGeneratedInput(RoutedEventArgs args, Func<bool> isCurrent, out bool inputSubmitted, RoutedEventArgs? followUp = null, Func<bool>? followUpIsCurrent = null)
    {
        inputSubmitted = false;
        bool keyboard = args is Avalonia.Input.KeyEventArgs or Avalonia.Input.TextInputEventArgs;
        if (followUp != null && (args is not Avalonia.Input.KeyEventArgs || followUp is not Avalonia.Input.TextInputEventArgs)) throw new ArgumentException("A follow-up event is only a TextInput after a key event.", nameof(followUp));
        // These fields are UI-owned: a clean entry means every correlation created by
        // this synchronous event belongs to it, never to a preexisting human key.
        if (keyboard && !CanGenerateInputSynchronously(true)) return false;
        if (_generatedInputInProgress) throw new InvalidOperationException("A synthetic event is already being dispatched.");
        _generatedInputInProgress = true;
        _generatedInputSubmitted = false;
        _generatedInputRefused = false;
        _generatedInputLease = isCurrent;
        try
        {
            RaiseEvent(args);
            if (followUp != null && !_generatedInputRefused)
            {
                _generatedInputLease = followUpIsCurrent ?? isCurrent;
                RaiseEvent(followUp);
            }
            return !_generatedInputRefused;
        }
        finally
        {
            inputSubmitted = _generatedInputSubmitted;
            if (keyboard)
            {
                ClearPendingKittyTextInput();
                ClearKittyTextFragment();
                _reportedKittyKeys?.Clear();
            }
            _generatedInputInProgress = false;
            _generatedInputLease = null;
        }
    }
}
