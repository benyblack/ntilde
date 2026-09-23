using Ntilde.Mux.Contracts;

namespace Ntilde.Mux.Tests.Contracts;

public sealed class MuxFrameCodecTests
{
    [Fact]
    public void Header_is_kind_then_little_endian_u32_length()
    {
        byte[] header = new byte[MuxProtocol.FrameHeaderBytes];
        MuxFrameCodec.WriteHeader(header, MuxFrameKind.Output, 0x01020304);
        Assert.Equal(new byte[] { 0x11, 0x04, 0x03, 0x02, 0x01 }, header);
    }

    [Fact]
    public void A_length_above_the_ceiling_is_frame_too_large()
    {
        byte[] header = [0x01, 0, 0, 0, 0];
        MuxFrameCodec.WriteHeader(header, MuxFrameKind.Request, 101);
        var ex = Assert.Throws<MuxProtocolException>(() => MuxFrameCodec.ReadHeader(header, maxFrameBytes: 100));
        Assert.Equal(MuxErrorCodes.FrameTooLarge, ex.Code);
    }

    [Fact]
    public void An_unknown_kind_is_a_protocol_error()
    {
        byte[] header = [0x7F, 1, 0, 0, 0];
        var ex = Assert.Throws<MuxProtocolException>(() => MuxFrameCodec.ReadHeader(header, MuxProtocol.MaxFrameBytes));
        Assert.Equal(MuxErrorCodes.ProtocolError, ex.Code);
    }

    [Fact]
    public void Reader_returns_null_on_a_clean_end_of_stream()
        => Assert.Null(MuxFrameReader.Read(new MemoryStream()));

    [Fact]
    public void Reader_round_trips_a_frame()
    {
        var stream = new MemoryStream();
        MuxOutboundFrame outbound = MuxFrames.Output(Guid.NewGuid(), 7, "hi"u8);
        outbound.WriteTo(stream);
        outbound.Release();
        stream.Position = 0;

        using MuxInboundFrame? frame = MuxFrameReader.Read(stream);

        Assert.NotNull(frame);
        Assert.Equal(MuxFrameKind.Output, frame!.Kind);
        Assert.Equal(MuxFrames.SessionIdBytes + sizeof(long) + 2, frame.Length);
    }

    [Fact]
    public void A_truncated_header_or_payload_is_end_of_stream()
    {
        Assert.Throws<EndOfStreamException>(() => MuxFrameReader.Read(new MemoryStream([0x01, 0x05])));
        Assert.Throws<EndOfStreamException>(() => MuxFrameReader.Read(new MemoryStream([0x01, 0x05, 0, 0, 0, (byte)'{'])));
    }

    [Fact]
    public void An_oversize_header_is_refused_before_any_payload_is_read()
    {
        var stream = new MemoryStream();
        byte[] header = new byte[MuxProtocol.FrameHeaderBytes];
        MuxFrameCodec.WriteHeader(header, MuxFrameKind.Request, 1000);
        stream.Write(header);
        stream.Write(new byte[1000]);
        stream.Position = 0;

        var ex = Assert.Throws<MuxProtocolException>(() => MuxFrameReader.Read(stream, maxFrameBytes: 999));

        Assert.Equal(MuxErrorCodes.FrameTooLarge, ex.Code);
        Assert.Equal(MuxProtocol.FrameHeaderBytes, stream.Position);
    }

    [Fact]
    public void Outbound_frames_are_reference_counted()
    {
        MuxOutboundFrame frame = MuxOutboundFrame.Rent(MuxFrameKind.Output, 4);
        frame.AddRef();
        frame.Release();
        Assert.Equal(MuxProtocol.FrameHeaderBytes + 4, frame.Bytes.Length); // still alive: one ref left
        frame.Release();
        Assert.Throws<ObjectDisposedException>(() => frame.AddRef());
        Assert.Throws<InvalidOperationException>(() => frame.Release());
    }
}
