using System;
using System.IO;
using Ntilde.Mux.Daemon;
using Ntilde.Shell;

namespace Ntilde.Tests.Core;

/// <summary>
/// Both log writers - the App's <c>debug.log</c> (<see cref="RotatingFileLogWriter"/>) and the daemon's
/// <c>mux.log</c> (<see cref="MuxLogFile"/>) - keep the previous run's log as the previous generation when they
/// start, instead of erasing it. The rule itself is <c>Ntilde.Mux.Tests</c>' <c>LogGenerationsTests</c>; this pins
/// that each writer applies it before it opens its fresh file.
/// </summary>
public class LogWritersKeepThePreviousRunTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ntilde-logwriters-tests", Guid.NewGuid().ToString("N"));

    public LogWritersKeepThePreviousRunTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* temp cleanup is best-effort */ }
        GC.SuppressFinalize(this);
    }

    public static TheoryData<string> Writers => new() { "debug.log", "mux.log" };

    private static (IDisposable Writer, Action<string> Write) Open(string name, string path) => name switch
    {
        "debug.log" => Wrap(new RotatingFileLogWriter(path, maxBytes: 1 << 20, queueCapacity: 16)),
        _ => Wrap(new MuxLogFile(path, maxBytes: 1 << 20, queueCapacity: 16)),
    };

    private static (IDisposable, Action<string>) Wrap(RotatingFileLogWriter writer) => (writer, writer.Write);

    private static (IDisposable, Action<string>) Wrap(MuxLogFile writer) => (writer, writer.Write);

    [Theory]
    [MemberData(nameof(Writers))]
    public void A_new_run_keeps_the_previous_runs_log_and_starts_its_own(string name)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, "the previous run\n");

        (IDisposable writer, Action<string> write) = Open(name, path);
        using (writer)
        {
            write("this run");
        }

        Assert.Equal("the previous run\n", File.ReadAllText(LogGenerations.PreviousOf(path)));
        string live = File.ReadAllText(path);
        Assert.Contains("this run", live);
        Assert.DoesNotContain("the previous run", live);
    }
}
