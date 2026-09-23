using Ntilde.Mux.Contracts;

namespace Ntilde.Mux;

/// <summary>Where a session offers frames: a server connection in production, a recorder in tests.</summary>
internal interface IMuxFrameSink
{
    /// <summary>
    /// Offers a frame. Called on a session's parse thread, so it must never block. A sink that keeps
    /// the frame takes its own reference (<see cref="MuxOutboundFrame.AddRef"/>); one that copies
    /// need not. Returning false means "drop me": the session unsubscribes the sink.
    /// </summary>
    bool TryEnqueue(MuxOutboundFrame frame);
}
