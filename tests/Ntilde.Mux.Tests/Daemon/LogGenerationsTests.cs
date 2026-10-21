using System;
using System.IO;
using Ntilde.Mux.Daemon;

namespace Ntilde.Mux.Tests.Daemon;

/// <summary>
/// What a new run does with the previous run's log (<see cref="LogGenerations.KeepPreviousRun"/>), for the App's
/// <c>debug.log</c> and the daemon's <c>mux.log</c> alike. Every run used to erase the one before it: in the
/// 0.12.0-rc.1 smoke test that lost the only record of why a multiplexer had stopped. That both writers call it is
/// App.Tests' <c>LogWritersKeepThePreviousRunTests</c>.
/// </summary>
public class LogGenerationsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ntilde-loggenerations-tests", Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public LogGenerationsTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "mux.log");
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* temp cleanup is best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void The_previous_runs_log_becomes_the_previous_generation()
    {
        File.WriteAllText(_path + ".1", "an older run\n");
        File.WriteAllText(_path, "the previous run\n");

        LogGenerations.KeepPreviousRun(_path);

        Assert.Equal("the previous run\n", File.ReadAllText(_path + ".1"));
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void An_empty_log_leaves_the_previous_generation_alone()
    {
        // A run that ended before its first flush leaves an empty file. Moving it would push out the last log
        // that says anything.
        File.WriteAllText(_path + ".1", "the last run that logged\n");
        File.WriteAllText(_path, string.Empty);

        LogGenerations.KeepPreviousRun(_path);

        Assert.Equal("the last run that logged\n", File.ReadAllText(_path + ".1"));
    }

    [Fact]
    public void A_log_another_writer_holds_open_is_not_moved_out_from_under_it()
    {
        // A second `serve` opens mux.log before it finds the lock taken, and a second GUI instance opens debug.log,
        // while the first still writes it. Moving it would leave that writer logging into the previous generation,
        // which the next run deletes. The stand-in allows Delete sharing, so on Windows too a move would go
        // through: only the in-use check keeps the file where it is.
        File.WriteAllText(_path + ".1", "an older run\n");
        using var running = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        running.Write("the running daemon\n"u8);
        running.Flush();

        LogGenerations.KeepPreviousRun(_path);

        Assert.Equal("an older run\n", File.ReadAllText(_path + ".1"));
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void No_log_yet_is_nothing_to_keep()
    {
        File.WriteAllText(_path + ".1", "an older run\n");

        LogGenerations.KeepPreviousRun(_path);

        Assert.Equal("an older run\n", File.ReadAllText(_path + ".1"));
        Assert.False(File.Exists(_path));
    }
}
