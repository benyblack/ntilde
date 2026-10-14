using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Shell;
using Ntilde.VT;

namespace Ntilde.Tests.Core;

/// <summary>A session that orders resizes in its stream owns the buffer's size (Phase 2 spec §8).</summary>
public class TerminalViewDeferredResizeTests
{
    private static (Window Window, TerminalView View, TerminalBuffer Buffer) Show()
    {
        var buffer = new TerminalBuffer(80, 24);
        var view = new TerminalView();
        view.SetBuffer(buffer);
        var window = new Window { Content = view, Width = 800, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.Bounds.Width > 0, $"the view was never arranged: {view.Bounds}");
        return (window, view, buffer);
    }

    [AvaloniaFact]
    public void A_deferred_view_requests_the_new_grid_but_leaves_the_buffer_alone()
    {
        var (window, view, buffer) = Show();
        try
        {
            (int cols, int rows) = (buffer.Cols, buffer.Rows);
            view.DefersBufferResizeToSession = true;
            view.ReleaseResizeThrottleForTest();
            var requests = new List<(int, int)>();
            view.OnResize += (c, r) => requests.Add((c, r));

            window.Width = 500;
            Dispatcher.UIThread.RunJobs();

            Assert.NotEmpty(requests);
            Assert.True(requests[^1].Item1 < cols, "the request names the narrower grid");
            Assert.Equal((cols, rows), (buffer.Cols, buffer.Rows));             // untouched
            Assert.Equal((cols, rows), view.LastDispatchedGridForTest);         // nothing applied yet
            Assert.Equal(requests[^1], view.LastRequestedGridForTest);
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact]
    public void The_same_grid_is_not_requested_twice()
    {
        var (window, view, _) = Show();
        try
        {
            view.DefersBufferResizeToSession = true;
            view.ReleaseResizeThrottleForTest();
            window.Width = 500;
            Dispatcher.UIThread.RunJobs();
            int requests = 0;
            view.OnResize += (_, _) => requests++;
            view.ReleaseResizeThrottleForTest();
            window.Height = 401; // sub-cell change: same grid
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, requests);
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact]
    public void NotifySessionResizedBuffer_records_the_buffers_real_grid()
    {
        var (window, view, buffer) = Show();
        try
        {
            view.DefersBufferResizeToSession = true;
            buffer.Resize(50, 20);          // what StreamResize does on the delivery thread
            view.NotifySessionResizedBuffer();
            Assert.Equal((50, 20), view.LastDispatchedGridForTest);
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact]
    public void A_font_change_in_deferred_mode_requests_but_does_not_resize()
    {
        var (window, view, buffer) = Show();
        try
        {
            (int cols, int rows) = (buffer.Cols, buffer.Rows);
            view.DefersBufferResizeToSession = true;
            int requests = 0;
            view.OnResize += (_, _) => requests++;
            view.ApplySettings(new TerminalSettings { FontSize = 28 });
            Dispatcher.UIThread.RunJobs();
            Assert.True(requests > 0, "a bigger font means a smaller grid, which must be requested");
            Assert.Equal((cols, rows), (buffer.Cols, buffer.Rows));
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }
}
