using System.Globalization;
using System.Text;

namespace Ntilde.VT
{
    /// <summary>
    /// Four xterm device reports: the window and icon title reports (<c>CSI 20 t</c>, <c>CSI 21 t</c>), the X resource
    /// query (XTGETXRES) and the palette stack (XTPUSHCOLORS, XTPOPCOLORS, XTREPORTCOLORS).
    /// </summary>
    public partial class AnsiParser
    {
        /// <summary>
        /// Host-settable gate for <c>CSI 20 t</c> / <c>CSI 21 t</c>. Off by default, as xterm ships them (its
        /// <c>disallowedWindowOps</c> lists both): a title is whatever any program that ever wrote to this terminal
        /// set, so answering with it types that text into the foreground program's input. While off the request is
        /// consumed and never answered.
        /// </summary>
        public bool AllowTitleReport { get; set; }

        /// <summary>
        /// The <c>TERM</c> value the host gave the session, answered for the XTGETXRES <c>termName</c> resource.
        /// Null, the default, means the host did not say, so <c>termName</c> is answered as not available: VT does not
        /// read the process environment or guess from the host application.
        /// </summary>
        public string? TerminalTypeName { get; set; }

        /// <summary>The primary DA reply, which DECID (<c>ESC Z</c>) shares: VT220 with Sixel and ANSI color.</summary>
        private const string PrimaryDeviceAttributesReply = "\x1b[?62;4;22c";

        /// <summary>
        /// DECREQTPARM, bare <c>CSI Ps x</c>. Ps 0 (or omitted) and 1 are answered with DECREPTPARM,
        /// <c>CSI sol ; 1 ; 1 ; 128 ; 128 ; 1 ; 0 x</c>, where sol is Ps + 2: no parity, eight bits, 38400 baud each
        /// way, clock multiplier 1 and no flags - the fixed values xterm reports, since there is no serial line.
        /// Any other Ps, more than one parameter or a subparameter is ignored. Ungated, as primary DA is.
        /// </summary>
        private void ReportTerminalParameters(in CsiSequence csi)
        {
            if (csi.Args.Length > 1 || csi.Separators.Contains(':')) return;
            int request = csi.Args.Length > 0 ? csi.Args[0] : 0;
            if (request is not (0 or 1)) return;
            OnResponse?.Invoke(request == 0 ? "\x1b[2;1;1;128;128;1;0x" : "\x1b[3;1;1;128;128;1;0x");
        }

        /// <summary>The only X resource XTGETXRES answers.</summary>
        internal const string XtgetxresTermNameResource = "termName";

        /// <summary>Most names one XTGETXRES request may ask for; more is not answered.</summary>
        internal const int XtgetxresMaxNames = 16;

        /// <summary>Most parameters one XTPUSHCOLORS or XTPOPCOLORS sequence acts on.</summary>
        private const int XtermColorStackMaxParameters = 16;

        /// <summary>
        /// <c>CSI 20 t</c> (icon label) and <c>CSI 21 t</c> (window title): <c>OSC L label ST</c> and
        /// <c>OSC l title ST</c>, the label as the stream last requested it with OSC 0/1/2, which is the raw
        /// state the title stack saves, not the caption the host presents. A label that holds any control character
        /// (C0, DEL or C1), or that was too long to retain, is answered as an empty label, so the reply can never carry
        /// a terminal or shell control.
        /// </summary>
        private void ReportWindowLabel(bool icon)
        {
            if (!AllowTitleReport || OnResponse is null) return;
            string label = (icon ? _iconName : _windowTitle) ?? string.Empty;
            if (!IsPlainLabel(label)) label = string.Empty;
            OnResponse($"\u001b]{(icon ? 'L' : 'l')}{label}\u001b\\");
        }

        private static bool IsPlainLabel(string label)
        {
            foreach (char ch in label)
            {
                if (ch < 0x20 || (ch >= 0x7F && ch <= 0x9F)) return false;
            }
            return true;
        }

        /// <summary>
        /// XTGETXRES, <c>DCS + Q name[;name…] ST</c> with hex-encoded resource names. Only <c>termName</c> is a
        /// resource here (there is no X resource database); it is answered with the host-supplied
        /// <see cref="TerminalTypeName"/>. The reply follows xterm's: one DCS, <c>DCS 1 + R name=value ST</c> for a
        /// known resource, the name echoed as the request spelled it and the value uppercase hex, or
        /// <c>DCS 0 + R name ST</c> for one that is not. The status is the first name's, and the reply ends after
        /// the first name that is not known. A request that is not entirely valid (truncated, BEL-terminated, an empty,
        /// odd-length or non-hex name, a name that decodes to anything but printable ASCII, or too many names) is
        /// not answered.
        /// </summary>
        private void HandleXtgetxres(string dcs, bool standardTerminator)
        {
            if (OnResponse is null || !standardTerminator || _dcsTruncated) return;
            List<(string Encoded, string Name)>? names = ParseXtgettcapNames(dcs.AsSpan(2));
            if (names is null || names.Count > XtgetxresMaxNames) return;

            OnResponse(BuildXtgetxresReply(names));
        }

        /// <summary>The one DCS for a validated name list: the first name's status, then each name until the first unknown one.</summary>
        private string BuildXtgetxresReply(List<(string Encoded, string Name)> names)
        {
            var reply = new StringBuilder("\u001bP");
            for (int i = 0; i < names.Count; i++)
            {
                string? value = names[i].Name == XtgetxresTermNameResource ? TerminalTypeName : null;
                if (i == 0) reply.Append(value is null ? "0+R" : "1+R");
                reply.Append(names[i].Encoded);
                if (value is null) break;
                reply.Append('=');
                foreach (char ch in value) reply.Append(((int)ch).ToString("X2", CultureInfo.InvariantCulture));
                if (i + 1 < names.Count) reply.Append(';');
            }
            return reply.Append("\u001b\\").ToString();
        }

        /// <summary>
        /// XTPUSHCOLORS (<c>CSI Pm # P</c>), XTPOPCOLORS (<c>CSI Pm # Q</c>) and XTREPORTCOLORS (<c>CSI # R</c>), xterm's
        /// palette stack over the dynamic colors and the 256-color palette the stream has set. Ten slots; no parameter
        /// or 0 pushes onto, or pops from, the top; 1..10 stores into, or restores from, that slot without moving the
        /// top. The report is <c>CSI ? depth ; deepest # Q</c>. This stack is independent of the kitty color stack
        /// (OSC 30001 / 30101). Only the exact identity (no leader, the single <c>#</c> intermediate, plain
        /// parameters) is admitted.
        /// </summary>
        private void HandleXtermColorStack(in CsiSequence csi)
        {
            if (csi.Leader != '\0' || !csi.Intermediates.SequenceEqual("#") || csi.Separators.Contains(':')) return;
            ReadOnlySpan<int> args = csi.Args;
            if (csi.Final == 'R')
            {
                if (OnResponse is null || args.Length > 1 || csi.Arg0 != 0) return;
                (int depth, int deepest) = _buffer.XtermColorStackDepths();
                OnResponse(string.Create(CultureInfo.InvariantCulture, $"\u001b[?{depth};{deepest}#Q"));
                return;
            }

            bool push = csi.Final == 'P';
            int count = Math.Min(Math.Max(args.Length, 1), XtermColorStackMaxParameters);
            for (int i = 0; i < count; i++)
            {
                int slot = args.Length > i ? args[i] : 0;
                if (push) _buffer.XtermPushColors(slot);
                else _buffer.XtermPopColors(slot);
            }
            if (!push) RefreshColorScheme();
        }
    }
}
