using System.Text.Json;
using Xunit;

namespace Ntilde.Rendering.Tests;

public sealed class RenderPerfWriterFlushTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ntilde-renderperf-flush", Guid.NewGuid().ToString("N"));

    public RenderPerfWriterFlushTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static RenderPerfMetrics Frame(long i) => new()
    {
        FrameIndex = i,
        FrameTimeMs = 1.25,
        DirtyRows = 50,
        DirtyCellsEstimated = 10200,
        AllocBytesThisFrame = 1_000_000,
        Backend = "GPU/OpenGL"
    };

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }

    // A frame line is ~400 bytes, so 30 frames overflow a 4 KB stream buffer before the
    // 60-frame flush threshold. The file must never end mid-line while the app is running:
    // an unflushed tail is fine, half a JSON object is not.
    [Fact]
    public void BeforeFlushThreshold_FileNeverHoldsAPartialLine()
    {
        string path = Path.Combine(_dir, "partial.jsonl");
        using RenderPerfWriter? writer = RenderPerfWriter.Create(path);
        Assert.NotNull(writer);

        for (int i = 1; i <= 30; i++)
        {
            writer!.TryWrite(Frame(i));

            string content = ReadShared(path);
            Assert.True(content.Length == 0 || content.EndsWith('\n'), $"file ends mid-line after frame {i}");
        }
    }

    // The app never disposes the shared writer, so a normal exit has to flush it.
    [Fact]
    public void ProcessExit_FlushesUnwrittenFrames()
    {
        string path = Path.Combine(_dir, "exit.jsonl");
        RenderPerfWriter? writer = RenderPerfWriter.Create(path);
        Assert.NotNull(writer);

        for (int i = 1; i <= 7; i++)
        {
            writer!.TryWrite(Frame(i));
        }

        writer!.OnProcessExit(null, EventArgs.Empty);

        string[] lines = File.ReadAllLines(path);
        Assert.Equal(7, lines.Length);
        Assert.All(lines, l => JsonDocument.Parse(l).Dispose());
    }

    [Fact]
    public void Dispose_WritesEveryFrameAsACompleteLine()
    {
        string path = Path.Combine(_dir, "dispose.jsonl");
        using (RenderPerfWriter? writer = RenderPerfWriter.Create(path))
        {
            for (int i = 1; i <= 75; i++)
            {
                writer!.TryWrite(Frame(i));
            }
        }

        string[] lines = File.ReadAllLines(path);
        Assert.Equal(75, lines.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            using JsonDocument doc = JsonDocument.Parse(lines[i]);
            Assert.Equal(i + 1, doc.RootElement.GetProperty("FrameIndex").GetInt64());
        }
    }
}
