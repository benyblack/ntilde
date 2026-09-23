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

        public HashSet<string> Files { get; } = new();

        public HashSet<string> Directories { get; } = new();

        /// <summary>Every path the opener asked the filesystem about.</summary>
        public List<string> Probed { get; } = new();

        public List<ProcessStartInfo> Started { get; } = new();

        public Exception? ThrowOnStart { get; init; }

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
        Assert.Equal("xdg-open", started.FileName);
        Assert.Equal(new[] { expectedArgument }, started.ArgumentList);
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
