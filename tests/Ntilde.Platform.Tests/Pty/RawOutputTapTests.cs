using System.Text;
using Ntilde.Pty;

namespace Ntilde.Platform.Tests.Pty;

public sealed class RawOutputTapTests
{
    private static string Ascii(ReadOnlyMemory<byte> m) => Encoding.ASCII.GetString(m.Span);

    [Fact]
    public void Chunks_published_before_the_first_subscriber_are_replayed_to_it_in_order()
    {
        var tap = new RawOutputTap();
        tap.Publish("ab"u8);
        tap.Publish("cd"u8);
        var seen = new List<string>();

        tap.Subscribe(m => seen.Add(Ascii(m)));
        tap.Publish("ef"u8);

        Assert.Equal(["ab", "cd", "ef"], seen);
    }

    [Fact]
    public void A_second_subscriber_gets_no_replay()
    {
        var tap = new RawOutputTap();
        tap.Publish("ab"u8);
        tap.Subscribe(_ => { });
        var second = new List<string>();

        tap.Subscribe(m => second.Add(Ascii(m)));
        tap.Publish("cd"u8);

        Assert.Equal(["cd"], second);
    }

    [Fact]
    public void StopRetaining_drops_what_was_retained_and_retains_nothing_afterwards()
    {
        var tap = new RawOutputTap();
        tap.Publish("ab"u8);
        tap.StopRetaining();
        tap.Publish("cd"u8);
        var seen = new List<string>();

        tap.Subscribe(m => seen.Add(Ascii(m)));
        tap.Publish("ef"u8);

        Assert.Equal(["ef"], seen);
    }

    [Fact]
    public void Each_chunk_is_a_copy_so_the_producer_may_reuse_its_buffer()
    {
        var tap = new RawOutputTap();
        ReadOnlyMemory<byte> got = default;
        tap.Subscribe(m => got = m);
        byte[] buffer = "xy"u8.ToArray();

        tap.Publish(buffer);
        buffer[0] = (byte)'Z';

        Assert.Equal("xy", Ascii(got));
    }

    [Fact]
    public void A_throwing_subscriber_does_not_escape_Publish_and_later_chunks_still_flow()
    {
        var tap = new RawOutputTap();
        int calls = 0;
        tap.Subscribe(_ => { calls++; throw new InvalidOperationException("boom"); });

        tap.Publish("a"u8);
        tap.Publish("b"u8);

        Assert.Equal(2, calls);
    }

    [Fact]
    public void A_throwing_subscriber_does_not_starve_the_subscribers_after_it()
    {
        // A multicast delegate invoked as one call stops at the first throw, so every later
        // subscriber would silently miss that chunk - desynchronising, say, a mux that happened to
        // subscribe second. Each subscriber is invoked and contained on its own.
        var tap = new RawOutputTap();
        var seen = new List<string>();
        tap.Subscribe(_ => throw new InvalidOperationException("first subscriber fails"));
        tap.Subscribe(m => seen.Add(Ascii(m)));

        tap.Publish("a"u8);
        tap.Publish("b"u8);

        Assert.Equal(["a", "b"], seen);
    }

    [Fact]
    public void Empty_chunks_are_not_published()
    {
        var tap = new RawOutputTap();
        int calls = 0;
        tap.Subscribe(_ => calls++);

        tap.Publish(ReadOnlySpan<byte>.Empty);

        Assert.Equal(0, calls);
    }
}
