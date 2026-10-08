using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Ntilde.Rendering
{
    public class SixelDecoder
    {
        /// <summary>
        /// Default for <see cref="MaxPixelDimension"/> here and in SkiaImageDecoder: the same
        /// 2000 pixels AnsiParser enforces on kitty and iTerm2 images, so no image protocol can
        /// produce a larger bitmap than the others.
        /// </summary>
        public const int DefaultMaxPixelDimension = 2000;

        /// <summary>
        /// Largest width and height, in pixels, of a decoded image. Sixel is remote-controlled
        /// input and a single repeat introducer can ask for two billion columns in 13 bytes, so
        /// the bound is applied while decoding: a pixel past either edge is dropped without being
        /// stored or looped over. Memory is then bounded by this alone, and time by this and the
        /// input length, never by the counts and sizes the input declares.
        /// </summary>
        public int MaxPixelDimension { get; init; } = DefaultMaxPixelDimension;

        private int Limit => Math.Max(0, MaxPixelDimension);

        private class SixelColor
        {
            public byte R, G, B;
            public SixelColor(byte r, byte g, byte b) { R = r; G = g; B = b; }
        }

        private readonly Dictionary<int, SixelColor> _palette = new();
        private int _currentColorIdx = 0;
        private int _cursorX = 0;
        private int _cursorY = 0;
        private int _maxWidth = 0;
        private int _maxHeight = 0;

        // The painted pixels, one array per 6-pixel band: six rows of equal width, allocated when
        // the band is first painted and widened (never past MaxPixelDimension) as runs reach
        // further right. A pixel holds its palette register + 1, 0 meaning never painted, and is
        // resolved to a color only when the bitmap is built, so a register redefined after use
        // recolors what it already drew. Painting overwrites in place: going back over a band
        // with '$' as often as the input likes costs time, never memory.
        private readonly List<ushort[]> _bands = new();

        public SixelDecoder()
        {
            // Default palette (simplified VT340 or similar)
            _palette[0] = new SixelColor(0, 0, 0);
            _palette[1] = new SixelColor(0, 0, 255);
            _palette[2] = new SixelColor(255, 0, 0);
            _palette[3] = new SixelColor(0, 255, 0);
            _palette[4] = new SixelColor(255, 0, 255);
            _palette[5] = new SixelColor(0, 255, 255);
            _palette[6] = new SixelColor(255, 255, 0);
            _palette[7] = new SixelColor(255, 255, 255);
        }

        internal static (byte R, byte G, byte B) HlsToRgb(int hue, int lightness, int saturation)
        {
            hue = Math.Clamp(hue, 0, 360);
            lightness = Math.Clamp(lightness, 0, 100);
            saturation = Math.Clamp(saturation, 0, 100);

            double l = lightness / 100.0;
            double s = saturation / 100.0;

            if (s == 0)
            {
                byte value = ToByte(l);
                return (value, value, value);
            }

            double h = ((hue + 240) % 360) / 360.0;
            double q = l < 0.5
                ? l * (1 + s)
                : l + s - (l * s);
            double p = (2 * l) - q;

            return (
                ToByte(HueToRgb(p, q, h + (1.0 / 3.0))),
                ToByte(HueToRgb(p, q, h)),
                ToByte(HueToRgb(p, q, h - (1.0 / 3.0))));
        }

        private static double HueToRgb(double p, double q, double hue)
        {
            if (hue < 0) hue += 1;
            if (hue > 1) hue -= 1;

            if (hue < 1.0 / 6.0) return p + ((q - p) * 6 * hue);
            if (hue < 1.0 / 2.0) return q;
            if (hue < 2.0 / 3.0) return p + ((q - p) * ((2.0 / 3.0) - hue) * 6);
            return p;
        }

        private static byte ToByte(double channel)
        {
            int percentage = Math.Clamp((int)(channel * 100), 0, 100);
            return (byte)((percentage * 255 + 50) / 100);
        }

        public SKBitmap? Decode(string dcs)
        {
            // dcs is everything between 'q' and 'ST'
            int qIdx = dcs.IndexOf('q');
            if (qIdx == -1) return null;

            string header = dcs.Substring(0, qIdx);
            string data = dcs.Substring(qIdx + 1);

            // Parse header: Ps ; Pi ; Pj
            // Ps: Pixel Aspect Ratio (default 2)
            // Pi: Background Select (0=remain, 1=flush)
            // Pj: (don't care for now)

            _cursorX = 0;
            _cursorY = 0;
            _maxWidth = 0;
            _maxHeight = 0;
            _bands.Clear();

            int i = 0;
            while (i < data.Length)
            {
                char c = data[i];

                if (c == '#') // Color palette
                {
                    i++;
                    int start = i;
                    while (i < data.Length && (char.IsDigit(data[i]) || data[i] == ';')) i++;
                    string[] parts = data.Substring(start, i - start).Split(';');
                    // Cap the palette index: sixel is remote-controlled input, and an
                    // arbitrary idx would grow the palette dictionary without bound
                    // (memory DoS). Modern extended implementations top out at 4096.
                    if (parts.Length > 0 && int.TryParse(parts[0], out int idx) && idx >= 0 && idx < 4096)
                    {
                        if (parts.Length == 5) // Set color: idx; type; p1; p2; p3
                        {
                            // Sixel is remote-controlled input: malformed params (e.g.
                            // "#1;;2;3;4" — consecutive ';' yields an empty part) must be
                            // skipped, not thrown out of Decode into the parser loop (#169).
                            if (int.TryParse(parts[1], out int type) &&
                                int.TryParse(parts[2], out int p1) &&
                                int.TryParse(parts[3], out int p2) &&
                                int.TryParse(parts[4], out int p3))
                            {
                                if (type == 2) // RGB 0-100
                                {
                                    // Clamp: values > 100 would otherwise wrap the byte cast.
                                    p1 = Math.Clamp(p1, 0, 100);
                                    p2 = Math.Clamp(p2, 0, 100);
                                    p3 = Math.Clamp(p3, 0, 100);
                                    _palette[idx] = new SixelColor(
                                        (byte)(p1 * 255 / 100),
                                        (byte)(p2 * 255 / 100),
                                        (byte)(p3 * 255 / 100));
                                }
                                else if (type == 1)
                                {
                                    var (r, g, b) = HlsToRgb(p1, p2, p3);
                                    _palette[idx] = new SixelColor(r, g, b);
                                }
                            }
                        }
                        else
                        {
                            _currentColorIdx = idx;
                        }
                    }
                    continue; // i already advanced
                }
                else if (c == '!') // Repeat: '!' count sixel
                {
                    i++;
                    int start = i;
                    long count = 0;
                    while (i < data.Length && char.IsAsciiDigit(data[i]))
                    {
                        // Saturate rather than overflow: every count from the width cap up
                        // paints the same clipped run.
                        count = Math.Min(count * 10 + (data[i] - '0'), int.MaxValue);
                        i++;
                    }
                    // The count applies to the sixel character after it. Anything else there
                    // (no count, or a control such as '$' or '#') drops the introducer, and that
                    // character is handled normally on the next pass instead of being painted as
                    // out-of-range bits.
                    if (i > start && i < data.Length && IsSixelData(data[i]))
                    {
                        ProcessSixel(data[i], (int)count);
                        i++;
                    }
                    continue;
                }
                else if (c == '$') // CR
                {
                    _cursorX = 0;
                    i++;
                }
                else if (c == '-') // LF
                {
                    _cursorX = 0;
                    // Bands from the height cap down are dropped whole, so there is no need to
                    // keep counting rows past it - and a flood of '-' can then never overflow.
                    if (_cursorY < Limit) _cursorY += 6;
                    i++;
                }
                else if (c == '"') // Raster attributes: "Pan;Pad;Ph;Pv (skipped)
                {
                    // The image is sized by what is actually drawn, clipped to the caps, so a
                    // declared size, however large, allocates nothing.
                    i++;
                    while (i < data.Length && (char.IsDigit(data[i]) || data[i] == ';')) i++;
                }
                else if (IsSixelData(c))
                {
                    ProcessSixel(c, 1);
                    i++;
                }
                else
                {
                    i++; // Skip unknown
                }
            }

            return RenderToBitmap();
        }

        private static bool IsSixelData(char c) => c >= '?' && c <= '~';

        // Paints sixel `c` in `count` consecutive columns from the cursor and advances past them.
        private void ProcessSixel(char c, int count)
        {
            int limit = Limit;
            int x0 = _cursorX;
            // The cursor never moves past the width cap, so the columns of a run that would
            // cross it are cut off here, before anything iterates over them.
            int x1 = (int)Math.Min((long)x0 + count, limit);
            if (x1 <= x0 || _cursorY >= limit) return;

            _cursorX = x1;
            if (x1 > _maxWidth) _maxWidth = x1;
            int bandBottom = Math.Min(_cursorY + 6, limit);
            if (bandBottom > _maxHeight) _maxHeight = bandBottom;

            // An undefined register draws nothing, but the columns still count toward the size.
            int bits = c - '?';
            if (bits == 0 || !_palette.ContainsKey(_currentColorIdx)) return;

            ushort[] band = GetBand(_cursorY / 6, x1);
            int stride = band.Length / 6;
            ushort register = (ushort)(_currentColorIdx + 1);
            int rows = bandBottom - _cursorY;
            for (int row = 0; row < rows; row++)
            {
                if ((bits & (1 << row)) != 0)
                {
                    band.AsSpan((row * stride) + x0, x1 - x0).Fill(register);
                }
            }
        }

        // The band's pixel rows, allocated or widened to at least `width` columns.
        private ushort[] GetBand(int index, int width)
        {
            while (_bands.Count <= index) _bands.Add(Array.Empty<ushort>());

            ushort[] band = _bands[index];
            int stride = band.Length / 6;
            if (stride >= width) return band;

            // Doubling keeps widening amortized; the cap bounds it (width never exceeds it).
            int newStride = Math.Min(Limit, Math.Max(width, Math.Max(stride * 2, 64)));
            var widened = new ushort[newStride * 6];
            for (int row = 0; row < 6; row++)
            {
                Array.Copy(band, row * stride, widened, row * newStride, stride);
            }
            _bands[index] = widened;
            return widened;
        }

        private SKBitmap? RenderToBitmap()
        {
            if (_maxWidth <= 0 || _maxHeight <= 0) return null;

            // Every register resolved to its pixel value once rather than per pixel. Bgra8888 is
            // B,G,R,A in memory, which is SKColor's 0xAARRGGBB value on a little-endian machine,
            // i.e. everywhere this ships. Slot 0 (never painted) stays 0: transparent.
            int maxRegister = 0;
            foreach (int idx in _palette.Keys) maxRegister = Math.Max(maxRegister, idx);
            var colors = new uint[maxRegister + 2];
            foreach (var (idx, color) in _palette)
            {
                colors[idx + 1] = (uint)new SKColor(color.R, color.G, color.B);
            }

            var bitmap = new SKBitmap(new SKImageInfo(_maxWidth, _maxHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
            Span<byte> pixels = bitmap.GetPixelSpan();
            int rowBytes = bitmap.RowBytes;

            // Each row is written in full, unpainted pixels as 0, straight into the bitmap's own
            // memory: no clearing pass, and no per-pixel call into Skia.
            for (int y = 0; y < _maxHeight; y++)
            {
                Span<uint> row = MemoryMarshal.Cast<byte, uint>(pixels.Slice(y * rowBytes, _maxWidth * 4));
                ushort[] band = y / 6 < _bands.Count ? _bands[y / 6] : Array.Empty<ushort>();
                int stride = band.Length / 6;
                int painted = Math.Min(stride, _maxWidth);
                ReadOnlySpan<ushort> registers = band.AsSpan((y % 6) * stride, painted);
                for (int x = 0; x < painted; x++)
                {
                    ushort register = registers[x];
                    row[x] = register < colors.Length ? colors[register] : 0;
                }
                row.Slice(painted).Clear();
            }

            return bitmap;
        }
    }
}
