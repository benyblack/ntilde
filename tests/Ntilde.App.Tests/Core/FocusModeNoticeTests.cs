using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace Ntilde.Tests.Core;

/// <summary>
/// Focus mode spec section 6: notices that arrive on their own wait until focus mode ends; notices that
/// answer something the user just did show immediately.
/// </summary>
public sealed class FocusModeNoticeTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    private static (bool Visible, string? Title, string? Message) Toast(MainWindow window) =>
        (window.FindControl<Border>("RecordingToast")!.IsVisible,
         window.FindControl<TextBlock>("RecordingToastTitle")!.Text,
         window.FindControl<TextBlock>("RecordingToastMessage")!.Text);

    private static bool UpdateVisible(MainWindow window) => window.FindControl<Border>("UpdateToast")!.IsVisible;

    private static string? UpdateMessage(MainWindow window) => window.FindControl<TextBlock>("UpdateToastMessage")!.Text;

    private static void Unprompted(MainWindow window, string title, string message, string key) =>
        WindowTestSupport.Invoke(window, "ShowUnpromptedToast", title, message, key);

    [AvaloniaFact]
    public void A_persistence_notice_raised_in_focus_mode_waits_for_leave()
    {
        MainWindow window = WindowTestSupport.NewShownWindow();
        window.EnterFocusMode();

        window.EnqueueNotice("T", "M");
        Dispatcher.UIThread.RunJobs();
        Assert.False(Toast(window).Visible);

        window.LeaveFocusMode();
        Dispatcher.UIThread.RunJobs();
        var toast = Toast(window);
        Assert.True(toast.Visible);
        Assert.Equal("T", toast.Title);
    }

    [AvaloniaFact]
    public void Several_held_notices_merge_into_one_toast_on_leave()
    {
        MainWindow window = WindowTestSupport.NewShownWindow();
        window.EnterFocusMode();

        window.EnqueueNotice("First", "one");
        Unprompted(window, "Second", "two", "focus:a");
        Unprompted(window, "Third", "three", "focus:b");
        Dispatcher.UIThread.RunJobs();
        Assert.False(Toast(window).Visible);

        window.LeaveFocusMode();
        Dispatcher.UIThread.RunJobs();
        var toast = Toast(window);
        Assert.True(toast.Visible);
        Assert.Equal("Third", toast.Title);
        Assert.Equal(3, toast.Message!.Split('\n').Length);
    }

    [AvaloniaFact]
    public void A_repeated_unprompted_notice_keeps_only_the_latest()
    {
        MainWindow window = WindowTestSupport.NewShownWindow();
        window.EnterFocusMode();

        Unprompted(window, "Command finished", "first", "focus:command-finished:x");
        Unprompted(window, "Command finished", "second", "focus:command-finished:x");
        window.LeaveFocusMode();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("second", Toast(window).Message);
    }

    [AvaloniaFact]
    public void A_dismissed_held_notice_never_shows()
    {
        MainWindow window = WindowTestSupport.NewShownWindow();
        window.EnterFocusMode();

        window.EnqueueNotice("T", "M", key: "k");
        window.DismissNotice("k");
        window.LeaveFocusMode();
        Dispatcher.UIThread.RunJobs();

        Assert.False(Toast(window).Visible);
    }

    // Final review I1 / R14: EnqueueNotice also carries direct answers to user commands; those show at once.

    [AvaloniaFact]
    public void A_prompted_notice_shows_at_once_in_focus_mode_while_an_unprompted_one_waits()
    {
        MainWindow window = WindowTestSupport.NewShownWindow();
        window.EnterFocusMode();

        // The held one first: the queue is then not empty when the prompted one arrives.
        window.EnqueueNotice("Held", "arrived on its own");
        window.EnqueueNotice("Asked", "answers the user", prompted: true);
        Dispatcher.UIThread.RunJobs();

        var toast = Toast(window);
        Assert.True(toast.Visible);
        Assert.Equal("Asked", toast.Title);
        Assert.Equal("answers the user", toast.Message);

        window.LeaveFocusMode();
        Dispatcher.UIThread.RunJobs();
        toast = Toast(window);
        Assert.True(toast.Visible);
        Assert.Equal("Held", toast.Title);
        Assert.Equal("arrived on its own", toast.Message);
    }

    [AvaloniaFact]
    public void A_held_notice_keeps_its_own_action_when_a_prompted_one_shows_first()
    {
        MainWindow window = WindowTestSupport.NewShownWindow();
        var action = window.FindControl<Button>("RecordingToastAction")!;
        int runs = 0;
        window.EnterFocusMode();

        window.EnqueueNotice("Held", "offer", new Ntilde.Controls.PersistenceNoticeAction("Do it", () => runs++));
        window.EnqueueNotice("Asked", "answer", prompted: true);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Asked", Toast(window).Title);
        Assert.False(action.IsVisible);

        window.LeaveFocusMode();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Held", Toast(window).Title);
        Assert.True(action.IsVisible);
        Assert.Equal("Do it", action.Content);
        Assert.Equal(0, runs);
    }

    [AvaloniaFact]
    public void Outside_focus_mode_prompted_and_unprompted_notices_merge_as_before()
    {
        MainWindow window = WindowTestSupport.NewShownWindow();

        window.EnqueueNotice("First", "one");
        window.EnqueueNotice("Second", "two", prompted: true);
        Dispatcher.UIThread.RunJobs();

        var toast = Toast(window);
        Assert.True(toast.Visible);
        Assert.Equal("Second", toast.Title);
        Assert.Equal("one\ntwo", toast.Message);
    }

    [AvaloniaFact]
    public async Task A_refused_pane_detach_answers_at_once_in_focus_mode()
    {
        MainWindow window = WindowTestSupport.NewShownWindow();
        TabItem split = WindowTestSupport.AddSplitTab(window, out _, out var second);
        window.FindControl<TabControl>("Tabs")!.SelectedItem = split;
        Dispatcher.UIThread.RunJobs();
        window.EnterFocusMode();

        // A plain local shell: "Pane: Detach" refuses, and says why.
        Assert.False(await window.DetachPaneAsync(second));
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsFocusMode);
        var toast = Toast(window);
        Assert.True(toast.Visible);
        Assert.Equal("Pane: Detach", toast.Title);
    }

    [AvaloniaFact]
    public void A_pane_refusing_remote_files_answers_at_once_but_its_other_notices_wait()
    {
        MainWindow window = WindowTestSupport.NewShownWindow();
        var pane = window.AllPanesForTest().First();
        window.EnterFocusMode();

        WindowTestSupport.Invoke(window, "OnPanePersistenceNotice", pane, "Keep shells running", "a fallback", null!);
        WindowTestSupport.Invoke(window, "OnPanePersistenceNotice", pane,
            Ntilde.Controls.TerminalPane.RemoteFilesUnavailableNoticeTitle, Ntilde.Controls.TerminalPane.RemoteFilesRetargetedMessage, null!);
        Dispatcher.UIThread.RunJobs();

        var toast = Toast(window);
        Assert.True(toast.Visible);
        Assert.Equal(Ntilde.Controls.TerminalPane.RemoteFilesUnavailableNoticeTitle, toast.Title);
        Assert.Equal(Ntilde.Controls.TerminalPane.RemoteFilesRetargetedMessage, toast.Message);

        window.LeaveFocusMode();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("a fallback", Toast(window).Message);
    }

    [AvaloniaFact]
    public void A_held_notice_that_turns_out_to_answer_the_user_shows_at_once()
    {
        MainWindow window = WindowTestSupport.NewShownWindow();
        window.EnterFocusMode();

        // The "Shell detached" a close raises itself (DisposeControlTree) for every kind of close; the user's own
        // Pane: Detach or Close then marks it as its answer.
        window.EnqueueNotice("Shell detached", "Shell kept running");
        Dispatcher.UIThread.RunJobs();
        Assert.False(Toast(window).Visible);

        WindowTestSupport.Invoke(window, "PromptPendingNotice", "Shell detached");
        Dispatcher.UIThread.RunJobs();

        var toast = Toast(window);
        Assert.True(toast.Visible);
        Assert.Equal("Shell detached", toast.Title);
    }

    [AvaloniaFact]
    public void Prompted_toasts_still_show_in_focus_mode()
    {
        MainWindow window = WindowTestSupport.NewShownWindow();
        window.EnterFocusMode();

        WindowTestSupport.Invoke(window, "ShowRecordingToast", "Recording started", "now recording", null!, null!, true, null!);
        Dispatcher.UIThread.RunJobs();

        var toast = Toast(window);
        Assert.True(toast.Visible);
        Assert.Equal("Recording started", toast.Title);
    }

    [AvaloniaFact]
    public void An_update_found_in_focus_mode_waits()
    {
        MainWindow window = WindowTestSupport.NewShownWindow();
        window.EnterFocusMode();

        WindowTestSupport.Invoke(window, "ShowUpdateToastUnprompted", "1.2.3");
        Assert.False(UpdateVisible(window));

        window.LeaveFocusMode();
        Assert.True(UpdateVisible(window));
        Assert.Contains("1.2.3", UpdateMessage(window));
    }

    [AvaloniaFact]
    public void An_update_toast_on_screen_is_hidden_on_enter_and_returns_on_leave()
    {
        MainWindow window = WindowTestSupport.NewShownWindow();
        WindowTestSupport.Invoke(window, "ShowUpdateToast", "1.2.3");
        Assert.True(UpdateVisible(window));

        window.EnterFocusMode();
        Assert.False(UpdateVisible(window));

        window.LeaveFocusMode();
        Assert.True(UpdateVisible(window));
        Assert.Contains("1.2.3", UpdateMessage(window));
    }

    [AvaloniaFact]
    public void A_closed_update_toast_is_not_revived_by_the_next_focus_cycle()
    {
        MainWindow window = WindowTestSupport.NewShownWindow();
        WindowTestSupport.Invoke(window, "ShowUpdateToast", "1.2.3");
        window.EnterFocusMode();
        window.LeaveFocusMode();
        Assert.True(UpdateVisible(window));

        WindowTestSupport.Invoke(window, "HideUpdateToast");
        window.EnterFocusMode();
        window.LeaveFocusMode();

        Assert.False(UpdateVisible(window));
    }
}
