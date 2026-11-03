using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Shell;
using Ntilde.VT;

namespace Ntilde.Tests.Core;

/// <summary>Computed or attempted geometry must not impersonate a successful session callback.</summary>
public sealed class TerminalViewResizeCompletionTests
{
    [AvaloniaFact]
    public void Successful_dispatches_publish_completion_after_subscribers_and_match_the_buffer()
    {
        var buffer = new TerminalBuffer(80, 24);
        var view = new TerminalView();
        view.SetBuffer(buffer);
        var callbacks = new List<(int Cols, int Rows)>();
        var completed = new List<(int Cols, int Rows)>();
        view.OnResize += (cols, rows) => callbacks.Add((cols, rows));
        view.ResizeDispatchCompleted += (cols, rows) =>
        {
            Assert.Equal((cols, rows), callbacks[^1]);
            buffer.Lock.EnterReadLock();
            try { Assert.Equal((cols, rows), (buffer.Cols, buffer.Rows)); }
            finally { buffer.Lock.ExitReadLock(); }
            completed.Add((cols, rows));
        };
        var window = new Window { Content = view, Width = 800, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.NotEmpty(completed);
            view.ReleaseResizeThrottleForTest();
            window.Width = 600;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(callbacks, completed);
            Assert.Equal(completed[^1], view.LastCompletedResizeDispatch);
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact]
    public void A_throwing_session_callback_cannot_publish_a_completed_dispatch()
    {
        var buffer = new TerminalBuffer(80, 24);
        var view = new TerminalView();
        view.SetBuffer(buffer);
        var window = new Window { Content = view, Width = 800, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var before = view.LastCompletedResizeDispatch;
            int completions = 0;
            view.ResizeDispatchCompleted += (_, _) => completions++;
            view.OnResize += (_, _) => throw new InvalidOperationException("expected resize failure");
            view.ReleaseResizeThrottleForTest();

            window.Width = 600;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(before, view.LastCompletedResizeDispatch);
            Assert.Equal(0, completions);
            buffer.Lock.EnterReadLock();
            try { Assert.NotEqual(before, (buffer.Cols, buffer.Rows)); }
            finally { buffer.Lock.ExitReadLock(); }
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact]
    public void A_replaced_buffer_does_not_inherit_the_previous_buffers_dispatch_proof()
    {
        var view = new TerminalView();
        view.SetBuffer(new TerminalBuffer(80, 24));
        var window = new Window { Content = view, Width = 800, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var before = view.LastCompletedResizeDispatch;
            Assert.True(before.Cols > 0 && before.Rows > 0);

            view.SetBuffer(new TerminalBuffer(before.Cols, before.Rows));

            Assert.Equal((0, 0), view.LastCompletedResizeDispatch);
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }
}
