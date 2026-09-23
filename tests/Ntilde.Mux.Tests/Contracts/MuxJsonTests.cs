using System.Text;
using Ntilde.Mux.Contracts;

namespace Ntilde.Mux.Tests.Contracts;

public sealed class MuxJsonTests
{
    [Fact]
    public void Requests_round_trip_with_camel_case_params()
    {
        var attach = new AttachParams
        {
            SessionId = Guid.NewGuid(),
            MaxScrollbackRows = 500,
            Presentation = new MuxPresentation { Cols = 80, Rows = 24, CellWidthPx = 9, CellHeightPx = 18, DefaultBg = 0xFF102030 },
        };
        MuxOutboundFrame frame = MuxFrames.Request(new MuxRequest
        {
            Id = 3,
            Method = MuxMethods.Attach,
            Params = MuxFrames.ToElement(attach, MuxJsonContext.Default.AttachParams),
        });
        byte[] payload = frame.Bytes[MuxProtocol.FrameHeaderBytes..].ToArray();
        frame.Release();

        string json = Encoding.UTF8.GetString(payload);
        Assert.Contains("\"maxScrollbackRows\":500", json, StringComparison.Ordinal);
        Assert.Contains("\"cellWidthPx\":9", json, StringComparison.Ordinal);

        MuxRequest request = MuxFrames.ParseJson(payload, MuxJsonContext.Default.MuxRequest);
        AttachParams back = MuxFrames.ParseParams(request.Params, MuxJsonContext.Default.AttachParams);
        Assert.Equal(attach, back);
    }

    [Fact]
    public void The_faulted_notification_round_trips_with_camel_case_params()
    {
        var faulted = new FaultedNotification { SessionId = Guid.NewGuid(), Message = "The parser threw at stream offset 3." };
        MuxOutboundFrame frame = MuxFrames.Notification(new MuxNotification
        {
            Method = MuxMethods.Faulted,
            Params = MuxFrames.ToElement(faulted, MuxJsonContext.Default.FaultedNotification),
        });
        byte[] payload = frame.Bytes[MuxProtocol.FrameHeaderBytes..].ToArray();
        frame.Release();

        string json = Encoding.UTF8.GetString(payload);
        Assert.Contains("\"method\":\"faulted\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"sessionId\":\"{faulted.SessionId}\"", json, StringComparison.Ordinal);
        Assert.Contains("\"message\":", json, StringComparison.Ordinal);

        MuxNotification back = MuxFrames.ParseJson(payload, MuxJsonContext.Default.MuxNotification);
        Assert.Equal(faulted, MuxFrames.ParseParams(back.Params, MuxJsonContext.Default.FaultedNotification));
    }

    [Fact]
    public void A_faulted_notification_without_a_message_still_parses()
    {
        // Additive and tolerant: only sessionId is load-bearing.
        FaultedNotification parsed = MuxFrames.ParseParams(
            MuxFrames.ParseJson("{\"method\":\"faulted\",\"params\":{\"sessionId\":\"00000000-0000-0000-0000-000000000001\"}}"u8,
                MuxJsonContext.Default.MuxNotification).Params,
            MuxJsonContext.Default.FaultedNotification);
        Assert.Equal(new Guid("00000000-0000-0000-0000-000000000001"), parsed.SessionId);
        Assert.Null(parsed.Message);
    }

    [Fact]
    public void Detach_params_are_additive_over_the_session_id_shape()
    {
        Guid id = new("00000000-0000-0000-0000-000000000002");

        // A plain detach is byte-for-byte the old SessionIdParams shape ...
        string plain = System.Text.Json.JsonSerializer.Serialize(new DetachParams { SessionId = id }, MuxJsonContext.Default.DetachParams);
        string old = System.Text.Json.JsonSerializer.Serialize(new SessionIdParams { SessionId = id }, MuxJsonContext.Default.SessionIdParams);
        Assert.Equal(old, plain);

        // ... one undoing a specific attach adds a single optional member ...
        string named = System.Text.Json.JsonSerializer.Serialize(new DetachParams { SessionId = id, AttachRequestId = 42 }, MuxJsonContext.Default.DetachParams);
        Assert.Equal("{\"sessionId\":\"00000000-0000-0000-0000-000000000002\",\"attachRequestId\":42}", named);

        // ... and each side reads the other's shape (an old server simply ignores the new member).
        Assert.Equal(new DetachParams { SessionId = id }, System.Text.Json.JsonSerializer.Deserialize(old, MuxJsonContext.Default.DetachParams));
        Assert.Equal(new SessionIdParams { SessionId = id }, System.Text.Json.JsonSerializer.Deserialize(named, MuxJsonContext.Default.SessionIdParams));
    }

    [Fact]
    public void Malformed_json_is_a_protocol_error()
    {
        var ex = Assert.Throws<MuxProtocolException>(() =>
            MuxFrames.ParseJson("{nope"u8, MuxJsonContext.Default.MuxRequest));
        Assert.Equal(MuxErrorCodes.ProtocolError, ex.Code);
    }

    [Fact]
    public void A_missing_required_member_is_a_protocol_error()
    {
        MuxRequest request = MuxFrames.ParseJson(
            "{\"id\":1,\"method\":\"attach\",\"params\":{\"sessionId\":\"00000000-0000-0000-0000-000000000001\"}}"u8,
            MuxJsonContext.Default.MuxRequest);
        var ex = Assert.Throws<MuxProtocolException>(() =>
            MuxFrames.ParseParams(request.Params, MuxJsonContext.Default.AttachParams));
        Assert.Equal(MuxErrorCodes.ProtocolError, ex.Code);
    }

    [Fact]
    public void Missing_params_are_a_protocol_error()
    {
        var ex = Assert.Throws<MuxProtocolException>(() =>
            MuxFrames.ParseParams(null, MuxJsonContext.Default.SessionIdParams));
        Assert.Equal(MuxErrorCodes.ProtocolError, ex.Code);
    }
}
