using System;
using System.IO;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Ntilde.Shell;
using Ntilde.VT;

namespace Ntilde.Tests.Core;

public sealed class TerminalViewTextBlinkTests
{
    private sealed class Clock : TimeProvider
    {
        internal long Milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }

    private static void Tick(TerminalView view)
        => typeof(TerminalView).GetMethod("OnRenderTimerTick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view, new object?[] { null, EventArgs.Empty });

    private static byte[] Capture(TerminalView view)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(view.Bounds.Width),
            (int)Math.Ceiling(view.Bounds.Height)), new Vector(96, 96));
        view.RenderOffscreen(bitmap);
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    [AvaloniaFact]
    public void TextBlink_UsesTheExistingRenderTick_WhenCursorBlinkAndFocusAreOff()
    {
        var clock = new Clock();
        var animation = new TextBlinkAnimation(clock);
        var view = new TerminalView(animation) { Height = 200 };
        var buffer = new TerminalBuffer(40, 8);
        view.SetBuffer(buffer);
        view.ApplySettings(new TerminalSettings { CursorBlink = false });
        var other = new Button { Content = "Other" };
        var panel = new DockPanel();
        DockPanel.SetDock(other, Dock.Bottom);
        panel.Children.Add(other);
        panel.Children.Add(view);
        var window = new Window { Content = panel, Width = 600, Height = 300 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(other.Focus());
            new AnsiParser(buffer).Process("\x1b[?25l\x1b[38;2;255;255;255;48;2;0;0;0;5mMMMM");
            view.InvalidateVisual();
            using (var frame = window.CaptureRenderedFrame()) Assert.NotNull(frame);
            Tick(view); // the real draw operation reported its blink ink to the view clock
            Assert.DoesNotContain("cursor blink", view.RunningTimersForTest);
            var before = Capture(view);
            clock.Milliseconds += 530;
            Tick(view);
            Assert.False(animation.ForegroundVisible);
            var hidden = Capture(view);
            Assert.NotEqual(before, hidden);
            clock.Milliseconds += 530;
            Tick(view);
            Assert.True(animation.ForegroundVisible);
            Assert.Equal(before, Capture(view));
        }
        finally { view.Retire(); window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact]
    public void HiddenOrRetiredView_DoesNotAdvanceTheTextAnimation()
    {
        var clock = new Clock();
        var animation = new TextBlinkAnimation(clock);
        var view = new TerminalView(animation);
        view.SetBuffer(new TerminalBuffer(40, 8));
        view.ApplySettings(new TerminalSettings { CursorBlink = false });
        var window = new Window { Content = view, Width = 600, Height = 300 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            animation.ReportCells(true);
            Tick(view);
            clock.Milliseconds += 530;
            Tick(view);
            Assert.False(animation.ForegroundVisible);
            view.IsVisible = false;
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("render", view.RunningTimersForTest);
            Assert.DoesNotContain("cursor blink", view.RunningTimersForTest);
            clock.Milliseconds += 10000;
            Tick(view); // queued tick after suspension
            Assert.True(animation.ForegroundVisible);
            view.IsVisible = true;
            Dispatcher.UIThread.RunJobs();
            Tick(view);
            Assert.True(animation.ForegroundVisible);
            view.Retire();
            clock.Milliseconds += 530;
            Tick(view);
            Assert.True(animation.ForegroundVisible);
            Assert.Empty(view.RunningTimersForTest);
        }
        finally { view.Retire(); window.Close(); Dispatcher.UIThread.RunJobs(); }
    }
}
