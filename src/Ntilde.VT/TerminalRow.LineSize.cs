namespace Ntilde.VT
{
    public partial class TerminalRow
    {
        private LineSize _lineSize;

        /// <summary>
        /// The row's VT100 line attribute. It belongs to the row, so it travels wherever the row object does
        /// (scrolling, IL, DL). A change is a presentation change with no cell diff, so it bumps the revision.
        /// </summary>
        public LineSize LineSize
        {
            get => _lineSize;
            internal set
            {
                if (_lineSize == value) return;
                _lineSize = value;
                TouchRevision();
            }
        }
    }
}
