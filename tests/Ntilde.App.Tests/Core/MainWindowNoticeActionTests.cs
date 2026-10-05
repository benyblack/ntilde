using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Shell;
using Ntilde.Shell.Mux.Remote;
using Ntilde.Tests.Controls; // FakeTerminalSession, RecordingSessionFactory

namespace Ntilde.Tests.Core;

/// <summary>
/// Phase 4 spec §7.5: the notice toast gets one generic action button, so a remote failure can offer
/// "Install ntilde-mux…" or "Update ntilde-mux…". Notices raised together merge into one toast, and the
/// last action raised wins it.
/// </summary>
/// <remarks>
/// <see cref="TestAppDataRoot"/> is taken for its lifetime: a real MainWindow may save the session,
/// which must not land in the developer's own profile (see MainWindowShellExitTests).
/// </remarks>
public sealed class MainWindowNoticeActionTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    private const string Message = "[nova@fake-host: ntilde-mux is not installed \u2014 this tab will not survive a disconnect]";

    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    private static MainWindow CreateWindow()
    {
        MainWindow window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
            SessionFactory = new RecordingSessionFactory(new FakeTerminalSession()),
        });
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static (bool Visible, string? Title, string? Message) Toast(MainWindow window) =>
        (window.FindControl<Border>("RecordingToast")!.IsVisible,
         window.FindControl<TextBlock>("RecordingToastTitle")!.Text,
         window.FindControl<TextBlock>("RecordingToastMessage")!.Text);

    private static Button ActionButton(MainWindow window) => window.FindControl<Button>("RecordingToastAction")!;

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static bool AutoHides(MainWindow window) =>
        ((DispatcherTimer)typeof(MainWindow).GetField("_recordingToastTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!).IsEnabled;

    [AvaloniaFact]
    public void Notice_with_an_action_shows_the_button_and_runs_it()
    {
        MainWindow window = CreateWindow();
        int runs = 0;

        window.EnqueueNotice(TerminalPane.RemoteMuxUnavailableNoticeTitle, Message, new PersistenceNoticeAction("Install ntilde-mux\u2026", () => runs++));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal((true, TerminalPane.RemoteMuxUnavailableNoticeTitle, Message), Toast(window));
        Button button = ActionButton(window);
        Assert.True(button.IsVisible);
        Assert.Equal("Install ntilde-mux\u2026", button.Content);
        Assert.False(AutoHides(window)); // an offer stays until the user takes it or closes it

        Click(button);

        Assert.Equal(1, runs);
        Assert.False(Toast(window).Visible);
    }

    [AvaloniaFact]
    public void A_notice_without_an_action_shows_no_button_and_does_not_inherit_the_last_one()
    {
        MainWindow window = CreateWindow();
        int runs = 0;
        window.EnqueueNotice(TerminalPane.RemoteMuxUnavailableNoticeTitle, Message, new PersistenceNoticeAction("Install ntilde-mux\u2026", () => runs++));
        Dispatcher.UIThread.RunJobs();

        window.EnqueueNotice(TerminalPane.MuxPreviousLostNoticeTitle, TerminalPane.MuxPreviousLostBanner);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal((true, TerminalPane.MuxPreviousLostNoticeTitle, TerminalPane.MuxPreviousLostBanner), Toast(window));
        Assert.False(ActionButton(window).IsVisible);
        Assert.True(AutoHides(window));
        Click(ActionButton(window)); // even if a click got there, nothing stale runs
        Assert.Equal(0, runs);
    }

    [AvaloniaFact]
    public void A_merged_toast_keeps_the_last_action_raised()
    {
        MainWindow window = CreateWindow();
        var ran = new List<string>();

        window.EnqueueNotice(TerminalPane.RemoteMuxUnavailableNoticeTitle, Message, new PersistenceNoticeAction("Install ntilde-mux\u2026", () => ran.Add("install")));
        window.EnqueueNotice(TerminalPane.RemoteMuxUnavailableNoticeTitle, Message, new PersistenceNoticeAction("Update ntilde-mux\u2026", () => ran.Add("update")));
        window.EnqueueNotice(TerminalPane.MuxPreviousLostNoticeTitle, TerminalPane.MuxPreviousLostBanner);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Update ntilde-mux\u2026", ActionButton(window).Content);
        Assert.Equal(2, Toast(window).Message!.Split('\n').Length);
        Click(ActionButton(window));
        Assert.Equal("update", Assert.Single(ran));
    }

    /// <summary>The whole path: the pane raises its notice with an action, and the window's toast offers it.</summary>
    [AvaloniaFact]
    public void A_pane_notice_brings_its_action_to_the_toast()
    {
        MainWindow window = CreateWindow();
        TerminalPane pane = window.AllPanesForTest()[0];
        int runs = 0;
        MethodInfo raise = typeof(TerminalPane).GetMethod("RaisePersistenceNotice", BindingFlags.NonPublic | BindingFlags.Instance)!;

        raise.Invoke(pane, [TerminalPane.RemoteMuxUnavailableNoticeTitle, Message, new PersistenceNoticeAction("Update ntilde-mux\u2026", () => runs++)]);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal((true, TerminalPane.RemoteMuxUnavailableNoticeTitle, Message), Toast(window));
        Click(ActionButton(window));
        Assert.Equal(1, runs);
    }

    [AvaloniaFact]
    public void An_action_that_throws_is_logged_and_the_toast_still_closes()
    {
        MainWindow window = CreateWindow();
        window.EnqueueNotice(TerminalPane.RemoteMuxUnavailableNoticeTitle, Message, new PersistenceNoticeAction("Install ntilde-mux\u2026", () => throw new InvalidOperationException("no dialog")));
        Dispatcher.UIThread.RunJobs();

        Click(ActionButton(window));

        Assert.False(Toast(window).Visible);
    }

    /// <summary>Task 23 assigns the install flow; until then - or for a failure an install cannot fix - no action is offered.</summary>
    [AvaloniaFact]
    public void The_remote_failure_action_opens_the_install_flow_for_the_profile()
    {
        MainWindow window = CreateWindow();
        Guid profile = Guid.NewGuid();
        var notInstalled = new RemoteMuxFailure(RemoteFailureKind.NotInstalled, "ntilde-mux is not installed");
        Assert.Null(window.OpenRemoteMuxInstall);
        Assert.Null(window.RemoteMuxNoticeAction(notInstalled, profile));

        var opened = new List<Guid>();
        window.OpenRemoteMuxInstall = opened.Add;
        PersistenceNoticeAction install = window.RemoteMuxNoticeAction(notInstalled, profile)!;
        install.Run();

        Assert.Equal(TerminalPane.RemoteMuxInstallActionLabel, install.Label);
        Assert.Equal(new[] { profile }, opened);
        Assert.Equal(TerminalPane.RemoteMuxUpdateActionLabel, window.RemoteMuxNoticeAction(notInstalled with { Kind = RemoteFailureKind.VersionMismatch }, profile)?.Label);
        Assert.Null(window.RemoteMuxNoticeAction(new RemoteMuxFailure(RemoteFailureKind.Unsupported, "musl libc is not supported"), profile));
    }

    [AvaloniaFact]
    public void The_remote_notice_texts_are_the_specs()
    {
        Assert.Equal("Persistent SSH unavailable", TerminalPane.RemoteMuxUnavailableNoticeTitle);
        Assert.Equal(Message, TerminalPane.RemoteMuxUnavailableMessage("nova@fake-host", "ntilde-mux is not installed"));
        Assert.Equal("Install ntilde-mux\u2026", TerminalPane.RemoteMuxInstallActionLabel);
        Assert.Equal("Update ntilde-mux\u2026", TerminalPane.RemoteMuxUpdateActionLabel);
    }
}
