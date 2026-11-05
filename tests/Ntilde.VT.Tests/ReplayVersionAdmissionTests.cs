using Ntilde.Replay;

namespace Ntilde.VT.Tests;

[Trait("Category", "Replay")]
public sealed class ReplayVersionAdmissionTests
{
    public static IEnumerable<object[]> TruncatedHeaders()
    {
        foreach (string header in new[]
        {
            "{\"type\":\"ntilderec\",\"v\":",
            "{\"type\":\"novarec\",\"v\":\"",
            "{\"v\":3,\"type\":\"ntilderec\"",
            "{\"\\u0074ype\":\"\\u006eovarec\",\"v\":",
            "{\"type\":\"ntilderec\" \"v\":3}"
        })
            foreach (int resume in new[] { 0, 1, 2, 3 })
                yield return new object[] { header, resume };
    }

    [Theory]
    [MemberData(nameof(TruncatedHeaders))]
    public async Task Complete_top_level_type_in_a_malformed_prefix_is_rejected_before_resume_or_preparation(string header, int resume)
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, header + "\n{\"t\":0,\"type\":\"data\",\"d\":\"WA==\"}\n", TestContext.Current.CancellationToken);
            int calls = 0;
            await Assert.ThrowsAsync<InvalidDataException>(() => new ReplayRunner(path).RunWithResultAsync(
                _ => { calls++; return Task.CompletedTask; },
                onHeaderCallback: _ => { calls++; return Task.CompletedTask; },
                options: new ReplayRunOptions
                {
                    StartAfterLineNumber = resume == 2 ? 2 : 0,
                    OnValidatedStart = _ => { calls++; return Task.FromResult(true); }
                },
                minTimeMs: resume == 1 ? 100 : 0,
                startOffsetBytes: resume == 3 ? System.Text.Encoding.UTF8.GetByteCount(header) + 1 : 0,
                ct: TestContext.Current.CancellationToken));
            Assert.Equal(0, calls);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("{\"meta\":{\"type\":\"ntilderec\"},\"t\":")]
    [InlineData("{\"note\":\"\\\"type\\\":\\\"ntilderec\\\"\",\"t\":")]
    [InlineData("{\"typewriter\":\"ntilderec\",\"t\":")]
    [InlineData("{\"type\":\"other\",\"t\":")]
    public async Task Malformed_legacy_prefix_with_a_nested_or_quoted_lookalike_keeps_byte_resume(string header)
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, header + "\n{\"t\":0,\"d\":\"WA==\"}\n", TestContext.Current.CancellationToken);
            var bytes = new List<byte>();
            await new ReplayRunner(path).RunAsync(data => { bytes.AddRange(data); return Task.CompletedTask; },
                startOffsetBytes: System.Text.Encoding.UTF8.GetByteCount(header) + 1, ct: TestContext.Current.CancellationToken);
            Assert.Equal(new byte[] { (byte)'X' }, bytes);
        }
        finally { File.Delete(path); }
    }

    public static IEnumerable<object[]> RejectedHeaders()
    {
        foreach (string type in new[] { "ntilderec", "novarec" })
            foreach (string version in new[] { "#missing", "3", "99", "0", "-1", "null", "\"2\"", "2,\"v\":3", "3,\"v\":2", "2,\"v\":2" })
                foreach (int resume in new[] { 0, 1, 2, 3 })
                    yield return new object[] { type, version, resume };
    }

    [Theory]
    [MemberData(nameof(RejectedHeaders))]
    public async Task Recognized_unsupported_or_ambiguous_version_never_falls_back_before_any_resume(string type, string version, int resume)
    {
        string path = Path.GetTempFileName();
        try
        {
            string versionField = version == "#missing" ? "" : $"\"v\":{version},";
            string header = $"{{\"type\":\"{type}\",{versionField}\"t\":0,\"d\":\"WA==\"}}";
            await File.WriteAllTextAsync(path, header + "\n{\"t\":0,\"type\":\"data\",\"d\":\"WA==\"}\n", TestContext.Current.CancellationToken);
            int calls = 0;
            Task Observe() { calls++; return Task.CompletedTask; }
            await Assert.ThrowsAsync<InvalidDataException>(() => new ReplayRunner(path).RunWithResultAsync(
                onDataCallback: _ => Observe(), onHeaderCallback: _ => Observe(), onResizeCallback: (_, _) => Observe(),
                onSixelCellSizeCallback: (_, _) => Observe(),
                options: new ReplayRunOptions
                {
                    StartAfterLineNumber = resume == 2 ? 2 : 0,
                    OnSkippedData = _ => calls++,
                    OnValidatedStart = _ => { calls++; return Task.FromResult(true); }
                },
                minTimeMs: resume == 1 ? 100 : 0,
                startOffsetBytes: resume == 3 ? System.Text.Encoding.UTF8.GetByteCount(header) + 1 : 0,
                ct: TestContext.Current.CancellationToken));
            Assert.Equal(0, calls);
        }
        finally { File.Delete(path); }
    }
}
