using System;
using Ntilde.Inference;

namespace Ntilde.AgentHost
{
    /// <summary>
    /// One judgment over a pane's visible text, as handed to
    /// <see cref="AgentSessionStatusMachine.NotifyObserved"/>. <see cref="OutputSequence"/> is the
    /// machine's output counter at the moment the screen was captured; the machine consults the
    /// observation only while no output has arrived since.
    /// </summary>
    public sealed record ScreenObservation
    {
        public required ScreenActivity Activity { get; init; }
        public required double Confidence { get; init; }
        public required double NeedsAttention { get; init; }
        public required double LastCommandFailed { get; init; }
        public required DateTimeOffset ObservedAt { get; init; }
        public required long OutputSequence { get; init; }
    }
}
