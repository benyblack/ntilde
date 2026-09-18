using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Ntilde.Inference;

namespace Ntilde.AppTests.Inference;

/// <summary>
/// Replays the screens measured on 2026-09-17 against the live API. Runs only when
/// TYPESAFE_API_KEY is set in the environment; skipped otherwise, so CI never calls out.
/// Costs about 8 requests of ~1k input tokens.
/// </summary>
[Trait("Category", "Live")]
public class ScreenActivityLiveTests
{
    private sealed class EnvKey : IApiKeySource
    {
        public string? TryGetKey() => Environment.GetEnvironmentVariable("TYPESAFE_API_KEY");
    }

    private static readonly bool HasKey = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TYPESAFE_API_KEY"));

    private const string ClaudeDone =
        "  Merged PR #468, ran 3 shell commands\n\n● Done. Both pieces are in place.\n\n  Release notes for v0.10.0 are rewritten and published. README is updated on main via PR #468.\n\n  Local state is clean: main checkout at 9585a98, worktree and branches removed.\n\n  Still stale: the site's Install component lists a winget command. Say if you want that fixed.\n\n✻ Crunched for 49s · done 3:42 PM\n\n※ recap: v0.10.0 is released with rewritten release notes. Nothing is in progress.\n                                                             new task? /clear to save 216.6k tokens\n─────────────────────────────────────────────────────────────────────────────────────────────────────\n❯\n─────────────────────────────────────────────────────────────────────────────────────────────────────\n  ⏵⏵ auto mode on (shift+tab to cycle) · ← for agents";

    private const string ClaudeWorking =
        "● Read(src/Ntilde.App/Shell/TabStatusTracker.cs)\n  ⎿  Read 120 lines\n\n● Grep(pattern: \"MinAttentionBurst\")\n  ⎿  Found 3 files\n\n✻ Crunching… (12s · ↓ 1.4k tokens · esc to interrupt)\n─────────────────────────────────────────────────────────────────────────────────────────────────────\n❯\n─────────────────────────────────────────────────────────────────────────────────────────────────────\n  ⏵⏵ auto mode on (shift+tab to cycle) · ← for agents";

    private const string CargoRunning =
        "   Compiling syn v2.0.87\n   Compiling serde_derive v1.0.215\n   Compiling tokio-macros v2.4.0\n   Compiling russh v0.45.0\n    Building [=======>                 ] 112/389: russh, tokio(build), openssl-sys(build)";

    private const string AptPrompt =
        "Reading package lists... Done\nBuilding dependency tree... Done\nThe following NEW packages will be installed:\n  htop\n0 upgraded, 1 newly installed, 0 to remove and 12 not upgraded.\nNeed to get 148 kB of archives.\nDo you want to continue? [Y/n] ";

    private const string SudoPrompt = "user@host:~$ sudo systemctl restart nginx\n[sudo] password for user: ";

    private const string PipelineIdle =
        "> done\n> echo NS_UPLOAD_DONE\n> OUTER\n> EOF\nuser@host:~/pipeline$ chmod +x ~/pipeline/gcp.sh ~/pipeline/upload.sh && bash -n ~/pipeline/gcp.sh && echo ALL_PUSHED && cd ~/pipeline && echo ===STORAGE===; ./gcp.sh gcloud storage ls gs://bucket/sap/t000/ 2>&1 | head -2; echo ===BQ_JOB===; ./gcp.sh bq query --nouse_legacy_sql --format=csv --quiet \"SELECT 1 AS test\" 2>&1 | tail -2\nALL_PUSHED\n===STORAGE===\ngs://bucket/sap/t000/._SUCCESS.crc\ngs://bucket/sap/t000/.part-00000-473a5c26.snappy.parquet.crc\n===BQ_JOB===\nUser does not have bigquery.jobs.create permission in project example-\nanalytics.\nuser@host:~/pipeline$ echo SA_AUTH_VERIFIED\nSA_AUTH_VERIFIED\nuser@host:~/pipeline$ ";

    public static TheoryData<string, string, ScreenActivity> Screens => new()
    {
        { "ssh pipeline idle", PipelineIdle, ScreenActivity.IdleShellPrompt },
        { "ssh empty prompt", "user@ai-gateway:~$ ", ScreenActivity.IdleShellPrompt },
        { "claude finished", ClaudeDone, ScreenActivity.WaitingForUser },
        { "blank", "", ScreenActivity.UnknownBlank },
        { "claude working", ClaudeWorking, ScreenActivity.AgentWorking },
        { "cargo running", CargoRunning, ScreenActivity.CommandRunning },
        { "apt prompt", AptPrompt, ScreenActivity.WaitingForUser },
        { "sudo prompt", SudoPrompt, ScreenActivity.WaitingForUser },
    };

    [Theory]
    [MemberData(nameof(Screens))]
    public async Task Live_classifier_matches_the_measured_activity(string name, string screen, ScreenActivity expected)
    {
        Assert.SkipUnless(HasKey, "TYPESAFE_API_KEY not set; live test skipped.");

        var classifier = new ScreenActivityClassifier(new SystemOneClient(new HttpClient(), new EnvKey()));
        var result = await classifier.ClassifyAsync(new ScreenSample(screen, 34, 101), CancellationToken.None);

        Assert.Equal(ScreenClassificationOutcome.Answered, result.Outcome);
        Assert.Equal(expected, result.Answer!.Activity);
        Assert.True(result.Answer.Confidence >= 0.8, $"{name}: confidence {result.Answer.Confidence:0.00} below 0.8");
    }
}
