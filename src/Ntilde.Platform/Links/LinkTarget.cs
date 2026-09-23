using System;

namespace Ntilde.Platform.Links
{
    /// <summary>What activating a link is allowed to do. See <see cref="LinkSchemes.Classify"/>.</summary>
    public enum LinkAction
    {
        /// <summary>Not clickable: no underline, no hand cursor, and a click does nothing.</summary>
        Reject = 0,

        /// <summary>
        /// A web or mail link (<c>http</c>, <c>https</c>, <c>mailto</c>), handed to the OS URL handler.
        /// </summary>
        OpenExternal,

        /// <summary>
        /// A <c>file:</c> link naming a plain path on this machine. It is shown in the file manager and
        /// never opened: opening a file is executing it when the file is a program.
        /// </summary>
        RevealLocalPath,
    }

    /// <summary>The outcome of <see cref="LinkSchemes.Classify"/> for one link.</summary>
    /// <param name="Action">What activating the link may do.</param>
    /// <param name="ExternalUri">The parsed URI, set only for <see cref="LinkAction.OpenExternal"/>.</param>
    /// <param name="LocalPath">
    /// The local path in the platform's native shape, set only for <see cref="LinkAction.RevealLocalPath"/>.
    /// Purely syntactic: whether it exists is for the caller to find out.
    /// </param>
    public readonly record struct LinkTarget(LinkAction Action, Uri? ExternalUri, string? LocalPath)
    {
        public static LinkTarget Rejected => default;

        public static LinkTarget External(Uri uri) => new(LinkAction.OpenExternal, uri, null);

        public static LinkTarget Local(string path) => new(LinkAction.RevealLocalPath, null, path);
    }
}
