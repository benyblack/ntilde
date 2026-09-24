using System.Globalization;
using System.Text.Json;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;

namespace Ntilde.Shell.Mux;

internal sealed record MuxServeOptions(TimeSpan IdleExitAfter, bool Foreground);

/// <summary>
/// <c>ntilde mux serve|ls|kill|kill-server</c> (spec §5). Exit codes: 0 success, 1 the operation
/// failed (including "no daemon running"), 2 the command line was wrong.
/// </summary>
public static class MuxCommand
{
    private const string Usage = """
        Usage:
          ntilde mux serve [--idle-exit-minutes N] [--foreground]
          ntilde mux ls [--json]
          ntilde mux kill <sessionId>
          ntilde mux kill-server
        """;

    public static bool IsSupportedCliMode(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Length > 0 && string.Equals(args[0], "mux", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsServe(string[] args) =>
        IsSupportedCliMode(args) && args.Length > 1 && string.Equals(args[1], "serve", StringComparison.OrdinalIgnoreCase);

    /// <param name="rootOverride">Test seam: app-data root. Null uses <see cref="MuxDiscovery.GetRootDirectory"/>.</param>
    public static int Execute(string[] args, TextWriter stdout, TextWriter stderr, string? rootOverride = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        if (args.Length < 2) return Fail(stderr, Usage);

        string descriptorPath = MuxDiscovery.GetDescriptorPath(rootOverride ?? MuxDiscovery.GetRootDirectory());
        try
        {
            return args[1].ToLowerInvariant() switch
            {
                "serve" => Serve(args, stderr),
                "ls" => List(args, stdout, stderr, descriptorPath),
                "kill" => Kill(args, stdout, stderr, descriptorPath),
                "kill-server" => KillServer(args, stdout, stderr, descriptorPath),
                _ => Fail(stderr, Usage),
            };
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or MuxProtocolException or UnauthorizedAccessException or MuxUnavailableException)
        {
            stderr.WriteLine($"mux: {ex.Message}");
            return 1;
        }
    }

    internal static bool TryParseServe(string[] args, out MuxServeOptions options, out string? error)
    {
        TimeSpan idle = TimeSpan.FromMinutes(10);
        bool foreground = false;
        options = new MuxServeOptions(idle, foreground);
        int i = 2;
        while (i < args.Length)
        {
            string arg = args[i++];
            switch (arg)
            {
                case "--foreground":
                    foreground = true;
                    break;
                case "--idle-exit-minutes":
                    // The value is the next argument: consumed here, so the loop resumes after it.
                    if (i >= args.Length || !int.TryParse(args[i++], NumberStyles.None, CultureInfo.InvariantCulture, out int m))
                    {
                        error = "--idle-exit-minutes needs a non-negative whole number.";
                        return false;
                    }

                    idle = TimeSpan.FromMinutes(m);
                    break;
                default:
                    error = $"Unknown option '{arg}'.";
                    return false;
            }
        }

        options = new MuxServeOptions(idle, foreground);
        error = null;
        return true;
    }

    private static int Serve(string[] args, TextWriter stderr)
    {
        if (!TryParseServe(args, out MuxServeOptions options, out string? error)) return Fail(stderr, error + Environment.NewLine + Usage);
        return MuxDaemonProcess.Run(options, stderr);
    }

    private static MuxClient? Connect(string descriptorPath, TextWriter stderr)
    {
        var launcher = new MuxDaemonLauncher(descriptorPath, new NoSpawn());
        MuxClient? client = launcher.TryConnectExistingAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (client is null) stderr.WriteLine("No multiplexer is running.");
        return client;
    }

    private sealed class NoSpawn : IMuxDaemonSpawner { public void Spawn() => throw new InvalidOperationException("CLI verbs never start a daemon."); }

    private static int List(string[] args, TextWriter stdout, TextWriter stderr, string descriptorPath)
    {
        bool json = args.Skip(2).Any(a => a == "--json");
        if (args.Skip(2).Any(a => a != "--json")) return Fail(stderr, Usage);
        using MuxClient? client = Connect(descriptorPath, stderr);
        if (client is null) return 1;

        IReadOnlyList<SessionSummary> sessions = client.ListSessionsAsync().GetAwaiter().GetResult();
        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new ListSessionsResult { Sessions = sessions }, MuxJsonContext.Default.ListSessionsResult));
            return 0;
        }

        if (sessions.Count == 0)
        {
            stdout.WriteLine("No sessions.");
            return 0;
        }

        stdout.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-36}  {1,-12}  {2,8}  {3,-9}  {4}", "ID", "STATE", "ATTACHED", "SIZE", "TITLE"));
        foreach (SessionSummary s in sessions)
        {
            string state = DescribeState(s);
            stdout.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-36}  {1,-12}  {2,8}  {3,-9}  {4}",
                s.SessionId, state, s.AttachedClients, $"{s.Cols}x{s.Rows}", s.Title));
        }

        return 0;
    }

    private static string DescribeState(SessionSummary s)
    {
        if (s.Faulted) return "faulted";
        return s.Running ? "running" : $"exited {s.ExitCode}";
    }

    private static int Kill(string[] args, TextWriter stdout, TextWriter stderr, string descriptorPath)
    {
        if (args.Length != 3 || !Guid.TryParse(args[2], out Guid id)) return Fail(stderr, Usage);
        using MuxClient? client = Connect(descriptorPath, stderr);
        if (client is null) return 1;
        try
        {
            client.KillAsync(id).GetAwaiter().GetResult();
        }
        catch (MuxProtocolException ex) when (ex.Code == MuxErrorCodes.UnknownSession)
        {
            stderr.WriteLine($"No session {id}.");
            return 1;
        }

        stdout.WriteLine($"Killed {id}.");
        return 0;
    }

    private static int KillServer(string[] args, TextWriter stdout, TextWriter stderr, string descriptorPath)
    {
        if (args.Length != 2) return Fail(stderr, Usage);
        MuxDiscovery.TryReadDescriptor(descriptorPath, out MuxEndpointDescriptor? d);
        using (MuxClient? client = Connect(descriptorPath, stderr))
        {
            if (client is null) return 1;
            client.ShutdownServerAsync().GetAwaiter().GetResult();
        }

        // Wait for it to really be gone, so "kill-server && start" cannot race the old daemon.
        if (!MuxDaemonExit.WaitForExit(descriptorPath, d, TimeSpan.FromSeconds(5), Environment.ProcessId))
        {
            stderr.WriteLine("Multiplexer did not stop within 5 s.");
            return 1;
        }

        stdout.WriteLine("Multiplexer stopped.");
        return 0;
    }

    private static int Fail(TextWriter stderr, string message)
    {
        stderr.WriteLine(message);
        return 2;
    }
}
