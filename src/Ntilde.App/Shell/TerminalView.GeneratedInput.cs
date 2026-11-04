using Avalonia.Interactivity;

namespace Ntilde.Shell;

public partial class TerminalView
{
    internal bool MatchesGeneratedInputContext(Ntilde.VT.TerminalBuffer buffer, Ntilde.Pty.ITerminalSession? session)
        => ReferenceEquals(_buffer, buffer) && ReferenceEquals(_session, session);

    /// <summary>This API deliberately excludes deferred associated-text keyboard work and existing pending key correlation.</summary>
    internal bool CanGenerateInputSynchronously(bool keyboard)
        => !keyboard || _pendingAssociatedKey == null && (!_enableKittyKeyboardProtocol
            || !TerminalInputModeEncoder.ReportsKittyAssociatedText(_buffer?.Modes.KittyKeyboard.Flags ?? 0));

    private bool _generatedInputInProgress;
    private bool _generatedInputRefused;
    private Func<bool>? _generatedInputLease;
    private bool CanApplyGeneratedInput => !_generatedInputInProgress || _generatedInputLease?.Invoke() == true;

    /// <summary>Keep routing and encoding intact while excluding synthetic input from user-command history observers.</summary>
    internal bool RaiseGeneratedInput(RoutedEventArgs args, Func<bool> isCurrent)
    {
        if (_generatedInputInProgress) throw new InvalidOperationException("A synthetic event is already being dispatched.");
        _generatedInputInProgress = true;
        _generatedInputRefused = false;
        _generatedInputLease = isCurrent;
        try { RaiseEvent(args); return !_generatedInputRefused; }
        finally
        {
            if (_generatedInputRefused) ClearPendingKittyTextInput();
            _generatedInputInProgress = false;
            _generatedInputLease = null;
        }
    }
}
