using System.Text;

namespace Ntilde.VT
{
    public partial class AnsiParser
    {
        private const string XtgettcapFailure = "\x1bP0+r\x1b\\";

        /// <summary>Most names one XTGETTCAP request may ask for; more fail the whole request.</summary>
        internal const int XtgettcapMaxNames = 16;

        /// <summary>Longest decoded name accepted, in characters (64 hex digits).</summary>
        internal const int XtgettcapMaxNameChars = 32;

        /// <summary>
        /// XTGETTCAP, <c>DCS + q name[;name…] ST</c> with hex-encoded terminfo names (#844).
        /// </summary>
        /// <remarks>
        /// Each name gets its own reply, in request order, as kitty and foot send them:
        /// <c>DCS 1 + r name=value ST</c> for a string or number, <c>DCS 1 + r name ST</c> for a
        /// boolean, and xterm's bare <c>DCS 0 + r ST</c> for a name Ntilde does not advertise.
        /// The name is echoed as the request spelled it, which is safe because it has already
        /// been checked to be hex; the value is always hex. A request that is not entirely valid
        /// (truncated, BEL-terminated, empty or odd-length or non-hex name, a name that decodes
        /// to anything but printable ASCII, or too many names) gets a single <c>DCS 0 + r ST</c>
        /// and no other reply, so it cannot multiply or inject output.
        /// </remarks>
        private void HandleXtgettcap(string dcs, bool standardTerminator)
        {
            List<(string Encoded, string Name)>? names = standardTerminator && !_dcsTruncated ? ParseXtgettcapNames(dcs.AsSpan(2)) : null;
            if (names is null)
            {
                OnResponse?.Invoke(XtgettcapFailure);
                return;
            }

            foreach ((string encoded, string name) in names)
            {
                TerminalCapability? capability = AdvertisedCapability(name);
                OnResponse?.Invoke(capability is null ? XtgettcapFailure : FormatXtgettcapReply(encoded, capability));
            }
        }

        private TerminalCapability? AdvertisedCapability(string name)
            => TerminalCapabilities.TryGet(name, out TerminalCapability? capability)
                && (!capability.RequiresClipboardWrite || ClipboardWriteAvailable?.Invoke() == true)
                ? capability
                : null;

        private static string FormatXtgettcapReply(string encodedName, TerminalCapability capability)
        {
            var reply = new StringBuilder("\x1bP1+r", 16 + encodedName.Length + capability.Value.Length * 2);
            reply.Append(encodedName);
            if (capability.Kind != TerminalCapabilityKind.Flag)
            {
                reply.Append('=');
                foreach (char ch in capability.Value)
                    reply.Append(((int)ch).ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
            return reply.Append("\x1b\\").ToString();
        }

        /// <summary>
        /// Returns each name as the request spelled it and decoded, or null when any part of the
        /// list is invalid.
        /// </summary>
        internal static List<(string Encoded, string Name)>? ParseXtgettcapNames(ReadOnlySpan<char> list)
        {
            var names = new List<(string Encoded, string Name)>();
            while (true)
            {
                int end = list.IndexOf(';');
                ReadOnlySpan<char> encoded = end < 0 ? list : list[..end];
                string? decoded = DecodeXtgettcapName(encoded);
                if (decoded is null || names.Count == XtgettcapMaxNames) return null;
                names.Add((encoded.ToString(), decoded));
                if (end < 0) return names;
                list = list[(end + 1)..];
            }
        }

        private static string? DecodeXtgettcapName(ReadOnlySpan<char> encoded)
        {
            if (encoded.Length == 0 || encoded.Length % 2 != 0 || encoded.Length > XtgettcapMaxNameChars * 2)
                return null;
            var name = new char[encoded.Length / 2];
            for (int i = 0; i < name.Length; i++)
            {
                int high = HexValue(encoded[2 * i]);
                int low = HexValue(encoded[2 * i + 1]);
                if (high < 0 || low < 0) return null;
                int ch = (high << 4) | low;
                if (ch is < 0x21 or > 0x7E) return null;
                name[i] = (char)ch;
            }
            return new string(name);
        }

        private static int HexValue(char ch) => ch switch
        {
            >= '0' and <= '9' => ch - '0',
            >= 'a' and <= 'f' => ch - 'a' + 10,
            >= 'A' and <= 'F' => ch - 'A' + 10,
            _ => -1,
        };
    }
}
