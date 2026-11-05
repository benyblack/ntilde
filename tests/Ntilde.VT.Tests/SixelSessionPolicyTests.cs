using System.Text;
using Ntilde.Replay;
using Ntilde.VT;

namespace Ntilde.VT.Tests;

public sealed class SixelSessionPolicyTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void Unsupported_creation_policy_cannot_allocate_a_buffer(int policy)
        => Assert.Throws<InvalidOperationException>(() => new TerminalBuffer(10, 3, policy));

    [Theory]
    [InlineData("\u001bc", true)]
    [InlineData("\x1b[!p", false)]
    [InlineData("\x1b[?1049h\u001bc", true)]
    public void Legacy_creation_policy_is_immutable_across_resets_and_restore(string reset, bool clearsScreen)
    {
        var buffer = new TerminalBuffer(10, 3, 0);
        var parser = new AnsiParser(buffer);
        parser.Process("before\u001b[?25l" + reset);
        Assert.Equal(clearsScreen ? "" : "before", BufferSnapshot.Capture(buffer).Lines[0]);
        Assert.True(buffer.Modes.IsCursorVisible);
        Assert.Equal(0, buffer.SixelRasterPolicyVersion);
        Assert.False(typeof(TerminalBuffer).GetProperty(nameof(TerminalBuffer.SixelRasterPolicyVersion))!.CanWrite);
        var state = buffer.ExportState(0);
        string json = Encoding.UTF8.GetString(TerminalStateSerializer.ToBytes(state));
        Assert.DoesNotContain("required_sixel_raster", json);
        var explicitZero = TerminalStateSerializer.FromBytes(Encoding.UTF8.GetBytes("{\"required_sixel_raster\":0," + json[1..]));
        Assert.Equal(TerminalStateSerializer.ToBytes(state), TerminalStateSerializer.ToBytes(explicitZero));
        TerminalStateTransfer.Restore(buffer, parser, explicitZero);
        Assert.Equal(0, buffer.SixelRasterPolicyVersion);
    }

    [Theory]
    [InlineData("1,\"required_sixel_raster\":0")]
    [InlineData("0,\"required_sixel_raster\":1")]
    [InlineData("0,\"required_sixel_raster\":0")]
    public void Duplicate_snapshot_policy_cannot_be_hidden_by_last_key_wins(string values)
    {
        string json = Encoding.UTF8.GetString(TerminalStateSerializer.ToBytes(new TerminalBuffer(10, 3).ExportState(0)));
        Assert.Throws<System.Text.Json.JsonException>(() => TerminalStateSerializer.FromBytes(
            Encoding.UTF8.GetBytes("{\"required_sixel_raster\":" + values + "," + json[1..])));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(1, false)]
    [InlineData(int.MaxValue, false)]
    [InlineData(-1, true)]
    [InlineData(1, true)]
    [InlineData(int.MaxValue, true)]
    public void Unsupported_snapshot_policy_fails_before_buffer_or_parser_mutation(int policy, bool complete)
    {
        var source = new TerminalBuffer(30, 8).ExportState(0);
        string json = Encoding.UTF8.GetString(TerminalStateSerializer.ToBytes(source));
        var snapshot = TerminalStateSerializer.FromBytes(Encoding.UTF8.GetBytes("{\"required_sixel_raster\":" + policy + "," + json[1..]));
        var target = new TerminalBuffer(10, 3);
        var parser = new AnsiParser(target);
        parser.Process("keep\x1b[?25l\x1b[31;");
        byte[] before = Pair(target, parser);
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (complete) TerminalStateTransfer.Restore(target, parser, snapshot);
            else target.ImportState(snapshot);
        });
        Assert.Equal(before, Pair(target, parser));
    }

    private static byte[] Pair(TerminalBuffer buffer, AnsiParser parser)
    {
        var state = buffer.ExportState(0);
        state.Parser = parser.ExportState();
        return TerminalStateSerializer.ToBytes(state);
    }
}
