using Ntilde.AgentHost.Contracts;

namespace Ntilde.AgentHost;

/// <summary>Read-only UI bridge, independent of the act executor. Null means no complete current observation.</summary>
public interface IAgentPaneObserver
{
    /// <summary>Marshal to the UI thread, honor cancellation and retain registration/buffer/session identity.</summary>
    Task<PaneMetadataResult?> ReadPaneMetadataAsync(AgentSessionRegistration registration, CancellationToken cancellationToken);
}
