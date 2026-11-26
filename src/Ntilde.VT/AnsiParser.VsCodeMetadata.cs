using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Ntilde.VT
{
    public partial class AnsiParser
    {
        internal const int MaxVsCodeMetadataChars = 8 * 1024;
        private const int MaxVsCodeNonceChars = 128;

        private bool _awaitingVsCodeCommand;
        private string? _vsCodeCommandLine;

        // A pending report is ephemeral. A replay/import or terminal reset has no live shell cycle
        // to bind it to, and neither that report nor its nonce is carried in a snapshot.
        private void ClearVsCodeCommandLine()
        {
            _awaitingVsCodeCommand = false;
            _vsCodeCommandLine = null;
        }

        private static bool TryDecodeVsCodeCommandLine(ReadOnlySpan<char> arguments, [NotNullWhen(true)] out string? value)
        {
            value = null;
            if (arguments.IsEmpty || arguments.Length > MaxVsCodeMetadataChars)
            {
                return false;
            }

            int separator = arguments.IndexOf(';');
            if (separator >= 0)
            {
                // Ntilde does not provision a session nonce for this protocol. Accept one opaque,
                // bounded token for compatibility and discard it: its presence conveys no trust.
                if (!IsVsCodeNonceToken(arguments[(separator + 1)..]))
                {
                    return false;
                }

                arguments = arguments[..separator];
            }

            value = DecodeVsCodeText(arguments, allowLiteralBackslash: false);
            return !string.IsNullOrWhiteSpace(value);
        }

        private static string? DecodeVsCodeDirectory(ReadOnlySpan<char> path)
        {
            if (path.IsEmpty || path.Length > MaxVsCodeMetadataChars)
            {
                return null;
            }

            // P takes the whole value after Cwd=, including literal semicolons. VS Code's shell
            // scripts double backslashes and escape delimiters. A plain Windows absolute path
            // has a single drive separator or a double UNC prefix, so preserve it verbatim.
            string? cwd = IsLiteralWindowsDirectory(path)
                ? path.ToString()
                : DecodeVsCodeText(path, allowLiteralBackslash: true);
            return !string.IsNullOrWhiteSpace(cwd) && IsPlausibleCommandText(cwd)
                && cwd.AsSpan().IndexOfAny('\t', '\r', '\n') < 0 ? cwd : null;
        }

        private static bool IsLiteralWindowsDirectory(ReadOnlySpan<char> path)
        {
            if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\')
            {
                return path.Length == 3 || path[3] != '\\';
            }

            return path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\\\", StringComparison.Ordinal);
        }

        private static bool IsVsCodeNonceToken(ReadOnlySpan<char> nonce)
        {
            if (nonce.IsEmpty || nonce.Length > MaxVsCodeNonceChars)
            {
                return false;
            }

            foreach (char c in nonce)
            {
                if (c is <= ' ' or > '~' or ';')
                {
                    return false;
                }
            }

            return true;
        }

        private static string? DecodeVsCodeText(ReadOnlySpan<char> encoded, bool allowLiteralBackslash)
        {
            var decoded = new StringBuilder(encoded.Length);
            while (!encoded.IsEmpty)
            {
                char c = encoded[0];
                encoded = encoded[1..];
                if (c != '\\')
                {
                    if (c < ' ')
                    {
                        return null;
                    }

                    decoded.Append(c);
                    continue;
                }

                if (!encoded.IsEmpty && encoded[0] == '\\')
                {
                    decoded.Append('\\');
                    encoded = encoded[1..];
                    continue;
                }

                if (TryDecodeVsCodeHex(encoded, out char ascii))
                {
                    decoded.Append(ascii);
                    encoded = encoded[3..];
                    continue;
                }

                if (!allowLiteralBackslash)
                {
                    return null;
                }

                decoded.Append(c);
            }

            return decoded.ToString();
        }

        private static bool TryDecodeVsCodeHex(ReadOnlySpan<char> encoded, out char ascii)
        {
            ascii = default;
            if (encoded.Length < 3 || encoded[0] is not ('x' or 'X')
                || !byte.TryParse(encoded.Slice(1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte value)
                || value > 0x7F)
            {
                return false;
            }

            ascii = (char)value;
            return true;
        }
    }
}
