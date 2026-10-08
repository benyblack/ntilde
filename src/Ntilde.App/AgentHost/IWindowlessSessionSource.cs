using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ntilde.Mux.Contracts;
using Ntilde.VT;

namespace Ntilde.AgentHost
{
    /// <summary>
    /// A daemon session no pane of the window shows or is about to show (Phase 5 spec §3, ruling R4). Its agent-facing id
    /// is <see cref="SessionId"/>, the mux session id.
    /// </summary>
    /// <param name="Endpoint">The daemon's endpoint, in its persisted form (<c>local</c>, or <c>ssh:</c> and the profile id).</param>
    /// <param name="SshProfileId">The SSH profile whose host runs the daemon; null for this computer's.</param>
    /// <param name="HostDisplayName">"this computer", or the SSH host's display name.</param>
    internal sealed record WindowlessSessionInfo(Guid SessionId, string Endpoint, Guid? SshProfileId, string HostDisplayName,
        string Title, int Cols, int Rows, bool Running, int? ExitCode);

    /// <summary>How a call on one windowless session ended.</summary>
    internal enum WindowlessOutcome
    {
        Ok,

        /// <summary>
        /// No daemon the window is connected to has it as a windowless session: unknown, shown in a pane of this window
        /// (its pane id is the one to use), faulted, gone since, or found on two endpoints (ambiguous).
        /// </summary>
        NotFound,

        /// <summary>The daemon is too old for the call (<c>readScreen</c>): the session's status is still reported.</summary>
        Unsupported,

        /// <summary>
        /// A daemon did not answer in time or failed the call, and the session may be on it; or the window did not say
        /// in time which sessions its panes show.
        /// </summary>
        Unreachable,

        /// <summary>The session has exited: it takes no input, and there is nothing to kill.</summary>
        NotRunning,
    }

    /// <summary>
    /// A windowless session's screen. <see cref="Session"/> is set whenever the session was found. <see cref="Snapshot"/>
    /// only when <see cref="Outcome"/> is <see cref="WindowlessOutcome.Ok"/>; <see cref="Status"/> then too, and also when
    /// it is <see cref="WindowlessOutcome.Unsupported"/> (taken from <c>sessionInfo</c>, with an empty
    /// <see cref="ReadScreenResult.Snapshot"/> and no <see cref="ReadScreenResult.LastOutputUnixMs"/>).
    /// </summary>
    internal sealed record WindowlessScreen(WindowlessOutcome Outcome, WindowlessSessionInfo? Session, TerminalStateSnapshot? Snapshot, ReadScreenResult? Status);

    /// <summary>
    /// The window's daemon sessions that no pane of it shows (Phase 5 spec §3): what the agent host lists, reads, sends
    /// input to and kills besides panes. Every call is thread-safe, never blocks the UI thread, and never connects or
    /// prompts: only daemons the window is connected to now are asked. Nothing is cached from one call to the next.
    /// A cancelled <c>ct</c> ends a call with <see cref="OperationCanceledException"/>; every other failure is an outcome.
    /// </summary>
    internal interface IWindowlessSessionSource
    {
        /// <summary>Every windowless session, on every daemon that answered; a daemon that did not answer in time is left out.</summary>
        Task<IReadOnlyList<WindowlessSessionInfo>> ListAsync(CancellationToken ct);

        /// <summary>
        /// The session's screen with up to <paramref name="maxScrollbackRows"/> scrollback rows (clamped to
        /// 0..<see cref="MuxReadScreenLimits.MaxScrollbackRows"/>), or fewer when that many do not fit in one read.
        /// </summary>
        Task<WindowlessScreen> ReadScreenAsync(Guid sessionId, int maxScrollbackRows, CancellationToken ct);

        /// <summary>Sends <paramref name="text"/> to a running session; <see cref="WindowlessOutcome.Ok"/> once it is on its way.</summary>
        Task<WindowlessOutcome> SendInputAsync(Guid sessionId, string text, CancellationToken ct);

        /// <summary>Kills a running session; <see cref="WindowlessOutcome.Ok"/> once its daemon confirmed the kill.</summary>
        Task<WindowlessOutcome> KillAsync(Guid sessionId, CancellationToken ct);

        /// <summary>The endpoint's SSH profile id for an act check; null for local or unknown.</summary>
        Task<(WindowlessOutcome Outcome, Guid? SshProfileId)> ResolveAsync(Guid sessionId, CancellationToken ct);
    }
}
