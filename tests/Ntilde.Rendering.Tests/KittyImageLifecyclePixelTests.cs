using Ntilde.VT;
using SkiaSharp;

namespace Ntilde.Rendering.Tests;

public sealed class KittyImageLifecyclePixelTests
{
    private static readonly bool SkiaAvailable = CheckSkiaAvailable();

    private static bool CheckSkiaAvailable()
    {
        try { using var surface = SKSurface.Create(new SKImageInfo(1, 1, SKColorType.Rgba8888)); return surface is not null; }
        catch { return false; }
    }

    private static string Command(string controls, string payload = "") => $"\x1b_G{controls};{payload}\x1b\\";

    [Fact]
    public void Numbered_transmit_place_soft_delete_and_snapshot_safe_hard_delete_preserve_real_pixels()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = new SkiaImageDecoder() };
        var replies = new List<string>(); parser.OnResponse = replies.Add;
        byte[] rgba = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 0, 255];
        parser.Process(Command("a=t,f=32,s=2,v=2,I=13", Convert.ToBase64String(rgba)));
        Assert.Empty(buffer.Images); Assert.Equal("\x1b_Gi=1,I=13;OK\x1b\\", Assert.Single(replies));
        parser.Process(Command("a=p,I=13,c=2,r=2,C=1"));
        SKBitmap bitmap = Assert.IsType<SKBitmap>(Assert.Single(buffer.Images).ImageHandle);
        var retired = new List<object>();
        try
        {
            AssertPixels(bitmap);
            parser.Process(Command("a=d,d=n,I=13"));
            buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Empty(retired); AssertPixels(bitmap);
            parser.Process(Command("a=p,i=1,C=1")); Assert.Same(bitmap, Assert.Single(buffer.Images).ImageHandle);
            int snapshot = buffer.BeginSnapshotSession();
            try
            {
                parser.Process(Command("a=d,d=I,i=1")); Assert.Empty(buffer.Images);
                buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Empty(retired); AssertPixels(bitmap);
            }
            finally { buffer.EndSnapshotSession(snapshot); }
            buffer.DrainRetiredImageHandles(retired, long.MaxValue);
            Assert.Same(bitmap, Assert.Single(retired));
            parser.Process(Command("a=p,i=1")); Assert.Contains(";ENOENT:", replies[^1], StringComparison.Ordinal);
            buffer.ClearImages(); buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Single(retired);
        }
        finally
        {
            buffer.ClearImages(); buffer.DrainRetiredImageHandles(retired, long.MaxValue);
            foreach (IDisposable handle in retired.Distinct(ReferenceEqualityComparer.Instance)) handle.Dispose();
        }
        Assert.Equal(IntPtr.Zero, bitmap.Handle);
    }

    [Fact]
    public void Invalid_raw_payload_has_a_negative_ack_and_does_not_consume_an_allocated_id()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = new SkiaImageDecoder() };
        var replies = new List<string>(); parser.OnResponse = replies.Add;
        parser.Process(Command("a=T,f=32,s=2,v=2,I=13", "AQIDBA=="));
        Assert.Empty(buffer.Images); Assert.StartsWith("\x1b_Gi=0,I=13;EINVAL:", Assert.Single(replies), StringComparison.Ordinal);
        parser.Process(Command("a=T,f=32,s=1,v=1,I=13,C=1", "/wAA/w=="));
        Assert.Equal("\x1b_Gi=1,I=13;OK\x1b\\", replies[^1]);
        SKBitmap bitmap = Assert.IsType<SKBitmap>(Assert.Single(buffer.Images).ImageHandle);
        Assert.Equal(SKColors.Red, bitmap.GetPixel(0, 0));
        buffer.ClearImages(); var retired = new List<object>(); buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Same(bitmap, Assert.Single(retired)); bitmap.Dispose();
    }

    [Theory]
    [InlineData(24, 0x18)]
    [InlineData(24, 0x1b)]
    [InlineData(32, 0x18)]
    [InlineData(32, 0x1b)]
    public void Leading_G_base64_reaches_the_production_decoder_with_exact_pixel_channels(int format, byte red)
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = new SkiaImageDecoder() };
        var replies = new List<string>(); parser.OnResponse = replies.Add;
        byte[] raw = format == 24 ? [red, 0x12, 0xab] : [red, 0x12, 0xab, 0xff];
        string encoded = Convert.ToBase64String(raw);
        Assert.Equal('G', encoded[0]);
        parser.Process(Command($"a=T,f={format},s=1,v=1,i=42,C=1", encoded));
        SKBitmap bitmap = Assert.IsType<SKBitmap>(Assert.Single(buffer.Images).ImageHandle);
        var retired = new List<object>();
        try
        {
            Assert.Equal("\x1b_Gi=42;OK\x1b\\", Assert.Single(replies));
            Assert.Equal(1, bitmap.Width); Assert.Equal(1, bitmap.Height);
            Assert.Equal(new SKColor(red, 0x12, 0xab, 0xff), bitmap.GetPixel(0, 0));
        }
        finally
        {
            buffer.ClearImages(); buffer.DrainRetiredImageHandles(retired, long.MaxValue);
            foreach (IDisposable handle in retired.Distinct(ReferenceEqualityComparer.Instance)) handle.Dispose();
        }
        Assert.Equal(IntPtr.Zero, bitmap.Handle);
    }

    private static void AssertPixels(SKBitmap bitmap)
    {
        Assert.NotEqual(IntPtr.Zero, bitmap.Handle);
        Assert.Equal(SKColors.Red, bitmap.GetPixel(0, 0)); Assert.Equal(SKColors.Lime, bitmap.GetPixel(1, 0));
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(0, 1)); Assert.Equal(SKColors.Yellow, bitmap.GetPixel(1, 1));
    }
}
