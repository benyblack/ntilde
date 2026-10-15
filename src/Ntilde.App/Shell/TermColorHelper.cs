using Avalonia.Media;
using Ntilde.Platform;
using Ntilde.VT;

namespace Ntilde.Shell
{
    public static class TermColorHelper
    {
        public static TermColor FromAvaloniaColor(Color c) => new TermColor(c.R, c.G, c.B, c.A);
        public static Color ToAvaloniaColor(this TermColor tc) => Color.FromArgb((byte)tc.A, (byte)tc.R, (byte)tc.G, (byte)tc.B);

        /// <summary>
        /// The colour as theme files store it: #RRGGBB when opaque, #AARRGGBB otherwise. Unlike
        /// <see cref="TermColor.ToString"/> it keeps the alpha, so text parsed back gives the same colour.
        /// </summary>
        public static string ToHex(this TermColor tc)
            => tc.A == 255 ? $"#{tc.R:X2}{tc.G:X2}{tc.B:X2}" : $"#{tc.A:X2}{tc.R:X2}{tc.G:X2}{tc.B:X2}";
    }
}
