using Ntilde.Rendering;
using SkiaSharp;

namespace Ntilde.Tests.RenderTests;

public sealed class RowImageCacheTests
{
    private static SKPicture Picture()
    {
        using var recorder = new SKPictureRecorder();
        recorder.BeginRecording(new SKRect(0, 0, 10, 10)).Clear(SKColors.Red);
        return recorder.EndRecording();
    }

    [Fact]
    public void InvalidateRow_RemovesAllVersionsWithoutDisposingPicturesMidFrame()
    {
        using var cache = new RowImageCache();
        var first = Picture();
        var second = Picture();
        var other = Picture();
        cache.Add(1, 1, first);
        cache.Add(1, 2, second);
        cache.Add(2, 1, other);
        cache.InvalidateRow(1);
        Assert.Null(cache.Get(1, 1));
        Assert.Null(cache.Get(1, 2));
        Assert.False(cache.TryGetLatestByRowId(1, out _, out _));
        Assert.Same(other, cache.Get(2, 1));
        Assert.NotEqual(IntPtr.Zero, first.Handle);
        Assert.NotEqual(IntPtr.Zero, second.Handle);
        cache.DrainDisposalsAndApplyClearIfRequested();
        Assert.Equal(IntPtr.Zero, first.Handle);
        Assert.Equal(IntPtr.Zero, second.Handle);
        Assert.NotEqual(IntPtr.Zero, other.Handle);
    }
}
