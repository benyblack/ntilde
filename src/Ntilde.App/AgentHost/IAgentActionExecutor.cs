using System;
using System.Threading.Tasks;
using System.Threading;

namespace Ntilde.AgentHost
{
    /// <summary>Outcome of an agent-requested spawn (A3).</summary>
    public readonly struct AgentSpawnResult
    {
        public AgentSpawnResult(Guid paneId, Guid? tabId, string profileName, string kind)
        {
            PaneId = paneId;
            TabId = tabId;
            ProfileName = profileName;
            Kind = kind;
        }

        public Guid PaneId { get; }
        public Guid? TabId { get; }
        public string ProfileName { get; }

        /// <summary>"local" or "ssh".</summary>
        public string Kind { get; }
    }

    /// <summary>
    /// Reason a spawn could not be satisfied, mapped to a protocol error code by
    /// the endpoint. Keeps Avalonia/UI concerns out of the service.
    /// </summary>
    public enum AgentSpawnError
    {
        /// <summary>No local or SSH profile matched the requested name.</summary>
        ProfileNotFound,

        /// <summary>An SSH profile matched but is not allowlisted for agent access.</summary>
        ProfileNotAllowed,

        /// <summary>The profile resolved but the tab failed to open.</summary>
        SpawnFailed,
    }

    /// <summary>
    /// A WYSIWYG capture of a pane's on-screen control (A5 <c>live</c> mode).
    /// Distinct from <see cref="AgentCaptureInfo"/> because a live capture has no
    /// grid dimensions of its own — it is pixels off the screen, and the endpoint
    /// fills in cols/rows from the buffer.
    /// </summary>
    public readonly record struct AgentLiveCapture(
        byte[] Png,
        int Width,
        int Height,
        bool Downscaled);

    /// <summary>Actual App buffer grid and whether its view/session resize dispatch has settled.</summary>
    public readonly record struct AgentPaneResizeResult(int Rows, int Cols, bool LayoutSettled);

    /// <summary>
    /// UI-thread bridge the agent-host endpoint uses to open, close and resize sessions
    /// (A3 acting). Implemented by MainWindow and published on the service
    /// while the window lives; the service never touches Avalonia directly.
    /// Implementations marshal to the UI thread themselves.
    /// </summary>
    public interface IAgentActionExecutor
    {
        /// <summary>
        /// Opens a new tab for <paramref name="profileName"/> (null/empty = default
        /// local profile). Returns the spawned pane's identity, or an error reason.
        /// The allowlist check for SSH profiles happens inside the implementation
        /// (it owns profile resolution).
        /// </summary>
        Task<(AgentSpawnResult? Result, AgentSpawnError? Error)> SpawnAsync(string? profileName);

        /// <summary>Closes the pane with <paramref name="paneId"/>. False if no such live pane.</summary>
        Task<bool> ClosePaneAsync(Guid paneId);

        /// <summary>
        /// Attempts a resize with a responsive-UI observation timeout, without activating tabs.
        /// Null means unavailable before a usable observation. A partial attempt returns actual App
        /// facts, because restoring window bounds cannot undo terminal reflow. Implementations marshal
        /// to the UI thread; unsupported executors keep the safe unavailable default.
        /// </summary>
        Task<AgentPaneResizeResult?> ResizePaneAsync(Guid paneId, int cols, int rows, CancellationToken cancellationToken)
            => Task.FromResult<AgentPaneResizeResult?>(null);

        /// <summary>One bounded routed event. The current-permission/registration lease must hold immediately before dispatch.
        /// Notify a returned transport call synchronously on the UI thread, without extending permissions or dispatching more input.
        /// Preserve dispatch facts even if post-dispatch metadata/identity becomes unavailable.</summary>
        Task<Ntilde.AgentHost.Contracts.GeneratePaneInputResult?> GeneratePaneInputAsync(
            Ntilde.AgentHost.Contracts.GeneratePaneInputParams input, AgentSessionRegistration registration,
            Func<bool> isCurrent, Action onInputSubmitted, CancellationToken cancellationToken)
            => Task.FromResult<Ntilde.AgentHost.Contracts.GeneratePaneInputResult?>(null);

        /// <summary>One bounded private clipboard fixture operation; no native clipboard access.</summary>
        Task<Ntilde.AgentHost.Contracts.ClipboardFixtureResult?> ClipboardFixtureAsync(
            Ntilde.AgentHost.Contracts.ClipboardFixtureParams input, AgentSessionRegistration registration,
            Func<bool> isCurrent, CancellationToken cancellationToken)
            => Task.FromResult<Ntilde.AgentHost.Contracts.ClipboardFixtureResult?>(null);

        /// <summary>
        /// Photographs the pane's live control (A5 <c>live</c> mode), scaled by
        /// <paramref name="scale"/> device pixels per DIP and resampled down to
        /// <paramref name="maxWidth"/> if that is non-zero. Null when there is no
        /// such pane on screen, or it has no laid-out size to capture.
        /// </summary>
        /// <remarks>
        /// This is the one part of the screenshot surface that needs the UI thread,
        /// which is exactly why it sits behind this bridge rather than in the
        /// registration: <c>render</c> mode captures a hidden or occluded pane
        /// precisely because it never touches a visual tree, and that property is
        /// worth keeping unconditionally true of the default path. Implementations
        /// marshal to the UI thread themselves.
        /// </remarks>
        Task<AgentLiveCapture?> CaptureLiveAsync(Guid paneId, int maxWidth, double scale);
    }
}
