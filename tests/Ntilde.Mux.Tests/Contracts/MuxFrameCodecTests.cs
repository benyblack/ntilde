using System.Collections.Concurrent;
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

    /// <summary>
    /// Regression for the increment-then-corrective-decrement race: two separate atomic ops let a
    /// concurrent AddRef on an already-released frame observe a transient post-increment value
    /// above 1 and succeed on a frame whose buffer is already back in the pool. While the creator
    /// holds its own reference, every AddRef/Release pair here must succeed cleanly; only once the
    /// creator also releases (dropping the count to zero) does a further AddRef see a dead frame.
    /// </summary>
    [Fact]
    public void Concurrent_AddRef_and_Release_pairs_never_corrupt_the_count()
    {
        MuxOutboundFrame frame = MuxOutboundFrame.Rent(MuxFrameKind.Output, 4);
        var exceptions = new ConcurrentBag<Exception>();
        var options = new ParallelOptions
        {
            CancellationToken = TestContext.Current.CancellationToken,
            MaxDegreeOfParallelism = Environment.ProcessorCount,
        };

        Parallel.For(0, 50, options, _ =>
        {
            for (int i = 0; i < 200; i++)
            {
                try
                {
                    frame.AddRef();
                    frame.Release();
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        });

        Assert.Empty(exceptions);

        frame.Release(); // the creator's own reference: count now reaches zero.
        Assert.Throws<ObjectDisposedException>(() => frame.AddRef());
    }
}
