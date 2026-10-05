using System.Diagnostics;
using Ntilde.Mux.Transport;

namespace Ntilde.Mux;

public sealed class MuxDaemonOptions
{
    public required string Endpoint { get; init; }
    public required string DescriptorPath { get; init; }
    /// <summary>Exit after this long with no running session and no connection. <see cref="TimeSpan.Zero"/> = never.</summary>
    public TimeSpan IdleExitAfter { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan ReapGrace { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Stop the daemon ("accept-failed") once accepting has failed continuously for this long AND no
    /// client is connected: nobody can reach its shells any more, and exiting releases the lock and
    /// descriptor so a new daemon can start. Never while a client is connected - its shells stay.
    /// A successful accept resets the clock.
    /// </summary>
    public TimeSpan AcceptFailureStopAfter { get; init; } = TimeSpan.FromSeconds(60);
    public Func<string, IMuxListener> ListenerFactory { get; init; } = MuxListeners.Create;
    public int Pid { get; init; } = Environment.ProcessId;
    public string ProcessName { get; init; } = CurrentProcessName();
    /// <summary>This process's start time (UTC ticks), recorded in the descriptor so a recycled pid is never mistaken for the daemon.</summary>
    public long? StartTimeUtcTicks { get; init; } = CurrentStartTimeUtcTicks();
    public Action<string>? Log { get; init; }

    private static string CurrentProcessName()
    {
        using Process p = Process.GetCurrentProcess();
        return p.ProcessName;
    }

    private static long? CurrentStartTimeUtcTicks()
    {
        try
        {
            using Process p = Process.GetCurrentProcess();
            return p.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }
}

public sealed class MuxDaemonAlreadyRunningException : IOException
{
    public MuxDaemonAlreadyRunningException(string message) : base(message) { }
}
