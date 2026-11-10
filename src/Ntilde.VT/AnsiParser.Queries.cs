using System.Globalization;
using System.Text;

namespace Ntilde.VT
{
    public partial class AnsiParser
    {
        private const string DecrqssFailure = "\x1bP0$r\x1b\\";

        private void HandleDecrqss(string dcs, bool standardTerminator)
        {
            // xterm-compatible DECRQSS: success is 1, failure is 0. A retained
            // prefix or permissive BEL recovery cannot establish a valid request.
            string? setting = standardTerminator && !_dcsTruncated
                ? dcs switch
                {
                    "$qr" => ReportTopBottomMargins(),
                    "$qs" => ReportLeftRightMargins(),
                    "$qm" => ReportGraphicRendition(),
                    "$q q" => ReportCursorStyle(),
                    _ => null,
                }
                : null;
            OnResponse?.Invoke(setting is null ? DecrqssFailure : "\x1bP1$r" + setting + "\x1b\\");
        }

        private string? ReportTopBottomMargins()
        {
            int top = _buffer.ScrollTop;
            int bottom = _buffer.ScrollBottom;
            if (top < 0 || top > bottom || bottom >= _buffer.Rows) return null;
            return string.Create(CultureInfo.InvariantCulture, $"{top + 1};{bottom + 1}r");
        }

        /// <summary>
        /// Absolute one-based left/right margins, as xterm answers on a VT420-level terminal: whether or not
        /// DECLRMM is set (full width then), and never origin-relative. Replaying it after CSI ? 69 h restores them.
        /// </summary>
        private string? ReportLeftRightMargins()
        {
            int left = _buffer.LeftMargin;
            int right = _buffer.RightMargin;
            if (left < 0 || left > right || right >= _buffer.Cols) return null;
            return string.Create(CultureInfo.InvariantCulture, $"{left + 1};{right + 1}s");
        }

        /// <summary>
        /// The current pen as an SGR a client can send back to restore it. Like xterm, the
        /// report starts with 0 so replaying it never inherits attributes from a later pen.
        /// Only the underline style uses a colon subparameter (it has no semicolon form);
        /// extended colors use the semicolon forms every SGR parser accepts.
        /// </summary>
        private string ReportGraphicRendition()
        {
            var sgr = new StringBuilder("0", 64);
            if (_buffer.IsBold) sgr.Append(";1");
            if (_buffer.IsFaint) sgr.Append(";2");
            if (_buffer.IsItalic) sgr.Append(";3");
            if (_buffer.IsUnderline)
            {
                UnderlineStyle style = _buffer.UnderlineStyle;
                sgr.Append(style == UnderlineStyle.Single
                    ? ";4"
                    : string.Create(CultureInfo.InvariantCulture, $";4:{(int)style}"));
            }
            if (_buffer.IsBlink) sgr.Append(";5");
            if (_buffer.IsInverse) sgr.Append(";7");
            if (_buffer.IsHidden) sgr.Append(";8");
            if (_buffer.IsStrikethrough) sgr.Append(";9");
            if (_buffer.IsOverline) sgr.Append(";53");
            if (!_buffer.IsDefaultForeground)
                AppendColor(sgr, 30, 90, 38, _buffer.CurrentFgIndex, _buffer.CurrentForeground);
            if (!_buffer.IsDefaultBackground)
                AppendColor(sgr, 40, 100, 48, _buffer.CurrentBgIndex, _buffer.CurrentBackground);
            if (_buffer.UnderlineColorIndex >= 0)
                AppendColor(sgr, -1, -1, 58, _buffer.UnderlineColorIndex, default);
            else if (_buffer.UnderlineColor is TermColor underline)
                AppendColor(sgr, -1, -1, 58, -1, underline);
            return sgr.Append('m').ToString();
        }

        /// <summary>
        /// Palette indexes 0-15 use the short ANSI codes where the attribute has them; other
        /// indexes use 256-color form; index -1 is direct RGB. Out-of-range indexes cannot be
        /// stored (the SGR parser ignores them), but a defensive bound keeps a bad import from
        /// reporting a number no client could send.
        /// </summary>
        private static void AppendColor(StringBuilder sgr, int basic, int bright, int extended, short index, TermColor rgb)
        {
            if (index is >= 0 and <= 7 && basic >= 0)
                sgr.Append(';').Append((basic + index).ToString(CultureInfo.InvariantCulture));
            else if (index is >= 8 and <= 15 && bright >= 0)
                sgr.Append(';').Append((bright + index - 8).ToString(CultureInfo.InvariantCulture));
            else if (index is >= 0 and <= 255)
                sgr.Append(string.Create(CultureInfo.InvariantCulture, $";{extended};5;{index}"));
            else if (index == -1)
                sgr.Append(string.Create(CultureInfo.InvariantCulture, $";{extended};2;{rgb.R};{rgb.G};{rgb.B}"));
        }

        /// <summary>
        /// DECSCUSR's current value. The host's configured cursor reports 0, the value that
        /// restores it; an application override reports xterm's 1-6 shape-and-blink code.
        /// </summary>
        private string ReportCursorStyle()
        {
            ModeState modes = _buffer.Modes;
            int code = 0;
            if (!modes.CursorStyleIsDefault)
            {
                code = modes.CursorStyle switch
                {
                    CursorStyle.Underline => 4,
                    CursorStyle.Beam => 6,
                    _ => 2,
                };
                if (modes.IsCursorBlinkEnabled) code--;
            }
            return string.Create(CultureInfo.InvariantCulture, $"{code} q");
        }
    }
}
