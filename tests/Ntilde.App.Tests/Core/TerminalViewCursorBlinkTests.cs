using System.Reflection;
using Avalonia;
using Avalonia.Media.Imaging;
using SkiaSharp;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Shell;
using Ntilde.VT;

namespace Ntilde.Tests.Core;

public sealed class TerminalViewCursorBlinkTests
{
    [AvaloniaTheory]
    [InlineData(1, "\u001b[!p")]
    [InlineData(3, "\u001b[!p")]
    [InlineData(5, "\u001b[!p")]
    [InlineData(1, "\u001bc")]
    [InlineData(3, "\u001bc")]
    [InlineData(5, "\u001bc")]
    public void Application_blink_starts_ticks_and_resets_until_the_host_default_is_restored(int style, string reset)
    {
        using var fixture = new ViewFixture(hostBlink: false);
        fixture.AssertBlinkTimer(false);
        fixture.Process($"\u001b[{style} q"); // mode change alone, without printable output
        fixture.AssertBlinkTimer(true);
        fixture.TickBlink();
        Assert.False(fixture.BlinkPhase);
        fixture.TickRender(); // a blink-driven frame must not restart the timer
        Assert.False(fixture.BlinkPhase);
        fixture.ResetBlink();
        Assert.True(fixture.BlinkPhase);
        fixture.AssertBlinkTimer(true);
        fixture.TickBlink();
        fixture.Process("\u001b[2;3H"); // VT cursor movement also resets the effective blink
        Assert.True(fixture.BlinkPhase);
        fixture.Process(reset);
        fixture.AssertBlinkTimer(false);
        Assert.True(fixture.BlinkPhase);
    }

    [AvaloniaTheory]
    [InlineData(false, 5, true)]
    [InlineData(true, 6, false)]
    public void Host_setting_changes_preserve_explicit_application_cursor_choices(bool hostBlink, int style, bool expectedBlink)
    {
        using var fixture = new ViewFixture(hostBlink);
        fixture.Process($"\u001b[{style} q");
        fixture.AssertBlinkTimer(expectedBlink);
        foreach (bool setting in new[] { !hostBlink, hostBlink })
        {
            fixture.View.ApplySettings(new TerminalSettings { CursorBlink = setting, CursorStyle = "Block" });
            fixture.AssertBlinkTimer(expectedBlink);
            Assert.False(fixture.Buffer.Modes.CursorStyleIsDefault);
            Assert.Equal(CursorStyle.Beam, fixture.Buffer.Modes.CursorStyle);
        }
        fixture.Process("\u001b[!p");
        fixture.AssertBlinkTimer(hostBlink);
    }

    [AvaloniaFact]
    public void Application_blink_stays_gated_by_focus_visibility_attachment_and_retirement()
    {
        using var fixture = new ViewFixture(hostBlink: false);
        fixture.Process("\u001b[5 q");
        fixture.AssertBlinkTimer(true);
        Assert.True(fixture.Other.Focus());
        fixture.AssertBlinkTimer(false);
        fixture.TickBlink(); // a previously queued tick cannot restart blinking
        Assert.True(fixture.BlinkPhase);
        Assert.True(fixture.View.Focus());
        fixture.AssertBlinkTimer(true);
        fixture.View.IsVisible = false;
        fixture.View.ApplySettings(new TerminalSettings { CursorBlink = false });
        fixture.AssertBlinkTimer(false);
        fixture.View.IsVisible = true;
        Assert.True(fixture.View.Focus());
        fixture.AssertBlinkTimer(true);
        fixture.Window.Content = null;
        fixture.TickBlink();
        Assert.Empty(fixture.View.RunningTimersForTest);
        fixture.Window.Content = fixture.Panel;
        Dispatcher.UIThread.RunJobs();
        Assert.True(fixture.View.Focus());
        fixture.AssertBlinkTimer(true);
        fixture.View.Retire();
        fixture.Process("\u001b[1 q");
        fixture.ResetBlink();
        fixture.TickBlink();
        Assert.Empty(fixture.View.RunningTimersForTest);
    }

    [AvaloniaFact]
    public void Replacing_the_buffer_refreshes_its_application_blink_override()
    {
        using var fixture = new ViewFixture(hostBlink: false);
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);
        parser.Process("\u001b[5 q");
        fixture.View.SetBuffer(buffer);
        fixture.AssertBlinkTimer(true);
        var steady = new TerminalBuffer(80, 24);
        new AnsiParser(steady).Process("\u001b[6 q");
        fixture.View.SetBuffer(steady);
        fixture.AssertBlinkTimer(false);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_hidden_VT_cursor_never_blinks_and_showing_it_refreshes_the_override(bool hostBlink)
    {
        using var fixture = new ViewFixture(hostBlink);
        fixture.Process("\u001b[5 q");
        fixture.AssertBlinkTimer(true);
        fixture.Process("\u001b[?25l");
        fixture.AssertBlinkTimer(false);
        Assert.True(fixture.BlinkPhase);
        fixture.TickBlink();
        fixture.AssertBlinkTimer(false);
        fixture.View.ApplySettings(new TerminalSettings { CursorBlink = !hostBlink });
        fixture.AssertBlinkTimer(false);
        fixture.Process("\u001b[?25h");
        fixture.AssertBlinkTimer(true);
        fixture.Process("\u001b[?25l\u001b[6 q\u001b[?25h");
        fixture.AssertBlinkTimer(false);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Input_clears_transient_suppression_and_redraws_a_steady_cursor(bool hostBlink)
    {
        using var fixture = new ViewFixture(hostBlink);
        fixture.Process("\u001b[6 q");
        fixture.Buffer.EnterBatchWrite();
        try { fixture.Buffer.ExtendCursorSuppression_NoLock(TimeSpan.FromDays(1)); }
        finally { fixture.Buffer.ExitBatchWrite(); }
        Assert.NotEqual(0, fixture.SuppressedUntil);
        Assert.False(fixture.Dirty);
        fixture.ResetBlink();
        Assert.Equal(0, fixture.SuppressedUntil);
        Assert.True(fixture.Dirty);
        fixture.AssertBlinkTimer(false);
    }

    [AvaloniaTheory]
    [Trait("Lane", "PlatformBoot")]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void Application_blink_controls_the_rendered_cursor_when_the_host_default_is_steady(int style)
    {
        using var fixture = new ViewFixture(hostBlink: false);
        fixture.View.ApplySettings(new TerminalSettings
        {
            CursorBlink = false,
            WindowOpacity = 1,
            FontFamily = "Cascadia Code, Consolas, Monospace"
        });
        fixture.Buffer.Theme.CursorColor = TermColor.FromRgb(255, 0, 0);
        fixture.Process($"\u001b[{style} q");
        fixture.TickBlink();
        Assert.False(fixture.CursorIsRendered());
        fixture.ResetBlink();
        Assert.True(fixture.CursorIsRendered());
    }

    private sealed class ViewFixture : IDisposable
    {
        public TerminalBuffer Buffer { get; } = new(80, 24);
        public TerminalView View { get; } = new() { Height = 300 };
        public Button Other { get; } = new() { Content = "Other focus" };
        public DockPanel Panel { get; } = new();
        public Window Window { get; }
        private readonly AnsiParser _parser;

        public ViewFixture(bool hostBlink)
        {
            _parser = new AnsiParser(Buffer);
            View.SetBuffer(Buffer);
            View.ApplySettings(new TerminalSettings { CursorBlink = hostBlink });
            DockPanel.SetDock(Other, Dock.Bottom);
            Panel.Children.Add(Other);
            Panel.Children.Add(View);
            Window = new Window { Content = Panel, Width = 800, Height = 400 };
            Window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(View.Focus());
            Assert.True(View.IsKeyboardFocusWithin);
            TickRender();
        }

        public long SuppressedUntil
        {
            get
            {
                Buffer.Lock.EnterReadLock();
                try { return Buffer.CursorSuppressedUntilUtcTicks; }
                finally { Buffer.Lock.ExitReadLock(); }
            }
        }
        public bool Dirty => (bool)typeof(TerminalView).GetField("_isDirty", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(View)!;
        public bool CursorIsRendered()
        {
            using var target = new RenderTargetBitmap(
                new PixelSize((int)Math.Ceiling(View.Bounds.Width), (int)Math.Ceiling(View.Bounds.Height)), new Vector(96, 96));
            View.RenderOffscreen(target);
            using var stream = new MemoryStream();
            target.Save(stream, PngBitmapEncoderOptions.Default);
            using var bitmap = SKBitmap.Decode(stream.ToArray());
            for (int y = 0; y < bitmap.Height; y++)
                for (int x = 0; x < bitmap.Width; x++)
                {
                    SKColor pixel = bitmap.GetPixel(x, y);
                    if (pixel.Red > 200 && pixel.Green < 50 && pixel.Blue < 50) return true;
                }
            return false;
        }
        public bool BlinkPhase => (bool)typeof(TerminalView).GetField("_cursorBlinkPhase", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(View)!;
        public void AssertBlinkTimer(bool expected) => Assert.Equal(expected, View.RunningTimersForTest.Contains("cursor blink"));
        public void Process(string text) { _parser.Process(text); TickRender(); }
        public void TickRender() => Invoke("OnRenderTimerTick", null, EventArgs.Empty);
        public void TickBlink() => Invoke("OnCursorBlinkTick", null, EventArgs.Empty);
        public void ResetBlink() => Invoke("ResetCursorBlink");
        private void Invoke(string method, params object?[] args) =>
            typeof(TerminalView).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(View, args);
        public void Dispose() { View.Retire(); Window.Close(); Dispatcher.UIThread.RunJobs(); }
    }
}
