using System.Reflection;
using Ntilde.Shell;

namespace Ntilde.Tests.Shell;

/// <summary>
/// <see cref="AppVersionInfo"/>: the one place the app reads its own version. The About window shows the
/// informational version as built; the remote installer asks for the release with that version, which
/// carries no build metadata.
/// </summary>
public sealed class AppVersionInfoTests
{
    [Fact]
    public void An_assembly_s_version_is_its_informational_version()
    {
        Assembly app = typeof(AppVersionInfo).Assembly;

        Assert.Equal(app.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion, AppVersionInfo.Of(app));
    }

    [Fact]
    public void No_assembly_has_no_version()
    {
        Assert.Null(AppVersionInfo.Of(null));
    }

    [Fact]
    public void The_running_version_is_the_entry_assembly_s()
    {
        Assert.Equal(AppVersionInfo.Of(Assembly.GetEntryAssembly()), AppVersionInfo.InformationalVersion);
        Assert.Equal(AppVersionInfo.WithoutBuildMetadata(AppVersionInfo.InformationalVersion), AppVersionInfo.Version);
        Assert.DoesNotContain('+', AppVersionInfo.Version);
    }

    [Theory]
    [InlineData("0.11.0", "0.11.0")]
    [InlineData("0.11.0+3f2c1ab9e", "0.11.0")]
    [InlineData("0.12.0-dev+3f2c1ab9e", "0.12.0-dev")]
    [InlineData(" 0.11.0 ", "0.11.0")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Build_metadata_is_not_part_of_the_release_version(string? informational, string release)
    {
        Assert.Equal(release, AppVersionInfo.WithoutBuildMetadata(informational));
    }
}
