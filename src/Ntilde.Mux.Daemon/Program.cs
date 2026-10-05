using System.Reflection;
using Ntilde.Mux.Cli;
using Ntilde.Mux.Daemon;

namespace Ntilde.MuxDaemon;

/// <summary>ntilde-mux: the remote multiplexer (spec §10.1). Every verb is Ntilde.Mux.Cli's.</summary>
internal static class Program
{
    private static int Main(string[] args) => MuxCli.Execute(args, Console.Out, Console.Error, new MuxCliHost
    {
        Paths = MuxPaths.Default(),
        UsagePrefix = "ntilde-mux",
        ServeArguments = ["serve"],
        SessionFactory = () => LocalShellSessionFactory.Instance,
        Verbs = MuxCliVerbs.Serve | MuxCliVerbs.Proxy | MuxCliVerbs.Ls | MuxCliVerbs.Kill | MuxCliVerbs.KillServer | MuxCliVerbs.Attach | MuxCliVerbs.Version,
        Version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0",
    });
}
