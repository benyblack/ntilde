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
