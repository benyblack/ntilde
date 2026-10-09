using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Shell.Mux; // MuxTestText

namespace Ntilde.Tests.Core;

/// <summary>
/// The window's notice toast, for the multiplexer window tests (<c>MainWindowMuxUpdateTests</c>,
/// <c>MainWindowMuxRemoteTests</c>): what it shows, its buttons, and when the window has decided about a connection's
/// "from another build" notice (Phase 5 Task 23), so a test that expects no notice waits for a positive signal.
/// </summary>
internal static class WindowToast
{
    public static (bool Visible, string? Title, string? Message) Toast(MainWindow window) =>
        (window.FindControl<Border>("RecordingToast")!.IsVisible,
         window.FindControl<TextBlock>("RecordingToastTitle")!.Text,
         window.FindControl<TextBlock>("RecordingToastMessage")!.Text);

    /// <summary>
    /// The visible toast's lines - one per notice merged into it - or none. Other notices share the toast (a machine with
    /// no keychain shows the credential notice at startup), so a test looks for its own line, never at "the" toast.
    /// </summary>
    public static string[] ToastLines(MainWindow window) =>
        Toast(window) is (true, _, { } message) ? message.Split('\n') : [];

    /// <summary>The toast's action button (<c>RecordingToastAction</c>) or its close button (<c>RecordingToastClose</c>).</summary>
    public static Button ToastButton(MainWindow window, string name) => window.FindControl<Button>(name)!;

    public static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>Enter, through Avalonia's real input pipeline: the pane's focused view first, the pane second.</summary>
    public static void PressEnter(TerminalPane pane)
    {
        pane.TermView.Focus();
        TopLevel.GetTopLevel(pane)!.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Whether <paramref name="pane"/> shows <paramref name="line"/>, however its width wrapped it.</summary>
    public static bool Shows(TerminalPane pane, string line) =>
        pane.Buffer is { } buffer && Unwrapped(MuxTestText.VisibleText(buffer)).Contains(Unwrapped(line), StringComparison.Ordinal);

    private static string Unwrapped(string text) =>
        text.Replace("\n", string.Empty, StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);

    /// <summary>The action the toast offers now, as the button would run it; null when it offers none.</summary>
    public static PersistenceNoticeAction? OfferedAction(MainWindow window) =>
        (PersistenceNoticeAction?)typeof(MainWindow).GetField("_recordingToastAction", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window);

    /// <summary>
    /// Pumps the UI thread until the window has finished deciding about <paramref name="count"/> connections of
    /// <paramref name="endpoint"/>, then once more, so a notice one of them raised is on the toast.
    /// </summary>
    public static void PumpUntilDecided(MainWindow window, MuxEndpointId endpoint, int count, Action? whileWaiting = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (window.MuxRestartDecisionsForTest(endpoint) < count)
        {
            if (sw.ElapsedMilliseconds > 20_000) Assert.Fail($"Timed out: the window decided about {window.MuxRestartDecisionsForTest(endpoint)} of {count} connection(s) of {endpoint}");
            whileWaiting?.Invoke();
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Assert.Equal(count, window.MuxRestartDecisionsForTest(endpoint));
        Dispatcher.UIThread.RunJobs(); // the notice's flush is posted at Background priority
        Dispatcher.UIThread.RunJobs();
    }
}
