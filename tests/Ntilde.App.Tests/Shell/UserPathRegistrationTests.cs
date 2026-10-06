using Ntilde.Shell;

namespace Ntilde.Tests.Shell;

/// <summary>
/// The install directory on the user PATH (Phase 4 spec §11.4), so <c>ntilde mux attach</c> typed at any
/// prompt finds <c>ntilde.com</c>. <see cref="UserPathRegistration.Merge"/> is pure and runs on every OS;
/// the write is tested through <see cref="UserPathRegistration.Update"/>'s seam with fake registry
/// access, never against the real user PATH.
/// </summary>
public sealed class UserPathRegistrationTests
{
    private const string Dir = @"C:\Users\u\AppData\Local\NtildeApp\current";

    [Theory]
    // Added to an empty or missing PATH.
    [InlineData(null, Dir, true, Dir)]
    [InlineData("", Dir, true, Dir)]
    // Appended when missing, after the others, which keep their order and spelling.
    [InlineData(@"C:\a;C:\b", Dir, true, @"C:\a;C:\b;" + Dir)]
    [InlineData(@"%USERPROFILE%\bin;C:\a", Dir, true, @"%USERPROFILE%\bin;C:\a;" + Dir)]
    // Empty entries are dropped from a PATH that is rewritten.
    [InlineData(@"C:\a;;C:\b;", Dir, true, @"C:\a;C:\b;" + Dir)]
    // Already there in another case: nothing to do, so the PATH comes back as it was.
    [InlineData(@"C:\a;c:\users\U\appdata\local\ntildeapp\CURRENT;;C:\b", Dir, true, @"C:\a;c:\users\U\appdata\local\ntildeapp\CURRENT;;C:\b")]
    // Removed: every match, whatever its case; the rest keep their order and %VAR% spellings.
    [InlineData(Dir + @";C:\a;C:\USERS\U\AppData\Local\NtildeApp\current;%USERPROFILE%\bin;" + Dir, Dir, false, @"C:\a;%USERPROFILE%\bin")]
    [InlineData(Dir, Dir, false, "")]
    [InlineData(null, Dir, false, "")]
    // Not there: nothing to do.
    [InlineData(@"C:\a;;C:\b", Dir, false, @"C:\a;;C:\b")]
    public void Merge_adds_once_and_removes_every_match(string? existing, string directory, bool add, string expected)
    {
        Assert.Equal(expected, UserPathRegistration.Merge(existing, directory, add));
    }

    /// <summary>
    /// <see cref="Path.TrimEndingDirectorySeparator(string)"/> is the platform's, and only Windows counts
    /// <c>\</c> as one; a PATH with backslashes only ever reaches <c>Merge</c> there.
    /// </summary>
    [Theory]
    [InlineData(@"C:\a;" + Dir + @"\;C:\b", Dir, true, @"C:\a;" + Dir + @"\;C:\b")]
    [InlineData(@"C:\a;" + Dir, Dir + @"\", true, @"C:\a;" + Dir)]
    [InlineData(@"C:\a;" + Dir + "/", Dir, true, @"C:\a;" + Dir + "/")]
    [InlineData(@"C:\a;" + Dir + @"\;" + Dir, Dir, false, @"C:\a")]
    public void Merge_tolerates_a_trailing_separator(string existing, string directory, bool add, string expected)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "A trailing backslash is a separator only on Windows.");
        Assert.Equal(expected, UserPathRegistration.Merge(existing, directory, add));
    }

    /// <summary>An entry is compared by what it expands to, and kept as it was written.</summary>
    [Fact]
    public void Merge_matches_an_entry_by_its_expansion()
    {
        string variable = "NTILDE_PATHREG_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, Dir);
        try
        {
            string existing = $@"%{variable}%;C:\a";

            Assert.Equal(existing, UserPathRegistration.Merge(existing, Dir, add: true));
            Assert.Equal(@"C:\a", UserPathRegistration.Merge(existing, Dir, add: false));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    private sealed class FakeUserPath(string? value)
    {
        public string? Value { get; private set; } = value;
        public int Writes { get; private set; }
        public int Announcements { get; private set; }

        public bool Update(string? directory, bool add) =>
            UserPathRegistration.Update(directory, add, () => Value, v => { Value = v; Writes++; }, () => Announcements++);
    }

    [Fact]
    public void Update_writes_the_merged_PATH_and_announces_it()
    {
        var path = new FakeUserPath(@"C:\a");

        Assert.True(path.Update(Dir, add: true));

        Assert.Equal(@"C:\a;" + Dir, path.Value);
        Assert.Equal(1, path.Writes);
        Assert.Equal(1, path.Announcements);
    }

    /// <summary>Every update re-runs the hook: a PATH that already has the directory is left alone.</summary>
    [Fact]
    public void Update_leaves_an_unchanged_PATH_alone()
    {
        var path = new FakeUserPath(@"C:\a;" + Dir);

        Assert.False(path.Update(Dir, add: true));
        Assert.False(new FakeUserPath(@"C:\a").Update(Dir, add: false));

        Assert.Equal(0, path.Writes);
        Assert.Equal(0, path.Announcements);
    }

    [Fact]
    public void Update_removes_the_directory()
    {
        var path = new FakeUserPath(Dir + @";C:\a");

        Assert.True(path.Update(Dir, add: false));

        Assert.Equal(@"C:\a", path.Value);
        Assert.Equal(1, path.Announcements);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Update_without_a_directory_touches_nothing(string? directory)
    {
        var path = new FakeUserPath(@"C:\a");

        Assert.False(path.Update(directory, add: true));

        Assert.Equal(@"C:\a", path.Value);
        Assert.Equal(0, path.Writes);
    }

    /// <summary>A Velopack hook must not fail an install, update or uninstall over the PATH: it logs and goes on.</summary>
    [Fact]
    public void Update_swallows_a_registry_failure()
    {
        Assert.False(UserPathRegistration.Update(Dir, true, () => throw new UnauthorizedAccessException("denied"), _ => { }, () => { }));
        Assert.False(UserPathRegistration.Update(Dir, true, () => @"C:\a", _ => throw new System.IO.IOException("gone"), () => { }));
    }
}
