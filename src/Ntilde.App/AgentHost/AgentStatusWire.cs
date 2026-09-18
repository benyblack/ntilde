using System;
using Ntilde.AgentHost.Contracts;
using Ntilde.Inference;

namespace Ntilde.AgentHost
{
    /// <summary>
    /// Maps the app-internal status types onto the wire strings defined in
    /// <see cref="AgentHostProtocol"/>. One direction only: the app never
    /// parses these strings back.
    /// </summary>
    public static class AgentStatusWire
    {
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
            AgentSessionStatusConfidence.Observed => AgentHostProtocol.StatusConfidences.Observed,
            _ => throw new ArgumentOutOfRangeException(nameof(confidence), confidence, null),
        };

        public static string ToWire(this ScreenActivity activity) => activity switch
        {
            ScreenActivity.CommandRunning => AgentHostProtocol.ObservedActivities.CommandRunning,
            ScreenActivity.AgentWorking => AgentHostProtocol.ObservedActivities.AgentWorking,
            ScreenActivity.WaitingForUser => AgentHostProtocol.ObservedActivities.WaitingForUser,
            ScreenActivity.IdleShellPrompt => AgentHostProtocol.ObservedActivities.IdleShellPrompt,
            ScreenActivity.UnknownBlank => AgentHostProtocol.ObservedActivities.UnknownBlank,
            _ => throw new ArgumentOutOfRangeException(nameof(activity), activity, null),
        };

        public static string ToWire(this AgentSessionEventType type) => type switch
        {
            AgentSessionEventType.StatusChanged => AgentHostProtocol.EventTypes.StatusChanged,
            AgentSessionEventType.CommandFinished => AgentHostProtocol.EventTypes.CommandFinished,
            AgentSessionEventType.Bell => AgentHostProtocol.EventTypes.Bell,
            AgentSessionEventType.Stalled => AgentHostProtocol.EventTypes.Stalled,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
        };

        public static SessionStatusDto ToDto(this AgentSessionStatusSnapshot snapshot, Guid paneId) => new()
        {
            PaneId = paneId,
            Status = snapshot.Kind.ToWire(),
            Confidence = snapshot.Confidence.ToWire(),
            ExitCode = snapshot.ExitCode,
            CurrentCommand = snapshot.CurrentCommand,
            StatusSinceMs = snapshot.StatusSince.ToUnixTimeMilliseconds(),
            LastOutputAtMs = snapshot.LastOutputAt.ToUnixTimeMilliseconds(),
            IsStalled = snapshot.IsStalled,
            StallThresholdSeconds = snapshot.StallThresholdSeconds,
            IdleThresholdSeconds = snapshot.IdleThresholdSeconds,
            ObservedOverrideThresholdPercent = snapshot.ObservedOverrideThresholdPercent,
            Observation = snapshot.Observation is { } o && snapshot.Kind != AgentSessionStatusKind.Exited
                ? new SessionObservationDto
                {
                    Activity = o.Activity.ToWire(),
                    Confidence = o.Confidence,
                    NeedsAttention = o.NeedsAttention,
                    LastCommandFailed = o.LastCommandFailed,
                    AgeMs = Math.Max(0, snapshot.ObservationAgeMs ?? 0),
                }
                : null,
        };
    }
}
