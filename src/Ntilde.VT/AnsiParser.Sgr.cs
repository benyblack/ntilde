namespace Ntilde.VT
{
    /// <summary>
    /// Select Graphic Rendition (CSI ... m): text attributes and colors, including the 256-color and truecolor forms.
    /// </summary>
    public partial class AnsiParser
    {
        private void HandleSgr(ReadOnlySpan<int> args, ReadOnlySpan<char> separators)
        {
            if (args.Length == 0)
            {
                ResetColors();
                _buffer.IsDefaultForeground = true;
                _buffer.IsDefaultBackground = true;
                _buffer.CurrentFgIndex = -1; // Default
                _buffer.CurrentBgIndex = -1; // Default
                _buffer.IsBold = false;
                _buffer.IsFaint = false;
                _buffer.IsItalic = false;
                _buffer.IsUnderline = false;
                _buffer.UnderlineStyle = UnderlineStyle.Single;
                _buffer.UnderlineColor = null;
                _buffer.UnderlineColorIndex = -1;
                _buffer.IsBlink = false;
                _buffer.IsStrikethrough = false;
                _buffer.IsInverse = false;
                return;
            }

            for (int i = 0; i < args.Length; i++)
            {
                int code = args[i];

                if (code == 0)
                {
                    ResetColors();
                    _buffer.IsDefaultForeground = true;
                    _buffer.IsDefaultBackground = true;
                    _buffer.CurrentFgIndex = -1;
                    _buffer.CurrentBgIndex = -1;
                    _buffer.IsBold = false;
                    _buffer.IsFaint = false;
                    _buffer.IsItalic = false;
                    _buffer.IsUnderline = false;
                    _buffer.UnderlineStyle = UnderlineStyle.Single;
                    _buffer.UnderlineColor = null;
                    _buffer.UnderlineColorIndex = -1;
                    _buffer.IsBlink = false;
                    _buffer.IsStrikethrough = false;
                    _buffer.IsInverse = false;
                }
                else if (code >= 30 && code <= 37)
                {
                    _buffer.CurrentForeground = GetBasicColor(code - 30);
                    _buffer.CurrentFgIndex = (short)(code - 30);
                    _buffer.IsDefaultForeground = false;
                }
                else if (code >= 90 && code <= 97)
                {
                    _buffer.CurrentForeground = GetBasicColor(code - 90, true);
                    _buffer.CurrentFgIndex = (short)((code - 90) + 8);
                    _buffer.IsDefaultForeground = false;
                }
                else if (code == 38)
                {
                    short idx = -1;
                    var color = ParseExtendedColor(args, separators, ref i, out idx);
                    if (color.HasValue)
                    {
                        // Snapping for Campbell Blue/Black
                        if (idx == -1 && color.Value.R == 0 && color.Value.G == 55 && color.Value.B == 218) idx = 4; // #0037DA Blue
                        if (idx == -1 && color.Value.R == 58 && color.Value.G == 150 && color.Value.B == 221) idx = 4; // #3A96DD Blue (PS)
                        if (idx == -1 && color.Value.R == 12 && color.Value.G == 12 && color.Value.B == 12) idx = 0; // #0C0C0C Black
                        if (idx == -1 && color.Value.R == 204 && color.Value.G == 204 && color.Value.B == 204) idx = 7; // #CCCCCC White
                        if (idx == -1 && color.Value.R == 242 && color.Value.G == 242 && color.Value.B == 242) idx = 15; // #F2F2F2 Bright White

                        // Use GetBasicColor ONLY for snapped or basic indices (0-15).
                        // For 256-color indices (16-255) or TrueColor (-1), use the raw color.Value.
                        _buffer.CurrentForeground = (idx >= 0 && idx <= 15) ? GetBasicColor(idx % 8, idx >= 8) : color.Value;
                        _buffer.CurrentFgIndex = idx;
                        _buffer.IsDefaultForeground = false;
                    }
                }
                else if (code == 39)
                {
                    _buffer.CurrentForeground = _buffer.Theme.Foreground;
                    _buffer.CurrentFgIndex = -1;
                    _buffer.IsDefaultForeground = true;
                }
                else if (code >= 40 && code <= 47)
                {
                    _buffer.CurrentBackground = GetBasicColor(code - 40);
                    _buffer.CurrentBgIndex = (short)(code - 40);
                    _buffer.IsDefaultBackground = false;
                }
                else if (code >= 100 && code <= 107)
                {
                    _buffer.CurrentBackground = GetBasicColor(code - 100, true);
                    _buffer.CurrentBgIndex = (short)((code - 100) + 8);
                    _buffer.IsDefaultBackground = false;
                }
                else if (code == 48)
                {
                    short idx = -1;
                    var color = ParseExtendedColor(args, separators, ref i, out idx);
                    if (color.HasValue)
                    {
                        // Snapping for Campbell Blue/Black
                        if (idx == -1 && color.Value.R == 0 && color.Value.G == 55 && color.Value.B == 218) idx = 4; // #0037DA Blue
                        if (idx == -1 && color.Value.R == 58 && color.Value.G == 150 && color.Value.B == 221) idx = 4; // #3A96DD Blue (PS)
                        if (idx == -1 && color.Value.R == 12 && color.Value.G == 12 && color.Value.B == 12) idx = 0; // #0C0C0C Black
                        if (idx == -1 && color.Value.R == 204 && color.Value.G == 204 && color.Value.B == 204) idx = 7; // #CCCCCC White
                        if (idx == -1 && color.Value.R == 242 && color.Value.G == 242 && color.Value.B == 242) idx = 15; // #F2F2F2 Bright White

                        _buffer.CurrentBackground = (idx >= 0 && idx <= 15) ? GetBasicColor(idx % 8, idx >= 8) : color.Value;
                        _buffer.CurrentBgIndex = idx;
                        _buffer.IsDefaultBackground = false;
                    }
                }
                else if (code == 58) // Underline color (#270): 58;5;n, 58;2;r;g;b and their colon forms
                {
                    var color = ParseExtendedColor(args, separators, ref i, out short idx);
                    if (color.HasValue)
                    {
                        _buffer.UnderlineColorIndex = idx;
                        _buffer.UnderlineColor = idx >= 0 ? null : color;
                    }
                }
                else if (code == 59) // Default underline color: the text color again
                {
                    _buffer.UnderlineColor = null;
                    _buffer.UnderlineColorIndex = -1;
                }
                else if (code == 49)
                {
                    _buffer.CurrentBackground = _buffer.Theme.Background;
                    _buffer.CurrentBgIndex = -1;
                    _buffer.IsDefaultBackground = true;
                }
                else if (code == 1) // Bold
                {
                    _buffer.IsBold = true;
                }
                else if (code == 22) // Normal Intensity (Not Bold, Not Faint)
                {
                    _buffer.IsBold = false;
                    _buffer.IsFaint = false;
                }
                else if (code == 7) // Inverse
                {
                    _buffer.IsInverse = true;
                }
                else if (code == 27) // No Inverse
                {
                    _buffer.IsInverse = false;
                }
                else if (code == 8) // Hidden
                {
                    _buffer.IsHidden = true;
                }
                else if (code == 28) // Visible (No Hidden)
                {
                    _buffer.IsHidden = false;
                }
                else if (code == 2) // Faint
                {
                    _buffer.IsFaint = true;
                }
                else if (code == 3) // Italic
                {
                    _buffer.IsItalic = true;
                }
                else if (code == 4) // Underline / Underline Style (4:0-5)
                {
                    // Support colon-form subparameters (e.g. 4:2) without misinterpreting
                    // the style selector as a standalone SGR code (notably 2=faint).
                    if (i < args.Length - 1 && i < separators.Length && separators[i] == ':')
                    {
                        int underlineStyle = args[i + 1];
                        _buffer.IsUnderline = underlineStyle != 0;
                        // 1 straight, 2 double, 3 curly, 4 dotted, 5 dashed; anything else is still an underline
                        _buffer.UnderlineStyle = underlineStyle is >= 1 and <= 5 ? (UnderlineStyle)underlineStyle : UnderlineStyle.Single;
                        i++; // consume subparameter
                    }
                    else
                    {
                        _buffer.IsUnderline = true;
                        _buffer.UnderlineStyle = UnderlineStyle.Single;
                    }
                }
                else if (code == 21) // Doubly underlined (ECMA-48), as xterm, kitty, ghostty and wezterm read it
                {
                    _buffer.IsUnderline = true;
                    _buffer.UnderlineStyle = UnderlineStyle.Double;
                }
                else if (code == 5) // Blink
                {
                    _buffer.IsBlink = true;
                }
                else if (code == 9) // Strikethrough
                {
                    _buffer.IsStrikethrough = true;
                }
                else if (code == 23) // No Italic
                {
                    _buffer.IsItalic = false;
                }
                else if (code == 24) // No Underline
                {
                    _buffer.IsUnderline = false;
                }
                else if (code == 25) // No Blink
                {
                    _buffer.IsBlink = false;
                }
                else if (code == 29) // No Strikethrough
                {
                    _buffer.IsStrikethrough = false;
                }
            }
        }

        private void ResetColors()
        {
            _buffer.CurrentForeground = _buffer.Theme.Foreground;
            _buffer.CurrentBackground = _buffer.Theme.Background;
            _buffer.CurrentFgIndex = -1;
            _buffer.CurrentBgIndex = -1;
            _buffer.IsInverse = false;
            _buffer.IsBold = false;
            _buffer.IsFaint = false;
            _buffer.IsItalic = false;
            _buffer.IsUnderline = false;
            _buffer.UnderlineStyle = UnderlineStyle.Single;
            _buffer.UnderlineColor = null;
            _buffer.UnderlineColorIndex = -1;
            _buffer.IsBlink = false;
            _buffer.IsStrikethrough = false;
            _buffer.IsHidden = false;
        }

        private TermColor? ParseExtendedColor(ReadOnlySpan<int> args, ReadOnlySpan<char> separators, ref int i, out short index)
        {
            index = -1;
            if (i + 1 >= args.Length) return null;

            int mode = args[++i];
            if (mode == 5) // 256 colors
            {
                if (i + 1 >= args.Length) return null;
                int idx = args[++i];
                // Past 255 names no color: ignored, as xterm and kitty do. Kept, it was white under an index no
                // palette has, wrapped negative past 32767, and refused by state transfer as an underline's.
                if (idx > 255) return null;
                index = (short)idx; // Use the palette index!
                return GetXtermColor(idx);
            }
            else if (mode == 2) // TrueColor (Next 3 args are R, G, B)
            {
                // Colon form may carry an optional colorspace-id parameter:
                // 38:2:<cs>:R:G:B and common omitted form 38:2::R:G:B.
                // If present, consume it so RGB doesn't shift/leak.
                bool modeWasColonDelimited = i < separators.Length && separators[i] == ':';
                if (modeWasColonDelimited && i + 4 < args.Length)
                {
                    i++; // skip colorspace-id (or omitted 0 placeholder from "::")
                }

                if (i + 3 >= args.Length) return null;
                byte r = (byte)args[++i];
                byte g = (byte)args[++i];
                byte b = (byte)args[++i];
                return TermColor.FromRgb(r, g, b);
            }
            return null;
        }

        private TermColor GetBasicColor(int index, bool bright = false)
        {
            return _buffer.Theme.GetAnsiColor(index, bright);
        }

        private TermColor GetXtermColor(int index)
        {
            // 0-15: Standard colors from theme
            if (index < 16)
            {
                return GetBasicColor(index % 8, index >= 8);
            }

            // 16-231: 6x6x6 Cube
            if (index < 232)
            {
                index -= 16;
                int r = (index / 36);
                int g = (index / 6) % 6;
                int b = index % 6;

                // Mapping 0-5 to 0-255: 0->0, 1->95, 2->135, 3->175, 4->215, 5->255
                byte ToByte(int v) => (byte)(v == 0 ? 0 : (v * 40 + 55));
                return TermColor.FromRgb(ToByte(r), ToByte(g), ToByte(b));
            }

            // 232-255: Grayscale
            if (index < 256)
            {
                index -= 232;
                byte v = (byte)(index * 10 + 8);
                return TermColor.FromRgb(v, v, v);
            }

            return TermColor.White;
        }
    }
}
