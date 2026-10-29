namespace Ntilde.VT
{
    public readonly struct SearchMatch
    {
        public int AbsRow { get; }
        public int StartCol { get; }
        public int EndCol { get; }

        public SearchMatch(int row, int start, int end)
        {
            AbsRow = row;
            StartCol = start;
            EndCol = end;
        }
    }
}
