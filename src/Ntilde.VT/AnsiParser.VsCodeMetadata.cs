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

        private static bool TryDecodeVsCodeMetadata(ReadOnlySpan<char> arguments, [NotNullWhen(true)] out string? value)
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

            value = DecodeVsCodeText(arguments);
            return !string.IsNullOrWhiteSpace(value);
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

        private static string? DecodeVsCodeText(ReadOnlySpan<char> encoded)
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

                if (encoded.IsEmpty)
                {
                    return null;
                }

                char escape = encoded[0];
                if (escape == '\\')
                {
                    decoded.Append(escape);
                    encoded = encoded[1..];
                    continue;
                }

                if (escape is not ('x' or 'X') || encoded.Length < 3
                    || !byte.TryParse(encoded.Slice(1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte ascii)
                    || ascii > 0x7F)
                {
                    return null;
                }

                decoded.Append((char)ascii);
                encoded = encoded[3..];
            }

            return decoded.ToString();
        }
    }
}
