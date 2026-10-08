using System.Reflection;
using Ntilde.Mux.Cli;
using Ntilde.Mux.Daemon;

namespace Ntilde.MuxDaemon;

/// <summary>ntilde-mux: the remote multiplexer (spec §10.1). Every verb is Ntilde.Mux.Cli's.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        // Its own root, not the GUI's (final review F3): on a host that also runs ntilde, the two daemons never meet.
        MuxPaths paths = MuxPaths.Standalone();
        return MuxCli.Execute(args, Console.Out, Console.Error, new MuxCliHost
        {
            Paths = paths,
            UsagePrefix = "ntilde-mux",
            ServeArguments = ["serve"],
            // The shells' stable SSH_AUTH_SOCK: a link beside the endpoint that every proxy keeps pointed at its own agent.
            SessionFactory = () => new LocalShellSessionFactory(AgentSocketLink.LinkPathForEndpoint(paths.Endpoint, line => Console.Error.WriteLine($"[ntilde-mux] {line}"))),
            Verbs = MuxCliVerbs.Serve | MuxCliVerbs.Proxy | MuxCliVerbs.Ls | MuxCliVerbs.Kill | MuxCliVerbs.KillServer | MuxCliVerbs.Attach | MuxCliVerbs.Version,
            Version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0",
        });
    }
}
