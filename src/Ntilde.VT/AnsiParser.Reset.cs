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
        }

        private void RestoreCursor()
        {
            _buffer.RestoreCursor();
            RestoreSavedCharsets();
        }

        private void SwitchToMainScreen()
        {
            bool restore = _buffer.IsAltScreenActive && _buffer.RestoresMainCursorOnAltExit;
            _buffer.SwitchToMainScreen();
            if (restore) RestoreSavedCharsets();
        }

        private void RestoreSavedCharsets()
        {
            var saved = _buffer.ActiveSavedCursor;
            if (saved.Charsets is not { } charsets) return; // legacy snapshot lacks this context
            for (int i = 0; i < _charsets.Length; i++) _charsets[i] = (Charset)charsets[i];
            _gl = saved.Gl;
        }

        private void ResetCharsets()
        {
            Array.Fill(_charsets, Charset.Ascii);
            _gl = 0;
            _pendingCharsetSlot = -1;
        }

        // DECSTR is separate from RIS: it leaves both screens and reporting modes intact.
        private void SoftReset()
        {
            _buffer.SoftReset();
            ResetCharsets();
            _swallowNextNewline = false;
            _verticalOffset = 0;
            _lastGraphicChar = null;
        }
    }
}
