using Ntilde.Mux.Contracts;
using Ntilde.Mux.Transport;
using Ntilde.VT;

namespace Ntilde.Mux.Tests.Support;

internal sealed class FakeMuxServerEnd : IDisposable
{
    private FakeMuxServerEnd(Stream clientEnd, Stream serverEnd)
    {
        ClientEnd = clientEnd;
        Raw = new RawMuxConnection(serverEnd);
    }

    public Stream ClientEnd { get; }
    public RawMuxConnection Raw { get; }

    public static FakeMuxServerEnd Create()
    {
        (Stream client, Stream server) = InMemoryDuplexPipe.Create(1 << 20);
        return new FakeMuxServerEnd(client, server);
    }

    public async Task<MuxRequest> ReadRequestAsync()
    {
        using MuxInboundFrame frame = await Raw.ReadAsync() ?? throw new EndOfStreamException();
        Assert.Equal(MuxFrameKind.Request, frame.Kind);
        return MuxFrames.ParseJson(frame.Payload, MuxJsonContext.Default.MuxRequest);
    }

    public void Reply<T>(long id, T result, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        Raw.Send(MuxFrames.Response(new MuxResponse { Id = id, Result = MuxFrames.ToElement(result, info) }));

    /// <summary>Answers the client's hello with <paramref name="version"/>.</summary>
    public async Task AcceptHelloAsync(int version = 1)
    {
        MuxRequest hello = await ReadRequestAsync();
        Reply(hello.Id, new WelcomeResult { Version = version }, MuxJsonContext.Default.WelcomeResult);
    }

    public static byte[] SnapshotJson(string screen = "", long streamSeq = 0, byte[]? tail = null)
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = null };
        parser.Process(screen);
        TerminalStateSnapshot s = buffer.ExportState(100);
        s.Parser = parser.ExportState();
        s.DecoderTail = tail ?? [];
        s.StreamSeq = streamSeq;
        return TerminalStateSerializer.ToBytes(s);
    }

    public void Dispose()
    {
        Raw.Dispose();
        ClientEnd.Dispose();
    }
}
