using System.Text;
using Ntilde.Platform.Ssh.Exec;

namespace Ntilde.Platform.Tests.Ssh.Exec;

/// <summary>The stderr tail an exec channel keeps (Phase 4 spec §8.2): the last N bytes, decoded leniently.</summary>
public sealed class BoundedTailTests
{
    [Fact]
    public void Keeps_everything_under_the_capacity()
    {
        var tail = new BoundedTail(16);

        tail.Append("abc"u8);
        tail.Append("def"u8);

        Assert.Equal("abcdef", tail.ToString());
    }

    [Fact]
    public void Keeps_only_the_last_bytes_across_appends_that_wrap()
    {
        var tail = new BoundedTail(8);

        tail.Append("0123456"u8);
        tail.Append("789"u8);
        tail.Append("AB"u8);

        Assert.Equal("456789AB", tail.ToString());
    }

    [Fact]
    public void An_append_larger_than_the_capacity_keeps_its_end()
    {
        var tail = new BoundedTail(4);

        tail.Append("xy"u8);
        tail.Append("0123456789"u8);

        Assert.Equal("6789", tail.ToString());
    }

    [Fact]
    public void A_cut_through_a_utf8_sequence_drops_the_partial_character()
    {
        var tail = new BoundedTail(5);

        // "é" is C3 A9: the cut leaves its A9 continuation byte first.
        tail.Append(Encoding.UTF8.GetBytes("abé1234"));

        Assert.Equal("1234", tail.ToString());
    }

    [Fact]
    public void Invalid_utf8_is_replaced_not_thrown()
    {
        var tail = new BoundedTail(16);

        tail.Append([(byte)'o', 0xFF, (byte)'k']);

        Assert.Equal("o�k", tail.ToString());
    }

    [Fact]
    public void Empty_and_invalid_capacity()
    {
        Assert.Equal(string.Empty, new BoundedTail(8).ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedTail(0));
    }
}
