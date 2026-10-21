using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Ntilde.Shell;

namespace Ntilde.Tests.Core;

/// <summary>
/// The debug log's sink. Covers what the previous <c>File.AppendAllText</c>-per-call version got
/// wrong: it wrote synchronously on the producer's thread, and it grew without limit for as long
/// as the process lived, so a long-lived SSH tab got slower and the file reached gigabytes.
/// </summary>
public class RotatingFileLogWriterTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "ntilde-logwriter-tests", Guid.NewGuid().ToString("N"), "debug.log");

    private static void Cleanup(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetDirectoryName(path));
            if (dir != null && Directory.Exists(dir)) { /* leaf only */ }
            string? leaf = Path.GetDirectoryName(path);
            if (leaf != null && Directory.Exists(leaf)) Directory.Delete(leaf, recursive: true);
        }
        catch
        {
            // Temp cleanup is best-effort.
        }
    }

    /// <summary>Reads the file while the writer still holds it open (FileShare.ReadWrite).</summary>
    private static string ReadAll(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void QueuedMessages_ReachTheFile()
    {
        string path = TempPath();
        try
        {
            using (var writer = new RotatingFileLogWriter(path, maxBytes: 1 << 20, queueCapacity: 1024))
            {
                writer.Write("first");
                writer.Write("second");
            } // Dispose drains and flushes.

            string contents = ReadAll(path);
            Assert.Contains("first", contents);
            Assert.Contains("second", contents);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Dispose_FlushesWhatIsStillBuffered()
    {
        // The whole point of the background thread is that Write returns before the disk does.
        // If Dispose did not drain, the last lines before a crash — the ones worth having — would
        // be the ones lost.
        string path = TempPath();
        try
        {
            using (var writer = new RotatingFileLogWriter(path, maxBytes: 1 << 20, queueCapacity: 1024))
            {
                for (int i = 0; i < 500; i++) writer.Write($"line {i}");
            }

            string contents = ReadAll(path);
            Assert.Contains("line 0", contents);
            Assert.Contains("line 499", contents);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void TheFileIsBounded_AndOnePreviousGenerationIsKept()
    {
        string path = TempPath();
        try
        {
            const long maxBytes = 4096;
            using (var writer = new RotatingFileLogWriter(path, maxBytes, queueCapacity: 4096))
            {
                // Comfortably more than one generation's worth.
                for (int i = 0; i < 2000; i++) writer.Write(new string('x', 100));
            }

            long live = new FileInfo(path).Length;

            // The cap is enforced per generation, not across the run: this is what stops a
            // 24-hour session from writing gigabytes.
            Assert.True(live < maxBytes * 2, $"live generation was {live} bytes, cap {maxBytes}");
            Assert.True(File.Exists(path + ".1"), "the previous generation should be retained");
        }
        finally
        {
            Cleanup(path);
            try { File.Delete(path + ".1"); } catch { }
        }
    }

    [Fact]
    public void RotationIsRecordedInTheLog()
    {
        string path = TempPath();
        try
        {
            using (var writer = new RotatingFileLogWriter(path, maxBytes: 2048, queueCapacity: 4096))
            {
                for (int i = 0; i < 500; i++) writer.Write(new string('y', 100));
            }

            // A log that silently discards its own history is a log that cannot be reasoned about.
            Assert.Contains("rotated", ReadAll(path));
        }
        finally
        {
            Cleanup(path);
            try { File.Delete(path + ".1"); } catch { }
        }
    }

    [Fact]
    public void AFullQueue_DropsRatherThanBlocking()
    {
        string path = TempPath();
        try
        {
            // Capacity 1 with a producer far faster than the disk: drops are expected, a stall is
            // not. Write is reached from the PTY read thread, where blocking is the original bug.
            using var writer = new RotatingFileLogWriter(path, maxBytes: 1 << 20, queueCapacity: 1);

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 20000; i++) writer.Write($"flood {i}");
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
                $"20k writes took {sw.Elapsed.TotalSeconds:F1}s — Write is blocking on the disk");
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ConcurrentProducers_AreSafe()
    {
        // The real callers are the PTY read thread, the UI thread and the render thread at once.
        string path = TempPath();
        try
        {
            using (var writer = new RotatingFileLogWriter(path, maxBytes: 1 << 20, queueCapacity: 8192))
            {
                var threads = Enumerable.Range(0, 8).Select(t => new Thread(() =>
                {
                    for (int i = 0; i < 200; i++) writer.Write($"t{t} line {i}");
                })).ToArray();

                foreach (var t in threads) t.Start();
                foreach (var t in threads) t.Join();
            }

            string contents = ReadAll(path);
            // Not a count assertion: capacity 8192 is above 1600 so nothing should drop, but the
            // property under test is that concurrent writers neither throw nor corrupt a line.
            Assert.Contains("t0 line 0", contents);
            Assert.Contains("t7 line 199", contents);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void AnUnwritablePath_DegradesToANoOp()
    {
        // A diagnostic must never be able to fail the app — construction included, because this
        // runs inside AppLogger's static initializer, where an escaping exception becomes a
        // TypeInitializationException on the first log call anywhere in the process. The directory
        // is created for us, so an unusable path is the interesting case: a file where a directory
        // has to go.
        string blocker = Path.Combine(Path.GetTempPath(), "ntilde-logwriter-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "not a directory");
        try
        {
            string path = Path.Combine(blocker, "debug.log");

            Exception? thrown = Record.Exception(() =>
            {
                using var writer = new RotatingFileLogWriter(path, maxBytes: 1024, queueCapacity: 16);
                writer.Write("this goes nowhere, quietly");
            });

            Assert.Null(thrown);

            // And it really is a no-op: nothing was created at or under the blocked path.
            Assert.False(Directory.Exists(blocker));
            Assert.False(File.Exists(path));
            Assert.Equal("not a directory", File.ReadAllText(blocker));
        }
        finally
        {
            try { File.Delete(blocker); } catch (IOException) { /* temp cleanup is best-effort */ }
        }
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        string path = TempPath();
        try
        {
            var writer = new RotatingFileLogWriter(path, maxBytes: 1 << 20, queueCapacity: 16);
            writer.Write("once");

            Exception? thrown = Record.Exception(() =>
            {
                writer.Dispose();
                writer.Dispose();
                writer.Write("after dispose");
            });

            Assert.Null(thrown);

            // The first Dispose still drained, and the post-dispose write was swallowed rather
            // than resurrecting the writer or throwing.
            string contents = ReadAll(path);
            Assert.Contains("once", contents);
            Assert.DoesNotContain("after dispose", contents);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ANewRun_KeepsThePreviousRunsLog_AsThePreviousGeneration()
    {
        // Each run used to open the file with FileMode.Create, erasing the run before it - the one worth reading
        // after a failure. In the 0.12.0-rc.1 smoke test that lost the only record of why a multiplexer was gone.
        string path = TempPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".1", "an older run\n");
            File.WriteAllText(path, "the previous run\n");

            using (var writer = new RotatingFileLogWriter(path, maxBytes: 1 << 20, queueCapacity: 16))
            {
                writer.Write("this run");
            }

            Assert.Equal("the previous run\n", File.ReadAllText(path + ".1"));
            string live = ReadAll(path);
            Assert.Contains("this run", live);
            Assert.DoesNotContain("the previous run", live);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void AnEmptyPreviousLog_LeavesThePreviousGenerationAlone()
    {
        // A run that ended before its first flush leaves an empty file. Moving it would push out the last log
        // that says anything.
        string path = TempPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".1", "the last run that logged\n");
            File.WriteAllText(path, string.Empty);

            using (var writer = new RotatingFileLogWriter(path, maxBytes: 1 << 20, queueCapacity: 16))
            {
                writer.Write("this run");
            }

            Assert.Equal("the last run that logged\n", File.ReadAllText(path + ".1"));
            Assert.Contains("this run", ReadAll(path));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ALogAnotherWriterHoldsOpen_IsNotMovedOutFromUnderIt()
    {
        // A second instance opens the file while the first still writes it. Moving it would leave the first
        // logging into the previous generation, which the next run deletes. The stand-in allows Delete sharing,
        // so on Windows too a move would go through: only the in-use check keeps the file where it is.
        string path = TempPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".1", "an older run\n");
            using (var running = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                running.Write("the running instance\n"u8);
                running.Flush();

                using var writer = new RotatingFileLogWriter(path, maxBytes: 1 << 20, queueCapacity: 16);
                writer.Write("a second instance");
            }

            Assert.Equal("an older run\n", File.ReadAllText(path + ".1"));
        }
        finally { Cleanup(path); }
    }
}
