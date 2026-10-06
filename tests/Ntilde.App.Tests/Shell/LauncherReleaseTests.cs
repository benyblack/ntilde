using Ntilde.Launcher;
using Ntilde.Shell;

namespace Ntilde.Tests.Shell;

/// <summary>
/// The App's half of the GUI release (Phase 4 spec §11.3): set the event ntilde.com handed down and take
/// the variable out of this process's environment, so no shell started from here inherits it.
/// </summary>
public sealed class LauncherReleaseTests
{
    private static string NewEventName() => @"Local\ntilde-launcher-test-" + Guid.NewGuid().ToString("N");

    private static void WithVariable(string? value, Action body)
    {
        string? previous = Environment.GetEnvironmentVariable(LauncherRelease.Variable);
        try
        {
            Environment.SetEnvironmentVariable(LauncherRelease.Variable, value);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(LauncherRelease.Variable, previous);
        }
    }

    [Fact]
    public void The_App_reads_the_variable_the_launcher_sets()
    {
        Assert.Equal(LauncherCommandLine.ReleaseEventVariable, LauncherRelease.Variable);
    }

    [Fact]
    public void Signal_sets_the_named_event_and_clears_the_variable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ntilde.com and its release event are Windows-only.");
        string name = NewEventName();
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, name);

        WithVariable(name, () =>
        {
            LauncherRelease.Signal();

            Assert.True(release.WaitOne(0), "The release event was not set.");
            Assert.Null(Environment.GetEnvironmentVariable(LauncherRelease.Variable));
        });
    }

    /// <summary>A launcher that already exited leaves a name nobody holds: nothing to set, nothing thrown.</summary>
    [Fact]
    public void Signal_for_an_event_that_no_longer_exists_only_clears_the_variable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ntilde.com and its release event are Windows-only.");

        WithVariable(NewEventName(), () =>
        {
            LauncherRelease.Signal();

            Assert.Null(Environment.GetEnvironmentVariable(LauncherRelease.Variable));
        });
    }

    [Fact]
    public void Signal_without_the_variable_does_nothing()
    {
        WithVariable(null, () =>
        {
            LauncherRelease.Signal();

            Assert.Null(Environment.GetEnvironmentVariable(LauncherRelease.Variable));
        });
    }

    /// <summary>
    /// A <c>mux</c> verb keeps its launcher waiting, and the daemon <c>mux attach</c> may start would otherwise
    /// pass the variable to every shell it hosts, where a GUI started from one would release that launcher.
    /// </summary>
    [Fact]
    public void Discard_clears_the_variable_without_setting_the_event()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ntilde.com and its release event are Windows-only.");
        string name = NewEventName();
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, name);

        WithVariable(name, () =>
        {
            LauncherRelease.Discard();

            Assert.False(release.WaitOne(0), "Discard must not release the launcher.");
            Assert.Null(Environment.GetEnvironmentVariable(LauncherRelease.Variable));
        });
    }
}
