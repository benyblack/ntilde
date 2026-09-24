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
    public Func<string, IMuxListener> ListenerFactory { get; init; } = MuxListeners.Create;
    public int Pid { get; init; } = Environment.ProcessId;
    public string ProcessName { get; init; } = CurrentProcessName();
    public Action<string>? Log { get; init; }

    private static string CurrentProcessName()
    {
        using Process p = Process.GetCurrentProcess();
        return p.ProcessName;
    }
}

public sealed class MuxDaemonAlreadyRunningException : IOException
{
    public MuxDaemonAlreadyRunningException(string message) : base(message) { }
}
