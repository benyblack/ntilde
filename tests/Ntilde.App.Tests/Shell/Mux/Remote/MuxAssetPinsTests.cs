using System.Xml.Linq;
using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Tests.Shell.Mux.Remote;

/// <summary>
/// The SHA-256 pins <c>release.yml</c> embeds in the App (Phase 5 Task 10): one resource per RID, named
/// <c>Ntilde.Resources.mux-sha256.ntilde-mux-&lt;rid&gt;.sha256</c>, holding a <c>sha256sum</c> line.
/// </summary>
public sealed class MuxAssetPinsTests
{
    private const string LinuxHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string MacHash = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
    private const string Prefix = "Ntilde.Resources.mux-sha256.";

    [Fact]
    public void A_sha256sum_line_pins_its_rid()
    {
        var pins = MuxAssetPins.Parse([
            new($"{Prefix}ntilde-mux-linux-x64.sha256", $"{LinuxHash}  ntilde-mux-linux-x64\n"),
            new($"{Prefix}ntilde-mux-osx-arm64.sha256", $"{MacHash.ToUpperInvariant()} *ntilde-mux-osx-arm64\r\n"),
        ]);

        Assert.Equal(2, pins.Count);
        Assert.Equal(LinuxHash, pins["linux-x64"]);
        Assert.Equal(MacHash, pins["osx-arm64"]);
    }

    [Fact]
    public void Malformed_pins_and_foreign_resources_are_ignored()
    {
        var pins = MuxAssetPins.Parse([
            new($"{Prefix}ntilde-mux-linux-arm64.sha256", "<html>not a checksum</html>"),
            new($"{Prefix}ntilde-mux-.sha256", $"{LinuxHash}  ntilde-mux-\n"),
            new($"{Prefix}ntilde-mux-../x.sha256", $"{LinuxHash}  x\n"),
            new($"{Prefix}other-linux-x64.sha256", $"{LinuxHash}  other\n"),
            new("Ntilde.Resources.vt-conformance-report.json", "{}"),
            new($"{Prefix}ntilde-mux-linux-x64.sha256", $"{LinuxHash}  ntilde-mux-linux-x64\n"),
        ]);

        Assert.Equal(["linux-x64"], pins.Keys);
    }

    [Fact]
    public void No_resources_means_no_pins()
    {
        Assert.Empty(MuxAssetPins.Parse([]));
    }

    [Fact]
    public void The_csproj_resource_name_is_the_one_MuxAssetPins_reads()
    {
        // Ntilde.App.csproj's LogicalName and MuxAssetPins.ResourcePrefix are two halves of one contract
        // that nothing else ties together: a rename of either would silently embed pins nobody reads.
        string csproj = Path.Combine(FindRepositoryRoot(), "src", "Ntilde.App", "Ntilde.App.csproj");
        XElement item = XDocument.Load(csproj).Descendants("EmbeddedResource")
            .Single(e => ((string?)e.Attribute("Condition"))?.Contains("NtildeMuxSha256Dir", StringComparison.Ordinal) == true);
        Assert.Contains("NtildeMuxSha256Dir", (string?)item.Attribute("Include"), StringComparison.Ordinal);

        string logicalName = (string?)item.Attribute("LogicalName") ?? item.Element("LogicalName")?.Value ?? "";
        string expanded = logicalName
            .Replace("%(Filename)", "ntilde-mux-linux-x64", StringComparison.Ordinal)
            .Replace("%(Extension)", ".sha256", StringComparison.Ordinal);

        Assert.Equal(MuxAssetPins.ResourcePrefix + "linux-x64.sha256", expanded);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Ntilde.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }

    [Fact]
    public void A_build_without_the_property_embeds_nothing()
    {
        // The test and CI builds pass no NtildeMuxSha256Dir, so behaviour is exactly what it was.
        Assert.Empty(MuxAssetPins.Load());
    }
}
