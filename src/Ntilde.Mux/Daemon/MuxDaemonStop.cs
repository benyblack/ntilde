using System.Diagnostics;
using Ntilde.Mux.Contracts;

namespace Ntilde.Mux.Daemon;

/// <summary>
/// Stopping the daemon at a root: the steps of <c>kill-server [--force]</c> (<c>MuxCli</c>), shared with the App's
/// uninstall hook (Phase 5 Task 21), which must stop the daemon its install no longer contains. Blocking: call them off
/// the UI thread.
/// </summary>
public static class MuxDaemonStop
{
    /// <summary>What asking the daemon to shut down came to.</summary>
    public enum ShutdownRequest
    {
        /// <summary>No trusted daemon is advertised, or the one advertised could not be reached.</summary>
        NotRunning,

        /// <summary>It was asked, and acknowledged: it is shutting down, and its sessions end.</summary>
        Sent,

        /// <summary>It speaks another protocol version and cannot be asked: only terminating it stops it.</summary>
        VersionMismatch,
    }

    /// <summary>How terminating the daemon by pid went.</summary>
    public enum TerminateResult
    {
        /// <summary>Terminated, gone, and its descriptor deleted.</summary>
        Terminated,

        /// <summary>The pid is no longer the daemon (or is this process): nothing was terminated.</summary>
        NotTheDaemon,

        /// <summary>The OS refused, or the process could not be opened: nothing was terminated.</summary>
        Failed,

        /// <summary>Terminated, but still there when the wait ran out.</summary>
        StillRunning,
    }

    /// <summary>
    /// Connects to the daemon <paramref name="descriptorPath"/> advertises - never starting one - and sends
    /// <c>shutdown</c>. A failure other than those <see cref="ShutdownRequest"/> names (the connection dropping
    /// mid-request, a timeout) is thrown, as kill-server reports it.
    /// </summary>
    /// <param name="options">The client's kind and request timeout.</param>
    public static ShutdownRequest RequestShutdown(string descriptorPath, MuxClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(descriptorPath);
        ArgumentNullException.ThrowIfNull(options);
        var launcher = new MuxDaemonLauncher(descriptorPath, new NoSpawn(), options);
        try
        {
            using MuxClient? client = launcher.TryConnectExistingAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (client is null) return ShutdownRequest.NotRunning;
            client.ShutdownServerAsync().GetAwaiter().GetResult();
            return ShutdownRequest.Sent;
        }
        catch (MuxUnavailableException ex) when (ex.VersionMismatch)
        {
            // It cannot be asked to stop: it does not speak our protocol (PR #489 follow-up).
            return ShutdownRequest.VersionMismatch;
        }
    }

    /// <summary>
    /// Terminates the process tree of the daemon <paramref name="descriptor"/> names - after re-verifying, on the very
    /// process about to be killed, its name and the start time the descriptor recorded, so a recycled pid is never
    /// killed, and never this process - then waits up to <paramref name="wait"/> for it to be gone and deletes its
    /// descriptor. <paramref name="failure"/> says why when the result is not <see cref="TerminateResult.Terminated"/>.
    /// </summary>
    public static TerminateResult Terminate(string descriptorPath, MuxEndpointDescriptor descriptor, TimeSpan wait, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(descriptorPath);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Pid == Environment.ProcessId)
        {
            failure = "Refusing to terminate this process.";
            return TerminateResult.NotTheDaemon;
        }

        try
        {
            using Process process = Process.GetProcessById(descriptor.Pid);
            // The caller's live check and this Kill are not atomic: the pid could have exited and been recycled for an
            // unrelated process in between.
            if (!IsDescribedDaemon(process, descriptor))
            {
                failure = $"pid {descriptor.Pid} is no longer the multiplexer; nothing was terminated.";
                return TerminateResult.NotTheDaemon;
            }

            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            failure = $"Could not terminate pid {descriptor.Pid}: {ex.Message}";
            return TerminateResult.Failed;
        }

        if (!MuxDaemonExit.WaitForExit(descriptorPath, descriptor, wait, Environment.ProcessId))
        {
            failure = $"Multiplexer did not stop within {wait.TotalSeconds:0.#} s.";
            return TerminateResult.StillRunning;
        }

        MuxDiscovery.DeleteDescriptorIfOwned(descriptorPath, descriptor.Pid);
        failure = null;
        return TerminateResult.Terminated;
    }

    /// <summary>
    /// Whether <paramref name="process"/> is the daemon <paramref name="descriptor"/> names: the process name and the start
    /// time it recorded. The name alone does not tell a recycled pid from the daemon (another ntilde, say): the start time
    /// does. Shared with the App's update path (Phase 5 Task 22), which reads the daemon's image from the process.
    /// </summary>
    public static bool IsDescribedDaemon(Process process, MuxEndpointDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(descriptor);
        return string.Equals(process.ProcessName, descriptor.ProcessName, StringComparison.OrdinalIgnoreCase)
            && MuxDiscovery.StartTimeMatches(process, descriptor.StartTime);
    }

    private sealed class NoSpawn : IMuxDaemonSpawner
    {
        public void Spawn() => throw new InvalidOperationException("Stopping the multiplexer never starts one.");
    }
}
