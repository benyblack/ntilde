using System.Text.Json.Serialization.Metadata;
using Ntilde.Mux.Contracts;

namespace Ntilde.Mux.Tests.Support;

/// <summary>A hand-driven client: speaks raw frames so tests can misbehave on purpose.</summary>
internal sealed class RawMuxConnection : IDisposable
{
    private long _nextId;

    public RawMuxConnection(Stream stream) => Stream = stream;

    public Stream Stream { get; }

    public void Send(MuxOutboundFrame frame)
    {
        try { frame.WriteTo(Stream); }
        finally { frame.Release(); }
    }

    public long Request<T>(string method, T parameters, JsonTypeInfo<T> typeInfo, long? id = null)
    {
        long requestId = id ?? Interlocked.Increment(ref _nextId);
        Send(MuxFrames.Request(new MuxRequest { Id = requestId, Method = method, Params = MuxFrames.ToElement(parameters, typeInfo) }));
        return requestId;
    }

    public void WriteRaw(ReadOnlySpan<byte> bytes) => Stream.Write(bytes);

    public async Task<MuxInboundFrame?> ReadAsync(TimeSpan? timeout = null) =>
        await Task.Run(() => MuxFrameReader.Read(Stream), TestContext.Current.CancellationToken)
            .WaitAsync(timeout ?? TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    public async Task<MuxResponse> ReadResponseAsync()
    {
        using MuxInboundFrame frame = await ReadAsync() ?? throw new EndOfStreamException("The server closed the connection.");
        Assert.Equal(MuxFrameKind.Response, frame.Kind);
        return MuxFrames.ParseJson(frame.Payload, MuxJsonContext.Default.MuxResponse);
    }

    public async Task<WelcomeResult> HelloAsync(int min = 1, int max = 1)
    {
        Request(MuxMethods.Hello, new HelloParams { MinVersion = min, MaxVersion = max, ClientKind = "raw" }, MuxJsonContext.Default.HelloParams);
        MuxResponse response = await ReadResponseAsync();
        Assert.Null(response.Error);
        return MuxFrames.ParseParams(response.Result, MuxJsonContext.Default.WelcomeResult);
    }

    /// <summary>True when the peer closes before sending another frame.</summary>
    public async Task<bool> IsClosedByPeerAsync()
    {
        try
        {
            using MuxInboundFrame? frame = await ReadAsync();
            return frame is null;
        }
        catch (IOException)
        {
            return true;
        }
    }

    public void Dispose() => Stream.Dispose();
}
