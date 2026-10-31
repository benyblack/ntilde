using System.Text;
using System.Text.Json;
using Ntilde.Replay;

namespace Ntilde.Platform.Tests.Replay;

public sealed class RecordingPrivacyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Regression")]
    public async Task New_recordings_preserve_output_and_resizes_but_never_input(bool useWriter)
    {
        string path = Path.GetTempFileName();
        const string secret = "synthetic-password-671\r";
        byte[] output = Encoding.UTF8.GetBytes("Password: \r\nauthenticated\r\n");
        try
        {
            using (PtyRecorder recorder = useWriter ? new ReplayWriter(path, 80, 24) : new PtyRecorder(path, 80, 24))
            {
                recorder.RecordInput(secret);
                recorder.RecordChunk(output, output.Length);
                recorder.RecordResize(100, 30);
            }

            string recording = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(secret.TrimEnd('\r'), recording, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)), recording, StringComparison.Ordinal);
            foreach (string line in File.ReadLines(path))
            {
                using JsonDocument document = JsonDocument.Parse(line);
                Assert.NotEqual("input", document.RootElement.GetProperty("type").GetString());
            }

            var captured = new List<byte>();
            (int Cols, int Rows) resized = default;
            await new ReplayRunner(path).RunAsync(
                onDataCallback: data => { captured.AddRange(data); return Task.CompletedTask; },
                onResizeCallback: (cols, rows) => { resized = (cols, rows); return Task.CompletedTask; },
                ct: TestContext.Current.CancellationToken);
            Assert.Equal(output, captured);
            Assert.Equal((100, 30), resized);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_input_events_remain_readable(bool useBase64)
    {
        string path = Path.GetTempFileName();
        try
        {
            // Old v2 files may have a base64 payload or only the legacy plaintext field.
            string input = useBase64
                ? "{\"t\":0,\"type\":\"input\",\"d\":\"bHMNCg==\",\"i\":\"ignored\"}"
                : "{\"t\":0,\"type\":\"input\",\"i\":\"ls\\r\\n\"}";
            await File.WriteAllLinesAsync(path,
                ["{\"type\":\"novarec\",\"v\":2,\"cols\":80,\"rows\":24}", input],
                TestContext.Current.CancellationToken);
            var captured = new StringBuilder();
            await new ReplayRunner(path).RunAsync(
                onDataCallback: _ => Task.CompletedTask,
                onInputCallback: text => { captured.Append(text); return Task.CompletedTask; },
                ct: TestContext.Current.CancellationToken);
            Assert.Equal("ls\r\n", captured.ToString());
        }
        finally { File.Delete(path); }
    }
}
