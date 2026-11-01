using System.Runtime.InteropServices;

namespace Ntilde.VT.Tests;

public sealed class ScrollbackByteCeilingTests
{
    [Fact]
    public void HostCeilingSurvivesHistoryOverridesAndAllResizePaths()
    {
        long cap = 128L * 20 * Marshal.SizeOf<TerminalCell>();
        var buffer = new TerminalBuffer(20, 4) { MaxHistory = 5000, MaxScrollbackBytesLimit = cap };
        var parser = new AnsiParser(buffer, false);
        void Check()
        {
            Assert.InRange(buffer.MaxScrollbackBytes, 1, cap);
            Assert.InRange(buffer.Scrollback.CurrentBytes, 0, cap);
        }
        for (int i = 0; i < 400; i++) parser.Process($"line {i}\r\n");
        Check();
        buffer.MaxHistory = int.MaxValue;
        Check();
        buffer.MaxScrollbackBytes = long.MaxValue;
        Check();
        buffer.Resize(40, 4);
        Check();
        buffer.Resize(10, 4);
        Check();
        buffer.Resize(10, 2);
        Check();
        parser.Process("\u001b[?1049h");
        buffer.Resize(80, 6);
        Check();
        parser.Process("\u001b[?1049l");
        Check();
    }

    [Fact]
    public void AByteCeilingBelowOneRowStillKeepsNoRetainedPages()
    {
        var buffer = new TerminalBuffer(80, 2) { MaxScrollbackBytesLimit = 1, MaxHistory = 100_000 };
        new AnsiParser(buffer, false).Process(string.Concat(Enumerable.Repeat("line\r\n", 200)));
        Assert.Equal(1, buffer.MaxScrollbackBytes);
        Assert.Equal(0, buffer.Scrollback.Count);
        buffer.Resize(160, 2);
        Assert.Equal(1, buffer.MaxScrollbackBytes);
        Assert.Equal(0, buffer.Scrollback.CurrentBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonpositiveCeilingsAreRejectedBeforeMutation(long value)
    {
        var buffer = new TerminalBuffer(20, 2);
        long before = buffer.MaxScrollbackBytes;
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.MaxScrollbackBytesLimit = value);
        Assert.Equal(long.MaxValue, buffer.MaxScrollbackBytesLimit);
        Assert.Equal(before, buffer.MaxScrollbackBytes);
    }

    [Fact]
    public void DefaultCeilingPreservesTheExistingWidthDerivedBudget()
    {
        var buffer = new TerminalBuffer(20, 2) { MaxHistory = 5000 };
        Assert.Equal(long.MaxValue, buffer.MaxScrollbackBytesLimit);
        Assert.Equal(20L * 5000 * Marshal.SizeOf<TerminalCell>(), buffer.MaxScrollbackBytes);
        buffer.Resize(40, 2);
        Assert.Equal(40L * 5000 * Marshal.SizeOf<TerminalCell>(), buffer.MaxScrollbackBytes);
    }
}
