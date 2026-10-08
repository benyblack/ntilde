using System.Diagnostics;
using Ntilde.Shell;

namespace Ntilde.Tests.Shell;

/// <summary>
/// What a Ctrl+click on a terminal link launches, routed through a recording environment so nothing
/// real is probed or started. Plain facts: <see cref="TerminalLinkOpener"/> never touches Avalonia.
/// </summary>
/// <remarks>
/// The environment's platform flags are set per test rather than read from the host, so every
/// platform's route runs on every CI lane.
/// </remarks>
public sealed class TerminalLinkOpenerTests
{
    private sealed class RecordingEnvironment : ILinkLaunchEnvironment
    {
        public bool IsWindows { get; init; }

        public bool IsMacOS { get; init; }

        /// <summary>
        /// Always set, so every rejection below also proves that knowing this machine's name does not
        /// loosen anything for a host that is not it.
        /// </summary>
        public IReadOnlyCollection<string> LocalHostNames { get; init; } = new[] { "DEVBOX", "devbox" };

        public HashSet<string> Files { get; } = new();

        public HashSet<string> Directories { get; } = new();

        /// <summary>Every path the opener asked the filesystem about.</summary>
        public List<string> Probed { get; } = new();

        public List<ProcessStartInfo> Started { get; } = new();

        public Exception? ThrowOnStart { get; init; }

        /// <summary>Where xdg-open is found on PATH; null when it is not installed.</summary>
        public string? XdgOpenPath { get; init; } = "/usr/bin/xdg-open";

        public string? FindOnPath(string executableName) =>
            executableName == "xdg-open" ? XdgOpenPath : null;

        public bool FileExists(string path)
        {
            Probed.Add(path);
            return Files.Contains(path);
        }

        public bool DirectoryExists(string path)
        {
            Probed.Add(path);
            return Directories.Contains(path);
        }

        public void Start(ProcessStartInfo startInfo)
        {
            if (ThrowOnStart is not null) throw ThrowOnStart;
            Started.Add(startInfo);
        }
    }

    private static RecordingEnvironment Windows() => new() { IsWindows = true };

    private static RecordingEnvironment MacOS() => new() { IsMacOS = true };

    private static RecordingEnvironment Linux() => new();

    private static RecordingEnvironment[] AllPlatforms() => new[] { Windows(), MacOS(), Linux() };

    // ---------------------------------------------------------------- web and mail: unchanged

    [Theory]
    [InlineData("https://example.com/a?b=c")]
    [InlineData("http://example.com")]
    [InlineData("mailto:me@example.com")]
    public void Web_and_mail_links_still_go_to_the_OS_URL_handler(string link)
    {
        foreach (RecordingEnvironment env in AllPlatforms())
        {
            Assert.True(new TerminalLinkOpener(env).TryOpen(link));

            ProcessStartInfo started = Assert.Single(env.Started);
            Assert.Equal(new Uri(link).ToString(), started.FileName);
            Assert.True(started.UseShellExecute);
            Assert.Empty(env.Probed);
        }
    }

    // ---------------------------------------------------------------- local files: revealed, never opened

    [Fact]
    public void Windows_local_file_is_selected_in_Explorer_not_opened()
    {
        var env = Windows();
        env.Files.Add(@"C:\Users\me\Downloads\evil.exe");

        Assert.True(new TerminalLinkOpener(env).TryOpen("file:///C:/Users/me/Downloads/evil.exe"));

        ProcessStartInfo started = Assert.Single(env.Started);
        Assert.False(started.UseShellExecute);
        Assert.Equal("explorer.exe", Path.GetFileName(started.FileName.Replace('\\', '/')));
        Assert.Equal(@"/select,""C:\Users\me\Downloads\evil.exe""", started.Arguments);
        Assert.Empty(started.ArgumentList);
    }

    /// <summary>
    /// A comma is explorer.exe's switch separator, so the path is always quoted, not only when it has
    /// a space.
    /// </summary>
    [Fact]
    public void Windows_file_path_with_commas_and_spaces_is_quoted_whole_for_Explorer()
    {
        var env = Windows();
        env.Files.Add(@"C:\a,b c\x.txt");

        Assert.True(new TerminalLinkOpener(env).TryOpen("file://localhost/C:/a,b%20c/x.txt"));

        Assert.Equal(@"/select,""C:\a,b c\x.txt""", Assert.Single(env.Started).Arguments);
    }

    [Theory]
    [InlineData("file:///C:/Users/me/dir", @"C:\Users\me\dir", @"""C:\Users\me\dir""")]
    [InlineData("file:///C:/Users/me/dir/", @"C:\Users\me\dir\", @"""C:\Users\me\dir""")]
    [InlineData("file:///C:/", @"C:\", @"C:\")]
    public void Windows_local_directory_opens_in_Explorer(string link, string directory, string expectedArguments)
    {
        var env = Windows();
        env.Directories.Add(directory);

        Assert.True(new TerminalLinkOpener(env).TryOpen(link));

        ProcessStartInfo started = Assert.Single(env.Started);
        Assert.False(started.UseShellExecute);
        Assert.Equal("explorer.exe", Path.GetFileName(started.FileName.Replace('\\', '/')));
        Assert.Equal(expectedArguments, started.Arguments);
    }

    /// <summary>
    /// Finder reveals directories as well as files: an application bundle is a directory, and a plain
    /// <c>open</c> on one would launch it.
    /// </summary>
    [Theory]
    [InlineData("file:///Users/me/Downloads/evil.command", "/Users/me/Downloads/evil.command", false)]
    [InlineData("file:///Users/me/Downloads/Evil.app", "/Users/me/Downloads/Evil.app", true)]
    public void MacOS_local_path_is_revealed_in_Finder(string link, string path, bool isDirectory)
    {
        var env = MacOS();
        (isDirectory ? env.Directories : env.Files).Add(path);

        Assert.True(new TerminalLinkOpener(env).TryOpen(link));

        ProcessStartInfo started = Assert.Single(env.Started);
        Assert.False(started.UseShellExecute);
        Assert.Equal("/usr/bin/open", started.FileName);
        Assert.Equal(new[] { "-R", path }, started.ArgumentList);
        Assert.Equal(string.Empty, started.Arguments);
    }

    [Theory]
    [InlineData("file:///home/u/Downloads/evil.sh", "/home/u/Downloads/evil.sh", false, "/home/u/Downloads")]
    [InlineData("file:///evil.sh", "/evil.sh", false, "/")]
    [InlineData("file:///home/u/my%20dir", "/home/u/my dir", true, "/home/u/my dir")]
    public void Linux_local_file_opens_its_folder_and_a_directory_opens_itself(
        string link, string path, bool isDirectory, string expectedArgument)
    {
        var env = Linux();
        (isDirectory ? env.Directories : env.Files).Add(path);

        Assert.True(new TerminalLinkOpener(env).TryOpen(link));

        ProcessStartInfo started = Assert.Single(env.Started);
        Assert.False(started.UseShellExecute);
        Assert.Equal("/usr/bin/xdg-open", started.FileName);
        Assert.Equal(new[] { expectedArgument }, started.ArgumentList);
    }

    [Fact]
    public void Linux_without_xdg_open_on_PATH_launches_nothing()
    {
        var env = new RecordingEnvironment { XdgOpenPath = null };
        env.Files.Add("/home/u/notes.txt");

        Assert.False(new TerminalLinkOpener(env).TryOpen("file:///home/u/notes.txt"));
        Assert.Empty(env.Started);
    }

    // ---------------------------------------------------------------- PATH lookup: absolute entries only

    /// <summary>
    /// A bare executable name would let Process.Start pick up a same-named file from the current
    /// directory, and so would a relative or empty PATH entry; only absolute entries are searched.
    /// </summary>
    [Fact]
    public void ResolveOnPath_searches_absolute_entries_in_order_and_skips_relative_ones()
    {
        string first = Path.Combine(Path.GetTempPath(), "ntilde-path-a");
        string second = Path.Combine(Path.GetTempPath(), "ntilde-path-b");
        char sep = Path.PathSeparator;
        string pathVariable = string.Join(sep, ".", "bin", "", first, second);
        var present = new HashSet<string>
        {
            Path.Combine(".", "xdg-open"),
            Path.Combine("bin", "xdg-open"),
            Path.Combine(second, "xdg-open"),
        };
        var asked = new List<string>();

        string? resolved = SystemLinkLaunchEnvironment.ResolveOnPath(
            "xdg-open", pathVariable, sep, candidate => { asked.Add(candidate); return present.Contains(candidate); });

        Assert.Equal(Path.Combine(second, "xdg-open"), resolved);
        Assert.Equal(new[] { Path.Combine(first, "xdg-open"), Path.Combine(second, "xdg-open") }, asked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ResolveOnPath_returns_null_without_a_PATH(string? pathVariable)
    {
        Assert.Null(SystemLinkLaunchEnvironment.ResolveOnPath("xdg-open", pathVariable, Path.PathSeparator, _ => true));
    }

    [Fact]
    public void ResolveOnPath_returns_null_when_no_absolute_entry_holds_the_executable()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ntilde-path-a");
        Assert.Null(SystemLinkLaunchEnvironment.ResolveOnPath("xdg-open", dir, Path.PathSeparator, _ => false));
    }

    // ---------------------------------------------------------------- this machine's own name (ls --hyperlink)

    /// <summary>
    /// <c>ls --hyperlink</c>, rg, fd and eza write <c>file://$HOSTNAME/path</c>. When the host is this
    /// machine the link routes exactly like a hostless one, and the only path ever probed is the local
    /// one: never <c>\\devbox\...</c>.
    /// </summary>
    [Fact]
    public void Host_naming_this_machine_routes_like_a_hostless_link()
    {
        var windows = Windows();
        windows.Files.Add(@"C:\Users\me\notes.txt");
        Assert.True(new TerminalLinkOpener(windows).TryOpen("file://DevBox/C:/Users/me/notes.txt"));
        Assert.Equal(@"/select,""C:\Users\me\notes.txt""", Assert.Single(windows.Started).Arguments);
        Assert.Equal(@"C:\Users\me\notes.txt", Assert.Single(windows.Probed));

        var mac = MacOS();
        string macPath = "/Users/me/notes.txt";
        mac.Files.Add(macPath);
        Assert.True(new TerminalLinkOpener(mac).TryOpen("file://DEVBOX.local/Users/me/notes.txt"));
        Assert.Equal(new[] { "-R", macPath }, Assert.Single(mac.Started).ArgumentList);

        var linux = Linux();
        linux.Files.Add("/home/u/notes.txt");
        Assert.True(new TerminalLinkOpener(linux).TryOpen("file://devbox.lan/home/u/notes.txt"));
        Assert.Equal("/home/u", Assert.Single(Assert.Single(linux.Started).ArgumentList));
    }

    /// <summary>
    /// On Windows a path under this machine's name with no drive is only reachable as
    /// <c>\\devbox\share\...</c>, so it stays inert and is never probed.
    /// </summary>
    [Fact]
    public void Host_naming_this_machine_without_a_drive_path_is_inert_on_Windows()
    {
        var env = Windows();
        env.Files.Add(@"\\devbox\share\x.lnk");

        Assert.False(new TerminalLinkOpener(env).TryOpen("file://devbox/share/x.lnk"));
        Assert.Empty(env.Started);
        Assert.Empty(env.Probed);
    }

    /// <summary>Without any local name (e.g. both reads failed) a hostname link is simply inert.</summary>
    [Fact]
    public void Host_link_is_inert_when_no_local_name_is_known()
    {
        var env = new RecordingEnvironment { LocalHostNames = Array.Empty<string>() };
        env.Files.Add("/home/u/notes.txt");

        Assert.False(new TerminalLinkOpener(env).TryOpen("file://devbox/home/u/notes.txt"));
        Assert.Empty(env.Probed);
    }

    [Fact]
    public void Local_path_that_does_not_exist_does_nothing()
    {
        foreach (var (env, link) in new[]
                 {
                     (Windows(), "file:///C:/nope.exe"),
                     (MacOS(), "file:///Users/me/nope"),
                     (Linux(), "file:///home/u/nope"),
                 })
        {
            Assert.False(new TerminalLinkOpener(env).TryOpen(link));
            Assert.Empty(env.Started);
        }
    }

    // ---------------------------------------------------------------- remote and UNC: inert, and never probed

    /// <summary>
    /// On Windows, merely asking whether a UNC path exists connects to the SMB host and offers the
    /// user's credentials, so a remote target must be refused before any filesystem call, not after.
    /// </summary>
    [Theory]
    [InlineData("file://attacker/share/x.lnk")]
    [InlineData("file:////attacker/share/x.lnk")]
    [InlineData("file:///%5C%5Cattacker%5Cshare%5Cx.lnk")]
    [InlineData("file://attacker.example.com/C:/Users/me/x.exe")]
    [InlineData("file:///%5C%5C%3F%5CUNC%5Cattacker%5Cshare%5Cx")]
    [InlineData("file://otherbox/home/u/x.txt")] // e.g. an SSH session's hostname
    [InlineData("file://otherbox/C:/Users/me/x.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://host/file")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejected_link_launches_nothing_and_touches_no_filesystem(string? link)
    {
        foreach (RecordingEnvironment env in AllPlatforms())
        {
            env.Files.Add(@"\\attacker\share\x.lnk"); // present, so only the rejection can keep it shut

            var opener = new TerminalLinkOpener(env);

            Assert.False(opener.IsActivatable(link));
            Assert.False(opener.TryOpen(link));
            Assert.Empty(env.Started);
            Assert.Empty(env.Probed);
        }
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("mailto:me@example.com", true)]
    [InlineData("file:///C:/Users/me/x.exe", true)] // syntactic: hover does not probe the disk
    [InlineData("file://attacker/share/x.lnk", false)]
    [InlineData("ftp://host/file", false)]
    public void Hover_clickability_follows_the_classification_without_probing(string link, bool expected)
    {
        var env = Windows();

        Assert.Equal(expected, new TerminalLinkOpener(env).IsActivatable(link));
        Assert.Empty(env.Probed);
    }

    [Fact]
    public void Failed_launch_reports_false()
    {
        var env = new RecordingEnvironment { IsWindows = true, ThrowOnStart = new InvalidOperationException("no handler") };
        env.Files.Add(@"C:\x.txt");
        var opener = new TerminalLinkOpener(env);

        Assert.False(opener.TryOpen("https://example.com"));
        Assert.False(opener.TryOpen("file:///C:/x.txt"));
    }
}
