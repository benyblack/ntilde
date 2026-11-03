using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.AgentHost;
using Ntilde.Tests.Controls;
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

            Assert.NotEqual((view.Cols, view.Rows), view.LastCompletedResizeDispatch);
            Assert.Equal(0, completions);
            buffer.Lock.EnterReadLock();
            try { Assert.NotEqual(before, (buffer.Cols, buffer.Rows)); }
            finally { buffer.Lock.ExitReadLock(); }
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact]
    public void Returning_to_an_earlier_grid_does_not_reuse_its_stale_success_after_failed_callbacks()
    {
        var buffer = new TerminalBuffer(80, 24);
        var view = new TerminalView();
        view.SetBuffer(buffer);
        var window = new Window { Content = view, Width = 800, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var original = view.LastCompletedResizeDispatch;
            view.OnResize += (_, _) => throw new InvalidOperationException("expected resize failure");
            view.ReleaseResizeThrottleForTest();

            window.Width = 600;
            Dispatcher.UIThread.RunJobs();
            window.Width = 800;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(original, (view.Cols, view.Rows));
            Assert.True(AgentPaneResizeObservation.TryRead(buffer, view.Cols, view.Rows, view.LastCompletedResizeDispatch, out var actual));
            Assert.Equal((original.Rows, original.Cols), (actual.Rows, actual.Cols));
            Assert.False(actual.LayoutSettled);
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_changed_session_cannot_reuse_the_previous_sessions_completion_on_the_same_buffer(bool detached)
    {
        var buffer = new TerminalBuffer(80, 24);
        var view = new TerminalView();
        using var previous = new FakeTerminalSession();
        using var current = new FakeTerminalSession();
        view.SetBuffer(buffer);
        view.SetSession(previous);
        view.OnResize += (cols, rows) => view.SessionForTest?.Resize(cols, rows);
        var window = new Window { Content = view, Width = 800, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var delivered = view.LastCompletedResizeDispatch;
            Assert.True(AgentPaneResizeObservation.TryRead(buffer, view.Cols, view.Rows, delivered, out var before));
            Assert.True(before.LayoutSettled);

            view.SetSession(detached ? null : current);

            Assert.True(AgentPaneResizeObservation.TryRead(buffer, view.Cols, view.Rows, view.LastCompletedResizeDispatch, out var changed));
            Assert.Equal((before.Rows, before.Cols), (changed.Rows, changed.Cols));
            Assert.False(changed.LayoutSettled);
            if (!detached)
            {
                view.ReleaseResizeThrottleForTest();
                window.Width = 600;
                Dispatcher.UIThread.RunJobs();
                var fresh = view.LastCompletedResizeDispatch;
                Assert.Equal((view.Cols, view.Rows), fresh);
                view.SetSession(current); // Reapplying the same session must preserve its own completion.
                Assert.Equal(fresh, view.LastCompletedResizeDispatch);
            }
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Replacing_the_target_inside_a_resize_callback_cannot_publish_completion_for_its_replacement(bool replaceBuffer)
    {
        var buffer = new TerminalBuffer(80, 24);
        var view = new TerminalView();
        using var previous = new FakeTerminalSession();
        using var current = new FakeTerminalSession();
        view.SetBuffer(buffer);
        view.SetSession(previous);
        var window = new Window { Content = view, Width = 800, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            view.OnResize += (cols, rows) =>
            {
                if (replaceBuffer) { buffer = new TerminalBuffer(cols, rows); view.SetBuffer(buffer); }
                else view.SetSession(current);
            };
            view.ReleaseResizeThrottleForTest();

            window.Width = 600;
            Dispatcher.UIThread.RunJobs();

            if (!replaceBuffer) Assert.Same(current, view.SessionForTest);
            Assert.True(AgentPaneResizeObservation.TryRead(buffer, view.Cols, view.Rows, view.LastCompletedResizeDispatch, out var actual));
            Assert.False(actual.LayoutSettled);
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
