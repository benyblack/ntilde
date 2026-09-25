using Ntilde.Mux.Contracts;

namespace Ntilde.Shell.Mux;

/// <summary>After a <c>shutdown</c> request: wait until the daemon is really gone (kill-server, the update path).</summary>
internal static class MuxDaemonExit
{
    /// <summary>
    /// Blocks (call it off the UI thread) until no live descriptor is advertised and, when
    /// <paramref name="before"/> named another process, that process has exited. The descriptor
    /// alone is not enough: the daemon deletes it before its last teardown finishes. A descriptor
    /// naming <paramref name="selfPid"/> is an in-process daemon (tests): only the descriptor is waited on.
    /// </summary>
    /// <param name="before">The descriptor read before the shutdown was sent; null skips the process wait.</param>
    /// <returns>False when it was still there at <paramref name="timeout"/>.</returns>
    public static bool WaitForExit(string descriptorPath, MuxEndpointDescriptor? before, TimeSpan timeout, int selfPid)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        bool Gone()
        {
            // Only the daemon that was asked to stop counts: a new one another window started meanwhile does not.
            if (MuxDiscovery.TryReadLiveDescriptor(descriptorPath, out MuxEndpointDescriptor? now) && (before is null || now.Pid == before.Pid)) return false;
            return before is null || before.Pid == selfPid || !MuxDiscovery.IsProcessAlive(before.Pid, before.ProcessName);
        }

        while (!Gone())
        {
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(50);
        }

        return true;
    }
}
