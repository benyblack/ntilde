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

    /// <summary>
    /// Codex E1 (Phase 4 spec §3), additive over the old shape: a spawn names its session's id only when the caller
    /// chose one. A reader skips members it does not know rather than refusing them, which is how a daemon built
    /// before the member reads a spawn that carries it.
    /// </summary>
    [Fact]
    public void Spawn_params_name_a_session_id_only_when_one_is_chosen()
    {
        var plain = new SpawnParams { Command = "pwsh", Arguments = "-NoLogo", Cols = 100, Rows = 30, Title = "work" };
        string plainJson = System.Text.Json.JsonSerializer.Serialize(plain, MuxJsonContext.Default.SpawnParams);
        Assert.DoesNotContain("sessionId", plainJson, StringComparison.Ordinal);
        Assert.Equal(plain, System.Text.Json.JsonSerializer.Deserialize(plainJson, MuxJsonContext.Default.SpawnParams));

        var named = plain with { SessionId = new Guid("00000000-0000-0000-0000-000000000004").ToString("D") };
        string namedJson = System.Text.Json.JsonSerializer.Serialize(named, MuxJsonContext.Default.SpawnParams);
        Assert.Contains("\"sessionId\":\"00000000-0000-0000-0000-000000000004\"", namedJson, StringComparison.Ordinal);
        Assert.Equal(named, System.Text.Json.JsonSerializer.Deserialize(namedJson, MuxJsonContext.Default.SpawnParams));

        SpawnParams future = MuxFrames.ParseParams(
            MuxFrames.ParseJson("{\"id\":1,\"method\":\"spawn\",\"params\":{\"command\":\"pwsh\",\"cols\":100,\"rows\":30,\"aMemberFromLater\":{\"x\":[1,2]},\"anotherOne\":\"y\"}}"u8,
                MuxJsonContext.Default.MuxRequest).Params,
            MuxJsonContext.Default.SpawnParams);
        Assert.Equal(("pwsh", 100, 30, (string?)null), (future.Command, future.Cols, future.Rows, future.SessionId));
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

    [Fact]
    public void The_protocol_range_is_1_to_2()
    {
        Assert.Equal(1, MuxProtocol.MinSupportedVersion);
        Assert.Equal(2, MuxProtocol.MaxSupportedVersion);
        Assert.Equal(2, MuxProtocol.SessionEventsVersion);
    }

    [Fact]
    public void A_shared_attach_keeps_the_v1_wire_shape()
    {
        var p = new AttachParams
        {
            SessionId = new Guid("00000000-0000-0000-0000-000000000003"),
            MaxScrollbackRows = 10,
            Presentation = new MuxPresentation { Cols = 80, Rows = 24 },
            Mode = MuxAttachModes.ToWire(MuxAttachMode.Shared),
        };

        string json = System.Text.Json.JsonSerializer.Serialize(p, MuxJsonContext.Default.AttachParams);

        Assert.DoesNotContain("\"mode\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MuxAttachMode.IfUnattached, "ifUnattached")]
    [InlineData(MuxAttachMode.ReadOnly, "readOnly")]
    public void Non_shared_modes_travel_as_strings(MuxAttachMode mode, string wire)
    {
        var p = new AttachParams { SessionId = Guid.NewGuid(), Presentation = new MuxPresentation { Cols = 80, Rows = 24 }, Mode = MuxAttachModes.ToWire(mode) };

        string json = System.Text.Json.JsonSerializer.Serialize(p, MuxJsonContext.Default.AttachParams);
        AttachParams back = System.Text.Json.JsonSerializer.Deserialize(json, MuxJsonContext.Default.AttachParams)!;

        Assert.Contains($"\"mode\":\"{wire}\"", json, StringComparison.Ordinal);
        Assert.True(MuxAttachModes.TryParse(back.Mode, out MuxAttachMode parsed));
        Assert.Equal(mode, parsed);
    }

    [Theory]
    [InlineData(null, true, MuxAttachMode.Shared)]
    [InlineData("shared", true, MuxAttachMode.Shared)]
    [InlineData("ifUnattached", true, MuxAttachMode.IfUnattached)]
    [InlineData("readOnly", true, MuxAttachMode.ReadOnly)]
    [InlineData("ReadOnly", false, MuxAttachMode.Shared)]   // exact, like every other wire string
    [InlineData("bogus", false, MuxAttachMode.Shared)]
    public void Attach_modes_parse_exactly(string? wire, bool ok, MuxAttachMode expected)
    {
        Assert.Equal(ok, MuxAttachModes.TryParse(wire, out MuxAttachMode mode));
        Assert.Equal(expected, mode);
    }

    [Fact]
    public void The_session_changed_notification_round_trips_with_camel_case_params()
    {
        var n = new SessionChangedNotification { SessionId = Guid.NewGuid(), AttachedClients = 3, Title = "vim", Cwd = "/tmp" };
        System.Text.Json.JsonElement e = MuxFrames.ToElement(n, MuxJsonContext.Default.SessionChangedNotification);

        Assert.Contains("\"attachedClients\":3", e.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(n, MuxFrames.ParseParams(e, MuxJsonContext.Default.SessionChangedNotification));
    }

    [Fact]
    public void The_killed_notification_round_trips_with_camel_case_params()
    {
        var n = new KilledNotification { SessionId = Guid.NewGuid(), ByClientKind = "ntilde-cli" };
        System.Text.Json.JsonElement e = MuxFrames.ToElement(n, MuxJsonContext.Default.KilledNotification);

        Assert.Contains("\"byClientKind\":\"ntilde-cli\"", e.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(n, MuxFrames.ParseParams(e, MuxJsonContext.Default.KilledNotification));
    }

    [Fact]
    public void A_v1_summary_and_session_info_parse_with_the_new_fields_absent()
    {
        SessionSummary s = System.Text.Json.JsonSerializer.Deserialize(
            "{\"sessionId\":\"00000000-0000-0000-0000-000000000004\",\"title\":\"t\",\"command\":\"pwsh\",\"cols\":80,\"rows\":24,\"running\":true,\"attachedClients\":1,\"faulted\":false}",
            MuxJsonContext.Default.SessionSummary)!;
        SessionInfoResult i = System.Text.Json.JsonSerializer.Deserialize(
            "{\"running\":true,\"hasActiveChildProcesses\":false}", MuxJsonContext.Default.SessionInfoResult)!;

        Assert.Null(s.Cwd);
        Assert.Null(i.Title);
        Assert.Null(i.Cwd);
        Assert.Null(i.AttachedClients);
    }

    [Fact]
    public void A_user_detach_adds_one_optional_member_and_a_plain_detach_keeps_the_old_shape()
    {
        Guid id = new("00000000-0000-0000-0000-000000000005");

        string plain = System.Text.Json.JsonSerializer.Serialize(new DetachParams { SessionId = id }, MuxJsonContext.Default.DetachParams);
        string user = System.Text.Json.JsonSerializer.Serialize(new DetachParams { SessionId = id, UserDetached = true }, MuxJsonContext.Default.DetachParams);

        Assert.DoesNotContain("userDetached", plain, StringComparison.Ordinal);
        Assert.Equal("{\"sessionId\":\"00000000-0000-0000-0000-000000000005\",\"userDetached\":true}", user);
        DetachParams back = System.Text.Json.JsonSerializer.Deserialize(user, MuxJsonContext.Default.DetachParams)!;
        Assert.Equal((id, (long?)null, (bool?)true), (back.SessionId, back.AttachRequestId, back.UserDetached)); // round-trips; a v1 server's DetachParams has no such member and skips it
    }

    [Fact]
    public void InteractiveClients_round_trips_and_zero_is_never_written()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new SessionSummary { SessionId = Guid.NewGuid(), AttachedClients = 3, InteractiveClients = 2 }, MuxJsonContext.Default.SessionSummary);

        Assert.Contains("\"interactiveClients\":2", json, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.Json.JsonSerializer.Deserialize(json, MuxJsonContext.Default.SessionSummary)!.InteractiveClients);
        Assert.DoesNotContain("interactiveClients", System.Text.Json.JsonSerializer.Serialize(new SessionSummary { SessionId = Guid.NewGuid(), AttachedClients = 1 }, MuxJsonContext.Default.SessionSummary), StringComparison.Ordinal); // the v1 shape
    }

    [Fact]
    public void DetachedByUser_round_trips_and_defaults_to_false()
    {
        var s = new SessionSummary { SessionId = Guid.NewGuid(), DetachedByUser = true };
        string json = System.Text.Json.JsonSerializer.Serialize(s, MuxJsonContext.Default.SessionSummary);

        Assert.Contains("\"detachedByUser\":true", json, StringComparison.Ordinal);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize(json, MuxJsonContext.Default.SessionSummary)!.DetachedByUser);
        Assert.False(System.Text.Json.JsonSerializer.Deserialize("{\"sessionId\":\"00000000-0000-0000-0000-000000000006\"}", MuxJsonContext.Default.SessionSummary)!.DetachedByUser);
        Assert.DoesNotContain("detachedByUser", System.Text.Json.JsonSerializer.Serialize(new SessionSummary { SessionId = Guid.NewGuid() }, MuxJsonContext.Default.SessionSummary), StringComparison.Ordinal); // false is never written
    }
}
