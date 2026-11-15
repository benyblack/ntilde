namespace Ntilde.VT
{
    /// <summary>
    /// xterm's video-attribute stack: XTPUSHSGR (<c>CSI Pm # {</c>) and XTPOPSGR (<c>CSI # }</c>).
    /// </summary>
    public partial class AnsiParser
    {
        /// <summary>
        /// Only the exact identity is admitted: no leader, the single <c>#</c> intermediate and plain parameters (a colon
        /// subparameter or any other spelling is ignored). A push selects the listed attributes (none listed: all of them;
        /// a value ctlseqs does not list selects nothing but still saves a level); a pop takes no parameter.
        /// </summary>
        private void HandleXtermSgrStack(in CsiSequence csi)
        {
            if (csi.Leader != '\0' || !csi.Intermediates.SequenceEqual("#") || csi.Separators.Contains(':')) return;
            ReadOnlySpan<int> args = csi.Args;
            if (csi.Final == '}')
            {
                if (args.Length == 0) _buffer.PopSgr();
                return;
            }

            int mask = 0;
            if (args.Length == 0) mask = TerminalBuffer.SgrStackAll;
            else
            {
                foreach (int value in args) mask |= TerminalBuffer.SgrStackBitFor(value);
            }

            _buffer.PushSgr(mask);
        }
    }
}
