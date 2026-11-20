using System;

namespace Ntilde.AgentHost
{
    /// <summary>What a session is doing right now (agent-host A2 status model).</summary>
    public enum AgentSessionStatusKind
    {
        /// <summary>A command or full-screen TUI is executing.</summary>
        Running,

        /// <summary>The shell is at a prompt waiting for input.</summary>
        AwaitingInput,

        /// <summary>At a prompt with no activity for <see cref="AgentSessionStatusMachine.IdleThresholdSeconds"/>.</summary>
        Idle,

        /// <summary>The session's process has exited.</summary>
        Exited,
    }

    /// <summary>
    /// How the status was derived. Precise means shell-integration events
    /// (prompt/command lifecycle) are driving the machine; heuristic means
    /// PTY-level signals only (child processes, alt screen, output activity).
    /// </summary>
    public enum AgentSessionStatusConfidence
    {
        Heuristic,
        Precise,
    }

    /// <summary>
    /// What the server asked an SSH connection an agent opened, which only the user can give (#610). Ntilde shows no
    /// sign-in dialog for such a connection, so it ends, and its exit says which of these it was.
    /// </summary>
    public enum AgentNeedsUserReason
    {
        /// <summary>The server's host key, which the user has not trusted yet.</summary>
        HostKey,

        /// <summary>A host key other than the one the user trusted for this server.</summary>
        ChangedHostKey,

        /// <summary>A password Ntilde had no saved one to answer with (<c>AgentHostProtocol.NeedsUserReasons.Password</c>).</summary>
        Password,

        /// <summary>The passphrase of the user's private key.</summary>
        Passphrase,

        /// <summary>A keyboard-interactive question, such as a one-time code.</summary>
        KeyboardInteractive,

        /// <summary>Signing in needs the user, and the connection cannot say with what (<c>AgentHostProtocol.NeedsUserReasons.SignIn</c>).</summary>
        SignIn,
    }

    public enum AgentSessionEventType
    {
        StatusChanged,
        CommandFinished,
        Bell,
        Stalled,
    }

    /// <summary>Point-in-time status, safe to read from any thread via <see cref="AgentSessionStatusMachine.Snapshot"/>.</summary>
    public sealed record AgentSessionStatusSnapshot
    {
        public required AgentSessionStatusKind Kind { get; init; }
        public required AgentSessionStatusConfidence Confidence { get; init; }
        public int? ExitCode { get; init; }

        /// <summary>Set when the session exited because its SSH connection needed the user (#610).</summary>
        public AgentNeedsUserReason? NeedsUser { get; init; }
        public string? CurrentCommand { get; init; }
        public required DateTimeOffset StatusSince { get; init; }
        public required DateTimeOffset LastOutputAt { get; init; }
        public required bool IsStalled { get; init; }
        public int StallThresholdSeconds { get; init; } = AgentSessionStatusMachine.StallThresholdSeconds;
        public int IdleThresholdSeconds { get; init; } = AgentSessionStatusMachine.IdleThresholdSeconds;
    }

    /// <summary>
    /// A transition emitted by the status machine. PR2 of the A2 design feeds
    /// these into the event ring behind <c>waitForEvents</c>; until then they
    /// exist for tests and future consumers.
    /// </summary>
    public sealed record AgentSessionStatusEvent
    {
        public required AgentSessionEventType Type { get; init; }
        public required AgentSessionStatusKind Status { get; init; }
        public required DateTimeOffset Timestamp { get; init; }
        public int? ExitCode { get; init; }

        /// <summary>On the exit of a session that stopped for the user: why (<see cref="AgentSessionStatusSnapshot.NeedsUser"/>).</summary>
        public AgentNeedsUserReason? NeedsUser { get; init; }
        public TimeSpan? Duration { get; init; }

        /// <summary>
        /// For <see cref="AgentSessionEventType.CommandFinished"/>: the command that finished, as shell integration
        /// reported it at its <c>OSC 133;C</c> edge; null when the mark carried no text. Unredacted here.
        /// </summary>
        public string? Command { get; init; }
    }
}
