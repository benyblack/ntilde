namespace Ntilde.VT
{
    /// <summary>
    /// Inline images from DCS (Sixel, and the DECRQSS / XTGETTCAP replies) and from the iTerm2 protocol (OSC 1337).
    /// </summary>
    public partial class AnsiParser
    {
        private void HandleDcs(string dcs)
        {
            // DCS routing (#169). Previously ANY DCS containing 'q' was fed to the Sixel
            // decoder, which swallowed DECRQSS (DCS $ q …, vim emits it to query SGR /
            // DECSCUSR) and XTGETTCAP (DCS + q …) without a response — clients blocked
            // on their reply timeout.
            if (dcs.StartsWith("$q", StringComparison.Ordinal))
            {
                // DECRQSS: setting reports aren't implemented; answer "invalid request"
                // (DCS 0 $ r ST) so the client gets an immediate, well-formed reply
                // instead of a timeout.
                OnResponse?.Invoke("\x1bP0$r\x1b\\");
            }
            else if (dcs.StartsWith("+q", StringComparison.Ordinal))
            {
                // XTGETTCAP: report failure for the requested capabilities (DCS 0 + r ST).
                OnResponse?.Invoke("\x1bP0+r\x1b\\");
            }
            else if (IsSixel(dcs))
            {
                HandleSixel(dcs);
            }
            _dcsStringBuffer.Clear();
        }

        // DECSIXEL is "DCS Ps ; Ps ; Ps q <data> ST": only digits and ';' may precede
        // the 'q' final byte. Anything else is a different DCS function.
        private static bool IsSixel(string dcs)
        {
            for (int i = 0; i < dcs.Length; i++)
            {
                char ch = dcs[i];
                if (ch == 'q') return true;
                if (!char.IsAsciiDigit(ch) && ch != ';') return false;
            }
            return false;
        }

        /// <summary>
        /// Ends an inline image placement with the cursor at column 0 of the row below the picture.
        /// </summary>
        /// <remarks>
        /// Every image protocol reserves its cells the same way - write <c>width</c> spaces, newline,
        /// repeat - and each one used to stop after the last row's spaces, leaving the cursor at
        /// <c>CellX + width</c> on the image's own final row. Whatever the program wrote next started
        /// from there: an indent beside a narrow image, and beside a wide one an overrun past the last
        /// column that wrapped the text mid-word (#405).
        ///
        /// The newline is what puts the cursor on a fresh line. <see cref="_swallowNextNewline"/> then
        /// absorbs the newline the emitting program almost always sends after its image, so the picture
        /// is not followed by a blank line - wanting that is why the last row's newline was skipped in
        /// the first place, but skipping it moved the text sideways instead of keeping it close.
        /// Emitting our own and swallowing theirs gives both: no blank line, and text that resumes at
        /// the left margin.
        /// </remarks>
        private void FinishImagePlacement()
        {
            // CR then LF, not LF alone: line feed only returns to column 0 when LNM is set, and it
            // is off by default, so a bare '\n' would drop a row and keep the column - the very
            // thing this exists to prevent.
            _buffer.WriteChar('\r');
            _buffer.WriteChar('\n');
            _swallowNextNewline = true;
        }

        private void HandleSixel(string dcs)
        {
            try
            {
                if (ImageDecoder == null) return;

                object? imageHandle = ImageDecoder.DecodeSixel(dcs, out int pixelWidth, out int pixelHeight);
                if (imageHandle != null)
                {
                    // Calculate cell dimensions (rough estimate based on current font metrics)
                    int widthCells = (int)Math.Max(1, Math.Ceiling(pixelWidth / (CellWidth > 0 ? CellWidth : 10f)));
                    int heightCells = (int)Math.Max(1, Math.Ceiling(pixelHeight / (CellHeight > 0 ? CellHeight : 20f)));

                    int absRow = _buffer.CursorRow + (_buffer.TotalLines - _buffer.Rows);
                    if (_buffer.IsAltScreenActive) absRow = _buffer.CursorRow;

                    var img = new TerminalImage(imageHandle, _buffer.CursorCol, absRow, widthCells, heightCells);
                    _buffer.AddImage(img);

                    // Position cursor past the image
                    // We move the cursor relative to current position, but try to avoid redundant newlines
                    // Tools like 'viu' usually handle their own layout. 
                    // We just need to ensure the buffer knows we've "used" these cells.
                    bool oldHidden = _buffer.IsHidden;
                    _buffer.IsHidden = true;
                    try
                    {
                        for (int y = 0; y < heightCells; y++)
                        {
                            // 1. Advance horizontally on current row
                            for (int x = 0; x < widthCells; x++) _buffer.WriteContent(" ", false);

                            // 2. If more rows exist, move to next row and align to image start column
                            if (y < heightCells - 1)
                            {
                                _buffer.WriteChar('\n');
                                for (int x = 0; x < img.CellX; x++) _buffer.WriteContent(" ", false);
                            }
                        }

                        FinishImagePlacement();
                    }
                    finally
                    {
                        _buffer.IsHidden = oldHidden;
                    }
                }
            }
            catch (Exception ex)
            {
                TerminalLogger.Debug($"[ANSI_PARSER] Sixel decode failed: {ex.Message}");
            }
        }

        private void HandleITerm2Image(string osc)
        {
            // The payload is the bulk of an image sequence (megabytes), so it is read in place as a
            // span: Split(':', 2) used to copy all of it once more just to separate the header.
            int colon = osc.IndexOf(':');
            if (colon < 0)
            {
                return;
            }

            string header = osc.Substring(0, colon);
            if (!header.StartsWith("1337;File="))
            {
                return;
            }

            var argsPart = header.Substring("1337;File=".Length);
            ReadOnlySpan<char> base64Data = osc.AsSpan(colon + 1);

            var args = argsPart.Split(';');
            int width = 0;
            int height = 0;
            bool inline = false;

            foreach (var arg in args)
            {
                var kv = arg.Split('=');
                if (kv.Length != 2) continue;
                string key = kv[0].ToLower();
                string val = kv[1];

                if (key == "width") width = ParseDimension(val, isHeight: false);
                else if (key == "height") height = ParseDimension(val, isHeight: true);
                else if (key == "inline") inline = (val == "1");
            }

            if (!inline) return;

            try
            {
                // Guardrail: Limit base64 length to avoid excessive memory usage before decoding
                if (base64Data.Length > 10 * 1024 * 1024) // 10MB limit
                {
                    return;
                }

                byte[]? data = DecodeBase64(base64Data);
                if (data == null) return;

                if (ImageDecoder == null) return;

                object? imageHandle = ImageDecoder.DecodeImageBytes(data, out int pixelWidth, out int pixelHeight);
                if (imageHandle == null) return;

                // Guardrail: Limit pixel dimensions
                if (pixelWidth > 2000 || pixelHeight > 2000)
                {
                    return;
                }

                if (width == 0 && height == 0)
                {
                    // Default: Scale to fit 100% of image pixel width mapped to cells
                    float divisor = CellWidth > 0 ? CellWidth : 10.0f;
                    width = Math.Max(10, (int)Math.Ceiling(pixelWidth / divisor));

                    // Sanity check: Clamp width to terminal width to prevent massive wrapping
                    width = Math.Min(width, _buffer.Cols);

                    double cellRatio = (CellHeight > 0 && CellWidth > 0) ? (CellWidth / (double)CellHeight) : 0.5;
                    double imageRatio = (double)pixelHeight / pixelWidth;
                    height = Math.Max(1, (int)Math.Round(width * cellRatio * imageRatio));
                }
                else if (width == 0)
                {
                    double cellRatio = (CellHeight > 0 && CellWidth > 0) ? (CellWidth / (double)CellHeight) : 0.5;
                    double imageRatio = (double)pixelHeight / pixelWidth;
                    width = Math.Max(1, (int)Math.Round(height / (cellRatio * imageRatio)));
                }
                else if (height == 0)
                {
                    double cellRatio = (CellHeight > 0 && CellWidth > 0) ? (CellWidth / (double)CellHeight) : 0.5;
                    double imageRatio = (double)pixelHeight / pixelWidth;
                    height = Math.Max(1, (int)Math.Round(width * cellRatio * imageRatio));
                }

                // Guardrail: Limit cell dimensions
                width = Math.Clamp(width, 1, Math.Min(200, _buffer.Cols));
                height = Math.Clamp(height, 1, 200);

                // Calculate absolute row
                int absRow = _buffer.CursorRow + (_buffer.TotalLines - _buffer.Rows);
                if (_buffer.IsAltScreenActive) absRow = _buffer.CursorRow;

                var img = new TerminalImage(imageHandle, _buffer.CursorCol, absRow, width, height);
                _buffer.AddImage(img);

                // Finalize placement
                bool oldHidden = _buffer.IsHidden;
                int startRow = _buffer.CursorRow;
                try
                {
                    // Calculate visual lines required
                    // Note: height is in cells.
                    for (int y = 0; y < height; y++)
                    {
                        // 1. Remember row before writing
                        int rowBefore = _buffer.CursorRow;

                        // 2. Advance horizontally on current row (width spaces)
                        // If width > Cols, WriteContent handles wrapping automatically
                        for (int i = 0; i < width; i++)
                        {
                            _buffer.WriteContent(" ", false);
                        }

                        // 3. If valid row, force newline ONLY if we didn't already wrap
                        if (y < height - 1)
                        {
                            // If cursor row is same as before, we fit on the line. Force newline.
                            // If cursor row changed, we (auto) wrapped. Don't double-newline unless we are mid-line?
                            // If we wrapped exactly to start of next line, we are good.
                            // If width == Cols, we wrap to col 0 of next line.

                            if (_buffer.CursorRow == rowBefore)
                            {
                                _buffer.WriteChar('\n');
                            }

                            // Move to image start col
                            for (int i = 0; i < img.CellX; i++) _buffer.WriteContent(" ", false);
                        }
                    }


                    // Before the delta below is measured, so the row this adds is counted in it.
                    FinishImagePlacement();

                    // Accumulate vertical offset for ConPTY sync
                    // Use actual visual cursor delta instead of image height to account for scrolling/clamping
                    int endRow = _buffer.CursorRow;
                    int delta = endRow - startRow;
                    // If we wrapped/scrolled, delta is how much further down the cursor is visually relative to start.
                    // This maps PTY (Start) -> Visual (End).
                    _verticalOffset += delta;

                }
                finally
                {
                    _buffer.IsHidden = oldHidden;
                }
            }
            catch (Exception ex)
            {
                TerminalLogger.Debug($"[ANSI_PARSER] Failed to decode iTerm2 image: {ex.Message}");
            }
        }

        private int ParseDimension(string val, bool isHeight = false)
        {
            if (string.IsNullOrEmpty(val)) return 0;

            if (val.EndsWith("px"))
            {
                if (double.TryParse(val.Substring(0, val.Length - 2), out double px))
                {
                    // Convert pixels to cells based on current metrics
                    float metric = isHeight ? CellHeight : CellWidth;
                    return (int)Math.Max(1, Math.Ceiling(px / (metric > 0 ? metric : 10f)));
                }
            }
            if (val.EndsWith("%"))
            {
                if (double.TryParse(val.Substring(0, val.Length - 1), out double pct))
                {
                    // % of terminal width/height in cells
                    int total = isHeight ? _buffer.Rows : _buffer.Cols;
                    return (int)Math.Max(1, (total * pct / 100.0));
                }
            }

            if (int.TryParse(val, out int result)) return result;
            return 0;
        }
    }
}
