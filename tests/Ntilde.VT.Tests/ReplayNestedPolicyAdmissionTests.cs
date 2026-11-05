using System.Text.Json;
using Ntilde.Replay;
using Ntilde.VT;

namespace Ntilde.VT.Tests;

public sealed class ReplayNestedPolicyAdmissionTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Nested_required_semantics_are_rejected_before_complete_or_legacy_restore(bool futureState, bool runner, bool hiddenNative)
    {
        var snapshot = Snapshot(futureState, hiddenNative);
        var buffer = new TerminalBuffer(10, 3);
        var parser = new AnsiParser(buffer);
        parser.Process("preserve\u001b[?25l\u001b[31;");
        byte[] before = Pair(buffer, parser);
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (runner) ReplayRunner.ApplySnapshot(buffer, parser, snapshot);
            else buffer.ApplySnapshot(snapshot);
        });
        Assert.Equal(before, Pair(buffer, parser));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Nested_policy_is_rejected_before_snapshot_delivery(bool futureState, bool hiddenNative)
    {
        string line = JsonSerializer.Serialize(new ReplayEvent { Type = "snapshot", Snapshot = Snapshot(futureState, hiddenNative) }, ReplayJsonContext.Default.ReplayEvent);
        await WithFile(line, async path =>
        {
            int delivered = 0;
            await Assert.ThrowsAsync<InvalidOperationException>(() => new ReplayRunner(path).RunAsync(
                _ => { delivered++; return Task.CompletedTask; },
                onSnapshotCallback: _ => { delivered++; return Task.CompletedTask; }, ct: TestContext.Current.CancellationToken));
            Assert.Equal(0, delivered);
        });
    }

    [Theory]
    [InlineData("1,\"required_sixel_raster\":0")]
    [InlineData("0,\"required_sixel_raster\":1")]
    public async Task Duplicate_nested_requirement_is_refused_before_event_delivery(string values)
    {
        string line = JsonSerializer.Serialize(new ReplayEvent { Type = "snapshot", Snapshot = new ReplaySnapshot { Cols = 20, Rows = 8, State = new TerminalBuffer(20, 8).ExportState(0) } }, ReplayJsonContext.Default.ReplayEvent);
        line = line.Replace("\"state\":{", "\"state\":{\"required_sixel_raster\":" + values + ",", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(line, ReplayJsonContext.Default.ReplayEvent));
        await WithFile(line, async path =>
        {
            int delivered = 0;
            await Assert.ThrowsAsync<JsonException>(() => new ReplayRunner(path).RunAsync(
                _ => { delivered++; return Task.CompletedTask; },
                onSnapshotCallback: _ => { delivered++; return Task.CompletedTask; }, ct: TestContext.Current.CancellationToken));
            Assert.Equal(0, delivered);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Absent_or_zero_requirement_preserves_legacy_fallback_for_an_unknown_state_version(bool absentState)
    {
        var state = new TerminalBuffer(20, 8).ExportState(0);
        state.Version = 99;
        var snapshot = new ReplaySnapshot { Cols = 20, Rows = 8, State = absentState ? null : state, IsCursorVisible = true };
        var buffer = new TerminalBuffer(10, 3);
        ReplayRunner.ApplySnapshot(buffer, new AnsiParser(buffer), snapshot);
        Assert.Equal((20, 8), (buffer.Cols, buffer.Rows));
        Assert.Equal(0, buffer.SixelRasterPolicyVersion);
    }

    private static ReplaySnapshot Snapshot(bool futureState, bool hiddenNative)
    {
        var state = new TerminalBuffer(20, 8).ExportState(0);
        if (futureState) state.Version = 99;
        if (hiddenNative)
            state.Sixels = [new() { Source = "q~", Width = 1, Height = 1, NativeRaster = new(1, 1, 1, new(0, 0, 1, 1)) }];
        else state.RequiredSixelRasterPolicy = 1;
        return new ReplaySnapshot { Cols = 20, Rows = 8, State = state };
    }

    private static byte[] Pair(TerminalBuffer buffer, AnsiParser parser)
    {
        var state = buffer.ExportState(0);
        state.Parser = parser.ExportState();
        return TerminalStateSerializer.ToBytes(state);
    }

    private static async Task WithFile(string line, Func<string, Task> action)
    {
        string path = Path.GetTempFileName();
        try
        {
            string header = JsonSerializer.Serialize(new ReplayHeader { Type = ReplayHeader.TypeToken }, ReplayJsonContext.Default.ReplayHeader);
            await File.WriteAllTextAsync(path, header + "\n" + line + "\n{\"t\":0,\"type\":\"data\",\"d\":\"WA==\"}\n", TestContext.Current.CancellationToken);
            await action(path);
        }
        finally { File.Delete(path); }
    }
}
