using System.Text.Json;
using Ntilde.Replay;
using Ntilde.VT;

namespace Ntilde.Tests.ReplayTests;

[Trait("Category", "Replay")]
public sealed class SixelRasterPolicyReplayTests
{
    [Theory]
    [InlineData("\"ntilderec\"", "\"unknown\"", false)]
    [InlineData("\"ntilderec\"", "\"unknown\"", true)]
    [InlineData("\"novarec\"", "\"unknown\"", false)]
    [InlineData("\"novarec\"", "\"unknown\"", true)]
    [InlineData("\"ntilderec\"", "null", false)]
    [InlineData("\"ntilderec\"", "null", true)]
    [InlineData("\"novarec\"", "null", false)]
    [InlineData("\"novarec\"", "null", true)]
    [InlineData("\"ntilderec\"", "7", false)]
    [InlineData("\"ntilderec\"", "7", true)]
    [InlineData("\"novarec\"", "7", false)]
    [InlineData("\"novarec\"", "7", true)]
    [InlineData("\"unknown\"", "\"ntilderec\"", false)]
    [InlineData("\"unknown\"", "\"novarec\"", true)]
    [InlineData("\"ntilderec\"", "\"ntilderec\"", false)]
    public async Task Duplicate_header_type_cannot_hide_a_required_policy_or_bypass_the_data_boundary(string firstType, string lastType, bool byteResume)
    {
        string path = Path.GetTempFileName();
        try
        {
            string header = $"{{\"type\":{firstType},\"type\":{lastType},\"v\":2,\"required_sixel_raster\":1,\"t\":0,\"d\":\"WA==\"}}";
            await File.WriteAllTextAsync(path, header + "\n{\"t\":0,\"type\":\"data\",\"d\":\"WA==\"}\n", TestContext.Current.CancellationToken);
            int calls = 0;
            Task Observed() { calls++; return Task.CompletedTask; }
            await Assert.ThrowsAsync<InvalidDataException>(() => new ReplayRunner(path).RunAsync(
                onDataCallback: _ => Observed(), onHeaderCallback: _ => Observed(), onResizeCallback: (_, _) => Observed(),
                startOffsetBytes: byteResume ? System.Text.Encoding.UTF8.GetByteCount(header) + 1 : 0,
                ct: TestContext.Current.CancellationToken));
            Assert.Equal(0, calls);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("\"1\"")]
    [InlineData("2147483648")]
    [InlineData("0.5")]
    [InlineData("null")]
    [InlineData("false")]
    [InlineData("0,\"required_sixel_raster\":1")]
    [InlineData("1,\"required_sixel_raster\":0")]
    public async Task Malformed_required_policy_cannot_fall_back_to_a_legacy_data_record(string value)
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, $"{{\"type\":\"ntilderec\",\"v\":2,\"required_sixel_raster\":{value},\"t\":0,\"d\":\"WA==\"}}\n", TestContext.Current.CancellationToken);
            int calls = 0;
            await Assert.ThrowsAsync<InvalidDataException>(() => new ReplayRunner(path).RunAsync(_ => { calls++; return Task.CompletedTask; }, ct: TestContext.Current.CancellationToken));
            Assert.Equal(0, calls);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(-1, 0, false)]
    [InlineData(1, 0, false)]
    [InlineData(int.MaxValue, 0, false)]
    [InlineData(1, 20, false)]
    [InlineData(1, 0, true)]
    public async Task Required_policy_is_rejected_before_header_resize_skipped_data_or_bytes(int policy, long startLine, bool byteResume)
    {
        string path = Path.GetTempFileName();
        try
        {
            var header = new ReplayHeader { Type = ReplayHeader.TypeToken, Cols = 20, Rows = 8, RequiredSixelRasterPolicy = policy };
            string first = JsonSerializer.Serialize(header, ReplayJsonContext.Default.ReplayHeader);
            await File.WriteAllTextAsync(path, first + "\n{\"t\":0,\"type\":\"data\",\"d\":\"WA==\"}\n", TestContext.Current.CancellationToken);
            int calls = 0;
            Task Observed() { calls++; return Task.CompletedTask; }
            await Assert.ThrowsAsync<InvalidDataException>(() => new ReplayRunner(path).RunWithResultAsync(
                onDataCallback: _ => Observed(), onResizeCallback: (_, _) => Observed(),
                onHeaderCallback: _ => Observed(),
                options: new ReplayRunOptions { StartAfterLineNumber = startLine },
                startOffsetBytes: byteResume ? System.Text.Encoding.UTF8.GetByteCount(first) + 1 : 0,
                ct: TestContext.Current.CancellationToken));
            Assert.Equal(0, calls);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("ntilderec", false)]
    [InlineData("novarec", false)]
    [InlineData("ntilderec", true)]
    [InlineData("novarec", true)]
    public async Task Legacy_headers_with_absent_or_explicit_zero_policy_keep_replaying(string type, bool explicitZero)
    {
        string path = Path.GetTempFileName();
        try
        {
            string policy = explicitZero ? ",\"required_sixel_raster\":0" : "";
            await File.WriteAllTextAsync(path, $"{{\"type\":\"{type}\",\"v\":2,\"cols\":10,\"rows\":3{policy}}}\n{{\"t\":0,\"type\":\"data\",\"d\":\"WA==\"}}\n", TestContext.Current.CancellationToken);
            var received = new List<byte>();
            await new ReplayRunner(path).RunAsync(data => { received.AddRange(data); return Task.CompletedTask; }, ct: TestContext.Current.CancellationToken);
            Assert.Equal(new byte[] { (byte)'X' }, received);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Version_three_is_not_enabled_by_the_foundation()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "{\"type\":\"ntilderec\",\"v\":3,\"required_sixel_raster\":1}\n{\"t\":0,\"type\":\"data\",\"d\":\"WA==\"}\n", TestContext.Current.CancellationToken);
            int calls = 0;
            await Assert.ThrowsAsync<InvalidDataException>(() => new ReplayRunner(path).RunAsync(_ => { calls++; return Task.CompletedTask; }, ct: TestContext.Current.CancellationToken));
            Assert.Equal(0, calls);
        }
        finally { File.Delete(path); }
    }
}
