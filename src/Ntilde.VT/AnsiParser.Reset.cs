using System;

namespace Ntilde.VT
{
    public partial class AnsiParser
    {
        private void SaveCursor()
        {
            _buffer.SaveCursor();
            var saved = _buffer.ActiveSavedCursor;
            saved.Charsets = Array.ConvertAll(_charsets, charset => (int)charset);
            saved.Gl = _gl;
            saved.Gr = _gr;
            saved.SingleShift = _singleShift;
        }

        private void RestoreCursor()
        {
            _buffer.RestoreCursor();
            RestoreSavedCharsets();
        }

        private void SwitchToMainScreen(bool clearAltBeforeExit = false)
        {
            bool restore = _buffer.IsAltScreenActive && _buffer.RestoresMainCursorOnAltExit;
            _buffer.SwitchToMainScreen(restoreSavedCursorIfArmed: true, clearAltBeforeExit: clearAltBeforeExit);
            if (restore) RestoreSavedCharsets();
        }

        private void RestoreSavedCharsets()
        {
            var saved = _buffer.ActiveSavedCursor;
            if (saved.Charsets is not { } charsets) return; // legacy snapshot lacks this context
            for (int i = 0; i < _charsets.Length; i++) _charsets[i] = (Charset)charsets[i];
            _gl = saved.Gl;
            _gr = saved.Gr;
            _singleShift = saved.SingleShift;
        }

        private void ResetCharsets()
        {
            Array.Fill(_charsets, Charset.Ascii);
            _gl = 0;
            _gr = 0;
            _singleShift = 0;
            _pendingCharsetSlot = -1;
            _pendingCharset96 = false;
        }

        private void HardReset()
        {
            _buffer.Reset();
            ClearVsCodeCommandLine();
            OnKittyClipboardReset?.Invoke();
            OnPointerShapeReset?.Invoke();
            ClearITerm2Multipart();
            ClearPendingOsc99();
            ResetCharsets();
            _verticalOffset = 0;
            _vt52Graphics = false;
            _vt52AddressRow = 0;
            _vt100ControlMode = false;
            _nationalCharset = false;
            _outputCoding = OutputCoding.Automatic;
            // RIS also disables parser-owned reporting modes.
            _send8BitControls = false;
            _inBandResizeReportsEnabled = false;
            _colorSchemeReportsEnabled = false;
            ResetTitleStack();
            RefreshColorScheme(notify: false);
            _state = State.Normal;
        }

        // DECSTR is separate from RIS: it leaves both screens and reporting modes intact.
        private void SoftReset()
        {
            _buffer.SoftReset();
            ClearVsCodeCommandLine();
            OnPointerShapeReset?.Invoke();
            ResetCharsets();
            _swallowNextNewline = false;
            _verticalOffset = 0;
            _lastGraphicChar = null;
        }
    }
}
