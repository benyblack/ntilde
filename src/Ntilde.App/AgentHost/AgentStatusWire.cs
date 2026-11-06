using System;
using Ntilde.AgentHost.Contracts;
using Ntilde.CommandAssist.Domain;

namespace Ntilde.AgentHost
{
    /// <summary>
    /// Maps the app-internal status types onto the wire strings defined in
    /// <see cref="AgentHostProtocol"/>. One direction only: the app never
    /// parses these strings back.
    /// </summary>
    public static class AgentStatusWire
    {
        private static readonly SecretsFilter CommandSecretsFilter = new();

        /// <summary>
        /// Command text on its way to an agent: the in-flight command in a status read, and the finished command on a
        /// <c>commandFinished</c> event. Both pass the same filter Command Assist applies before text leaves the app, so
        /// a <c>--password</c> or bearer token typed at the prompt is not handed to every observing agent.
        /// </summary>
        public static string? RedactCommand(string? commandText)
            => commandText is null ? null : CommandSecretsFilter.Redact(commandText).RedactedText;

        public static string ToWire(this AgentSessionStatusKind kind) => kind switch
        {
            AgentSessionStatusKind.Running => AgentHostProtocol.StatusKinds.Running,
            AgentSessionStatusKind.AwaitingInput => AgentHostProtocol.StatusKinds.AwaitingInput,
            AgentSessionStatusKind.Idle => AgentHostProtocol.StatusKinds.Idle,
            AgentSessionStatusKind.Exited => AgentHostProtocol.StatusKinds.Exited,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

        public static string ToWire(this AgentSessionStatusConfidence confidence) => confidence switch
        {
            AgentSessionStatusConfidence.Precise => AgentHostProtocol.StatusConfidences.Precise,
            AgentSessionStatusConfidence.Heuristic => AgentHostProtocol.StatusConfidences.Heuristic,
            _ => throw new ArgumentOutOfRangeException(nameof(confidence), confidence, null),
        };

        public static string ToWire(this AgentSessionEventType type) => type switch
        {
            AgentSessionEventType.StatusChanged => AgentHostProtocol.EventTypes.StatusChanged,
            AgentSessionEventType.CommandFinished => AgentHostProtocol.EventTypes.CommandFinished,
            AgentSessionEventType.Bell => AgentHostProtocol.EventTypes.Bell,
            AgentSessionEventType.Stalled => AgentHostProtocol.EventTypes.Stalled,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
        };

        public static string ToWire(this AgentNeedsUserReason reason) => reason switch
        {
            AgentNeedsUserReason.HostKey => AgentHostProtocol.NeedsUserReasons.HostKey,
            AgentNeedsUserReason.ChangedHostKey => AgentHostProtocol.NeedsUserReasons.ChangedHostKey,
            AgentNeedsUserReason.Password => AgentHostProtocol.NeedsUserReasons.Password,
            AgentNeedsUserReason.Passphrase => AgentHostProtocol.NeedsUserReasons.Passphrase,
            AgentNeedsUserReason.KeyboardInteractive => AgentHostProtocol.NeedsUserReasons.KeyboardInteractive,
            AgentNeedsUserReason.SignIn => AgentHostProtocol.NeedsUserReasons.SignIn,
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
        };

        public static SessionStatusDto ToDto(this AgentSessionStatusSnapshot snapshot, Guid paneId) => new()
        {
            PaneId = paneId,
            Status = snapshot.Kind.ToWire(),
            Confidence = snapshot.Confidence.ToWire(),
            ExitCode = snapshot.ExitCode,
            NeedsUser = snapshot.NeedsUser?.ToWire(),
            CurrentCommand = RedactCommand(snapshot.CurrentCommand),
            StatusSinceMs = snapshot.StatusSince.ToUnixTimeMilliseconds(),
            LastOutputAtMs = snapshot.LastOutputAt.ToUnixTimeMilliseconds(),
            IsStalled = snapshot.IsStalled,
            StallThresholdSeconds = snapshot.StallThresholdSeconds,
            IdleThresholdSeconds = snapshot.IdleThresholdSeconds,
        };
    }
}
