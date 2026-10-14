namespace Ntilde.Mux.Cli;

/// <summary>
/// The verbs an executable offers through <see cref="MuxCli"/> (Phase 4 spec §6.2): the App's
/// <c>ntilde mux</c> offers serve, ls, kill, kill-server, attach and probe-console, plus the hidden
/// spawn-for-test (Phase 5 Task 24); the standalone <c>ntilde-mux</c> adds proxy and version. A verb
/// outside the host's set reads as unknown.
/// </summary>
[Flags]
public enum MuxCliVerbs
{
    None = 0,
    Serve = 1,
    Ls = 2,
    Kill = 4,
    KillServer = 8,
    Attach = 16,
    ProbeConsole = 32,
    Proxy = 64,
    Version = 128,

    /// <summary>Hidden: starts a session running a given command in the running daemon, for verification runs.</summary>
    SpawnForTest = 256,
}
