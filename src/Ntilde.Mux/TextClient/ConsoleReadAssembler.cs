namespace Ntilde.Mux.TextClient;

/// <summary>
/// The Windows surface's read loop, over a ReadConsoleW-shaped delegate so it can be tested without a
/// console: surrogate pairs split across reads, and which results mean "input closed".
/// </summary>
internal static class ConsoleReadAssembler
{
    /// <summary>One ReadConsoleW call into <paramref name="buffer"/> at <paramref name="offset"/>. False when the call failed.</summary>
    internal delegate bool ReadChunk(char[] buffer, int offset, int count, out int read);

    /// <summary>
    /// Returns the chars read, 0 only when input is closed. A trailing high surrogate is held back in
    /// <paramref name="pendingHigh"/> for the next call, so a pair split across two reads never reaches
    /// the caller as a lone half.
    /// </summary>
    /// <remarks>
    /// Only a failed call (FALSE: the handle is gone, the console detached) means closed. A console
    /// handle has no end-of-file in raw mode: with line input off, Ctrl+Z arrives as an ordinary 0x1A
    /// char (the "Ctrl+Z is EOF" rule belongs to cooked reads and the C runtime, not ReadConsoleW),
    /// and it must reach the shell. A TRUE read of zero chars is a wakeup with nothing typed (the
    /// console can end a read early, e.g. on a signal key); read again rather than detach.
    /// </remarks>
    /// <summary>Consecutive empty reads (nothing pending) retried at full speed before each retry backs off.</summary>
    internal const int EmptyReadsBeforeBackoff = 64;

    /// <param name="emptyReadBackoff">Runs before each retry past <see cref="EmptyReadsBeforeBackoff"/>; a 10 ms sleep by default. Tests count it.</param>
    internal static int Read(char[] buffer, ref char? pendingHigh, ReadChunk readChunk, Action? emptyReadBackoff = null)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(readChunk);
        if (buffer.Length == 0) return 0;
        int start = 0;
        if (pendingHigh is char high)
        {
            buffer[0] = high;
            pendingHigh = null;
            start = 1;
        }

        int emptyReads = 0;
        while (true)
        {
            if (start == buffer.Length) return start;
            if (!readChunk(buffer, start, buffer.Length - start, out int read)) return start;
            if (read == 0)
            {
                // Something is already in hand (a held-back high half): hand it over instead of waiting
                // on a console that returned nothing.
                if (start > 0) return start;

                // An empty read is normally a one-off. A console that keeps returning them must not
                // pin a core on the input thread, so a long run of them backs off between retries.
                if (++emptyReads > EmptyReadsBeforeBackoff)
                {
                    if (emptyReadBackoff is null) Thread.Sleep(10);
                    else emptyReadBackoff();
                }

                continue;
            }

            emptyReads = 0;

            int n = start + read;
            if (!char.IsHighSurrogate(buffer[n - 1]) || buffer.Length < 2) return n;
            if (n > 1)
            {
                pendingHigh = buffer[n - 1];
                return n - 1;
            }

            start = 1; // only the high half so far: wait for its low half
        }
    }
}
