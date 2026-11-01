using System.Reflection;
using Avalonia.Headless.XUnit;
using Ntilde.Shell;
using Ntilde.VT;

namespace Ntilde.Tests.Input;

public sealed class TerminalViewSearchTests
{
    [AvaloniaFact]
    public void WrappedMatchesNavigateAndHighlightAsOneLogicalResultEach()
    {
        var buffer = new TerminalBuffer(5, 2);
        new AnsiParser(buffer, false).Process("abcdefgh\r\nabcdefgh");
        var view = new TerminalView();
        view.SetBuffer(buffer);
        (int Current, int Count) state = default;
        view.SearchStateChanged += (current, count) => state = (current, count);

        view.Search("abcdefgh", caseSensitive: true);
        Assert.Equal((1, 2), state);
        Assert.Equal(2, view.ScrollOffset);
        AssertActiveHighlights(0);

        view.NextMatch();
        Assert.Equal((2, 2), state);
        Assert.Equal(0, view.ScrollOffset);
        AssertActiveHighlights(2);

        view.NextMatch();
        Assert.Equal((1, 2), state);
        view.PrevMatch();
        Assert.Equal((2, 2), state);
        AssertActiveHighlights(2);
        view.ClearSearch();
        Assert.Equal((0, 0), state);

        void AssertActiveHighlights(int firstRow)
        {
            var matches = (IReadOnlyList<SearchMatch>)typeof(TerminalView)
                .GetField("_searchMatches", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
            using var snapshot = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest
            {
                ViewportRows = buffer.Rows,
                ViewportCols = buffer.Cols,
                ScrollOffset = view.ScrollOffset,
                SearchMatches = matches,
                ActiveSearchIndex = state.Current - 1,
                Isolated = true
            }, out _);
            Assert.Equal(2, snapshot.SearchHighlights.Length);
            var first = snapshot.SearchHighlights.Array[0];
            var second = snapshot.SearchHighlights.Array[1];
            Assert.Equal((firstRow, 0, 4, true), (first.AbsRow, first.StartCol, first.EndCol, first.IsActive));
            Assert.Equal((firstRow + 1, 0, 2, true), (second.AbsRow, second.StartCol, second.EndCol, second.IsActive));
        }
    }
}
