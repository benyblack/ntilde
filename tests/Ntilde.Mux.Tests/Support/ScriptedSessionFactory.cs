using System.Collections.Concurrent;
using Ntilde.Pty;

namespace Ntilde.Mux.Tests.Support;

internal sealed class ScriptedSessionFactory : ITerminalSessionFactory
{
    public ConcurrentQueue<TerminalSessionRequest> Requests { get; } = new();
    public bool FailNext { get; set; }
    public bool ProduceSessionsWithoutByteTap { get; set; }
    public NoTapTerminalSession? LastNoTapSession { get; private set; }

    public ITerminalSession Create(TerminalSessionRequest request)
    {
        Requests.Enqueue(request);
        if (FailNext)
        {
            FailNext = false;
            throw new InvalidOperationException("scripted spawn failure");
        }

        if (ProduceSessionsWithoutByteTap)
        {
            return LastNoTapSession = new NoTapTerminalSession();
        }

        return new ScriptedTerminalSession(request);
    }
}
