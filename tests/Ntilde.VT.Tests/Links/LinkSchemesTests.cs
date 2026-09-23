using Ntilde.VT.Links;

namespace Ntilde.VT.Tests.Links;

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
