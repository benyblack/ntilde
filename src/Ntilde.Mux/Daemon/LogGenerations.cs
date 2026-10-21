using System.Diagnostics;

namespace Ntilde.Mux.Daemon;

/// <summary>
/// The previous generation of a log file, <c>path + ".1"</c>, as <see cref="MuxLogFile"/> keeps it: for the daemon's
/// <c>mux.log</c>, and for the App's <c>debug.log</c> through its <c>RotatingFileLogWriter</c>.
/// </summary>
internal static class LogGenerations
{
    /// <summary>How long a starter waits for another one that is keeping the same log: that takes milliseconds.</summary>
    private static readonly TimeSpan DefaultGateWait = TimeSpan.FromSeconds(1);

    /// <summary>The previous generation of <paramref name="path"/>.</summary>
    internal static string PreviousOf(string path) => path + ".1";

    /// <summary>
    /// The lock file that serializes starters on <paramref name="path"/>'s name. It is never moved or deleted, so its
    /// lock means the same thing however late it is taken. Left in place, empty.
    /// </summary>
    internal static string GateOf(string path) => path + ".lock";

    /// <summary>
    /// Moves the previous run's log to the previous generation before a new run opens the file. Every run used to
    /// erase the one before it, which is the one worth reading after a failure. The file stays where it is when it is
    /// empty (nothing to keep, and the previous generation may hold the last run that logged) and while another
    /// process writes it - a second GUI instance, or a second <c>serve</c>, which opens <c>mux.log</c> before it finds
    /// the lock taken - since moving it would leave that writer logging into the generation the next run deletes.
    /// Never throws: a log that cannot be kept stays where it is, and the writer appends to it.
    /// </summary>
    internal static void KeepPreviousRun(string path) => KeepPreviousRun(path, DefaultGateWait);

    /// <param name="path">The live log file.</param>
    /// <param name="gateWait">How long to wait for another starter holding <see cref="GateOf"/>; then the file is left alone.</param>
    internal static void KeepPreviousRun(string path, TimeSpan gateWait)
    {
        try
        {
            if (!HasRunToKeep(path)) return;

            // Starters take the name first (Codex on #579). On Unix .NET opens a file before it locks it, so without
            // this a starter paused between the two could pass its probe on a log another starter had already moved,
            // then move the file that one had just reopened over the previous generation.
            using FileStream gate = EnterGate(GateOf(path), gateWait);

            // Checked again: another starter may have kept it while this one waited.
            if (!HasRunToKeep(path)) return;

            // An exclusive open fails while any other handle is open: Windows checks its sharing modes, and on Unix
            // .NET takes an flock for every FileStream. Held through the move. Windows: Delete is shared so that this
            // process can rename the file it holds. Unix: exactly None, since .NET takes the exclusive flock only for
            // None (any other share mode is a shared lock, which another writer's would not stop).
            FileShare share = OperatingSystem.IsWindows() ? FileShare.Delete : FileShare.None;
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, share);

            // One replacing move, never a delete first (Codex and CodeRabbit on #579): a move that fails leaves the
            // previous generation as it was.
            File.Move(path, PreviousOf(path), overwrite: true);
        }
        catch
        {
            // In use, the gate not free in time, or the move failed: the writer appends to the file. Anything at all
            // is caught: this runs inside AppLogger's static initializer, where an escaping exception becomes a
            // TypeInitializationException on every later log call.
        }
    }

    private static bool HasRunToKeep(string path)
    {
        var live = new FileInfo(path);
        return live.Exists && live.Length > 0;
    }

    /// <summary>The gate's exclusive handle; throws <see cref="IOException"/> when it is still held after <paramref name="wait"/>.</summary>
    private static FileStream EnterGate(string gatePath, TimeSpan wait)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(gatePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (waited.Elapsed < wait)
            {
                Thread.Sleep(20);
            }
        }
    }
}
