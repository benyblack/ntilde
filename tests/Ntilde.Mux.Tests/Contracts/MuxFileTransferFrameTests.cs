using System.Buffers.Binary;
using System.Text;
using Ntilde.Mux.Contracts;

namespace Ntilde.Mux.Tests.Contracts;

/// <summary>The FileTransfer frame of Kitty file transport (#887): a daemon's read of a <c>t=f</c> file, forwarded to a client before the output frame that names it.</summary>
public sealed class MuxFileTransferFrameTests
{
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static byte[] Payload(MuxOutboundFrame frame)
    {
        byte[] bytes = frame.Payload.ToArray();
        frame.Release();
        return bytes;
    }

    [Fact]
    public void A_file_and_an_unavailable_read_round_trip_with_the_stream_position_and_path()
    {
        byte[] file = [0, 1, 2, 255];
        byte[] withFile = Payload(MuxFrames.FileTransfer(Id, 1234, "/tmp/frame é.rgba", file, hasData: true));
        Assert.True(MuxFrames.TryParseFileTransfer(withFile, out Guid id, out long seq, out string path, out bool hasData, out ReadOnlySpan<byte> data));
        Assert.Equal((Id, 1234L, "/tmp/frame é.rgba", true), (id, seq, path, hasData));
        Assert.True(data.SequenceEqual(file));

        byte[] unavailable = Payload(MuxFrames.FileTransfer(Id, 9, "/tmp/missing", [], hasData: false));
        Assert.True(MuxFrames.TryParseFileTransfer(unavailable, out _, out long unavailableSeq, out string unavailablePath, out bool unavailableHasData, out ReadOnlySpan<byte> none));
        Assert.Equal((9L, "/tmp/missing", false, 0), (unavailableSeq, unavailablePath, unavailableHasData, none.Length));

        // An empty file is a real file, not an unavailable one.
        Assert.True(MuxFrames.TryParseFileTransfer(Payload(MuxFrames.FileTransfer(Id, 0, "/tmp/empty", [], hasData: true)), out _, out _, out _, out bool emptyHasData, out ReadOnlySpan<byte> empty));
        Assert.True(emptyHasData); Assert.Equal(0, empty.Length);
    }

    [Fact]
    public void The_frame_kind_is_known_and_a_frame_announces_the_header_it_carries()
    {
        Assert.True(MuxFrameCodec.IsKnownKind((byte)MuxFrameKind.FileTransfer));
        Assert.Equal(0x14, (byte)MuxFrameKind.FileTransfer);
        Assert.False(MuxFrameCodec.IsKnownKind(0x16));
        MuxOutboundFrame frame = MuxFrames.FileTransfer(Id, 0, "/p", [7], hasData: true);
        Assert.Equal(MuxFrameKind.FileTransfer, frame.Kind);
        Assert.Equal(MuxFrames.FileTransferHeaderBytes + 2 + 1, frame.Payload.Length);
        frame.Release();
    }

    [Fact]
    public void A_file_frame_is_accounted_as_bulk_so_it_cannot_make_the_output_behind_it_a_slow_client()
    {
        MuxOutboundFrame frame = MuxFrames.FileTransfer(Id, 0, "/p", new byte[1024], hasData: true);
        Assert.True(frame.IsBulk);
        frame.Release();
        MuxOutboundFrame output = MuxFrames.Output(Id, 0, [1]);
        Assert.False(output.IsBulk);
        output.Release();
    }

    [Fact]
    public void A_payload_no_correct_daemon_sends_is_refused()
    {
        byte[] good = Payload(MuxFrames.FileTransfer(Id, 1, "/tmp/a", [1, 2], hasData: true));
        bool Parses(byte[] payload) => MuxFrames.TryParseFileTransfer(payload, out _, out _, out _, out _, out _);
        Assert.True(Parses(good));
        Assert.False(Parses(good[..(MuxFrames.FileTransferHeaderBytes - 1)]));                                         // shorter than the header

        byte[] flags = (byte[])good.Clone(); flags[MuxFrames.SessionIdBytes + 8] = 2;                                  // an unknown flag
        Assert.False(Parses(flags));

        byte[] length = (byte[])good.Clone(); BinaryPrimitives.WriteInt32LittleEndian(length.AsSpan(MuxFrames.SessionIdBytes + 9), good.Length);
        Assert.False(Parses(length));                                                                                  // a path longer than the payload
        BinaryPrimitives.WriteInt32LittleEndian(length.AsSpan(MuxFrames.SessionIdBytes + 9), -1);
        Assert.False(Parses(length));                                                                                  // a negative one
        BinaryPrimitives.WriteInt32LittleEndian(length.AsSpan(MuxFrames.SessionIdBytes + 9), MuxFrames.MaxFileTransferPathBytes + 1);
        Assert.False(Parses(length));                                                                                  // past the path cap

        byte[] trailing = Payload(MuxFrames.FileTransfer(Id, 1, "/tmp/a", [], hasData: false)).Concat(new byte[] { 1 }).ToArray();
        Assert.False(Parses(trailing));                                                                                // an unavailable read carries no bytes

        byte[] notUtf8 = (byte[])good.Clone(); notUtf8[MuxFrames.FileTransferHeaderBytes] = 0xC0; notUtf8[MuxFrames.FileTransferHeaderBytes + 1] = 0xAF;
        Assert.False(Parses(notUtf8));                                                                                 // an overlong, invalid UTF-8 path
    }

    [Fact]
    public void A_path_or_file_past_what_a_frame_carries_cannot_be_built()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MuxFrames.FileTransfer(Id, 0, new string('a', MuxFrames.MaxFileTransferPathBytes + 1), [], hasData: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => MuxFrames.FileTransfer(Id, 0, "/p", new byte[MuxFrames.MaxFileTransferBytes + 1], hasData: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => MuxFrames.FileTransfer(Id, 0, "/p", [1], hasData: false));    // bytes on an unavailable read
        Assert.True(MuxFrames.FileTransferHeaderBytes + MuxFrames.MaxFileTransferPathBytes + MuxFrames.MaxFileTransferBytes <= MuxProtocol.MaxFrameBytes);
        MuxOutboundFrame longest = MuxFrames.FileTransfer(Id, 0, new string('é', MuxFrames.MaxFileTransferPathBytes / 2), [], hasData: true);
        Assert.Equal(MuxFrames.MaxFileTransferPathBytes, Encoding.UTF8.GetByteCount(new string('é', MuxFrames.MaxFileTransferPathBytes / 2)));
        longest.Release();
    }
}
