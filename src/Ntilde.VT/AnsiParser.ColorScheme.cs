using System.Threading;

namespace Ntilde.VT
{
    public partial class AnsiParser
    {
        private TermColor? _defaultBackground;
        private int _colorScheme = 1; // The unset OSC 11 background is black.
        private bool _colorSchemeReportsEnabled;

        /// <summary>
        /// Host-provided effective background for OSC 11 and dark/light reporting. A change
        /// across the shared perceived-luma threshold sends one DSR only while mode 2031 is on.
        /// This is terminal palette preference, including profile overrides, not a raw OS theme.
        /// </summary>
        public TermColor? DefaultBackground
        {
            get => _defaultBackground;
            set
            {
                _defaultBackground = value;
                RefreshColorScheme();
            }
        }

        private void RefreshColorScheme(bool notify = true)
        {
            int scheme = TermColor.IsDark(_buffer.RuntimeBackground ?? _defaultBackground ?? TermColor.Black) ? 1 : 2;
            int previous = Interlocked.Exchange(ref _colorScheme, scheme);
            if (notify && previous != scheme && Volatile.Read(ref _colorSchemeReportsEnabled)) SendColorSchemeReport(scheme);
        }

        /// <summary>Fixed, numeric-only Contour DSR: 1 is dark and 2 is light.</summary>
        private void ReportColorScheme() => SendColorSchemeReport(Volatile.Read(ref _colorScheme));

        private void SendColorSchemeReport(int scheme) => OnResponse?.Invoke(
            scheme == 1 ? "\u001b[?997;1n" : "\u001b[?997;2n");
    }
}
