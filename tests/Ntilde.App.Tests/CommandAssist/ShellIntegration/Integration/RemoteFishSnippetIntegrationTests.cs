using Ntilde.CommandAssist.ShellIntegration.Remote;

namespace Ntilde.Tests.CommandAssist.ShellIntegration.Integration;

/// <summary>
/// The shipped fish snippet (what the remote installer writes into <c>conf.d</c>), run through a
/// real fish on a real PTY. <see cref="FishShellIntegrationTests"/> covers the local bootstrap,
/// which is a separate script. Skipped at runtime when fish is not on PATH.
/// </summary>
[Trait("Category", "ShellIntegration")]
[Collection(nameof(ShellIntegrationCollection))]
public sealed class RemoteFishSnippetIntegrationTests : IDisposable
{
    private readonly string _tempRoot;

    public RemoteFishSnippetIntegrationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"ntilde_fish_snippet_{Guid.NewGuid():N}");
        string confD = Path.Combine(_tempRoot, "fish", "conf.d");
        Directory.CreateDirectory(confD);
        File.WriteAllText(
            Path.Combine(confD, "ntilde-shell-integration.fish"),
            RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.Fish));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private HarnessResult RunFish(string stdin, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        string? fish = ShellHarness.FindFish();
        if (fish is null)
        {
            Assert.Skip("fish not found on this system");
        }

        // fish sources $XDG_CONFIG_HOME/fish/conf.d/*.fish, which is where the installer puts the
        // snippet (under ~/.config) on a real host.
        var env = new Dictionary<string, string>
        {
            ["XDG_CONFIG_HOME"] = _tempRoot,
            ["HOME"] = _tempRoot,
        };
        foreach (var kv in extraEnv ?? new Dictionary<string, string>())
        {
            env[kv.Key] = kv.Value;
        }

        return ShellHarness.Run(fish, "-i", stdin, env, TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void Snippet_ReportsTheCommandAndItsExitCode()
    {
        HarnessResult result = RunFish("echo hello\nfalse\nexit 0\n");

        Assert.Contains(result.Events, e => e.Kind == "B");
        Assert.Contains(result.Events, e => e.Kind == "C" && e.DecodedCommand == "echo hello");
        Assert.Contains(result.Events, e => e.Kind == "D" && e.DecodedFinish.exitCode == 0);
        Assert.Contains(result.Events, e => e.Kind == "D" && e.DecodedFinish.exitCode is { } code && code != 0);
    }

    [Fact]
    public void Snippet_MarksEachCommandOnce()
    {
        HarnessResult result = RunFish("true\nfalse\nexit 0\n");

        Assert.Contains(result.Events, e => e.Kind == "D");
        ShellHarness.AssertEachCommandMarkedOnce(result.Events);
    }

    [Fact]
    public void Snippet_WritesItsOwnCommandMarks_WhenFishsAreTurnedOff()
    {
        HarnessResult result = RunFish(
            "echo hello\nexit 0\n",
            extraEnv: new Dictionary<string, string> { ["fish_features"] = "no-mark-prompt" });

        Assert.Contains(result.Events, e => e.Kind == "C" && e.DecodedCommand == "echo hello");
        Assert.Contains(result.Events, e => e.Kind == "D" && e.DecodedFinish is (0, not null));
        ShellHarness.AssertEachCommandMarkedOnce(result.Events, oneWriterOfB: true);
    }
}
