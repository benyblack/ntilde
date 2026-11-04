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
    internal bool RaiseGeneratedInput(RoutedEventArgs args, Func<bool> isCurrent, out bool inputSubmitted)
    {
        inputSubmitted = false;
        bool keyboard = args is Avalonia.Input.KeyEventArgs or Avalonia.Input.TextInputEventArgs;
        // These fields are UI-owned: a clean entry means every correlation created by
        // this synchronous event belongs to it, never to a preexisting human key.
        if (keyboard && !CanGenerateInputSynchronously(true)) return false;
        if (_generatedInputInProgress) throw new InvalidOperationException("A synthetic event is already being dispatched.");
        _generatedInputInProgress = true;
        _generatedInputSubmitted = false;
        _generatedInputRefused = false;
        _generatedInputLease = isCurrent;
        try { RaiseEvent(args); return !_generatedInputRefused; }
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
