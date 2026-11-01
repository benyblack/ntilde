using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.OpenSsh;

namespace Ntilde.Platform.Tests.Ssh;

public sealed class OpenSshConfigSnapshotTests
{
    [Fact]
    public async Task Interleaved_publications_keep_each_launch_and_cache_bound_to_its_snapshot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ntilde_config_race_{Guid.NewGuid():N}");
        using var firstPublished = new ManualResetEventSlim();
        using var finishFirst = new ManualResetEventSlim();
        var firstProfile = new SshProfile { Host = "first.example.invalid" };
        var secondProfile = new SshProfile { Id = firstProfile.Id, Host = "second.example.invalid" };
        var firstCompiler = new OpenSshConfigCompiler(root)
        {
            AfterConfigPublishedForTest = () =>
            {
                firstPublished.Set();
                Assert.True(finishFirst.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "Second publication did not finish.");
            }
        };
        Task<OpenSshCompilationResult> firstTask = Task.Run(() => firstCompiler.Compile([firstProfile], firstProfile.Id), TestContext.Current.CancellationToken);
        try
        {
            Assert.True(firstPublished.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "First config was not published.");
            OpenSshCompilationResult second = new OpenSshConfigCompiler(root).Compile([secondProfile], secondProfile.Id);
            finishFirst.Set();
            OpenSshCompilationResult first = await firstTask;
            firstCompiler.AfterConfigPublishedForTest = null;
            // A fresh instance must not trust a sidecar from a different publication.
            OpenSshCompilationResult cachedFirst = new OpenSshConfigCompiler(root).Compile([firstProfile], firstProfile.Id);
            Assert.Contains("HostName first.example.invalid", File.ReadAllText(cachedFirst.ConfigFilePath));
            Assert.Contains("HostName first.example.invalid", File.ReadAllText(first.ConfigFilePath));
            Assert.Contains("HostName second.example.invalid", File.ReadAllText(second.ConfigFilePath));
            Assert.NotEqual(first.ConfigFilePath, second.ConfigFilePath);
            Assert.Equal(first.ConfigFilePath, cachedFirst.ConfigFilePath);
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally
        {
            finishFirst.Set();
            await firstTask;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Same_content_published_after_cache_miss_is_reused_while_a_launch_holds_it_open()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ntilde_config_same_{Guid.NewGuid():N}");
        using var ready = new ManualResetEventSlim();
        using var publish = new ManualResetEventSlim();
        var profile = new SshProfile { Host = "same.example.invalid" };
        var compiler = new OpenSshConfigCompiler(root)
        {
            BeforeConfigPublicationForTest = () =>
            {
                ready.Set();
                Assert.True(publish.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            }
        };
        Task<OpenSshCompilationResult> pending = Task.Run(() => compiler.Compile([profile], profile.Id), TestContext.Current.CancellationToken);
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            OpenSshCompilationResult published = new OpenSshConfigCompiler(root).Compile([profile], profile.Id);
            // Windows forbids replacing this open file. Publishing the same bytes must
            // accept the winner's existing snapshot rather than try to replace it.
            using (var reader = new FileStream(published.ConfigFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                publish.Set();
                OpenSshCompilationResult result = await pending;
                Assert.Equal(published.ConfigFilePath, result.ConfigFilePath);
                Assert.Contains("HostName same.example.invalid", File.ReadAllText(result.ConfigFilePath));
            }
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally
        {
            publish.Set();
            await pending;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Cached_snapshot_content_is_validated_and_repaired()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ntilde_config_cache_{Guid.NewGuid():N}");
        try
        {
            var profile = new SshProfile { Host = "expected.example.invalid" };
            OpenSshCompilationResult result = new OpenSshConfigCompiler(root).Compile([profile], profile.Id);
            File.WriteAllText(result.ConfigFilePath, "Host *\n  HostName wrong.example.invalid\n");
            OpenSshCompilationResult repaired = new OpenSshConfigCompiler(root).Compile([profile], profile.Id);
            Assert.Contains("HostName expected.example.invalid", File.ReadAllText(repaired.ConfigFilePath));
            Assert.Equal(result.ConfigFilePath, repaired.ConfigFilePath);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
