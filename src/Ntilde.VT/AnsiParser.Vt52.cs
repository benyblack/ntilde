namespace Ntilde.VT;

public partial class AnsiParser
{
    private bool _vt52Graphics;
    private int _vt52AddressRow;
    private bool _vt100ControlMode;

    private void EnterVt52()
    {
        _buffer.Modes.IsVt52 = true;
        _vt52Graphics = false;
        _send8BitControls = false;
        _lastGraphicChar = null;
    }

    private void HandleVt52Escape(char c)
    {
        switch (c)
        {
            case '\x1b' or '\x7f':
                return;
            case '\x18' or '\x1a':
                _state = State.Normal;
                return;
            case < '\x20':
                ExecuteC0Control(c);
                return;
            case <= '\x2f':
                // Consume unsupported intermediate sequences through their final byte.
                _escapeSpacePending = true;
                return;
        }

        _state = State.Normal;
        _lastGraphicChar = null;
        switch (c)
        {
            case 'A': MoveCursorUp(1, toFirstColumn: false); break;
            case 'B': MoveCursorDown(1, toFirstColumn: false); break;
            case 'C': _buffer.CursorForward(1); break;
            case 'D':
                int left = _buffer.CursorCol >= _buffer.LeftMargin ? _buffer.LeftMargin : 0;
                _buffer.SetCursorPosition(Math.Max(left, _buffer.CursorCol - 1), _buffer.CursorRow);
                break;
            case 'F': _vt52Graphics = true; break;
            case 'G': _vt52Graphics = false; break;
            case 'H': _buffer.SetCursorPosition(0, 0); break;
            case 'I': _buffer.ReverseIndex(); break;
            case 'J': EraseInDisplay(0, allowHistoryErase: true); break;
            case 'K': _buffer.EraseLineToEnd(); break;
            case 'Y': _state = State.Vt52AddressRow; break;
            case 'Z': OnResponse?.Invoke("\x1b/Z"); break;
            case '=' or '>': _buffer.Modes.IsApplicationKeypad = c == '='; break;
            case '<':
                _buffer.Modes.IsVt52 = false;
                _vt52Graphics = false;
                _vt100ControlMode = true;
                break;
            case 'c': HardReset(); break;
        }
    }

    private void ConsumeVt52Address(char c)
    {
        switch (c)
        {
            case '\x1b' or '\x18' or '\x1a':
                _vt52AddressRow = 0;
                _state = c == '\x1b' ? State.Esc : State.Normal;
                return;
            case < '\x20':
                ExecuteC0Control(c);
                return;
        }

        int address = c <= '\x7f' ? c : 0;
        if (_state == State.Vt52AddressRow)
        {
            _vt52AddressRow = address;
            _state = State.Vt52AddressColumn;
            return;
        }

        // VT100 emulating VT52 ignores an out-of-range coordinate on that axis.
        int row = _vt52AddressRow - 0x20;
        int col = address - 0x20;
        _buffer.SetCursorPosition(
            col >= 0 && col < _buffer.Cols ? col : _buffer.CursorCol,
            row >= 0 && row < _buffer.Rows ? row : _buffer.CursorRow);
        _vt52AddressRow = 0;
        _state = State.Normal;
    }

    /// <summary>DECSCL's VT100/VT220 control-width selection, used when leaving VT52.</summary>
    private void SelectAnsiControlMode(ReadOnlySpan<int> parameters)
    {
        if (parameters.Length is < 1 or > 2 || parameters[0] is not (61 or 62)) return;
        int width = parameters.Length == 2 ? parameters[1] : 0;
        if (width is < 0 or > 2) return;

        _vt100ControlMode = parameters[0] == 61;
        _send8BitControls = !_vt100ControlMode && width != 1;
    }
}
