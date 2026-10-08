using Ntilde.Mux.Contracts;

namespace Ntilde.Mux.Tests.Support;

/// <summary>
/// A stalled link as a client feels it: the daemon end stops reading, the client's sender is stuck in a write, and its
/// outbound queue fills behind it. Before Phase 5 whoever sent next on that client blocked until the daemon read again -
/// the UI thread too, closing a pane. Now the send returns, and its frame waits in the client's overflow. Shared with
/// Ntilde.App.Tests (linked as MuxSupport).
/// </summary>
internal static class FullSendQueue
{
    /// <summary>The daemon end's pipe (<see cref="FakeMuxServerEnd.Create"/>): small, so that one larger frame holds the sender.</summary>
    public const int PipeCapacityBytes = 4 * 1024;

    private static readonly TimeSpan Patient = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a close may run before it counts as blocked. One that does not block returns at once; one that does
    /// waits on the daemon, which this test only lets read later.
    /// </summary>
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Fills <paramref name="client"/>'s outbound queue once its daemon stops reading: first a frame larger than the
    /// pipe, which the sender takes and is stuck writing, then exactly as many frames as the queue holds. Returns once
    /// all of them are in: the next frame sent on the client cannot join the queue until the daemon reads.
    /// </summary>
    public static Task FillAsync(MuxClient client) =>
        Task.Run(
            () =>
            {
                client.SendInput(Guid.Empty, new string('x', 2 * PipeCapacityBytes));
                for (int i = 0; i < MuxClient.OutboundCapacity; i++) client.SendInput(Guid.Empty, "x");
            },
            Ct).WaitAsync(Patient, Ct);

    /// <summary>
    /// The daemon reads again: everything queued ahead drains, and the first request behind it is returned, unanswered.
    /// </summary>
    public static Task<MuxRequest> DrainUntilRequestAsync(FakeMuxServerEnd daemon) =>
        Task.Run(
            () =>
            {
                while (true)
                {
                    using MuxInboundFrame frame = MuxFrameReader.Read(daemon.Raw.Stream) ?? throw new EndOfStreamException("The client closed before it sent a request.");
                    if (frame.Kind == MuxFrameKind.Request) return MuxFrames.ParseJson(frame.Payload, MuxJsonContext.Default.MuxRequest);
                }
            },
            Ct).WaitAsync(Patient, Ct);

    /// <summary>The session a kill request names.</summary>
    public static Guid KilledSession(MuxRequest kill)
    {
        Assert.Equal(MuxMethods.Kill, kill.Method);
        return MuxFrames.ParseParams(kill.Params, MuxJsonContext.Default.SessionIdParams).SessionId;
    }

    /// <summary>
    /// Runs a pane's close off the test thread, so a close that blocks fails the test instead of hanging it: true when
    /// it returned (and returned true) within <see cref="Prompt"/>.
    /// </summary>
    public static async Task<bool> ReturnsPromptlyAsync(Func<bool> close)
    {
        Task<bool> closing = Task.Run(close, Ct);
        return await Task.WhenAny(closing, Task.Delay(Prompt, Ct)) == closing && await closing;
    }
}
