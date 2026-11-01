using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ntilde.VT.Storage;

namespace Ntilde.VT.Tests;

public class ScrollbackConcurrentReadTests
{
    private static void Append(ScrollbackPages pages, char character, int rows = 64)
    {
        var cells = new[] { TerminalCell.Default };
        cells[0].Character = character;
        for (int i = 0; i < rows; i++) pages.AppendRow(cells);
    }

    [Theory]
    [InlineData(0, 64, 'A')]
    [InlineData(64, 0, 'B')]
    public void CapturedPage_RemainsCoherent_WhenAnotherReaderReplacesCache(
        int firstRow, int competingRow, char expected)
    {
        var pool = new TerminalPagePool();
        var pages = new ScrollbackPages(1, pool);
        using var gate = new ReaderWriterLockSlim();
        gate.EnterWriteLock();
        try
        {
            Append(pages, 'A');
            Append(pages, 'B');
        }
        finally { gate.ExitWriteLock(); }

        gate.EnterReadLock();
        try
        {
            _ = pages.GetRow(firstRow);
            var captured = pages.CachedPage;
            Assert.NotNull(captured);
            // Pause reader one immediately after its cache capture. Reader two completes
            // a lookup on the other page before reader one resumes the production lookup.
            _ = pages.GetRow(competingRow);
            Assert.NotSame(captured, pages.CachedPage);
            Assert.Equal(expected, pages.GetRow(firstRow, captured)[0].Character);
        }
        finally { gate.ExitReadLock(); }
        pages.Clear();
        pool.Clear();
    }

    [Fact]
    public async Task SharedReaders_AlwaysReadRequestedRows_WithoutAnyWriter()
    {
        var pool = new TerminalPagePool();
        var pages = new ScrollbackPages(1, pool);
        using var gate = new ReaderWriterLockSlim();
        gate.EnterWriteLock();
        try
        {
            Append(pages, 'A');
            Append(pages, 'B');
        }
        finally { gate.ExitWriteLock(); }

        using var start = new Barrier(4);
        int wrongRows = 0;
        var readers = Enumerable.Range(0, 4).Select(reader => Task.Factory.StartNew(() =>
        {
            gate.EnterReadLock();
            try
            {
                if (!start.SignalAndWait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Readers did not reach their shared-lock start barrier.");
                for (int iteration = 0; iteration < 500_000; iteration++)
                {
                    int row = ((iteration + reader) & 1) * 64;
                    char expected = row == 0 ? 'A' : 'B';
                    if (pages.GetRow(row)[0].Character != expected)
                        Interlocked.Increment(ref wrongRows);
                }
            }
            finally { gate.ExitReadLock(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        try
        {
            await Task.WhenAll(readers);
            Assert.Equal(0, wrongRows);
        }
        finally
        {
            gate.EnterWriteLock();
            try { pages.Clear(); }
            finally { gate.ExitWriteLock(); }
            pool.Clear();
        }
    }

    [Theory]
    [InlineData("budget")]
    [InlineData("release")]
    [InlineData("pop")]
    [InlineData("clear")]
    public void ReturnedPage_IsInvalidated_BeforePoolReusesIt(string removal)
    {
        var pool = new TerminalPagePool();
        var pages = new ScrollbackPages(1, pool, rowsPerPage: 1);
        var other = new ScrollbackPages(1, pool, rowsPerPage: 1);
        using var gate = new ReaderWriterLockSlim();
        gate.EnterWriteLock();
        try
        {
            Append(pages, 'A', 1);
            Append(pages, 'B', 1);
            int cachedRow = removal == "pop" ? 1 : 0;
            _ = pages.GetRow(cachedRow);
            var cached = pages.CachedPage!;
            switch (removal)
            {
                case "budget":
                    pages.MaxScrollbackBytes = cached.Page.ByteSize;
                    pages.TryEvictUntilWithinBudget();
                    break;
                case "release":
                    Assert.Equal(1, pages.ReleaseOldestPage());
                    break;
                case "pop":
                    Assert.True(pages.TryPopLastRow(new TerminalCell[1]));
                    break;
                case "clear":
                    pages.Clear();
                    break;
            }
            Assert.Null(pages.CachedPage);
            Append(other, 'X', 1);
            _ = other.GetRow(0);
            var firstReusedPage = other.CachedPage!.Page;
            // Clear returns two pages: rent the second too, so the captured page is
            // definitely live again even if the pool changes which one it gives first.
            Append(other, 'Y', 1);
            _ = other.GetRow(1);
            Assert.True(ReferenceEquals(cached.Page, firstReusedPage)
                || ReferenceEquals(cached.Page, other.CachedPage!.Page));
            Assert.False(cached.Page.IsReturned);
            if (removal == "clear") Append(pages, 'C', 1);
            char expected = removal switch { "pop" => 'A', "clear" => 'C', _ => 'B' };
            Assert.Equal(expected, pages.GetRow(0)[0].Character);
        }
        finally
        {
            pages.Clear();
            other.Clear();
            gate.ExitWriteLock();
            pool.Clear();
        }
    }

    [Fact]
    public void MetadataCache_KeepsAbsolutePageCoordinates_AfterReplacementAndEviction()
    {
        var pool = new TerminalPagePool();
        var pages = new ScrollbackPages(1, pool, rowsPerPage: 1);
        try
        {
            foreach (string text in new[] { "e\u0301", "👩‍💻" })
            {
                var map = new SmallMap<string>();
                map.Set(0, text);
                pages.AppendRow(new[] { TerminalCell.Default }, extendedText: map);
            }
            Assert.True(pages.GetExtendedTextMap(1)!.TryGet(0, out string? expected));
            Assert.Equal("👩‍💻", expected);
            Assert.NotNull(pages.CachedPage);
            Assert.Equal(1, pages.CachedPage.StartAbsRow);
            _ = pages.GetRow(0); // A competing reader replaces the cached page.
            Assert.True(pages.GetExtendedTextMap(1)!.TryGet(0, out string? replaced));
            Assert.Equal(expected, replaced);
            Assert.Equal(1, pages.ReleaseOldestPage());
            Assert.True(pages.GetExtendedTextMap(0)!.TryGet(0, out string? evicted));
            Assert.Equal(expected, evicted);
            pages.Clear();
            Assert.Null(pages.CachedPage);
            pages.AppendRow(new[] { TerminalCell.Default });
            Assert.Null(pages.GetExtendedTextMap(0));
        }
        finally { pages.Clear(); pool.Clear(); }
    }

    [Fact]
    public void SequentialReads_AllocatePerPage_NotPerRow()
    {
        const int rows = 8192;
        var pool = new TerminalPagePool();
        var pages = new ScrollbackPages(1, pool);
        Append(pages, 'A', rows);
        for (int i = 0; i < rows; i++) _ = pages.GetRow(i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < rows; i++) _ = pages.GetRow(i);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, (rows / 64) * 64);

        _ = pages.GetRow(0);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < rows; i++) _ = pages.GetRow(i % 64);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        pages.Clear();
        pool.Clear();
    }
}
