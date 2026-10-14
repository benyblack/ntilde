namespace Ntilde.Pty
{
    /// <summary>
    /// Optional companion to <see cref="ITerminalSession"/>: what a session does for itself that
    /// the host would otherwise do on its behalf.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="ITerminalSession"/> and deliberately optional. Every
    /// session that exists today - <c>RustPtySession</c>, <c>OpenSshSession</c>,
    /// <c>NativeSshSession</c> - does none of this, and a host tests with
    /// <c>session is ITerminalSessionCapabilities { … : true }</c>, so "not implemented" reads as
    /// all-false with no edit to any existing type.
    ///
    /// The sessions that will implement it are the multiplexer's: a pane attached to
    /// <c>ntilde mux serve</c> is a second, downstream parser over a stream whose authoritative
    /// parser lives in the daemon. Anything the local parser would write back to the child has to
    /// be suppressed, or the child receives it twice.
    /// </remarks>
    public interface ITerminalSessionCapabilities
    {
        /// <summary>
        /// The session answers terminal device queries (DA1/DA2, DSR cursor reports, DECRPM,
        /// OSC colour queries) itself, so the host must not forward its own parser's
        /// <c>OnResponse</c> output into <see cref="ITerminalIO.SendInput"/>, and must not send
        /// unsolicited in-band resize reports (kitty mode 2048).
        /// </summary>
        bool AnswersDeviceQueries { get; }

        /// <summary>
        /// The session sequences window-size changes inside the output stream rather than as an
        /// out-of-band <see cref="ITerminalLifecycle.Resize"/> that races it.
        /// </summary>
        /// <remarks>
        /// Declared only in Phase 0 and read by nobody. Phase 1 consumes it: when a multiplexer
        /// session orders resizes in-stream, the pane stops resizing its local buffer directly
        /// and lets the resize arrive at its stream position, which is what keeps an attached
        /// pane's reflow identical to the daemon's.
        /// </remarks>
        bool OrdersResizeInStream { get; }
    }
}
