using System.Collections.Concurrent;
using System.Text;
using Ntilde.Pty;
using Xunit;

namespace Ntilde.Tests;

public sealed class PtyDeviceReplyQueueTests
{
    [Fact]
    public void Reply_flood_never_fills_the_keyboard_queue_or_waits_for_a_consumer()
    {
        using var queue = new BlockingCollection<byte[]>(1024);
        var gate = new object();
        for (int i = 0; i < 10000; i++) RustPtySession.TryEnqueueDeviceReply(queue, gate, "\u001b]4;0;rgb:0000/0000/0000\u001b\\");
        Assert.Equal(768, queue.Count);
        Assert.True(queue.TryAdd(Encoding.UTF8.GetBytes("keyboard")));
        Assert.False(RustPtySession.TryEnqueueDeviceReply(queue, gate, "reply"));
        for (int i = 0; i < 768; i++) Assert.True(queue.TryTake(out _));
        Assert.True(queue.TryTake(out byte[]? input));
        Assert.Equal("keyboard", Encoding.UTF8.GetString(input!));
        Assert.True(RustPtySession.TryEnqueueDeviceReply(queue, gate, "after drain"));
        Assert.True(queue.TryTake(out byte[]? reply));
        Assert.Equal("after drain", Encoding.UTF8.GetString(reply!));
    }

    [Fact]
    public void Closed_and_oversized_replies_are_rejected_without_queue_growth()
    {
        using var queue = new BlockingCollection<byte[]>(1024);
        var gate = new object();
        Assert.False(RustPtySession.TryEnqueueDeviceReply(queue, gate, new string('x', 65537)));
        Assert.Empty(queue);
        queue.CompleteAdding();
        Assert.False(RustPtySession.TryEnqueueDeviceReply(queue, gate, "reply"));
        Assert.Empty(queue);
    }
}
