using System.Diagnostics;

namespace Ntilde.Platform.Ssh.Storage;

/// <summary>A stable sibling file serializes profile transactions across cooperating processes.</summary>
internal sealed class SshProfileStoreLease : IDisposable
{
    private readonly FileStream _stream;

    private SshProfileStoreLease(FileStream stream) => _stream = stream;

    internal static SshProfileStoreLease Acquire(string path, TimeSpan timeout, Action? contended)
    {
        var elapsed = Stopwatch.StartNew();
        bool reported = false;
        while (true)
        {
            try
            {
                // OpenOrCreate never truncates before the OS has acquired exclusive sharing.
                return new SshProfileStoreLease(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1));
            }
            catch (IOException error) when (IsSharingViolation(error))
            {
                if (!reported)
                {
                    reported = true;
                    contended?.Invoke(); // Tests observe actual contention, without elapsed-time guesses.
                }
                if (elapsed.Elapsed >= timeout)
                    throw new IOException("The SSH profile transaction is busy. Retry after the other operation completes.", error);
                Thread.Sleep(20);
            }
        }
    }

    private static bool IsSharingViolation(IOException error) => OperatingSystem.IsWindows()
        ? (error.HResult & 0xffff) is 32 or 33
        : error.HResult is 11 or 35; // Linux EAGAIN / macOS EWOULDBLOCK.

    public void Dispose() => _stream.Dispose(); // Never delete or replace the stable lock file.
}
