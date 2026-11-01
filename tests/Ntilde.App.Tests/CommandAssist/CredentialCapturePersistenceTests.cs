using Ntilde.CommandAssist.Application;
using Ntilde.CommandAssist.Domain;
using Ntilde.CommandAssist.Models;
using Ntilde.CommandAssist.ShellIntegration.Contracts;
using Ntilde.CommandAssist.Storage;

namespace Ntilde.Tests.CommandAssist;

public sealed class CredentialCapturePersistenceTests
{
    private const string Secret = "synthetic_secret_677";

    public static TheoryData<bool, string> Captures
    {
        get
        {
            var data = new TheoryData<bool, string>();
            foreach (bool structured in new[] { false, true })
                foreach (string command in new[]
                {
                $"tool \"--token=prefix {Secret}\" --verbose",
                $"env \"GITHUB_TOKEN=prefix {Secret}\" tool",
                $"tool --token \"`printf '%s' \"prefix {Secret}\"`\" --verbose",
                $"tool --token \"prefix`\" middle`\" {Secret}\" --verbose",
                $"tool --token \"prefix\\`\" {Secret}\" --verbose",
                $"tool --token prefix\\` {Secret} --verbose",
                $"tool --token {Secret} --verbose", $"tool --api-key=\"{Secret} with spaces\" --verbose",
                $"GITHUB_TOKEN={Secret} gh repo list", $"export OPENAI_API_KEY='{Secret}'",
                $"$env:GITHUB_TOKEN = '{Secret}'; gh repo list", $"set \"NPM_TOKEN={Secret} with spaces\" && tool"
            }) data.Add(structured, command);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Captures))]
    public async Task SupportedCredentialNeverReachesHistoryJsonInEitherCapturePath(bool structured, string command)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ntilde-677-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "history.jsonl");
        try
        {
            var store = new JsonlHistoryStore(path, maxEntries: 50);
            var context = new AssistSessionContext();
            context.UpdateSession("bash", "/synthetic", "profile", "session", "host", false, false);
            var pipeline = new CapturePipeline(store, new SecretsFilter(), context);
            if (structured)
            {
                await pipeline.HandleShellIntegrationEventAsync(new ShellIntegrationEvent(
                    ShellIntegrationEventType.CommandAccepted, DateTimeOffset.UtcNow, command, "/synthetic", null, null));
                await pipeline.HandleShellIntegrationEventAsync(new ShellIntegrationEvent(
                    ShellIntegrationEventType.CommandFinished, DateTimeOffset.UtcNow, null, null, 0, TimeSpan.FromMilliseconds(1)));
            }
            else
            {
                await pipeline.CaptureSubmissionAsync(command, isSubmissionSuppressed: false);
                await pipeline.CompleteSubmissionAsync(0);
            }
            string json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(Secret, json, StringComparison.Ordinal);
            CommandHistoryEntry entry = Assert.Single(await store.GetRecentAsync(10, TestContext.Current.CancellationToken));
            Assert.True(entry.IsRedacted);
            Assert.Contains("[REDACTED]", entry.CommandText, StringComparison.Ordinal);
            Assert.Equal(0, entry.ExitCode);
            Assert.Equal(structured ? CommandCaptureSource.ShellIntegration : CommandCaptureSource.Heuristic, entry.Source);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
