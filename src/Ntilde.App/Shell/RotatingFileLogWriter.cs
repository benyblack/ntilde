using System;
using Ntilde.Mux.Daemon;

namespace Ntilde.Shell
{
    /// <summary>
    /// The debug log's writer: a bounded queue drained by one background thread into a buffered, size-capped file
    /// that keeps the previous run's log as <c>debug.log.1</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The work is <see cref="MuxLogFile"/>'s, which also writes the multiplexer's <c>mux.log</c> and documents
    /// why each property is there: <see cref="AppLogger"/> used to be <c>File.AppendAllText</c> per call, on the
    /// producer's thread and without a size limit. This was a copy of it until a fix to one had to be made in
    /// both (#579); <c>Ntilde.Mux</c> cannot reference the App, so the App uses the daemon's.
    /// </para>
    /// <para>
    /// Separate from <see cref="AppLogger"/>, which is a process-global static bound to a path resolved at
    /// type-initialization time: this takes its path and its limits, which is what makes the rotation and the drop
    /// accounting testable.
    /// </para>
    /// </remarks>
    internal sealed class RotatingFileLogWriter : IDisposable
    {
        private readonly MuxLogFile _file;

        /// <param name="path">The live log file. One previous generation is kept at <c>path + ".1"</c>.</param>
        /// <param name="maxBytes">Rotate once the live file passes this.</param>
        /// <param name="queueCapacity">
        /// Depth of the hand-off queue. Deep enough that an ordinary burst is never dropped,
        /// shallow enough that a runaway producer cannot grow the heap.
        /// </param>
        public RotatingFileLogWriter(string path, long maxBytes, int queueCapacity) =>
            _file = new MuxLogFile(path, maxBytes, queueCapacity, threadName: "Ntilde.LogWriter");

        /// <summary>Messages dropped because the queue was full. Test seam and health signal.</summary>
        public int DroppedCount => _file.DroppedCount;

        /// <summary>
        /// Queues one line. Never blocks, never throws, and never touches the disk on the caller's
        /// thread — this is reached from the PTY read thread and the render thread.
        /// </summary>
        public void Write(string message) => _file.Write(message);

        /// <summary>Stops accepting messages, then drains and flushes what is already queued. Safe to call more than once.</summary>
        public void Dispose() => _file.Dispose();
    }
}
