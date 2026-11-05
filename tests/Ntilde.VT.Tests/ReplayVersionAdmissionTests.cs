using Ntilde.Replay;

namespace Ntilde.VT.Tests;

[Trait("Category", "Replay")]
public sealed class ReplayVersionAdmissionTests
{
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
