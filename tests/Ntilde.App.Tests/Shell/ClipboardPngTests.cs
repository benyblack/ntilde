using System.Buffers.Binary;
using Avalonia.Input;
using Ntilde.Shell.Clipboard;
using Ntilde.VT;

namespace Ntilde.Tests.Shell;

public class ClipboardPngTests
{
    private static byte[] Header(uint width, uint height)
    {
        byte[] data = new byte[33];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(data.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20, 4), height);
        return data;
    }

    [Fact]
    public void Pixel_budget_is_checked_before_bitmap_creation_and_does_not_overflow()
    {
        Assert.Equal(1024, ClipboardPng.PixelBytes(Header(16, 16), 1024));
        Assert.Throws<ClipboardSizeLimitException>(() => ClipboardPng.PixelBytes(Header(16, 16), 1023));
        Assert.Throws<ClipboardSizeLimitException>(() => ClipboardPng.PixelBytes(Header(uint.MaxValue, uint.MaxValue), int.MaxValue));
        Assert.Throws<InvalidDataException>(() => ClipboardPng.PixelBytes(Header(0, 1), 1024));
        Assert.Throws<InvalidDataException>(() => ClipboardPng.PixelBytes([0, 1, 2], 1024));
    }

    [Fact]
    public void Original_encoded_slot_is_native_only_and_maps_back_to_the_public_MIME()
    {
        Assert.False(KittyClipboardPacket.IsMime(ClipboardPng.OriginalFormat));
        Assert.Equal("image/png", AvaloniaKittyClipboard.ToMime(DataFormat.CreateBytesPlatformFormat(ClipboardPng.OriginalFormat)));
    }

    private sealed class Resource : IDisposable
    {
        public int Disposals;
        public void Dispose() => Disposals++;
    }

    [Fact]
    public void Transfer_owns_native_resources_until_disposed_and_releases_them_once()
    {
        var resource = new Resource();
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText("owned clipboard fixture"));
        var transfer = new OwnedClipboardTransfer(data, [resource]);
        Assert.Contains(DataFormat.Text, transfer.Formats);
        Assert.Equal(0, resource.Disposals);
        transfer.Dispose();
        transfer.Dispose();
        Assert.Equal(1, resource.Disposals);
    }
}
