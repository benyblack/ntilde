using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.Threading;
using Ntilde.Shell;
using Ntilde.VT;

namespace Ntilde.Tests.Input;

public sealed class TerminalViewSearchTests
{
    [AvaloniaFact]
    public void SessionResizeRefreshesSearchOptionsNavigationAndHighlightSpans()
    {
        var buffer = new TerminalBuffer(5, 2);
        new AnsiParser(buffer, false).Process("abcdefgh\r\nabcdefgh\r\nABCDEFGH");
        var view = new TerminalView();
        view.SetBuffer(buffer);
        (int Current, int Count) state = default;
        view.SearchStateChanged += (current, count) => state = (current, count);
        view.Search("abc.*h", useRegex: true, caseSensitive: true);
        view.NextMatch();
        Assert.Equal((2, 2), state);
        ResizeAndCheck(10, 1);
        view.PrevMatch();
        Assert.Equal((1, 2), state);
        ResizeAndCheck(5, 2);
        view.ClearSearch();
        buffer.Resize(10, 2);
        view.NotifySessionResizedBuffer();
        Assert.Equal((0, 0), state);
        Assert.Empty(Matches());

        IReadOnlyList<SearchMatch> Matches() => (IReadOnlyList<SearchMatch>)typeof(TerminalView)
            .GetField("_searchMatches", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;

        void ResizeAndCheck(int width, int expectedSpans)
        {
            int selected = state.Current;
            buffer.Resize(width, 2);
            view.NotifySessionResizedBuffer();
            Assert.Equal((selected, 2), state);
            var fresh = buffer.FindMatches("abc.*h", true, true);
            var actual = Matches();
            Assert.Equal(fresh.Count, actual.Count);
            for (int i = 0; i < actual.Count; i++)
            {
                Assert.Equal(expectedSpans, actual[i].SpanCount);
                for (int span = 0; span < actual[i].SpanCount; span++)
                    Assert.Equal(fresh[i].GetSpan(span), actual[i].GetSpan(span));
            }
            using var snapshot = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest
            {
                ViewportRows = buffer.Rows,
                ViewportCols = buffer.Cols,
                ScrollOffset = view.ScrollOffset,
                SearchMatches = actual,
                ActiveSearchIndex = selected - 1,
                Isolated = true
            }, out _);
            var active = snapshot.SearchHighlights.Array.Take(snapshot.SearchHighlights.Length)
                .Where(span => span.IsActive).ToArray();
            Assert.Equal(expectedSpans, active.Length);
            for (int span = 0; span < expectedSpans; span++)
                Assert.Equal(fresh[selected - 1].GetSpan(span).AbsRow, active[span].AbsRow);
        }
    }

    [AvaloniaFact]
    public void CoalescedSessionResizeRoundTripRefreshesAfterRetentionChanges()
    {
        var buffer = new TerminalBuffer(4, 4) { MaxHistory = 1 };
        new AnsiParser(buffer, false).Process("abcdefgh\r\nabcdefgh");
        var view = new TerminalView { DefersBufferResizeToSession = true };
        view.SetBuffer(buffer);
        view.NotifySessionResizedBuffer();
        (int Current, int Count) state = default;
        view.SearchStateChanged += (current, count) => state = (current, count);
        view.Search("abcdefgh", caseSensitive: true);
        view.NextMatch();
        Assert.Equal((2, 2), state);
        buffer.Resize(2, 4);
        buffer.Resize(4, 4);
        Assert.Single(buffer.FindMatches("abcdefgh", false, true));
        view.NotifySessionResizedBuffer();
        Assert.Equal((1, 1), state);
        var matches = (IReadOnlyList<SearchMatch>)typeof(TerminalView)
            .GetField("_searchMatches", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        Assert.Single(matches);
    }

    [AvaloniaFact]
    public void LocalResizeRebuildsWrappedSearchSpans()
    {
        var buffer = new TerminalBuffer(80, 24);
        var view = new TerminalView();
        view.SetBuffer(buffer);
        var window = new Window { Content = view, Width = 800, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            string query = new('a', buffer.Cols + 3);
            new AnsiParser(buffer, false).Process(query);
            view.Search(query, caseSensitive: true);
            int oldWidth = buffer.Cols;
            view.ReleaseResizeThrottleForTest();
            window.Width = 500;
            Dispatcher.UIThread.RunJobs();
            Assert.True(buffer.Cols < oldWidth);
            var fresh = Assert.Single(buffer.FindMatches(query, false, true));
            var actual = Assert.Single((IReadOnlyList<SearchMatch>)typeof(TerminalView)
                .GetField("_searchMatches", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!);
            Assert.Equal(fresh.SpanCount, actual.SpanCount);
            for (int span = 0; span < fresh.SpanCount; span++)
                Assert.Equal(fresh.GetSpan(span), actual.GetSpan(span));
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

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
