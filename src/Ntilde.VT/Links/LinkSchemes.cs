using System;
using System.Collections.Generic;
using System.Text;

namespace Ntilde.VT.Links
{
    /// <summary>
    /// Decides what activating a terminal link may do. A link target is untrusted program output: an
    /// OSC 8 hyperlink carries an arbitrary target under arbitrary display text, and the URL detector
    /// turns any <c>scheme://...</c> in plain text into a link, so a remote host or a <c>cat</c> of a
    /// crafted file chooses what a Ctrl+click reaches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Web and mail links go to the OS URL handler, which is what the user expects of them. A
    /// <c>file:</c> link must never go there. Shell-executing a file runs it when it is a program (or a
    /// shortcut to one), whatever the link text claimed, and on Windows a UNC target
    /// (<c>file://host/share/x</c>) makes the OS connect to that SMB host and offer the user's NTLM
    /// credentials before anything is even opened. So a <c>file:</c> link is accepted only when it
    /// parses to a plain path on this machine, and the App reveals it in the file manager instead of
    /// opening it (<see cref="LinkAction.RevealLocalPath"/>).
    /// </para>
    /// <para>
    /// Everything here is syntactic: no filesystem access and no process launching, both of which
    /// belong to the App. That is part of the defence, not only a convenience for tests. On Windows
    /// even asking whether <c>\\host\share\x</c> exists makes the SMB connection, so a hostile path has
    /// to be refused before anything touches the filesystem.
    /// </para>
    /// </remarks>
    public static class LinkSchemes
    {
        /// <summary>Classifies <paramref name="link"/> for a click on this machine.</summary>
        /// <param name="link">An OSC 8 target or a URL detected in plain text.</param>
        /// <param name="isWindows">
        /// Whether local paths follow Windows rules. A parameter rather than a runtime check so both
        /// rule sets can be tested on either OS.
        /// </param>
        /// <param name="localHostNames">
        /// This machine's own names (the App passes <c>Environment.MachineName</c> and the DNS host
        /// name). A <c>file:</c> link whose host is one of them counts as local, which is what keeps
        /// <c>ls --hyperlink</c>, rg, fd and eza links working: they write <c>file://$HOSTNAME/path</c>.
        /// Null or empty accepts only an empty host and <c>localhost</c>. Passed in rather than read
        /// here so the classification stays pure.
        /// </param>
        public static LinkTarget Classify(string? link, bool isWindows, IReadOnlyCollection<string>? localHostNames = null)
        {
            if (string.IsNullOrWhiteSpace(link)) return LinkTarget.Rejected;

            // The scheme is read from the raw text before Uri parses it, as the scheme allowlist this
            // replaced always did. Uri.TryCreate trims surrounding whitespace, and off Windows it
            // accepts a bare "/path" as an implicit file URI, so trusting the parsed scheme alone
            // would quietly make more text clickable than before.
            int colon = link.IndexOf(':');
            if (colon <= 0) return LinkTarget.Rejected;
            string scheme = link.Substring(0, colon);

            if (!Uri.TryCreate(link, UriKind.Absolute, out Uri? uri)) return LinkTarget.Rejected;
            if (!uri.Scheme.Equals(scheme, StringComparison.OrdinalIgnoreCase)) return LinkTarget.Rejected;

            if (scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
                scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase))
            {
                return LinkTarget.External(uri);
            }

            if (scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                return ClassifyFile(uri, isWindows, localHostNames);
            }

            return LinkTarget.Rejected;
        }

        private static LinkTarget ClassifyFile(Uri uri, bool isWindows, IReadOnlyCollection<string>? localHostNames)
        {
            // The authority names the machine the path lives on. Only an empty one, "localhost"
            // (RFC 8089) or this machine's own name means here; any other host is remote (an SSH
            // session's hostname, say, whose path does not exist on this machine), and on Windows it
            // is also the UNC server the OS would connect to. A host that does name this machine
            // only ever selects the local reading of the path: the host is judged here, before the
            // path, because Uri reports such targets as UNC too (file://localhost/C:/x is
            // \\localhost\C:\x to it), which is why the path below comes from AbsolutePath and never
            // from LocalPath, and why it must pass the same local-shape rules as a hostless link.
            if (uri.Host.Length > 0
                    ? !NamesThisMachine(uri, localHostNames)
                    : uri.IsUnc)
            {
                return LinkTarget.Rejected;
            }

            // A path has no query; one here means the text was not a plain path (file:////?/C:/x
            // parses as the path "//" plus a query, for example). A fragment is dropped instead:
            // some tools point at a place inside a file with one, and it cannot change which file.
            if (uri.Query.Length > 0) return LinkTarget.Rejected;

            string path = Uri.UnescapeDataString(uri.AbsolutePath);
            foreach (char c in path)
            {
                if (char.IsControl(c)) return LinkTarget.Rejected;
            }

            string? local = isWindows ? ToWindowsDrivePath(path) : ToPosixPath(path);
            return local is null ? LinkTarget.Rejected : LinkTarget.Local(local);
        }

        /// <summary>Whether a <c>file:</c> link's (non-empty) host is this machine.</summary>
        /// <remarks>
        /// Names are compared by their first DNS label, case-insensitively: a shell's
        /// <c>$HOSTNAME</c> may or may not carry a domain (<c>box</c>, <c>box.local</c>,
        /// <c>box.lan</c>), and Windows' machine name is the upper-case NetBIOS form. That is loose on
        /// purpose, and safe because a match never reaches the network: nothing resolves or contacts
        /// the host, it only decides that the path is read as a local one. The worst a false match
        /// can do is reveal a local path the link happened to name. IP-address hosts are never
        /// matched; no local name is an address.
        /// </remarks>
        private static bool NamesThisMachine(Uri uri, IReadOnlyCollection<string>? localHostNames)
        {
            if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
            if (uri.HostNameType != UriHostNameType.Dns || localHostNames is null) return false;

            string label = FirstLabel(uri.Host);
            foreach (string name in localHostNames)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (FirstLabel(name.Trim()).Equals(label, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private static string FirstLabel(string host)
        {
            int dot = host.IndexOf('.');
            return dot < 0 ? host : host.Substring(0, dot);
        }

        /// <summary>
        /// The one local shape accepted on Windows: a fully qualified drive path, <c>X:\...</c>.
        /// </summary>
        /// <remarks>
        /// A single positive rule rather than a list of bad prefixes, because it is what shuts out
        /// every route to a remote or device path at once: <c>\\server\share</c>, the <c>\\?\</c> and
        /// <c>\\.\</c> device namespaces, and the <c>///server/share</c> that
        /// <c>file:///%5C%5Cserver%5Cshare</c> parses to with an empty host, which Uri does not call
        /// UNC but Windows resolves as one. Nothing that starts with a drive root can turn back into
        /// one of those, however many separators or dot segments follow it. A rooted path with no
        /// drive (<c>\x</c>, <c>/home/u</c>) is refused too: it would resolve against whichever drive
        /// the process happens to be on.
        /// </remarks>
        private static string? ToWindowsDrivePath(string path)
        {
            // Uri hands a drive path back as "C:/x" for file:///C:/x, but as "/C:/x" for
            // file://localhost/C:/x. Exactly one leading slash is dropped; "//C:/x" stays refused.
            int start = path.Length > 0 && path[0] == '/' ? 1 : 0;
            if (!IsDriveRoot(path, start)) return null;

            var native = new StringBuilder(path.Length - start);
            native.Append(path, start, 2); // "X:"
            for (int i = start + 2; i < path.Length; i++)
            {
                char c = path[i] == '/' ? '\\' : path[i];

                // Collapse separator runs; Uri keeps "C:/a//b" as is.
                if (c == '\\' && native[native.Length - 1] == '\\') continue;

                // Characters Windows allows nowhere in a path. Refusing '"' is also what lets the App
                // quote the path for explorer.exe without an escaping scheme, and ':' past the drive
                // only ever means an alternate data stream.
                if (c is '"' or '<' or '>' or '|' or '?' or '*' or ':') return null;

                native.Append(c);
            }

            return native.ToString();
        }

        /// <summary>The one local shape accepted off Windows: an absolute POSIX path.</summary>
        /// <remarks>
        /// A path whose root is followed by another separator is refused: that is the UNC shape
        /// surviving an empty-host URI (<c>file:///%5C%5Chost%5Cshare</c>), and POSIX leaves the
        /// meaning of a leading <c>//</c> to the implementation anyway. A drive path is refused
        /// because it names a Windows machine, not this one.
        /// </remarks>
        private static string? ToPosixPath(string path)
        {
            if (path.Length == 0 || path[0] != '/') return null;
            if (path.Length > 1 && (path[1] == '/' || path[1] == '\\')) return null;
            if (IsDriveRoot(path, 1)) return null;
            return path;
        }

        private static bool IsDriveRoot(string path, int index) =>
            path.Length > index + 2 &&
            char.IsAsciiLetter(path[index]) &&
            path[index + 1] == ':' &&
            (path[index + 2] == '/' || path[index + 2] == '\\');
    }
}
