using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Ntilde.Rendering;
using Ntilde.Shell;
using Ntilde.VT;
using SkiaSharp;

namespace Ntilde.Tests.RenderTests;

/// <summary>
/// The live view advances Kitty animations from the buffer clock and paints the frame the clock is in
/// (#988). The clock is injected here, so these establish the view-to-pixels wiring and wake-up
/// policy, not wall-clock playback; that needs the owned runtime measurement.
/// </summary>
public sealed class KittyAnimationRenderTests
{
    private static string Command(string controls, string payload = "") => $"\x1b_G{controls};{payload}\x1b\\";
    private static string Solid(byte r, byte g, byte b) => Convert.ToBase64String(new[] { r, g, b, (byte)255 });

    private static SKColor CentralPixel(TerminalView view)
    {
        using var target = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(view.Bounds.Width), (int)Math.Ceiling(view.Bounds.Height)), new Vector(96, 96));
        view.RenderOffscreen(target);
        using var stream = new MemoryStream();
        target.Save(stream, PngBitmapEncoderOptions.Default);
        using var bitmap = SKBitmap.Decode(stream.ToArray());
        // Cell (0,0) is 16x32 px in this view's metrics once padding is allowed for; sample well inside it.
        return bitmap.GetPixel(14, 14);
    }

    private sealed class Rig : IDisposable
    {
        internal readonly TerminalView View = new() { Height = 200 };
        internal readonly TerminalBuffer Buffer = new(40, 8);
        internal readonly AnsiParser Parser;
        internal readonly Window Window;
        internal long Now;

        internal Rig()
        {
            Parser = new AnsiParser(Buffer, forceConPtyFiltering: false) { ImageDecoder = new SkiaImageDecoder(), CellWidth = 8, CellHeight = 16 };
            Buffer.KittyAnimationClock = () => Now;
            View.SetBuffer(Buffer);
            Window = new Window { Content = View, Width = 600, Height = 300 };
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        internal void Send(string controls, string payload = "") => Parser.Process(Command(controls, payload));

        public void Dispose() { View.Retire(); Window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    private static void Build(Rig rig)
    {
        rig.Send("a=T,f=32,s=1,v=1,i=1,c=2,r=2,C=1", Convert.ToBase64String(new byte[] { 255, 0, 0, 255 }));
        rig.Send("a=f,i=1,f=32,s=1,v=1,z=100", Convert.ToBase64String(new byte[] { 0, 255, 0, 255 }));
        rig.Send("a=f,i=1,f=32,s=1,v=1,z=100", Convert.ToBase64String(new byte[] { 0, 0, 255, 255 }));
        rig.Send("a=a,i=1,r=1,z=100");
    }

    [AvaloniaFact]
    public void The_view_paints_the_frame_the_clock_is_in_and_a_stopped_animation_stays_put()
    {
        using var rig = new Rig();
        Build(rig);
        Assert.Equal(SKColors.Red, CentralPixel(rig.View));
        rig.Now = 10_000;
        Assert.Equal(SKColors.Red, CentralPixel(rig.View));            // stopped: time alone changes nothing
        rig.Send("a=a,i=1,s=3");
        Assert.Equal(SKColors.Red, CentralPixel(rig.View));
        rig.Now += 99; Assert.Equal(SKColors.Red, CentralPixel(rig.View));
        rig.Now += 1; Assert.Equal(new SKColor(0, 255, 0), CentralPixel(rig.View));
        rig.Now += 100; Assert.Equal(new SKColor(0, 0, 255), CentralPixel(rig.View));
        rig.Now += 100; Assert.Equal(SKColors.Red, CentralPixel(rig.View));   // looped
        rig.Send("a=a,i=1,s=1");
        rig.Now += 5000; Assert.Equal(SKColors.Red, CentralPixel(rig.View));
    }

    [AvaloniaFact]
    public void A_wake_up_is_armed_only_for_a_running_placed_animation_and_cleared_on_retire()
    {
        using var rig = new Rig();
        Build(rig);
        rig.View.InvalidateVisual();
        using (var frame = rig.Window.CaptureRenderedFrame()) Assert.NotNull(frame);
        Assert.False(rig.View.KittyAnimationWakeArmedForTest);        // stopped: no timer for a still image
        rig.Send("a=a,i=1,s=3");
        rig.View.InvalidateVisual();
        using (var frame = rig.Window.CaptureRenderedFrame()) Assert.NotNull(frame);
        Assert.True(rig.View.KittyAnimationWakeArmedForTest);
        rig.Send("a=a,i=1,s=1");
        rig.Send("a=d,d=i,i=1");
        rig.View.InvalidateVisual();
        using (var frame = rig.Window.CaptureRenderedFrame()) Assert.NotNull(frame);
    }

    [AvaloniaFact]
    public void Retiring_the_view_cancels_the_wake_up_and_a_later_frame_cannot_arm_another()
    {
        using var rig = new Rig();
        Build(rig);
        rig.Send("a=a,i=1,s=3");
        rig.View.InvalidateVisual();
        using (var frame = rig.Window.CaptureRenderedFrame()) Assert.NotNull(frame);
        Assert.True(rig.View.KittyAnimationWakeArmedForTest);
        rig.View.Retire();
        Assert.False(rig.View.KittyAnimationWakeArmedForTest);
        rig.View.InvalidateVisual();
        using (var frame = rig.Window.CaptureRenderedFrame()) Assert.NotNull(frame);
        Assert.False(rig.View.KittyAnimationWakeArmedForTest);
    }

    [AvaloniaFact]
    public void A_wake_up_requests_no_repaint_inside_a_synchronized_output_batch()
    {
        using var rig = new Rig();
        Build(rig);
        Assert.True(rig.View.OnKittyAnimationWake());
        rig.Parser.Process("\x1b[?2026h");
        Assert.True(rig.Buffer.IsSynchronizedOutput);
        Assert.False(rig.View.OnKittyAnimationWake());          // held until the batch ends
        rig.Parser.Process("\x1b[?2026l");
        Assert.False(rig.Buffer.IsSynchronizedOutput);
        Assert.True(rig.View.OnKittyAnimationWake());
    }
}
