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
