using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Daemon;
using Ntilde.VT;

namespace Ntilde.Shell.Mux;

/// <summary>
/// Velopack's uninstall hook, for the multiplexer (Phase 5 Task 21 review). On a Windows install the local daemon runs from
/// its own copy under the app-data root (<see cref="MuxDaemonImage"/>), outside the install root that uninstall stops and
/// deletes: left alone, the daemon and every shell it hosts would outlive the uninstall, with about 75 MB of copies. So
/// the hook stops it as <c>ntilde mux kill-server --force</c> would - asked to shut down, and terminated by pid when it
/// cannot be asked or has not exited within 5 s - then removes the copies (<see cref="MuxDaemonImage.RemoveAllCopies"/>).
/// About 10 s at worst, inside the budget Velopack gives a fast callback.
/// </summary>
internal static class MuxUninstall
{
    /// <summary>How long each step may take.</summary>
    /// <param name="Request">For the shutdown request to be answered: a daemon that never answers must not hold the uninstall.</param>
    /// <param name="Shutdown">For the daemon to exit once asked: kill-server's 5 s.</param>
    /// <param name="Terminate">For it to be gone once terminated.</param>
    internal sealed record Waits(TimeSpan Request, TimeSpan Shutdown, TimeSpan Terminate)
    {
        public static Waits Default { get; } = new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2));
    }

    /// <summary>The hook (Program.cs): this user's daemon at the app-data root, and its copies. Windows only; never throws.</summary>
    public static void Run()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            string root = MuxDiscovery.GetRootDirectory();
            StopDaemonAndRemoveCopies(root, new LocalDaemon(MuxDiscovery.GetDescriptorPath(root)), MuxImageFileSystem.Instance,
                static message => VelopackHookLog.Write(LogLevel.Info, message));
        }
        catch (Exception ex)
        {
            // A hook that threw would take the uninstall down with it.
            VelopackHookLog.Write(LogLevel.Warning, $"[Mux] uninstall: {ex.Message}");
        }
    }

    /// <summary>Stops the daemon <paramref name="daemon"/> stands for, then removes the copies under <paramref name="appDataRoot"/>. Never throws.</summary>
    internal static void StopDaemonAndRemoveCopies(string appDataRoot, IMuxUninstallDaemon daemon, IMuxImageFileSystem fs, Action<string> log, Waits? waits = null)
    {
        ArgumentNullException.ThrowIfNull(daemon);
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            StopDaemon(daemon, log, waits ?? Waits.Default);
        }
        catch (Exception ex)
        {
            log($"[Mux] uninstall: stopping the multiplexer daemon failed: {ex.Message}");
        }

        // After the daemon: a copy it still runs from cannot be deleted, and is kept whole.
        MuxDaemonImage.RemoveAllCopies(appDataRoot, fs, log);
    }

    private static void StopDaemon(IMuxUninstallDaemon daemon, Action<string> log, Waits waits)
    {
        if (daemon.ReadLive() is not { } d) return;

        bool asked = false;
        Task<bool> request = Task.Run(daemon.RequestShutdown);
        try
        {
            asked = request.Wait(waits.Request) && request.Result;
            if (!request.IsCompleted) log($"[Mux] uninstall: the multiplexer daemon (pid {d.Pid}) did not answer within {waits.Request.TotalSeconds:0.#} s");
        }
        catch (AggregateException ex)
        {
            log($"[Mux] uninstall: the multiplexer daemon (pid {d.Pid}) could not be asked to stop: {ex.GetBaseException().Message}");
        }

        if (asked && daemon.WaitForExit(d, waits.Shutdown))
        {
            log($"[Mux] uninstall: stopped the multiplexer daemon (pid {d.Pid}) and its shells");
            return;
        }

        log(asked
            ? $"[Mux] uninstall: the multiplexer daemon (pid {d.Pid}) did not stop within {waits.Shutdown.TotalSeconds:0.#} s; terminating it"
            : $"[Mux] uninstall: terminating the multiplexer daemon (pid {d.Pid})");
        log(daemon.Terminate(d, waits.Terminate, out string? failure)
            ? $"[Mux] uninstall: terminated the multiplexer daemon (pid {d.Pid}) and its shells"
            : $"[Mux] uninstall: {failure}");
    }

    /// <summary>The real daemon at a root: kill-server's steps (<see cref="MuxDaemonStop"/>).</summary>
    private sealed class LocalDaemon(string descriptorPath) : IMuxUninstallDaemon
    {
        public MuxEndpointDescriptor? ReadLive() => MuxDiscovery.TryReadLiveDescriptor(descriptorPath, out MuxEndpointDescriptor? d) ? d : null;

        public bool RequestShutdown() =>
            MuxDaemonStop.RequestShutdown(descriptorPath, new MuxClientOptions { ClientKind = "ntilde-uninstall", RequestTimeout = Waits.Default.Request })
            == MuxDaemonStop.ShutdownRequest.Sent;

        public bool WaitForExit(MuxEndpointDescriptor descriptor, TimeSpan timeout) =>
            MuxDaemonExit.WaitForExit(descriptorPath, descriptor, timeout, Environment.ProcessId);

        public bool Terminate(MuxEndpointDescriptor descriptor, TimeSpan wait, out string? failure) =>
            MuxDaemonStop.Terminate(descriptorPath, descriptor, wait, out failure) == MuxDaemonStop.TerminateResult.Terminated;
    }
}

/// <summary>The daemon-side steps of <see cref="MuxUninstall"/>; tests use a fake.</summary>
internal interface IMuxUninstallDaemon
{
    /// <summary>The daemon the root advertises, alive; null when none runs.</summary>
    MuxEndpointDescriptor? ReadLive();

    /// <summary>Connects and sends <c>shutdown</c>; false when it cannot be asked (not reachable, another protocol version).</summary>
    bool RequestShutdown();

    /// <summary>Whether <paramref name="descriptor"/>'s daemon is gone within <paramref name="timeout"/>.</summary>
    bool WaitForExit(MuxEndpointDescriptor descriptor, TimeSpan timeout);

    /// <summary>
    /// Terminates <paramref name="descriptor"/>'s process tree once it is verified to still be the daemon, and waits up
    /// to <paramref name="wait"/> for it to be gone; false, with why, when it was not terminated or is still there.
    /// </summary>
    bool Terminate(MuxEndpointDescriptor descriptor, TimeSpan wait, out string? failure);
}
