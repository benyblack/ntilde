using Ntilde.Mux.Contracts;

namespace Ntilde.Mux.Tests.Contracts;

public sealed class MuxFramesTests
{
    private static readonly Guid Id = Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");

    private static byte[] PayloadOf(MuxOutboundFrame frame)
    {
        byte[] payload = frame.Bytes[MuxProtocol.FrameHeaderBytes..].ToArray();
        frame.Release();
        return payload;
    }

    [Fact]
    public void Output_round_trips_session_seq_and_bytes()
    {
        byte[] payload = PayloadOf(MuxFrames.Output(Id, 123_456_789_012, "abc"u8));
        Assert.True(MuxFrames.TryParseOutput(payload, out Guid id, out long seq, out ReadOnlySpan<byte> data));
        Assert.Equal(Id, id);
        Assert.Equal(123_456_789_012, seq);
        Assert.Equal("abc"u8.ToArray(), data.ToArray());
    }

    [Fact]
    public void Input_carries_utf8_bytes_verbatim()
    {
        byte[] payload = PayloadOf(MuxFrames.Input(Id, "é\r"u8));
        Assert.True(MuxFrames.TryParseInput(payload, out Guid id, out ReadOnlySpan<byte> utf8));
        Assert.Equal(Id, id);
        Assert.Equal(new byte[] { 0xC3, 0xA9, 0x0D }, utf8.ToArray());
    }

    [Fact]
    public void ResizeEvent_is_exactly_32_bytes_and_round_trips()
    {
        byte[] payload = PayloadOf(MuxFrames.ResizeEvent(Id, 42, 132, 43));
        Assert.Equal(32, payload.Length);
        Assert.True(MuxFrames.TryParseResizeEvent(payload, out Guid id, out long seq, out int cols, out int rows));
        Assert.Equal((Id, 42L, 132, 43), (id, seq, cols, rows));
    }

    [Fact]
    public void Snapshot_round_trips()
    {
        byte[] payload = PayloadOf(MuxFrames.Snapshot(9, Id, 77, "{}"u8));
        Assert.True(MuxFrames.TryParseSnapshot(payload, out long requestId, out Guid id, out long seq, out ReadOnlySpan<byte> json));
        Assert.Equal((9L, Id, 77L), (requestId, id, seq));
        Assert.Equal("{}"u8.ToArray(), json.ToArray());
    }

    [Fact]
    public void Short_payloads_are_rejected_not_sliced()
    {
        Assert.False(MuxFrames.TryParseInput(new byte[15], out _, out _));
        Assert.False(MuxFrames.TryParseOutput(new byte[23], out _, out _, out _));
        Assert.False(MuxFrames.TryParseResizeEvent(new byte[31], out _, out _, out _, out _));
        Assert.False(MuxFrames.TryParseResizeEvent(new byte[33], out _, out _, out _, out _));
        Assert.False(MuxFrames.TryParseSnapshot(new byte[31], out _, out _, out _, out _));
    }
}
