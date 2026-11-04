using System;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Ntilde.Rendering.Tests;

public sealed class LinuxNativeAssetTests
{
    [Fact]
    public void Supported_Linux_release_architecture_requires_native_raster_clear_and_readback()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux native-asset packaging guard; other operating systems retain their existing native tests.");
        // release.yml ships linux-x64 and linux-arm64. This guard does not declare
        // new supported desktop architectures merely because NuGet has more assets.
        Assert.SkipUnless(RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64,
            "Outside the repository's Linux desktop release architecture matrix.");

        // Deliberately do not catch native-load errors or convert them to skips: otherwise
        // a missing Linux asset can silently skip the whole pixel/decoder test surface.
        var info = new SKImageInfo(2, 2, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        Assert.NotNull(surface);
        var expected = new SKColor(23, 91, 173, 255);
        surface.Canvas.Clear(expected);
        using var pixels = new SKBitmap(info);
        Assert.True(surface.ReadPixels(info, pixels.GetPixels(), pixels.RowBytes, 0, 0));
        Assert.Equal(expected, pixels.GetPixel(0, 0));
        Assert.Equal(expected, pixels.GetPixel(1, 1));
    }
}
