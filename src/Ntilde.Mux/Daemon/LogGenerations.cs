namespace Ntilde.Mux.Daemon;

/// <summary>
/// The previous generation of a log file, <c>path + ".1"</c>, as the App's <c>RotatingFileLogWriter</c> and the
/// daemon's <see cref="MuxLogFile"/> keep it. Shared here rather than copied into both: the App references this
/// assembly, and the rule has cases a copy could drift on.
/// </summary>
public static class LogGenerations
{
    /// <summary>The previous generation of <paramref name="path"/>.</summary>
    public static string PreviousOf(string path) => path + ".1";

    /// <summary>
    /// Moves the previous run's log to the previous generation before a new run starts the file over. Every run
    /// used to erase the one before it, which is the one worth reading after a failure. The file stays where it is
    /// when it is empty (nothing to keep, and the previous generation may hold the last run that logged) and while
    /// another process writes it - a second GUI instance, or a second <c>serve</c>, which opens <c>mux.log</c>
    /// before it finds the lock taken - since moving it would leave that writer logging into the generation the
    /// next run deletes. Never throws: a log that cannot be kept stays where it is, and the writer appends to it.
    /// </summary>
    public static void KeepPreviousRun(string path)
    {
        try
        {
            var live = new FileInfo(path);
            if (!live.Exists || live.Length == 0) return;

            // An exclusive open fails while any other handle is open: Windows checks its sharing modes, and on Unix
            // .NET takes an flock for every FileStream. It is held through the move (Codex on #579), so a process
            // starting at the same moment cannot pass its own probe on this file before it has moved, and then finds
            // the new live file empty or in use. Windows: Delete is shared so that this process can rename the file it
            // holds. Unix: exactly None, since .NET takes the exclusive flock only for None (any other share mode is a
            // shared lock, which another writer's would not stop), and a rename ignores the lock anyway.
            FileShare share = OperatingSystem.IsWindows() ? FileShare.Delete : FileShare.None;
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, share);

            // One replacing move, never a delete first (Codex and CodeRabbit on #579): a delete could remove the
            // generation another starter had just moved there, or be followed by a move that fails. A move that fails
            // leaves the previous generation as it was.
            File.Move(path, PreviousOf(path), overwrite: true);
        }
        catch
        {
            // In use, or the move failed: the writer appends to the file. Anything at all is caught, as everywhere
            // in the writers: this runs inside AppLogger's static initializer, where an escaping exception becomes a
            // TypeInitializationException on every later log call.
        }
    }
}
