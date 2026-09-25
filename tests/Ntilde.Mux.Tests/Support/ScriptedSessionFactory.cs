using System.Collections.Concurrent;
using Ntilde.Pty;

namespace Ntilde.Mux.Tests.Support;

internal sealed class ScriptedSessionFactory : ITerminalSessionFactory
{
    public ConcurrentQueue<TerminalSessionRequest> Requests { get; } = new();
    public bool FailNext { get; set; }

    /// <summary>Every Create throws while set (unlike <see cref="FailNext"/>, which fails once).</summary>
    public bool ThrowOnCreate { get; set; }
    public bool ProduceSessionsWithoutByteTap { get; set; }
    public NoTapTerminalSession? LastNoTapSession { get; private set; }

    /// <summary>The next session's raw-tap subscription throws (see <see cref="ScriptedTerminalSession.ThrowOnRawSubscribe"/>).</summary>
    public bool ThrowOnSubscribeNext { get; set; }

    public ScriptedTerminalSession? LastScriptedSession { get; private set; }

    /// <summary>When set, Create signals <see cref="CreateEntered"/> and then waits for this gate - a spawn caught mid-flight.</summary>
    public ManualResetEventSlim? CreateGate { get; set; }

    public ManualResetEventSlim CreateEntered { get; } = new();

    public ITerminalSession Create(TerminalSessionRequest request)
    {
        Requests.Enqueue(request);
        if (CreateGate is { } gate)
        {
            CreateEntered.Set();
            gate.Wait(TimeSpan.FromSeconds(30));
        }

        if (FailNext || ThrowOnCreate)
        {
            FailNext = false;
            throw new InvalidOperationException("scripted spawn failure");
        }

        if (ProduceSessionsWithoutByteTap)
        {
            return LastNoTapSession = new NoTapTerminalSession();
        }

        bool throwOnSubscribe = ThrowOnSubscribeNext;
        ThrowOnSubscribeNext = false;
        return LastScriptedSession = new ScriptedTerminalSession(request) { ThrowOnRawSubscribe = throwOnSubscribe };
    }
}
