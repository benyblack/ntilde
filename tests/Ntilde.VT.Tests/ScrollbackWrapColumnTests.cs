using Ntilde.VT.Storage;

namespace Ntilde.VT.Tests;

public sealed class ScrollbackWrapColumnTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void PopAndPageReuseDoNotResurrectWrapPadding(int width)
    {
        var pool = new TerminalPagePool();
        var pages = new ScrollbackPages(4, pool);
        var cells = Enumerable.Repeat(TerminalCell.Default, 4).ToArray();
        try
        {
            pages.AppendRow(cells, true, null, null, null, width);
            Assert.Equal(width, pages.GetWrappedColumns(0));
            Assert.True(pages.TryPopLastRow(cells, out bool wrapped, out _, out _, out _, out int? restored));
            Assert.True(wrapped);
            Assert.Equal(width, restored);
            pages.AppendRow(cells);
            Assert.Null(pages.GetWrappedColumns(0));
            Assert.False(pages.IsRowWrapped(0));
        }
        finally { pages.Clear(); pool.Clear(); }
    }

    [Theory]
    [InlineData("text")]
    [InlineData("background")]
    [InlineData("metadata")]
    public void InvalidPaddingIsRefusedBeforeAppend(string kind)
    {
        var pool = new TerminalPagePool();
        var pages = new ScrollbackPages(4, pool);
        var cells = Enumerable.Repeat(TerminalCell.Default, 4).ToArray();
        var map = new SmallMap<string>();
        if (kind == "text") cells[3].Character = 'X';
        if (kind == "background") cells[3].IsDefaultBackground = false;
        if (kind == "metadata") map.Set(3, "X");
        try
        {
            Assert.Throws<ArgumentException>(() => pages.AppendRow(cells, true, map, null, null, 3));
            Assert.Equal(0, pages.Count);
        }
        finally { pages.Clear(); pool.Clear(); }
    }
}
