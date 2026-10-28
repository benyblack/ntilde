using Ntilde.Pty;
using Xunit;

namespace Ntilde.Platform.Tests.Pty;

/// <summary>
/// #173: output buffered for a session's first subscriber had no bound. <see cref="PendingOutputReplay"/>
/// keeps the newest output within a character budget, used by both RustPtySession and NativeSshSession.
/// </summary>
public sealed class PendingOutputReplayTests
{
    [Fact]
    public void Within_the_bound_everything_is_kept_in_order()
    {
        var replay = new PendingOutputReplay(maxChars: 10);
        replay.Add("abc");
        replay.Add("defg");

        Assert.Equal(["abc", "defg"], replay.ToArray());
        Assert.Equal(7, replay.Chars);
        Assert.False(replay.DroppedOutput);
    }

    [Fact]
    public void Past_the_bound_the_oldest_chunks_go()
    {
        var replay = new PendingOutputReplay(maxChars: 10);
        replay.Add("aaaa");
        replay.Add("bbbb");
        replay.Add("cccc");

        Assert.Equal(["bbbb", "cccc"], replay.ToArray());
        Assert.Equal(8, replay.Chars);
        Assert.True(replay.DroppedOutput);
    }

    [Fact]
    public void The_newest_chunk_is_kept_whole_even_past_the_bound()
    {
        var replay = new PendingOutputReplay(maxChars: 10);
        replay.Add("aa");
        replay.Add("0123456789ABCDEF");

        Assert.Equal(["0123456789ABCDEF"], replay.ToArray());
        Assert.True(replay.DroppedOutput);
    }
}
