using System.Globalization;

namespace Ntilde.VT
{
    public partial class AnsiParser
    {
        private void HandleDecrqss(string dcs, bool standardTerminator)
        {
            string reply = "\x1bP0$r\x1b\\";
            // xterm-compatible DECRQSS: success is 1, failure is 0. A retained
            // prefix or permissive BEL recovery cannot establish a valid request.
            if (standardTerminator && !_dcsTruncated && dcs == "$qr")
            {
                int top = _buffer.ScrollTop;
                int bottom = _buffer.ScrollBottom;
                if (top >= 0 && top <= bottom && bottom < _buffer.Rows)
                {
                    reply = string.Create(CultureInfo.InvariantCulture,
                        $"\x1bP1$r{top + 1};{bottom + 1}r\x1b\\");
                }
            }
            OnResponse?.Invoke(reply);
        }
    }
}
