using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Shell;
using Ntilde.UI.Replay;
using Ntilde.VT;

namespace Ntilde.Tests.Core;

public sealed class ReplayWindowTests
{
    [Fact]
    public void TryAttachDeveloperTools_SwallowsDuplicateAttachmentFailure()
    {
        bool invoked = false;

        Exception? error = Record.Exception(() =>
            ReplayWindow.TryAttachDeveloperToolsForTest(() =>
            {
                invoked = true;
                throw new InvalidOperationException("Developer tools have already been attached. Multiple attachments are not supported.");
            }));

        Assert.True(invoked);
        Assert.Null(error);
    }

    [Fact]
    public void TryAttachDeveloperTools_RethrowsUnexpectedFailures()
    {
        Exception ex = Assert.Throws<InvalidOperationException>(() =>
            ReplayWindow.TryAttachDeveloperToolsForTest(() =>
            {
                throw new InvalidOperationException("Some other failure");
            }));

        Assert.Equal("Some other failure", ex.Message);
    }

    /// <summary>
    /// The buffer builds its rows on the Default theme before the replay's theme reaches it, so the
    /// theme has to remap them: setting it alone would leave unstyled text on Default's colours.
    /// </summary>
    [AvaloniaFact]
    public void A_replay_theme_reaches_the_rows_the_buffer_already_built()
    {
        // A path with nothing at it: the buffer is built before the recording is read, and a
        // real file would stay open in the view model's scan, which Windows will not delete.
        string recording = Path.Combine(Path.GetTempPath(), $"ntilde_replay_{Guid.NewGuid():N}.rec");
        var theme = new TerminalTheme
        {
            Name = "replay light",
            Foreground = TermColor.FromRgb(0x24, 0x29, 0x2F),
            Background = TermColor.FromRgb(0xFF, 0xFF, 0xFF),
        };
        var window = new ReplayWindow(recording, theme);
        try
        {
            var buffer = (TerminalBuffer)typeof(ReplayWindow)
                .GetField("_buffer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            TerminalCell cell;
            buffer.Lock.EnterReadLock();
            try
            {
                cell = buffer.GetCell(0, 0);
            }
            finally
            {
                buffer.Lock.ExitReadLock();
            }

            Assert.Equal(theme.Foreground.ToUint(), cell.Fg);
            Assert.Equal(theme.Background.ToUint(), cell.Bg);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// #644, Codex review of #651: a replay window hosts its own view, which only a pane used to retire, so its glyph
    /// atlas and row pictures waited for the finalizers after the window closed.
    /// </summary>
    [AvaloniaFact]
    public void Closing_the_window_disposes_its_views_render_caches()
    {
        string recording = Path.Combine(Path.GetTempPath(), $"ntilde_replay_{Guid.NewGuid():N}.rec");
        var window = new ReplayWindow(recording);
        TerminalView view = window.FindControl<TerminalView>("TermView")!;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();

        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(view.RenderCachesForTest.TryLease()); // the window let its view go

        // The compositor drops the closed window's last frame, and the draw operation holding the caches with it,
        // on its next tick; the app's other windows keep it ticking.
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.RenderCachesForTest.IsDisposed);
    }
}
