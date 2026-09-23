using Ntilde.Platform.Links;

namespace Ntilde.Platform.Tests.Links;

/// <summary>
/// What a Ctrl+click on a terminal link may do. Link targets are untrusted program output (an OSC 8
/// target under arbitrary display text, or a <c>scheme://</c> the URL detector found in plain text), so
/// a <c>file:</c> link is accepted only as a plain local path, which the App reveals rather than opens.
/// </summary>
/// <remarks>
/// Every case runs for both values of <c>isWindows</c> where it should not depend on it, so the Windows
/// rules are exercised on the Linux CI lane and the POSIX rules on the Windows one.
/// </remarks>
public class LinkSchemesTests
{
    private static readonly bool[] BothPlatforms = { true, false };

    // ---------------------------------------------------------------- web and mail: unchanged

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com/path?q=1#frag")]
    [InlineData("HTTPS://EXAMPLE.COM")] // case-insensitive scheme
    [InlineData("mailto:me@example.com")]
    public void Web_and_mail_links_open_externally(string link)
    {
        foreach (bool isWindows in BothPlatforms)
        {
            LinkTarget target = LinkSchemes.Classify(link, isWindows);

            Assert.Equal(LinkAction.OpenExternal, target.Action);
            Assert.Equal(new Uri(link), target.ExternalUri);
            Assert.Null(target.LocalPath);
        }
    }

    // ---------------------------------------------------------------- local file links: revealed

    [Theory]
    [InlineData("file:///C:/x.exe", @"C:\x.exe")]
    [InlineData("file:///c:/tmp/x.txt", @"c:\tmp\x.txt")]
    [InlineData("FILE:///C:/x.exe", @"C:\x.exe")] // mixed-case scheme
    [InlineData("file://localhost/C:/x.exe", @"C:\x.exe")]
    [InlineData("file://LOCALHOST/C:/Users/me/a%20b.txt", @"C:\Users\me\a b.txt")]
    [InlineData("file:///C:/", @"C:\")]
    [InlineData("file:///C:/dir/", @"C:\dir\")]
    [InlineData("file:///C:/a/../../x.exe", @"C:\x.exe")] // dot segments cannot climb off the drive
    [InlineData("file:///C:/a%5C..%5C..%5C%5Chost%5Cshare", @"C:\host\share")] // still on C:
    [InlineData("file:///C:/notes.md#section", @"C:\notes.md")] // fragment ignored
    public void Windows_drive_path_is_revealed_on_Windows(string link, string expected)
    {
        LinkTarget target = LinkSchemes.Classify(link, isWindows: true);

        Assert.Equal(LinkAction.RevealLocalPath, target.Action);
        Assert.Equal(expected, target.LocalPath);
        Assert.Null(target.ExternalUri);
    }

    [Theory]
    [InlineData("file:///home/u/x", "/home/u/x")]
    [InlineData("file://localhost/home/u/x", "/home/u/x")]
    [InlineData("FILE:///home/u/my%20notes.txt", "/home/u/my notes.txt")] // mixed-case scheme
    [InlineData("file:///", "/")]
    public void Posix_path_is_revealed_off_Windows(string link, string expected)
    {
        LinkTarget target = LinkSchemes.Classify(link, isWindows: false);

        Assert.Equal(LinkAction.RevealLocalPath, target.Action);
        Assert.Equal(expected, target.LocalPath);
        Assert.Null(target.ExternalUri);
    }

    /// <summary>
    /// A path shaped for the other OS is not a path on this machine. A driveless path on Windows would
    /// resolve against whatever drive the process happens to be on; a drive path off Windows names a
    /// Windows machine.
    /// </summary>
    [Theory]
    [InlineData("file:///home/u/x", true)]
    [InlineData("file://localhost/home/u/x", true)]
    [InlineData("file:///C:/x.exe", false)]
    [InlineData("file://localhost/C:/x.exe", false)]
    public void Path_shaped_for_the_other_platform_is_rejected(string link, bool isWindows)
    {
        Assert.Equal(LinkTarget.Rejected, LinkSchemes.Classify(link, isWindows));
    }

    // ---------------------------------------------------------------- remote, UNC and device paths: inert

    [Theory]
    [InlineData("file://attacker/share/x.lnk")]
    [InlineData("file://host/share/x")]
    [InlineData("file:////host/share/x")]
    [InlineData("file://///host/share/x")]
    [InlineData(@"file:///\\host\share\x")]
    // Empty host and Uri.IsUnc == false, but the path is ///host/share/x, which Windows resolves as UNC.
    [InlineData("file:///%5C%5Chost%5Cshare%5Cx")]
    [InlineData("file://localhost//host/share/x")]
    [InlineData("file://localhost/%5C%5Chost/share")]
    [InlineData("file://host")]
    [InlineData("file://127.0.0.1/C:/x.exe")]
    [InlineData("file://[::1]/C:/x.exe")]
    [InlineData("file://localhost./C:/x.exe")]
    [InlineData("file://attacker/home/u/x")]
    // Device namespaces: \\?\ and \\.\, in each spelling that survives URI parsing.
    [InlineData("file:///%5C%5C%3F%5CC:%5Cx.exe")]
    [InlineData(@"file:///\\?\C:\x.exe")]
    [InlineData("file:////?/C:/x.exe")]
    [InlineData("file:////?/UNC/host/share/x")]
    [InlineData("file:///%5C%5C.%5Cpipe%5Cx")]
    [InlineData("file:////./pipe/x")]
    public void Remote_unc_and_device_file_links_are_rejected(string link)
    {
        foreach (bool isWindows in BothPlatforms)
        {
            Assert.Equal(LinkTarget.Rejected, LinkSchemes.Classify(link, isWindows));

            // Knowing this machine's name must not loosen anything for a host that is not it.
            Assert.Equal(LinkTarget.Rejected, LinkSchemes.Classify(link, isWindows, MyBox));
        }
    }

    // ---------------------------------------------------------------- this machine's own name: local

    /// <summary>The names the App would pass: the NetBIOS-style machine name and the DNS host name.</summary>
    private static readonly string[] MyBox = { "MYBOX", "mybox" };

    /// <summary>
    /// <c>ls --hyperlink</c>, rg, fd and eza write <c>file://$HOSTNAME/path</c>. A host naming this
    /// machine reads exactly like an empty host: the path is taken from the URI path, never turned
    /// into <c>\\host\...</c>, and still has to be a drive path on Windows.
    /// </summary>
    [Theory]
    [InlineData("file://mybox/C:/Users/me/x.exe", @"C:\Users\me\x.exe")]
    [InlineData("file://MyBox/C:/Users/me/a%20b.txt", @"C:\Users\me\a b.txt")] // different case
    [InlineData("file://mybox.lan/C:/x.txt", @"C:\x.txt")] // domain suffix
    public void Host_naming_this_machine_is_local_on_Windows(string link, string expected)
    {
        LinkTarget target = LinkSchemes.Classify(link, isWindows: true, MyBox);

        Assert.Equal(LinkAction.RevealLocalPath, target.Action);
        Assert.Equal(expected, target.LocalPath);
    }

    [Theory]
    [InlineData("file://mybox/home/u/x", "/home/u/x")]
    [InlineData("file://MYBOX/home/u/my%20notes.txt", "/home/u/my notes.txt")] // different case
    [InlineData("file://mybox.local/home/u/x", "/home/u/x")] // macOS-style .local suffix
    public void Host_naming_this_machine_is_local_off_Windows(string link, string expected)
    {
        LinkTarget target = LinkSchemes.Classify(link, isWindows: false, MyBox);

        Assert.Equal(LinkAction.RevealLocalPath, target.Action);
        Assert.Equal(expected, target.LocalPath);
    }

    private static readonly string[] FullyQualifiedDevBox = { "devbox.example.com" };

    /// <summary>A local name that itself carries a domain still matches a bare link host.</summary>
    [Fact]
    public void Fully_qualified_local_name_matches_a_bare_link_host()
    {
        LinkTarget target = LinkSchemes.Classify("file://devbox/home/u/x", isWindows: false, FullyQualifiedDevBox);

        Assert.Equal(LinkTarget.Local("/home/u/x"), target);
    }

    /// <summary>
    /// A matching host only picks the local reading of the path; it is not a way past the path rules.
    /// On Windows, <c>file://mybox/share/x</c> is <c>\\mybox\share\x</c> to Uri and must not become that.
    /// </summary>
    [Theory]
    [InlineData("file://mybox/share/x", true)] // no drive: would only be reachable as UNC
    [InlineData("file://mybox//host/share/x", true)]
    [InlineData("file://mybox//host/share/x", false)]
    [InlineData("file://mybox/%5C%5Chost/share", true)]
    [InlineData("file://mybox/%5C%5Chost/share", false)]
    [InlineData("file://mybox/home/u/x", true)] // other platform's shape
    [InlineData("file://mybox/C:/x.exe", false)]
    [InlineData("file://mybox/C:/x.exe?y", true)] // query
    public void Host_naming_this_machine_still_needs_a_plain_local_path(string link, bool isWindows)
    {
        Assert.Equal(LinkTarget.Rejected, LinkSchemes.Classify(link, isWindows, MyBox));
    }

    /// <summary>
    /// Any other host stays remote: an SSH session's hostname names a machine whose path does not
    /// exist here. Near-misses included, since the match is on the whole first label.
    /// </summary>
    [Theory]
    [InlineData("file://otherbox/home/u/x")]
    [InlineData("file://otherbox/C:/x.exe")]
    [InlineData("file://myboxen/home/u/x")]
    [InlineData("file://box/home/u/x")]
    [InlineData("file://other.mybox/home/u/x")]
    [InlineData("file://attacker/share/x.lnk")]
    public void Host_not_naming_this_machine_is_rejected(string link)
    {
        foreach (bool isWindows in BothPlatforms)
        {
            Assert.Equal(LinkTarget.Rejected, LinkSchemes.Classify(link, isWindows, MyBox));
        }
    }

    /// <summary>
    /// With no usable local name the behaviour is exactly that of an empty name list: only an empty
    /// host and <c>localhost</c> are local.
    /// </summary>
    [Fact]
    public void Null_or_empty_local_names_accept_only_empty_host_and_localhost()
    {
        string[]?[] noNames = { null, Array.Empty<string>(), new[] { "" }, new[] { "  " } };

        foreach (string[]? names in noNames)
        {
            foreach (bool isWindows in BothPlatforms)
            {
                string drive = isWindows ? "C:/" : "";
                string expected = isWindows ? @"C:\x" : "/x";

                Assert.Equal(LinkTarget.Local(expected), LinkSchemes.Classify($"file:///{drive}x", isWindows, names));
                Assert.Equal(LinkTarget.Local(expected), LinkSchemes.Classify($"file://localhost/{drive}x", isWindows, names));
                Assert.Equal(LinkTarget.Rejected, LinkSchemes.Classify($"file://mybox/{drive}x", isWindows, names));
            }
        }
    }

    /// <summary>Things a plain local path never contains.</summary>
    [Theory]
    [InlineData("file:///C:/x%00y.exe", true)] // control character
    [InlineData("file:///home/u/x%0Ay", false)]
    [InlineData("file:///C:/x.exe?y", true)] // query
    [InlineData("file:///home/u/x?y", false)]
    [InlineData("file://localhost/C:", true)] // drive-relative, not fully qualified
    [InlineData("file:///C:/a%22b", true)] // characters Windows forbids in a path
    [InlineData("file:///C:/a%3Fb", true)]
    [InlineData("file:///C:/a%2Ab", true)]
    [InlineData("file:///C:/x.exe:stream", true)]
    public void Local_path_with_characters_no_plain_path_has_is_rejected(string link, bool isWindows)
    {
        Assert.Equal(LinkTarget.Rejected, LinkSchemes.Classify(link, isWindows));
    }

    // ---------------------------------------------------------------- other schemes and garbage

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("vbscript:msgbox")]
    [InlineData("ftp://host/file")]
    [InlineData("smb://host/share/x")]
    [InlineData("ms-settings:")]
    [InlineData("notaurl")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("::::")]
    [InlineData("http://")]
    [InlineData(" https://example.com")] // leading whitespace: the raw scheme is " https"
    [InlineData("file:")]
    [InlineData("file:x")]
    [InlineData(@"C:\x.exe")] // a bare path is not a link, whatever the platform
    [InlineData(@"\\host\share\x")]
    [InlineData("/home/u/x")]
    public void Other_schemes_and_garbage_are_rejected(string? link)
    {
        foreach (bool isWindows in BothPlatforms)
        {
            Assert.Equal(LinkTarget.Rejected, LinkSchemes.Classify(link, isWindows));
        }
    }
}
